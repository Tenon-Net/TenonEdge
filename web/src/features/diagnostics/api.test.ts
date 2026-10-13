import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createDiagnosticsApi } from './api'

function client(fetcher: typeof fetch) {
    return createDiagnosticsApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'not-used',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('uses generated GET endpoints, bounded pages and exact cursors without requesting bodies or sending credentials', async () => {
    const requests: Request[] = []
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        requests.push(request)
        return new Response('{"items":[],"nextCursor":9223372036854775807}', {
            headers: { 'Content-Type': 'application/json' },
        })
    })
    const api = client(fetcher)
    const result = await api.events(
        { level: 'error', type: 'device.offline', target: 'meter' },
        '9223372036854775806',
    )
    expect(result.nextCursor).toBe('9223372036854775807')
    expect(requests[0].method).toBe('GET')
    const url = new URL(requests[0].url)
    expect(url.pathname).toBe('/api/edge-admin/v1/runtime/events')
    expect(Object.fromEntries(url.searchParams)).toEqual({
        level: 'error',
        type: 'device.offline',
        target: 'meter',
        limit: '50',
        before: '9223372036854775806',
    })
    expect(requests[0].headers.has('X-CSRF-Token')).toBe(false)
    await expect(api.events({}, 'invalid')).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it('does not retry failures and propagates query cancellation', async () => {
    const failure = vi.fn(
        async () => new Response('{"status":500}', { status: 500 }),
    )
    await expect(client(failure).status()).rejects.toThrow()
    expect(failure).toHaveBeenCalledTimes(1)
    const controller = new AbortController()
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        controller.abort()
        expect(request.signal.aborted).toBe(true)
        throw new DOMException('aborted', 'AbortError')
    })
    await expect(client(fetcher).trends(controller.signal)).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
})

it('current values preserve signed 64-bit text and use version-bound field pages', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        requests.push(input as Request)
        return new Response(
            '{"revision":9223372036854775807,"deviceKey":"meter","inputKey":"main","items":[{"key":"v","value":{"type":"integer","text":"9223372036854775807","quality":"good","isStale":false}}],"nextKey":"v","total":105}',
        )
    })
    const values = await api.values(
        'meter',
        'main',
        '9223372036854775807',
        'p099',
    )
    expect(values.items[0].value.text).toBe('9223372036854775807')
    expect(values.revision).toBe('9223372036854775807')
    const url = new URL(requests[0].url)
    expect(url.pathname).toBe(
        '/api/edge-admin/v1/runtime/devices/meter/inputs/main/values',
    )
    expect(Object.fromEntries(url.searchParams)).toEqual({
        revision: '9223372036854775807',
        after: 'p099',
        limit: '100',
    })
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.has('X-CSRF-Token')).toBe(false)
})
