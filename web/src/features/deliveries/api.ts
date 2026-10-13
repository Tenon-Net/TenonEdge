import type { createGatewayClient } from '../../api/client'
import { ApiError } from '../../api/errors'
import type { components } from '../../api/schema'
import { revisionId } from '../configuration/api'
import { decodeDiagnosticsJson } from '../diagnostics/api'
import { eventTime } from '../diagnostics/model'

export type DeliveryArea = 'failed' | 'quarantined'
export type DeliveryFilter = {
    state: DeliveryArea
    connectionId?: string
    deviceKey?: string
    reason?: string
    from?: string
    to?: string
}
export type DeliveryItem = components['schemas']['DeliveryInspectionItem']
export type DeliveryPage = components['schemas']['DeliveryInspectionPage']
export type BodyPreview = components['schemas']['DeliveryBodyPreview']
export type RetryItem = components['schemas']['DeliveryRetryItem']
export function retryProblem(error: unknown) {
    const problem = error instanceof ApiError ? error.problem : null
    if (!problem || typeof problem !== 'object' || !('code' in problem))
        return 'unknown'
    switch (problem.code) {
        case 'delivery.retrySelectionChanged':
            return 'selection'
        case 'delivery.retryTargetChanged':
            return 'target'
        case 'delivery.retryQuotaExceeded':
            return 'quota'
        case 'delivery.retryStorageProtection':
            return 'storage'
        case 'delivery.retrySequenceExhausted':
            return 'sequence'
        case 'delivery.retryUnavailable':
            return 'unavailable'
        default:
            return 'unknown'
    }
}
export function retrySelection(items: readonly RetryItem[]): RetryItem[] {
    if (items.length < 1 || items.length > 1000)
        throw new Error('delivery-retry-size')
    const selected = items.map((item) => {
        if (
            !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
                item.deliveryId,
            ) ||
            item.deliveryId === '00000000-0000-0000-0000-000000000000'
        )
            throw new Error('delivery-retry-id')
        return {
            sequence: revisionId(item.sequence),
            deliveryId: item.deliveryId.toLowerCase(),
        }
    })
    if (
        new Set(selected.map((item) => item.sequence)).size !==
            selected.length ||
        new Set(selected.map((item) => item.deliveryId)).size !==
            selected.length
    )
        throw new Error('delivery-retry-duplicate')
    return selected
}
export function connectionFilter(value: string): string | undefined {
    if (!value) return undefined
    const id = revisionId(value)
    if (BigInt(id) > 9007199254740991n) throw new Error('delivery-target')
    return id
}
export function readFilter(draft: {
    state: DeliveryArea
    connectionId: string
    deviceKey: string
    reason: string
    from: string
    to: string
}): DeliveryFilter {
    const from = eventTime(draft.from),
        to = eventTime(draft.to)
    if (from && to && from >= to) throw new Error('delivery-time')
    if (
        !['failed', 'quarantined'].includes(draft.state) ||
        draft.deviceKey.length > 128 ||
        draft.reason.length > 1024
    )
        throw new Error('delivery-filter')
    return {
        state: draft.state,
        connectionId: connectionFilter(draft.connectionId.trim()),
        deviceKey: draft.deviceKey.trim() || undefined,
        reason: draft.reason.trim() || undefined,
        from,
        to,
    }
}

/** Freeze at most one explicit batch. Pages are not a snapshot; the writer rechecks every generation atomically. */
export async function readFailedBatch(
    api: Pick<ReturnType<typeof createDeliveryApi>, 'records'>,
    filter: DeliveryFilter,
    signal?: AbortSignal,
) {
    if (filter.state !== 'failed') throw new Error('delivery-retry-area')
    const items: DeliveryItem[] = []
    let after: string | undefined
    for (let pageNumber = 0; pageNumber < 10; pageNumber++) {
        const page = await api.records(filter, after, signal)
        if (page.items.length > 100) throw new Error('delivery-page')
        for (const item of page.items) {
            const sequence = revisionId(item.sequence)
            if (
                (after && BigInt(sequence) <= BigInt(after)) ||
                item.state !== 'failed'
            )
                throw new Error('delivery-retry-page')
            after = sequence
            items.push(item)
        }
        if (page.nextSequence === null) {
            retrySelection(items)
            return { items, hasMore: false }
        }
        if (
            page.items.length !== 100 ||
            revisionId(page.nextSequence) !== after
        )
            throw new Error('delivery-retry-cursor')
    }
    retrySelection(items)
    return { items, hasMore: true }
}
export function createDeliveryApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async retry(revision: string, items: readonly RetryItem[]) {
            const selected = retrySelection(items)
            const { data } = await api.POST(
                '/api/edge-admin/v1/deliveries/retry',
                {
                    body: { revision: revisionId(revision), items: selected },
                },
            )
            if (!data || Number(data.requeuedRecords) !== selected.length)
                throw new Error('delivery-retry-outcome-unknown')
            return data
        },
        async records(
            filter: DeliveryFilter,
            after?: string,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET('/api/edge-admin/v1/deliveries', {
                params: {
                    query: {
                        ...filter,
                        connectionId: connectionFilter(
                            filter.connectionId ?? '',
                        ) as unknown as number | undefined,
                        after:
                            after === undefined
                                ? undefined
                                : (revisionId(after) as unknown as number),
                        limit: 100,
                    },
                },
                parseAs: 'text',
                signal,
            })
            const page = decodeDiagnosticsJson<DeliveryPage>(data)
            if (!Array.isArray(page.items) || page.items.length > 100)
                throw new Error('delivery-page')
            if (page.nextSequence !== null) revisionId(page.nextSequence)
            return page
        },
        async body(
            deliveryId: string,
            state: DeliveryArea,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/deliveries/{deliveryId}/body',
                {
                    params: { path: { deliveryId }, query: { state } },
                    parseAs: 'text',
                    signal,
                },
            )
            return decodeDiagnosticsJson<BodyPreview>(data)
        },
    }
}
