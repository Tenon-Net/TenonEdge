import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { ApiError } from '../../api/errors'
import {
    connectionFilter,
    createDeliveryApi,
    readFilter,
    readDeliveryBatch,
    retrySelection,
    retryProblem,
    cleanupSelection,
    cleanupProblem,
    type DeliveryItem,
    type DeliveryFilter,
} from './api'

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

function item(sequence: number): DeliveryItem {
    return {
        sequence: String(sequence),
        deliveryId: `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`,
        connectionId: '11',
        deviceKey: 'meter',
        routeId: null,
        nodeLabel: null,
        revision: '1',
        createdAt: '2026-10-13T00:00:00Z',
        failedAt: '2026-10-13T00:01:00Z',
        failureCount: '0',
        state: 'failed',
        quarantineReason: null,
        lastError: 'HTTP 422',
        byteSize: '100',
        clockAnomaly: false,
        migrationId: null,
    }
}
it('freezes a single bounded 1000-record batch from applied filters without body reads or hidden writes', async () => {
    const filter = {
        state: 'failed' as const,
        connectionId: '11',
        reason: '422',
    }
    const records = vi.fn(async (received: DeliveryFilter, after?: string) => {
        expect(received).toEqual(filter)
        const start = Number(after ?? 0)
        return {
            capturedAt: '',
            items: Array.from({ length: 100 }, (_, i) => item(start + i + 1)),
            nextSequence: String(start + 100),
        }
    })
    const batch = await readDeliveryBatch({ records }, filter)
    expect(batch.items).toHaveLength(1000)
    expect(batch.items[999].sequence).toBe('1000')
    expect(batch.hasMore).toBe(true)
    expect(records).toHaveBeenCalledTimes(10)
})
it('stops at the exact final page, retains generations and refuses an empty/wrong-area/malformed selection', async () => {
    const records = vi.fn(async () => ({
        capturedAt: '',
        items: [item(7)],
        nextSequence: null,
    }))
    expect(await readDeliveryBatch({ records }, { state: 'failed' })).toEqual({
        items: [item(7)],
        hasMore: false,
    })
    expect(records).toHaveBeenCalledTimes(1)
    await expect(
        readDeliveryBatch({ records }, { state: 'quarantined' }),
    ).rejects.toThrow()
    records.mockResolvedValueOnce({
        capturedAt: '',
        items: [],
        nextSequence: null,
    })
    await expect(
        readDeliveryBatch({ records }, { state: 'failed' }),
    ).rejects.toThrow()
    records.mockResolvedValueOnce({
        capturedAt: '',
        items: [{ ...item(7), state: 'pending' }],
        nextSequence: null,
    })
    await expect(
        readDeliveryBatch({ records }, { state: 'failed' }),
    ).rejects.toThrow()
})
it('does not write or silently continue when a batch read fails or has a nonadvancing cursor', async () => {
    const records = vi.fn(async (_filter: DeliveryFilter, after?: string) => ({
        capturedAt: '',
        items: Array.from({ length: 100 }, (_, i) => item(i + 1)),
        nextSequence: after ?? '100',
    }))
    await expect(
        readDeliveryBatch({ records }, { state: 'failed' }),
    ).rejects.toThrow()
    expect(records).toHaveBeenCalledTimes(2)
    const failed = vi.fn(async () => {
        throw new Error('offline')
    })
    await expect(
        readDeliveryBatch({ records: failed }, { state: 'failed' }),
    ).rejects.toThrow('offline')
    expect(failed).toHaveBeenCalledTimes(1)
})
it('copies only stable IDs and exact generations, rejects empty/overlimit/duplicate choices before sending', async () => {
    const fetcher = vi.fn(async () => new Response('{"requeuedRecords":1000}'))
    const api = client(fetcher)
    const items = Array.from({ length: 1000 }, (_, i) => item(i + 1))
    expect(retrySelection(items)).toHaveLength(1000)
    expect(retrySelection([item(1)])[0]).toEqual({
        sequence: '1',
        deliveryId: item(1).deliveryId,
    })
    for (const invalid of [
        [],
        [...items, item(1001)],
        [item(1), item(1)],
        [item(1), { ...item(2), sequence: '1' }],
        [{ ...item(1), sequence: '9223372036854775808' }],
        [{ ...item(1), deliveryId: 'not-uuid' }],
    ])
        await expect(api.retry('1', invalid)).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    await expect(api.retry('1', items)).resolves.toEqual({
        requeuedRecords: 1000,
    })
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it('sends exact Int64 IDs with CSRF and no client fingerprint, treats 200 as queue submission only', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        expect(request.method).toBe('POST')
        expect(new URL(request.url).pathname).toBe(
            '/api/edge-admin/v1/deliveries/retry',
        )
        expect(request.headers.get('X-CSRF-Token')).toBe('not-used')
        expect(await request.json()).toEqual({
            revision: '9223372036854775806',
            items: [
                {
                    sequence: '9223372036854775805',
                    deliveryId: item(1).deliveryId,
                },
            ],
        })
        return new Response('{"requeuedRecords":1}')
    })
    await expect(
        client(fetcher).retry('9223372036854775806', [
            { ...item(1), sequence: '9223372036854775805' },
        ]),
    ).resolves.toEqual({ requeuedRecords: 1 })
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it.each([403, 409, 503, 'lost-response'] as const)(
    'never replays retry after %s',
    async (status) => {
        const fetcher = vi.fn(async () => {
            if (status === 'lost-response') throw new TypeError('network')
            return new Response('{}', { status })
        })
        await expect(client(fetcher).retry('1', [item(1)])).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('does not report success when the response count is missing or inconsistent', async () => {
    for (const response of [
        '{}',
        '{"requeuedRecords":0}',
        '{"requeuedRecords":2}',
    ]) {
        const fetcher = vi.fn(async () => new Response(response))
        await expect(client(fetcher).retry('1', [item(1)])).rejects.toThrow(
            'delivery-retry-outcome-unknown',
        )
        expect(fetcher).toHaveBeenCalledTimes(1)
    }
})

it('shows only known retry problem codes, never arbitrary server text', () => {
    expect(
        retryProblem(
            new ApiError(409, { code: 'delivery.retrySelectionChanged' }),
        ),
    ).toBe('selection')
    expect(
        retryProblem(
            new ApiError(409, { code: 'delivery.retryTargetChanged' }),
        ),
    ).toBe('target')
    expect(
        retryProblem(
            new ApiError(409, { code: 'delivery.retryQuotaExceeded' }),
        ),
    ).toBe('quota')
    expect(
        retryProblem(
            new ApiError(409, { code: 'delivery.retryStorageProtection' }),
        ),
    ).toBe('storage')
    expect(
        retryProblem(
            new ApiError(409, { code: 'delivery.retrySequenceExhausted' }),
        ),
    ).toBe('sequence')
    expect(
        retryProblem(new ApiError(409, { code: 'delivery.retryUnavailable' })),
    ).toBe('unavailable')
    expect(
        retryProblem(
            new ApiError(503, {
                code: 'raw-private-error',
                title: 'sensitive',
            }),
        ),
    ).toBe('unknown')
    expect(retryProblem(new Error('network'))).toBe('unknown')
})

it('cleanup binds state, connection and exact migration/sequence identities and explicit confirmation with CSRF', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        expect(request.method).toBe('POST')
        expect(new URL(request.url).pathname).toBe(
            '/api/edge-admin/v1/deliveries/cleanup',
        )
        expect(request.headers.get('X-CSRF-Token')).toBe('not-used')
        expect(await request.json()).toEqual({
            state: 'quarantined',
            confirmed: true,
            items: [
                {
                    sequence: '9223372036854775806',
                    deliveryId: item(1).deliveryId,
                    connectionId: '9007199254740991',
                    migrationId: '9223372036854775807',
                },
            ],
        })
        return new Response('{"clearedRecords":1}')
    })
    await expect(
        client(fetcher).cleanup('quarantined', [
            {
                ...item(1),
                state: 'quarantined',
                sequence: '9223372036854775806',
                connectionId: '9007199254740991',
                migrationId: '9223372036854775807',
            },
        ]),
    ).resolves.toEqual({ clearedRecords: 1 })
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it('cleanup rejects unknown/invalid generations, pending records and oversized selections before any request', async () => {
    const missing = item(1)
    Reflect.deleteProperty(missing, 'migrationId')
    const fetcher = vi.fn(async () => new Response('{"clearedRecords":1}'))
    const api = client(fetcher)
    for (const items of [
        [],
        [item(1), item(1)],
        [missing],
        [{ ...item(1), migrationId: '0' }],
        [{ ...item(1), connectionId: '9007199254740992' }],
        [{ ...item(1), state: 'pending' as const }],
        Array.from({ length: 1001 }, (_, i) => item(i + 1)),
    ])
        await expect(api.cleanup('failed', items)).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    expect(cleanupSelection('failed', [item(1)])[0]).toEqual({
        sequence: '1',
        deliveryId: item(1).deliveryId,
        connectionId: '11',
        migrationId: null,
    })
    expect(
        cleanupSelection(
            'failed',
            Array.from({ length: 1000 }, (_, i) => item(i + 1)),
        ),
    ).toHaveLength(1000)
})
it('quarantine batch selection uses the same bounded metadata-only pagination', async () => {
    const records = vi.fn(async (_filter: DeliveryFilter, after?: string) => ({
        capturedAt: '',
        items: Array.from({ length: 100 }, (_, i) => ({
            ...item(Number(after ?? 0) + i + 1),
            state: 'quarantined' as const,
        })),
        nextSequence: String(Number(after ?? 0) + 100),
    }))
    const batch = await readDeliveryBatch(
        { records },
        { state: 'quarantined', reason: 'targetMissing' },
    )
    expect(batch.items).toHaveLength(1000)
    expect(batch.hasMore).toBe(true)
    expect(records).toHaveBeenCalledTimes(10)
})
it.each([403, 409, 503, 'lost-response'] as const)(
    'cleanup does not replay after %s',
    async (status) => {
        const fetcher = vi.fn(async () => {
            if (status === 'lost-response') throw new Error('disconnected')
            return new Response('{}', { status })
        })
        await expect(
            client(fetcher).cleanup('failed', [item(1)]),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('cleanup never invents success and only exposes known problem codes', async () => {
    await expect(
        client(async () => new Response('{"clearedRecords":0}')).cleanup(
            'failed',
            [item(1)],
        ),
    ).rejects.toThrow('delivery-cleanup-outcome-unknown')
    expect(
        cleanupProblem(
            new ApiError(409, { code: 'delivery.cleanupSelectionChanged' }),
        ),
    ).toBe('selection')
    expect(
        cleanupProblem(
            new ApiError(409, { code: 'delivery.cleanupUnavailable' }),
        ),
    ).toBe('unavailable')
    expect(
        cleanupProblem(new ApiError(503, { code: 'private-storage-error' })),
    ).toBe('unknown')
})
