import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import {
    createDeliveryExportApi,
    exportDownloadHref,
    type ExportStatus,
} from './export-api'

const id = '10000000-0000-4000-8000-000000000001'
function status(
    state: ExportStatus['state'] = 'awaitingConfirmation',
): ExportStatus {
    return {
        id,
        state,
        busy: true,
        expiresAt: '2026-10-13T00:15:00Z',
        fileBytes: state === 'ready' ? '73473000' : null,
        error: null,
        review: {
            filter: {
                state: 'quarantined',
                connectionId: 9007199254740991,
                reason: '%_bad',
            },
            count: '1025',
            payloadBytes: '73472000',
            maximumFileBytes: '440965000',
            firstCreatedAt: '2026-10-13T00:00:00Z',
            lastCreatedAt: '2026-10-13T00:01:00Z',
        },
    }
}
function client(fetcher: typeof fetch) {
    return createDeliveryExportApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'fixture-csrf',
            epoch: () => 1,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('prepares the entire applied filter without paginating, inspecting bodies or imposing a thousand-record limit', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        expect(request.headers.get('X-CSRF-Token')).toBe('fixture-csrf')
        expect(await request.json()).toEqual({
            state: 'quarantined',
            connectionId: 9007199254740991,
            reason: '%_bad',
        })
        return new Response(JSON.stringify(status()), { status: 202 })
    })
    const result = await api.prepare({
        state: 'quarantined',
        connectionId: '9007199254740991',
        reason: '%_bad',
    })
    expect(result.review?.count).toBe('1025')
    expect(result.review?.payloadBytes).toBe('73472000')
    expect(String(result.review?.filter.connectionId)).toBe('9007199254740991')
    expect(requests).toHaveLength(1)
    expect(new URL(requests[0].url).pathname).toBe(
        '/api/edge-admin/v1/deliveries/export',
    )
})
it('confirmation and cancellation are explicit writes; read-only recovery and native href never fetch the download body', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        if (request.method === 'GET')
            return new Response(JSON.stringify({ export: status('ready') }))
        if (request.url.endsWith('/confirm'))
            expect(await request.json()).toEqual({ confirmed: true })
        return new Response(
            JSON.stringify(
                status(
                    request.url.endsWith('/cancel')
                        ? 'cancelled'
                        : 'generating',
                ),
            ),
        )
    })
    await api.confirm(id)
    const read = await api.current()
    expect(read.export?.state).toBe('ready')
    expect(exportDownloadHref(id)).toBe(
        `/api/edge-admin/v1/deliveries/export/${id}/download`,
    )
    expect(requests).toHaveLength(2)
    expect(requests[1].headers.has('X-CSRF-Token')).toBe(false)
    await api.cancel(id)
    expect(requests.map((r) => r.method)).toEqual(['POST', 'GET', 'POST'])
    expect(requests.some((r) => r.url.endsWith('/download'))).toBe(false)
})
it.each([400, 403, 409, 503])(
    'never retries an HTTP %i or turns it into an empty export',
    async (code) => {
        const fetcher = vi.fn(
            async () =>
                new Response(JSON.stringify({ code: 'fixture' }), {
                    status: code,
                }),
        )
        const api = client(fetcher)
        await expect(api.confirm(id)).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('lost confirmation replies are not replayed; cancellation reaches status reads', async () => {
    const fetcher = vi.fn(async () => {
        throw new TypeError('lost reply')
    })
    await expect(client(fetcher).confirm(id)).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
    const controller = new AbortController()
    await expect(
        client(async (input) => {
            controller.abort()
            expect((input as Request).signal.aborted).toBe(true)
            throw new DOMException('aborted', 'AbortError')
        }).current(controller.signal),
    ).rejects.toThrow()
})
it('rejects unsafe paths, invalid counts and malformed state instead of enabling native download', async () => {
    for (const value of [
        '../../auth/logout',
        'https://attacker.invalid',
        '00000000-0000-0000-0000-000000000000',
    ])
        expect(() => exportDownloadHref(value)).toThrow()
    for (const value of [
        { ...status(), state: 'unknown' },
        { ...status('ready'), review: null },
        { ...status(), review: { ...status().review, count: '1.5' } },
        { ...status(), fileBytes: '9223372036854775808' },
    ]) {
        await expect(
            client(
                async () => new Response(JSON.stringify({ export: value })),
            ).current(),
        ).rejects.toThrow()
    }
    const fetcher = vi.fn(async () => new Response(JSON.stringify(status())))
    await expect(
        client(fetcher).prepare({
            state: 'failed',
            connectionId: '9007199254740992',
        }),
    ).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
})
it('no current resource is explicit null, not a query error', async () => {
    expect(
        await client(async () => new Response('{"export":null}')).current(),
    ).toEqual({ export: null })
    await expect(
        client(async () => new Response('{}')).current(),
    ).rejects.toThrow()
})
