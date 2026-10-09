/** Product-owned local prototype format. Never contains React Flow state. */
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
export type NodeKind = (typeof nodeKinds)[number]
export type Position = { x: number; y: number }
export type GraphNode = { id: string; kind: NodeKind; name: string }
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
    if (!Number.isFinite(position.x) || !Number.isFinite(position.y))
        throw new GraphEditError('position')
}
