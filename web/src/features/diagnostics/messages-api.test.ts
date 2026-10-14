import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createMessagesApi } from './messages-api'

function client(fetcher: typeof fetch) {
    return createMessagesApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'unused',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('keeps identities and body text exact and requests only the explicitly selected record', async () => {
    const requests: Request[] = []
    const body = '{"value":18446744073709551615,"broken":"prefix'
    const record = {
        sequence: '9223372036854775807',
        revision: '9007199254740993',
        sourceMessageId: '69f5eea2-6dfe-489e-a3b3-a43170c7f5aa',
        capturedAt: '2026-10-15T00:00:00Z',
        originalBytes: 70000,
        storedBytes: 65536,
        truncated: true,
        privacyBlocked: false,
        details: null,
    }
    const object = { kind: 'output.http', key: 'route/node', unresolved: false }
    const api = client(async (input) => {
        requests.push(input as Request)
        return new Response(
            JSON.stringify(
                requests.length === 1
                    ? {
                          revision: record.revision,
                          items: [object],
                          nextCursor: null,
                      }
                    : requests.length === 2
                      ? { revision: record.revision, object, items: [record] }
                      : { record, body },
            ),
        )
    })
    expect((await api.objects()).items).toEqual([object])
    expect((await api.records(object, record.revision)).items[0].sequence).toBe(
        record.sequence,
    )
    expect(requests.some((request) => request.url.includes('/body'))).toBe(
        false,
    )
    expect((await api.body(object, '9223372036854775806', record)).body).toBe(
        body,
    )
    const url = new URL(requests[2].url)
    expect(url.pathname).toBe(
        '/api/edge-admin/v1/runtime/messages/9223372036854775807/body',
    )
    expect(Object.fromEntries(url.searchParams)).toEqual({
        kind: object.kind,
        key: object.key,
        revision: '9223372036854775806',
        recordRevision: record.revision,
        sourceMessageId: record.sourceMessageId,
    })
    expect(
        requests.every(
            (request) =>
                request.method === 'GET' &&
                !request.headers.has('X-CSRF-Token'),
        ),
    ).toBe(true)
    expect(new URL(requests[0].url).searchParams.get('limit')).toBe('100')
    await expect(api.records(object, 'invalid')).rejects.toThrow()
    expect(requests).toHaveLength(3)
})
it('passes cancellation through and never retries failed reads', async () => {
    const controller = new AbortController()
    const fetcher = vi.fn(async (input) => {
        controller.abort()
        expect((input as Request).signal.aborted).toBe(true)
        throw new DOMException('aborted', 'AbortError')
    })
    await expect(
        client(fetcher).objects(undefined, undefined, controller.signal),
    ).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
    const failure = vi.fn(
        async () => new Response('{"status":500}', { status: 500 }),
    )
    await expect(client(failure).objects()).rejects.toThrow()
    expect(failure).toHaveBeenCalledTimes(1)
})
