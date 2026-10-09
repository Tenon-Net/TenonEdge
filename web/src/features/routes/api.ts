import type { createGatewayClient } from '../../api/client'
import {
    decodeGraph,
    encodeGraph,
    graphRequest,
    routeObject,
} from '../canvas/codec'
import {
    maxGraphBytes,
    readRouteJson,
    RouteNumber,
    writeRouteJson,
    type RouteJson,
} from '../canvas/json'
import { emptyGraph, type GraphDocument } from '../canvas/model'

export type RouteForm = {
    id?: number
    version: string
    key: string
    name: string
    description: string
    enabled: boolean
    document: GraphDocument
}
export function newRoute(version: string): RouteForm {
    return {
        version,
        key: '',
        name: '',
        description: '',
        enabled: false,
        document: emptyGraph(),
    }
}
function text(value: RouteJson): string {
    if (typeof value !== 'string') throw new Error('route-response')
    return value
}
function routeId(value: RouteJson): number {
    const raw = value instanceof RouteNumber ? value.text : text(value)
    if (!/^[1-9]\d*$/.test(raw)) throw new Error('route-response')
    return checkedId(Number(raw))
}
function checkedId(id: number): number {
    if (!Number.isSafeInteger(id) || id <= 0) throw new Error('route-id')
    return id
}
export function decodeRouteResponse(body: string): RouteForm {
    // Response adds metadata/envelope depth. The graph itself retains its own 1 MiB/16-level limit.
    const result = routeObject(readRouteJson(body, maxGraphBytes + 65536, 20))
    const route = routeObject(result.route)
    if (typeof route.enabled !== 'boolean') throw new Error('route-response')
    return {
        id: routeId(route.id),
        version: text(result.version),
        key: text(route.key),
        name: text(route.name),
        description: route.description === null ? '' : text(route.description),
        enabled: route.enabled,
        document: decodeGraph(writeRouteJson(route.graph)),
    }
}
/** Same OpenAPI client/middleware as other management features; never response.json for graph bodies. */
export function createRouteApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async list(signal?: AbortSignal) {
            const { data } = await api.GET('/api/edge-admin/v1/routes', {
                signal,
            })
            if (!data) throw new Error('route-response')
            return data
        },
        async read(id: number, signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/routes/{routeId}',
                {
                    params: { path: { routeId: checkedId(id) } },
                    parseAs: 'text',
                    signal,
                },
            )
            if (!data) throw new Error('route-response')
            return decodeRouteResponse(data)
        },
        async save(form: RouteForm) {
            encodeGraph(form.document)
            const body = {
                expectedVersion: form.version,
                key: form.key,
                name: form.name,
                description: form.description,
                enabled: form.enabled,
                graph: graphRequest(form.document),
            }
            // The complete write request (not just its graph) has a 1 MiB server limit.
            const serialized = writeRouteJson(body)
            const options = {
                body,
                bodySerializer: () => serialized,
                parseAs: 'text' as const,
            }
            const { data } =
                form.id === undefined
                    ? await api.POST('/api/edge-admin/v1/routes', options)
                    : await api.PUT('/api/edge-admin/v1/routes/{routeId}', {
                          ...options,
                          params: { path: { routeId: checkedId(form.id) } },
                      })
            if (!data) throw new Error('route-response')
            return decodeRouteResponse(data)
        },
        async copy(id: number, version: string, key: string, name: string) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/routes/{routeId}/copy',
                {
                    params: { path: { routeId: checkedId(id) } },
                    body: { expectedVersion: version, key, name },
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('route-response')
            return decodeRouteResponse(data)
        },
        async remove(id: number, version: string) {
            const { data } = await api.DELETE(
                '/api/edge-admin/v1/routes/{routeId}',
                {
                    params: {
                        path: { routeId: checkedId(id) },
                        query: { expectedVersion: version },
                    },
                },
            )
            if (!data) throw new Error('route-response')
            return data
        },
        async compile(version: string) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/routes/compile',
                { body: { expectedVersion: version } },
            )
            if (!data) throw new Error('route-response')
            return data
        },
    }
}
