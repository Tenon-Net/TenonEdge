/** Product-owned editor state. Use codec.ts for the persisted API format. */
export const nodeKinds = [
    'input.modbus',
    'input.mqtt',
    'input.http',
    'input.simulator',
    'transform.map',
    'output.mqtt',
    'output.http',
    'output.tenon',
] as const
export type NodeKind =
    | (typeof nodeKinds)[number]
    | `transform.${string}`
    | `input.${string}`
    | `output.${string}`
export function isOutputModuleKind(
    value: unknown,
): value is `output.${string}` {
    return (
        typeof value === 'string' &&
        value.length <= 64 &&
        /^output\.[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)*$/.test(value) &&
        !nodeKinds.some((kind) => kind === value)
    )
}
export function isInputModuleKind(value: unknown): value is `input.${string}` {
    return (
        typeof value === 'string' &&
        value.length <= 64 &&
        /^input\.[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)*$/.test(value) &&
        !nodeKinds.some((kind) => kind === value)
    )
}
export function isNodeKind(value: unknown): value is NodeKind {
    return (
        typeof value === 'string' &&
        (nodeKinds.some((kind) => kind === value) ||
            isInputModuleKind(value) ||
            isOutputModuleKind(value) ||
            (/^transform\.[A-Za-z0-9._-]+$/.test(value) && value.length <= 64))
    )
}
export type Position = { x: number; y: number }
export type GraphNode = {
    id: string
    kind: NodeKind
    version?: number // Legacy editor documents used version 1 implicitly.
    name: string
    configText: string
}
export type GraphEdge = { id: string; source: string; target: string }
export type GraphDocument = {
    schemaVersion: 1
    definition: { nodes: GraphNode[]; edges: GraphEdge[] }
    layout: { positions: Record<string, Position> }
}
export const maxNodes = 50
export const historyLimit = 50
export const emptyGraph = (): GraphDocument => ({
    schemaVersion: 1,
    definition: { nodes: [], edges: [] },
    layout: { positions: {} },
})
export type GraphErrorCode =
    'limit' | 'missing' | 'direction' | 'cycle' | 'duplicate' | 'position'
export class GraphEditError extends Error {
    constructor(public readonly code: GraphErrorCode) {
        super(code)
    }
}
export function connectionError(
    doc: GraphDocument,
    source: string,
    target: string,
): GraphErrorCode | undefined {
    const from = doc.definition.nodes.find((n) => n.id === source)
    const to = doc.definition.nodes.find((n) => n.id === target)
    if (!from || !to) return 'missing'
    if (source === target) return 'cycle'
    if (from.kind.startsWith('output.') || to.kind.startsWith('input.'))
        return 'direction'
    if (
        doc.definition.edges.some(
            (e) => e.source === source && e.target === target,
        )
    )
        return 'duplicate'
    const pending = [target]
    const seen = new Set<string>()
    while (pending.length) {
        const id = pending.pop()!
        if (id === source) return 'cycle'
        if (seen.has(id)) continue
        seen.add(id)
        for (const edge of doc.definition.edges)
            if (edge.source === id) pending.push(edge.target)
    }
}
/** Topology hints only: not an executable-plan compiler/reference validator. */
export function topologyHints(doc: GraphDocument) {
    const incoming = new Set(doc.definition.edges.map((e) => e.target))
    const outgoing = new Set(doc.definition.edges.map((e) => e.source))
    return doc.definition.nodes
        .filter(
            (n) =>
                (!n.kind.startsWith('input.') && !incoming.has(n.id)) ||
                (!n.kind.startsWith('output.') && !outgoing.has(n.id)),
        )
        .map((n) => n.id)
}
export function checkPosition(position: Position) {
    if (
        !Number.isFinite(position.x) ||
        !Number.isFinite(position.y) ||
        Math.abs(position.x) > 10000000 ||
        Math.abs(position.y) > 10000000
    )
        throw new GraphEditError('position')
}
