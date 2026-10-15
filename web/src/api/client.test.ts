import { createServer } from 'node:http'
import { once } from 'node:events'
import { describe, it, expect, vi } from 'vitest'
import { createGatewayClient, fetchWithDeadline } from './client'
import { ApiError, fieldErrors } from './errors'

function fixture(fetcher: typeof fetch) {
    const unauthorized = vi.fn()
    let epoch = 0
    const api = createGatewayClient({
        baseUrl: 'http://gateway.test',
        csrf: () => 'memory-csrf',
        epoch: () => epoch,
        unauthorized,
        fetcher,
    })
    return {
        api,
        unauthorized,
        advance: () => {
            epoch++
        },
    }
}

describe('single same-origin API client', () => {
    it('sends cookie credentials and current CSRF once for a write, not for a query', async () => {
        const requests: Request[] = []
        const f = fixture(async (input) => {
            const request = new Request(input)
            requests.push(request)
            return Response.json({})
        })
        await f.api.GET('/api/edge-admin/v1/setup')
        await f.api.POST('/api/edge-admin/v1/auth/logout')
        expect(requests).toHaveLength(2)
        expect(requests[0].credentials).toBe('same-origin')
        expect(requests[0].headers.get('X-CSRF-Token')).toBeNull()
        expect(requests[1].headers.get('X-CSRF-Token')).toBe('memory-csrf')
    })
    it('500 stays an error and a write is never replayed', async () => {
        const fetcher = vi.fn(async () =>
            Response.json({ title: 'failure' }, { status: 500 }),
        )
        const f = fixture(fetcher)
        await expect(
            f.api.POST('/api/edge-admin/v1/auth/logout'),
        ).rejects.toBeInstanceOf(ApiError)
        expect(fetcher).toHaveBeenCalledTimes(1)
        expect(f.unauthorized).not.toHaveBeenCalled()
    })
    it('401 ends the session except invalid login; 403 does not log out', async () => {
        const f = fixture(async () => Response.json({}, { status: 401 }))
        await expect(
            f.api.POST('/api/edge-admin/v1/auth/login', {
                body: { accountName: 'fixture', password: 'fixture' },
            }),
        ).rejects.toBeInstanceOf(ApiError)
        expect(f.unauthorized).not.toHaveBeenCalled()
        await expect(
            f.api.GET('/api/edge-admin/v1/auth/session'),
        ).rejects.toBeInstanceOf(ApiError)
        expect(f.unauthorized).toHaveBeenCalledTimes(1)
        const denied = fixture(async () => Response.json({}, { status: 403 }))
        await expect(
            denied.api.GET('/api/edge-admin/v1/auth/session'),
        ).rejects.toBeInstanceOf(ApiError)
        expect(denied.unauthorized).not.toHaveBeenCalled()
    })
    it('rejects old-account responses even when they succeed', async () => {
        let finish!: (response: Response) => void
        const f = fixture(
            () =>
                new Promise<Response>((resolve) => {
                    finish = resolve
                }),
        )
        const pending = f.api.GET('/api/edge-admin/v1/auth/session')
        await vi.waitFor(() => expect(finish).toBeDefined())
        f.advance()
        finish(Response.json({ accountName: 'old' }))
        await expect(pending).rejects.toThrow('stale-session-response')
    })
    it('forwards caller cancellation and bounds hanging requests without secure-context APIs', async () => {
        const fetcher: typeof fetch = async (input) => {
            const request = new Request(input)
            return new Promise((_resolve, reject) => {
                if (request.signal.aborted)
                    reject(new DOMException('aborted', 'AbortError'))
                else
                    request.signal.addEventListener(
                        'abort',
                        () => reject(new DOMException('aborted', 'AbortError')),
                        { once: true },
                    )
            })
        }
        const controller = new AbortController()
        const pending = fetchWithDeadline(
            new Request('http://gateway.test', { signal: controller.signal }),
            fetcher,
        )
        controller.abort()
        await expect(pending).rejects.toThrow('aborted')
        await expect(
            fetchWithDeadline(new Request('http://gateway.test'), fetcher, 5),
        ).rejects.toThrow('aborted')
    })
    it.each([
        ['POST', '/api/edge-admin/v1/routes/test-send/confirm', 35000],
        ['POST', '/api/edge-admin/v1/routes/test-send/prepare', 15000],
        ['GET', '/api/edge-admin/v1/routes/test-send/confirm', 15000],
        ['POST', '/api/edge-admin/v1/routes/preview', 35000],
        ['POST', '/api/edge-admin/v1/routes/preview/http', 35000],
        ['POST', '/api/edge-admin/v1/routes/preview/mqtt', 35000],
        ['POST', '/api/edge-admin/v1/routes/preview/modbus', 35000],
        ['POST', '/api/edge-admin/v1/routes/preview/recent', 35000],
        ['POST', '/api/edge-admin/v1/routes/preview/recent/messages', 35000],
        ['GET', '/api/edge-admin/v1/routes/preview/http', 15000],
        ['POST', '/api/edge-admin/v1/devices/42/preview/mqtt', 35000],
        ['POST', '/api/edge-admin/v1/devices/42/preview/http', 35000],
        ['POST', '/api/edge-admin/v1/devices/42/preview/modbus', 35000],
        ['POST', '/api/edge-admin/v1/connections/test/mqtt', 65000],
        ['POST', '/api/edge-admin/v1/connections/test/http', 65000],
        ['POST', '/api/edge-admin/v1/connections/test/modbus', 65000],
        ['GET', '/api/edge-admin/v1/auth/session', 15000],
        ['POST', '/api/edge-admin/v1/auth/logout', 15000],
        ['GET', '/api/edge-admin/v1/devices/42/preview/mqtt', 15000],
    ])(
        'bounds %s %s at %i ms without cutting off server work',
        async (method, path, deadline) => {
            vi.useFakeTimers()
            try {
                let signal: AbortSignal | undefined
                const result = fetchWithDeadline(
                    new Request('http://gateway.test' + path, { method }),
                    async (input) => {
                        signal = new Request(input).signal
                        return new Promise<Response>((_resolve, reject) =>
                            signal!.addEventListener(
                                'abort',
                                () => reject(new Error('aborted')),
                                { once: true },
                            ),
                        )
                    },
                ).catch((error) => error.message)
                await vi.advanceTimersByTimeAsync(deadline - 1)
                expect(signal?.aborted).toBe(false)
                await vi.advanceTimersByTimeAsync(1)
                expect(await result).toBe('aborted')
                expect(vi.getTimerCount()).toBe(0)
            } finally {
                vi.useRealTimers()
            }
        },
    )
    it('the deadline includes a real HTTP response body that stalls after its headers', async () => {
        const server = createServer((_request, response) => {
            response.writeHead(200, { 'Content-Type': 'application/json' })
            response.write('{')
        })
        server.listen(0, '127.0.0.1')
        await once(server, 'listening')
        const address = server.address()
        if (!address || typeof address === 'string')
            throw new Error('missing-test-port')
        let headersReceived = false
        try {
            await expect(
                fetchWithDeadline(
                    new Request(`http://127.0.0.1:${address.port}`),
                    async (input, init) => {
                        const response = await fetch(input, init)
                        headersReceived = true
                        return response
                    },
                    200,
                ),
            ).rejects.toThrow()
            expect(headersReceived).toBe(true)
        } finally {
            server.closeAllConnections()
            await new Promise<void>((resolve, reject) =>
                server.close((error) => (error ? reject(error) : resolve())),
            )
        }
    })
    it('only exposes structured string field errors', () => {
        expect(
            fieldErrors({ errors: { accountName: ['required'], other: 123 } }),
        ).toEqual({ accountName: ['required'] })
        expect(fieldErrors('<html>failure</html>')).toEqual({})
    })
})

describe('live OpenAPI document shares the session transport', () => {
    it('reads only the fixed same-origin document and never sends CSRF or a write', async () => {
        const fetcher = vi.fn(async (input: RequestInfo | URL) => {
            const request = new Request(input)
            expect(request.url).toBe(
                'http://gateway.test/api/edge-admin/v1/openapi/v1.json',
            )
            expect(request.method).toBe('GET')
            expect(request.credentials).toBe('same-origin')
            expect(request.headers.get('Accept')).toBe('application/json')
            expect(request.headers.get('X-CSRF-Token')).toBeNull()
            return Response.json({ openapi: '3.1.1' })
        })
        expect(await fixture(fetcher).api.readOpenApiDocument()).toEqual({
            openapi: '3.1.1',
        })
        expect(fetcher).toHaveBeenCalledOnce()
    })
    it('401 logs out once without retry; stale session cannot return content or invalidate the new session', async () => {
        const fetcher = vi.fn(async () => Response.json({}, { status: 401 }))
        const denied = fixture(fetcher)
        await expect(denied.api.readOpenApiDocument()).rejects.toBeInstanceOf(
            ApiError,
        )
        expect(denied.unauthorized).toHaveBeenCalledOnce()
        expect(fetcher).toHaveBeenCalledOnce()
        let release!: (response: Response) => void
        const stale = fixture(
            () =>
                new Promise((resolve) => {
                    release = resolve
                }),
        )
        const pending = stale.api.readOpenApiDocument()
        stale.advance()
        release(Response.json({}, { status: 401 }))
        await expect(pending).rejects.toThrow('stale-session-response')
        expect(stale.unauthorized).not.toHaveBeenCalled()
    })
    it('propagates document cancellation', async () => {
        const controller = new AbortController()
        const client = fixture(
            (input) =>
                new Promise((_resolve, reject) => {
                    new Request(input).signal.addEventListener(
                        'abort',
                        () => reject(new Error('cancelled')),
                        { once: true },
                    )
                }),
        )
        const pending = client.api.readOpenApiDocument(controller.signal)
        controller.abort()
        await expect(pending).rejects.toThrow('cancelled')
    })
})
