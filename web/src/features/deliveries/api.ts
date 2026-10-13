import type { createGatewayClient } from '../../api/client'
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
export function createDeliveryApi(api: ReturnType<typeof createGatewayClient>) {
    return {
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
