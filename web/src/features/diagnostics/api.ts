import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { readRouteJson, RouteNumber, type RouteJson } from '../canvas/json'
import { revisionId } from '../configuration/api'

type Schema = components['schemas']
export type Diagnostics = Schema['RuntimeDiagnosticsResponse']
export type TrendPoint = Schema['RuntimeTrendPoint']
export type EventPage = Schema['RuntimeEventPage']
export type SystemInfo = Schema['SystemInfoResponse']
export type EventFilter = {
    from?: string
    to?: string
    level?: '' | 'info' | 'warning' | 'error'
    type?: string
    target?: string
}

function metadata(value: RouteJson): unknown {
    if (value instanceof RouteNumber) return value.text
    if (Array.isArray(value)) return value.map(metadata)
    if (value !== null && typeof value === 'object')
        return Object.fromEntries(
            Object.entries(value).map(([key, item]) => [key, metadata(item)]),
        )
    return value
}
// Generated OpenAPI number|string fields retain the server's exact decimal text.
// These endpoints contain metadata only; no message bodies or credentials are fetched.
export function decodeDiagnosticsJson<T>(body: string | undefined): T {
    if (!body) throw new Error('diagnostics-response')
    return metadata(readRouteJson(body, 16 * 1024 * 1024, 32)) as T
}
export function createDiagnosticsApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async status(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/status',
                { parseAs: 'text', signal },
            )
            return decodeDiagnosticsJson<Diagnostics>(data)
        },
        async trends(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/trends',
                { parseAs: 'text', signal },
            )
            const points = decodeDiagnosticsJson<TrendPoint[]>(data)
            if (!Array.isArray(points) || points.length > 360)
                throw new Error('diagnostics-trends')
            return points
        },
        async info(signal?: AbortSignal) {
            const { data } = await api.GET('/api/edge-admin/v1/system/info', {
                parseAs: 'text',
                signal,
            })
            return decodeDiagnosticsJson<SystemInfo>(data)
        },
        async events(
            filter: EventFilter,
            before?: string,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/events',
                {
                    params: {
                        query: {
                            ...filter,
                            level: filter.level || undefined,
                            limit: 50,
                            // openapi-fetch serializes path/query numbers as text; do not round an Int64 cursor.
                            before:
                                before === undefined
                                    ? undefined
                                    : (revisionId(before) as unknown as number),
                        },
                    },
                    parseAs: 'text',
                    signal,
                },
            )
            const page = decodeDiagnosticsJson<EventPage>(data)
            if (!Array.isArray(page.items) || page.items.length > 50)
                throw new Error('diagnostics-events')
            if (page.nextCursor !== null) revisionId(page.nextCursor)
            return page
        },
    }
}
