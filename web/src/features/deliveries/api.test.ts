import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { connectionFilter, createDeliveryApi, readFilter } from './api'

function client(fetcher: typeof fetch) {
    return createDeliveryApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'not-used',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('queries metadata using exact Int64 cursors and literal filters without requesting body', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        requests.push(input as Request)
        return new Response(
            '{"capturedAt":"2026-10-13T00:00:00Z","items":[{"sequence":9223372036854775806}],"nextSequence":9223372036854775806}',
        )
    })
    const result = await api.records(
        {
            state: 'failed',
            connectionId: '9007199254740991',
            deviceKey: 'meter',
            reason: '%_bad',
        },
        '9223372036854775805',
    )
    expect(result.items[0].sequence).toBe('9223372036854775806')
    expect(result.nextSequence).toBe('9223372036854775806')
    const url = new URL(requests[0].url)
    expect(url.pathname).toBe('/api/edge-admin/v1/deliveries')
    expect(Object.fromEntries(url.searchParams)).toEqual({
        state: 'failed',
        connectionId: '9007199254740991',
        deviceKey: 'meter',
        reason: '%_bad',
        after: '9223372036854775805',
        limit: '100',
    })
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.has('X-CSRF-Token')).toBe(false)
    await expect(
        api.records({ state: 'failed' }, '9223372036854775808'),
    ).rejects.toThrow()
    expect(requests).toHaveLength(1)
})
it('body query is explicit, stable-ID and state bound, and cancellation reaches fetch', async () => {
    const controller = new AbortController()
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        const url = new URL(request.url)
        expect(url.pathname).toBe(
            '/api/edge-admin/v1/deliveries/a32c9d72-c1f3-4546-99a7-0be3a7141dd2/body',
        )
        expect(url.searchParams.get('state')).toBe('quarantined')
        controller.abort()
        expect(request.signal.aborted).toBe(true)
        throw new DOMException('aborted', 'AbortError')
    })
    await expect(
        client(fetcher).body(
            'a32c9d72-c1f3-4546-99a7-0be3a7141dd2',
            'quarantined',
            controller.signal,
        ),
    ).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it.each([403, 404, 503])(
    'does not turn HTTP %i into an empty list or retry',
    async (status) => {
        const fetcher = vi.fn(
            async () => new Response(JSON.stringify({ status }), { status }),
        )
        await expect(
            client(fetcher).records({ state: 'failed' }),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('validates target IDs and local time filters without rounding identifiers', () => {
    expect(connectionFilter('')).toBeUndefined()
    expect(connectionFilter('9007199254740991')).toBe('9007199254740991')
    for (const value of ['0', '-1', '9007199254740992', '1.2', '1e3'])
        expect(() => connectionFilter(value)).toThrow()
    const draft = {
        state: 'failed' as const,
        connectionId: ' 42 ',
        deviceKey: ' meter ',
        reason: '%_',
        from: '',
        to: '',
    }
    expect(readFilter(draft)).toEqual({
        state: 'failed',
        connectionId: '42',
        deviceKey: 'meter',
        reason: '%_',
        from: undefined,
        to: undefined,
    })
    expect(() => readFilter({ ...draft, from: 'not-date' })).toThrow()
    expect(() =>
        readFilter({
            ...draft,
            from: '2026-10-13T00:00',
            to: '2026-10-13T00:00',
        }),
    ).toThrow()
    expect(() => readFilter({ ...draft, deviceKey: 'x'.repeat(129) })).toThrow()
    expect(() => readFilter({ ...draft, reason: 'x'.repeat(1025) })).toThrow()
})
