import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createOutputControlsApi } from './controls-api'

const token = 'a'.repeat(32)
function client(fetcher: typeof fetch) {
    return createOutputControlsApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'csrf-test',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('reads the control version without a write and requires confirmation for a CSRF-protected CAS write', async () => {
    const requests: Request[] = []
    const bodies: unknown[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        if (request.method === 'PUT') bodies.push(await request.json())
        return Response.json({ manuallyPaused: false, resumeVersion: token })
    })
    expect((await api.read('9007199254740991')).resumeVersion).toBe(token)
    await expect(api.write('11', true, token, false)).rejects.toThrow(
        'output-confirmation',
    )
    expect(requests).toHaveLength(1)
    await api.write('11', true, token, true)
    expect(requests.map((r) => r.method)).toEqual(['GET', 'PUT'])
    expect(new URL(requests[0].url).pathname).toBe(
        '/api/edge-admin/v1/connections/9007199254740991/delivery-control',
    )
    expect(requests[0].headers.has('X-CSRF-Token')).toBe(false)
    expect(requests[1].headers.get('X-CSRF-Token')).toBe('csrf-test')
    expect(bodies).toEqual([
        { paused: true, expectedVersion: token, confirmed: true },
    ])
})
it('binds retry to the exact Int64 revision and backoff window; 202 is only signal acceptance', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        expect(request.method).toBe('POST')
        expect(request.headers.get('X-CSRF-Token')).toBe('csrf-test')
        expect(new URL(request.url).pathname).toBe(
            '/api/edge-admin/v1/connections/11/retry-now',
        )
        expect(await request.json()).toEqual({
            revision: '9223372036854775807',
            backoffVersion: token,
        })
        return new Response(null, { status: 202 })
    })
    await client(fetcher).retry('11', '9223372036854775807', token)
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it.each([
    ['0', '1', token],
    ['9007199254740992', '1', token],
    ['11', '9223372036854775808', token],
    ['11', '1.1', token],
    ['11', '1', 'stale'],
])(
    'rejects invalid target/revision/window before sending (%s, %s, %s)',
    async (id, revision, window) => {
        const fetcher = vi.fn()
        await expect(
            client(fetcher).retry(id, revision, window),
        ).rejects.toThrow()
        expect(fetcher).not.toHaveBeenCalled()
    },
)
it.each([403, 409, 503])(
    'does not retry failed writes or retry-now on HTTP %i',
    async (status) => {
        const fetcher = vi.fn(async () => Response.json({ status }, { status }))
        const api = client(fetcher)
        await expect(api.write('11', false, token, true)).rejects.toThrow()
        await expect(api.retry('11', '1', token)).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(2)
    },
)
it('does not accept an unexpected 200 or automatically replay a lost response', async () => {
    const fetcher = vi.fn(async () => new Response('{}', { status: 200 }))
    await expect(client(fetcher).retry('11', '1', token)).rejects.toThrow(
        'output-retry-response',
    )
    expect(fetcher).toHaveBeenCalledTimes(1)
    const lost = vi.fn(async () => {
        throw new TypeError('network-response-lost')
    })
    await expect(client(lost).write('11', true, token, true)).rejects.toThrow()
    expect(lost).toHaveBeenCalledTimes(1)
})
