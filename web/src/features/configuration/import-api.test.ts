import { describe, expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createConfigurationImportApi, maximumImportBytes } from './import-api'
import { decodeImportReview, decodeCurrentImport } from './api'

const id = 'a1a86620-3a11-4931-a431-182349919559'
const review = `{"id":"${id}","version":"v1","mode":"merge","changes":[{"kind":"route","id":42,"key":"values","deviceId":null,"change":"added","before":null,"after":{"constant":9223372036854775807,"decimal":0.123456789012345678901,"exponent":1e-20},"updatedBy":null,"updatedAt":null}],"credentials":[],"expiresAt":"2026-10-15T01:00:00Z"}`
function setup(fetcher: typeof fetch) {
    return createConfigurationImportApi(
        createGatewayClient({
            baseUrl: 'http://gateway.test',
            csrf: () => 'fixture-csrf',
            epoch: () => 0,
            unauthorized: vi.fn(),
            fetcher,
        }),
    )
}
describe('configuration import uses exact file bytes and explicit once-only actions', () => {
    it('decodes the complete before/after without rounding numbers, including current candidate', () => {
        const decoded = decodeImportReview(review)
        expect(decoded.changes[0].afterText).toBe(
            '{"constant":9223372036854775807,"decimal":0.123456789012345678901,"exponent":1e-20}',
        )
        expect(decodeCurrentImport('{"review":' + review + '}')).toEqual(
            decoded,
        )
        expect(decodeCurrentImport('{"review":null}')).toBeNull()
    })
    it('uploads the raw file with CSRF and explicit mode/version then sends only confirmation id', async () => {
        const requests: Request[] = []
        const api = setup(async (input) => {
            const request =
                input instanceof Request ? input : new Request(input)
            requests.push(request.clone())
            return new Response(
                request.url.includes('prepare') ? review : '{"version":"v2"}',
            )
        })
        const bytes =
            '{ "formatVersion": 1, "constant": 9223372036854775807, "value": 1e-20 }\n'
        const file = new File([bytes], 'configuration.json', {
            type: 'application/octet-stream',
        })
        const signal = new AbortController().signal
        expect((await api.prepare(file, 'replace', 'v1', signal)).id).toBe(id)
        expect(await requests[0].text()).toBe(bytes)
        expect(requests[0].headers.get('content-type')).toBe('application/json')
        expect(requests[0].headers.get('X-CSRF-Token')).toBe('fixture-csrf')
        expect(new URL(requests[0].url).searchParams.get('mode')).toBe(
            'replace',
        )
        expect(
            new URL(requests[0].url).searchParams.get('expectedVersion'),
        ).toBe('v1')
        expect(await api.confirm(id, signal)).toEqual({ version: 'v2' })
        expect(await requests[1].json()).toEqual({ id, confirmed: true })
    })
    it('refuses oversize files without reading or sending a truncated prefix', async () => {
        const fetcher = vi.fn()
        const file = new File(
            [new Uint8Array(maximumImportBytes + 1)],
            'large.json',
        )
        await expect(
            setup(fetcher).prepare(
                file,
                'merge',
                'v1',
                new AbortController().signal,
            ),
        ).rejects.toThrow('configuration-import-size')
        expect(fetcher).not.toHaveBeenCalled()
    })
    it('does not automatically retry unknown confirmations', async () => {
        const fetcher = vi.fn(async () => {
            throw new TypeError('lost-response')
        })
        await expect(
            setup(fetcher).confirm(id, new AbortController().signal),
        ).rejects.toThrow('lost-response')
        expect(fetcher).toHaveBeenCalledTimes(1)
    })
    it('reads and cancels only the current session candidate', async () => {
        const calls: Request[] = []
        const api = setup(async (input) => {
            const request =
                input instanceof Request ? input : new Request(input)
            calls.push(request)
            return request.method === 'DELETE'
                ? new Response(null, { status: 204 })
                : new Response('{"review":' + review + '}')
        })
        const signal = new AbortController().signal
        expect((await api.current(signal))?.id).toBe(id)
        await api.cancel(id, signal)
        expect(calls.map((value) => value.method)).toEqual(['GET', 'DELETE'])
        expect(calls[1].url).toContain(id)
    })
})
