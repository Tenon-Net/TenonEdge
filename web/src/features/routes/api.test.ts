import { describe, expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createRouteApi, decodeRouteResponse, newRoute } from './api'
import { decodeGraph, encodeGraph } from '../canvas/codec'
import { ApiError } from '../../api/errors'

const config =
    '{"payload":{"mode":"custom","fields":[{"target":"a","constant":9223372036854775807},{"target":"b","constant":0.1234567890123456789012345678}]},"connectionId":1}'
const graph = `{"schemaVersion":1,"nodes":[{"id":"out","type":"output.http","version":1,"name":"Out","config":${config}}],"edges":[],"layout":{"nodes":{}}}`
const response = (version = 'v1', id = '42') =>
    `{"version":"${version}","route":{"id":${id},"key":"route","name":"Route","description":null,"enabled":false,"graph":${graph},"updatedAt":1790000000000,"updatedBy":"admin"}}`
function fixture(fetcher: typeof fetch) {
    let epoch = 0
    const unauthorized = vi.fn()
    const api = createRouteApi(
        createGatewayClient({
            baseUrl: 'http://gateway.test',
            csrf: () => 'csrf',
            epoch: () => epoch,
            unauthorized,
            fetcher,
        }),
    )
    return { api, unauthorized, advance: () => epoch++ }
}

describe('route API lossless transport', () => {
    it('reads and writes exact number tokens through the real OpenAPI serializer and middleware', async () => {
        const requests: Request[] = []
        const bodies: string[] = []
        const f = fixture(async (input) => {
            const request = new Request(input)
            requests.push(request)
            bodies.push(await request.text())
            return new Response(
                response(request.method === 'GET' ? 'v1' : 'v2'),
                { headers: { 'Content-Type': 'application/json' } },
            )
        })
        const form = await f.api.read(42)
        expect(form.document.definition.nodes[0].configText).toBe(config)
        form.name = 'Edited'
        const original = JSON.stringify(form)
        const saved = await f.api.save(form)
        expect(saved.version).toBe('v2')
        expect(saved.document.definition.nodes[0].configText).toBe(config)
        expect(JSON.stringify(form)).toBe(original)
        expect(requests.map((request) => request.method)).toEqual([
            'GET',
            'PUT',
        ])
        expect(requests[1].url).toBe(
            'http://gateway.test/api/edge-admin/v1/routes/42',
        )
        expect(requests[1].headers.get('X-CSRF-Token')).toBe('csrf')
        expect(requests[1].headers.get('Content-Type')).toContain(
            'application/json',
        )
        expect(requests[1].credentials).toBe('same-origin')
        expect(bodies[1]).toContain('"config":' + config)
        expect(bodies[1]).toContain('"expectedVersion":"v1"')
        expect(bodies[1]).not.toContain('configText')
        expect(bodies[1]).not.toContain('"text":"9223372036854775807"')
    })
    it('creates with POST and independently decodes copied graph bodies', async () => {
        const paths: string[] = []
        const f = fixture(async (input) => {
            const request = new Request(input)
            paths.push(request.method + ' ' + new URL(request.url).pathname)
            return new Response(response('new', '43'))
        })
        const form = newRoute('v1')
        form.document = decodeGraph(graph)
        await f.api.save(form)
        const copy = await f.api.copy(42, 'v1', 'copy', 'Copy')
        expect(paths).toEqual([
            'POST /api/edge-admin/v1/routes',
            'POST /api/edge-admin/v1/routes/42/copy',
        ])
        expect(copy.id).toBe(43)
        expect(copy.enabled).toBe(false)
        expect(copy.document.definition.nodes[0].configText).toBe(config)
        expect(form.id).toBeUndefined()
    })
    it.each([409, 500, 403, 401])(
        'does not replay or mutate a failed save (%s)',
        async (status) => {
            const fetcher = vi.fn(async () =>
                Response.json({ title: 'failure' }, { status }),
            )
            const f = fixture(fetcher)
            const form = decodeRouteResponse(response())
            const before = JSON.stringify(form)
            await expect(f.api.save(form)).rejects.toBeInstanceOf(ApiError)
            expect(fetcher).toHaveBeenCalledTimes(1)
            expect(JSON.stringify(form)).toBe(before)
            expect(f.unauthorized).toHaveBeenCalledTimes(status === 401 ? 1 : 0)
        },
    )
    it('does not return old-account graph data after session replacement', async () => {
        let finish!: (response: Response) => void
        const f = fixture(
            () =>
                new Promise((resolve) => {
                    finish = resolve
                }),
        )
        const pending = f.api.read(42)
        await vi.waitFor(() => expect(finish).toBeDefined())
        f.advance()
        finish(new Response(response()))
        await expect(pending).rejects.toThrow('stale-session-response')
    })
    it('passes query cancellation to the managed request', async () => {
        const f = fixture(async (input) => {
            const request = new Request(input)
            return new Promise((_resolve, reject) => {
                request.signal.addEventListener(
                    'abort',
                    () => reject(new Error('cancelled')),
                    { once: true },
                )
            })
        })
        const controller = new AbortController()
        const pending = f.api.read(42, controller.signal)
        await new Promise((resolve) => setTimeout(resolve, 0))
        controller.abort()
        await expect(pending).rejects.toThrow('cancelled')
    })
    it('checks the entire request byte budget before making a write', async () => {
        const fetcher = vi.fn(async () => new Response(response()))
        const f = fixture(fetcher)
        const form = decodeRouteResponse(response())
        form.description = '中'.repeat(400000)
        await expect(f.api.save(form)).rejects.toThrow('limit')
        expect(fetcher).not.toHaveBeenCalled()
    })
    it.each(['9007199254740993', '0', '-1', 'true', '1.00000000000000001'])(
        'rejects unsafe route IDs %s',
        (id) => {
            expect(() => decodeRouteResponse(response('v1', id))).toThrow()
        },
    )
    it('keeps graph validation and exact response types at the boundary', () => {
        expect(() =>
            decodeRouteResponse(
                response().replace('"enabled":false', '"enabled":"false"'),
            ),
        ).toThrow()
        expect(() =>
            decodeRouteResponse(
                response().replace('"version":1', '"version":2'),
            ),
        ).toThrow()
        expect(() =>
            decodeRouteResponse(
                response().replace(
                    '"version":"v1"',
                    '"version":"v1","version":"v2"',
                ),
            ),
        ).toThrow('duplicate')
        expect(encodeGraph(decodeRouteResponse(response()).document)).toContain(
            config,
        )
    })
    it('uses versioned delete and structural compile without pretending to apply', async () => {
        const requests: Request[] = []
        const f = fixture(async (input) => {
            const request = new Request(input)
            requests.push(request)
            if (request.method === 'GET')
                return Response.json({ version: 'v1', routes: [] })
            return Response.json({
                version: 'v2',
                compiled: false,
                issues: [{ code: 'field.missing' }],
            })
        })
        expect((await f.api.list()).routes).toEqual([])
        await f.api.remove(42, 'v1')
        const result = await f.api.compile('v2')
        expect(requests[1].url).toContain('/routes/42?expectedVersion=v1')
        expect(await requests[2].text()).toBe('{"expectedVersion":"v2"}')
        expect(result.compiled).toBe(false)
        expect(requests.map((request) => request.url)).not.toContain(
            expect.stringContaining('/apply'),
        )
    })
    it('analyzes the current unsaved graph without rounding constants or updating its version', async () => {
        let body = ''
        const f = fixture(async (input) => {
            const request = new Request(input)
            expect(new URL(request.url).pathname).toBe(
                '/api/edge-admin/v1/routes/fields',
            )
            expect(request.headers.get('X-CSRF-Token')).toBe('csrf')
            body = await request.text()
            return Response.json({
                version: 'v1',
                nodeId: 'out',
                analyzed: true,
                fields: [],
                excludedFields: [],
                differencesTruncated: false,
                unitsDiffer: false,
                nextAfterKey: null,
                commonCount: 0,
                upstreamCount: 1,
                issues: [],
            })
        })
        const form = decodeRouteResponse(response())
        const original = JSON.stringify(form)
        const result = await f.api.fields(form, 'out', { search: 'v' })
        expect(result.analyzed).toBe(true)
        expect(body).toContain('"constant":9223372036854775807')
        expect(body).toContain('"constant":0.1234567890123456789012345678')
        expect(body).toContain('"search":"v"')
        expect(JSON.stringify(form)).toBe(original)
    })
})
