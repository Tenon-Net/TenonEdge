import type { createGatewayClient } from '../../api/client'
import { ApiError } from '../../api/errors'
import type { components } from '../../api/schema'
import { decodeDiagnosticsJson } from '../diagnostics/api'
import { connectionFilter, readFilter, type DeliveryFilter } from './api'

export type ExportStatus = components['schemas']['DeliveryExportStatus']
type CurrentExport = components['schemas']['DeliveryExportCurrent']
const root = '/api/edge-admin/v1/deliveries/export' as const
const states = [
    'preparing',
    'awaitingConfirmation',
    'generating',
    'ready',
    'downloading',
    'responseWritten',
    'failed',
    'cancelled',
    'expired',
]
function exportId(id: string) {
    if (
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
            id,
        ) ||
        id === '00000000-0000-0000-0000-000000000000'
    )
        throw new Error('export-id')
    return id
}
function count(value: string) {
    if (
        typeof value !== 'string' ||
        !/^(0|[1-9]\d*)$/.test(value) ||
        BigInt(value) > 9223372036854775807n
    )
        throw new Error('export-count')
}
function validate(value: ExportStatus): ExportStatus {
    exportId(value.id)
    if (
        !states.includes(value.state) ||
        typeof value.busy !== 'boolean' ||
        !Number.isFinite(Date.parse(value.expiresAt))
    )
        throw new Error('export-status')
    if (value.review) {
        for (const n of [
            value.review.count,
            value.review.payloadBytes,
            value.review.maximumFileBytes,
        ])
            count(n)
        if (!['failed', 'quarantined'].includes(value.review.filter.state))
            throw new Error('export-area')
    }
    if (value.fileBytes !== null) count(value.fileBytes)
    if (
        ['awaitingConfirmation', 'generating', 'ready', 'downloading'].includes(
            value.state,
        ) &&
        !value.review
    )
        throw new Error('export-review')
    if (
        value.state === 'ready' &&
        (!value.fileBytes || value.review?.count === '0')
    )
        throw new Error('export-file')
    return value
}

/** Native same-origin download only. Never pass this URL through the management client's arrayBuffer/Blob path. */
export function exportDownloadHref(id: string) {
    return `${root}/${exportId(id)}/download`
}

export function exportProblem(error: unknown): string {
    if (
        error instanceof ApiError &&
        error.problem &&
        typeof error.problem === 'object' &&
        'code' in error.problem &&
        typeof error.problem.code === 'string'
    )
        return error.problem.code
    return 'unknown'
}

export function createDeliveryExportApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async current(signal?: AbortSignal) {
            const { data } = await api.GET(root, { signal, parseAs: 'text' })
            const current = decodeDiagnosticsJson<CurrentExport>(data)
            return {
                export:
                    current.export === null ? null : validate(current.export),
            }
        },
        async prepare(filter: DeliveryFilter) {
            const frozen = readFilter({
                state: filter.state,
                connectionId: filter.connectionId ?? '',
                deviceKey: filter.deviceKey ?? '',
                reason: filter.reason ?? '',
                from: filter.from ?? '',
                to: filter.to ?? '',
            })
            const connectionId = connectionFilter(frozen.connectionId ?? '')
            const { data } = await api.POST(root, {
                body: {
                    ...frozen,
                    connectionId: connectionId
                        ? Number(connectionId)
                        : undefined,
                },
                parseAs: 'text',
            })
            return validate(decodeDiagnosticsJson<ExportStatus>(data))
        },
        async confirm(id: string) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/deliveries/export/{id}/confirm',
                {
                    params: { path: { id: exportId(id) } },
                    body: { confirmed: true },
                    parseAs: 'text',
                },
            )
            return validate(decodeDiagnosticsJson<ExportStatus>(data))
        },
        async cancel(id: string) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/deliveries/export/{id}/cancel',
                {
                    params: { path: { id: exportId(id) } },
                    parseAs: 'text',
                },
            )
            return validate(decodeDiagnosticsJson<ExportStatus>(data))
        },
    }
}
