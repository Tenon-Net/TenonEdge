import {
    isNodeKind,
    maxNodes,
    checkPosition,
    type GraphDocument,
    type NodeKind,
    type Position,
} from './model'
import {
    readRouteJson,
    writeRouteJson,
    RouteNumber,
    type RouteJson,
} from './json'

export function routeObject(value: RouteJson): Record<string, RouteJson> {
    if (
        !value ||
        typeof value !== 'object' ||
        Array.isArray(value) ||
        value instanceof RouteNumber
    )
        throw new Error('graph')
    return value
}
function keys(value: Record<string, RouteJson>, expected: string[]) {
    if (Object.keys(value).some((key) => !expected.includes(key)))
        throw new Error('graph')
}
function string(value: RouteJson): string {
    if (typeof value !== 'string') throw new Error('graph')
    return value
}
function id(value: RouteJson): string {
    const text = string(value)
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(text))
        throw new Error('graph')
    return text
}
function one(value: RouteJson) {
    if (!(value instanceof RouteNumber) || value.text !== '1')
        throw new Error('version')
}
export function defaultConfig(kind: NodeKind, version = 1): string {
    if (kind.startsWith('input.')) return '{"sourceIds":[]}'
    if (kind === 'transform.map' && version === 1) return '{"rules":[]}'
    if (kind.startsWith('transform.')) return '{}'
    return '{"connectionId":null}'
}
/** Reject ambiguous JSON, not incomplete graph semantics; the server compiles those. */
export function normalizeConfigText(text: string): string {
    return writeRouteJson(routeObject(readRouteJson(text)))
}
/** Input must be response text, never a response.json() value. */
export function decodeGraph(text: string): GraphDocument {
    const graph = routeObject(readRouteJson(text))
    keys(graph, ['schemaVersion', 'nodes', 'edges', 'layout'])
    one(graph.schemaVersion)
    if (
        !Array.isArray(graph.nodes) ||
        graph.nodes.length > maxNodes ||
        !Array.isArray(graph.edges) ||
        graph.edges.length > 2450
    )
        throw new Error('graph')
    const seen = new Set<string>()
    const nodes = graph.nodes.map((value) => {
        const node = routeObject(value)
        keys(node, ['id', 'type', 'version', 'name', 'config'])
        const version =
            node.version instanceof RouteNumber
                ? Number(node.version.text)
                : NaN
        if (!Number.isInteger(version) || version < 1 || version > 2147483647)
            throw new Error('version')
        const nodeId = id(node.id)
        if (seen.has(nodeId)) throw new Error('graph')
        seen.add(nodeId)
        const kind = node.type
        if (
            !isNodeKind(kind) ||
            (!kind.startsWith('transform.') && version !== 1)
        )
            throw new Error('graph')
        const name = string(node.name)
        if (
            !kind ||
            name.length > 64 ||
            // Match the backend prohibition on control characters in display names.
            // eslint-disable-next-line no-control-regex
            /[\u0000-\u001f\u007f-\u009f]/.test(name)
        )
            throw new Error('graph')
        return {
            id: nodeId,
            kind,
            version,
            name,
            configText: writeRouteJson(routeObject(node.config)),
        }
    })
    const edges = graph.edges.map((value, index) => {
        const edge = routeObject(value)
        keys(edge, ['source', 'target'])
        return {
            id: `edge-${index}`,
            source: id(edge.source),
            target: id(edge.target),
        }
    })
    const layout = routeObject(graph.layout)
    keys(layout, ['nodes'])
    const positions: Record<string, Position> = Object.fromEntries(
        Object.entries(routeObject(layout.nodes)).map(([key, value]) => {
            if (!seen.has(key)) throw new Error('graph')
            const point = routeObject(value)
            keys(point, ['x', 'y'])
            if (
                !(point.x instanceof RouteNumber) ||
                !(point.y instanceof RouteNumber)
            )
                throw new Error('graph')
            const position = {
                x: Number(point.x.text),
                y: Number(point.y.text),
            }
            checkPosition(position)
            return [key, position]
        }),
    )
    // Missing positions are legal saved drafts. Supply deterministic display layout.
    nodes.forEach((node, index) => {
        if (!Object.hasOwn(positions, node.id))
            positions[node.id] = {
                x: 80 + (index % 5) * 240,
                y: 80 + Math.floor(index / 5) * 140,
            }
    })
    return {
        schemaVersion: 1,
        definition: { nodes, edges },
        layout: { positions },
    }
}
export function graphRequest(doc: GraphDocument) {
    return {
        schemaVersion: doc.schemaVersion,
        nodes: doc.definition.nodes.map((node) => ({
            id: node.id,
            type: node.kind,
            version: node.version ?? 1,
            name: node.name,
            config: routeObject(readRouteJson(node.configText)),
        })),
        edges: doc.definition.edges.map((edge) => ({
            source: edge.source,
            target: edge.target,
        })),
        layout: { nodes: doc.layout.positions },
    }
}
export function encodeGraph(doc: GraphDocument): string {
    const text = writeRouteJson(graphRequest(doc))
    decodeGraph(text) // same structural guard in both directions; no topology rejection
    return text
}
