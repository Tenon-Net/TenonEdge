import { createStore } from 'zustand/vanilla'
import { defaultConfig, normalizeConfigText, encodeGraph } from './codec'
import {
    checkPosition,
    connectionError,
    emptyGraph,
    GraphEditError,
    historyLimit,
    maxNodes,
    type GraphDocument,
    type NodeKind,
    type Position,
} from './model'

type State = {
    document: GraphDocument
    past: GraphDocument[]
    future: GraphDocument[]
    selectedNodes: string[]
    selectedEdges: string[]
    clipboard: GraphDocument | null
    load: (document: GraphDocument) => void
    configure: (id: string, text: string) => void
    select: (nodes: string[], edges: string[]) => void
    add: (kind: NodeKind, name: string, position: Position) => string
    connect: (source: string, target: string) => void
    rename: (id: string, name: string) => void
    move: (positions: Record<string, Position>) => void
    remove: () => void
    copy: () => void
    paste: () => void
    undo: () => void
    redo: () => void
}
export function createGraphEditor() {
    let sequence = 0
    const usedIds = new Set<string>()
    const nextId = () => {
        let id: string
        do {
            id = `g${++sequence}`
        } while (usedIds.has(id))
        usedIds.add(id)
        return id
    }
    return createStore<State>((set, get) => {
        const commit = (document: GraphDocument) => {
            const state = get()
            if (JSON.stringify(document) === JSON.stringify(state.document))
                return
            set({
                document,
                past: [...state.past, state.document].slice(-historyLimit),
                future: [],
            })
        }
        return {
            document: emptyGraph(),
            past: [],
            future: [],
            selectedNodes: [],
            selectedEdges: [],
            clipboard: null,
            load: (document) => {
                encodeGraph(document)
                const copy = structuredClone(document)
                usedIds.clear()
                for (const item of [
                    ...copy.definition.nodes,
                    ...copy.definition.edges,
                ])
                    usedIds.add(item.id)
                set({
                    document: copy,
                    past: [],
                    future: [],
                    selectedNodes: [],
                    selectedEdges: [],
                    clipboard: null,
                })
            },
            configure: (id, text) => {
                const configText = normalizeConfigText(text)
                const doc = get().document
                const document = {
                    ...doc,
                    definition: {
                        ...doc.definition,
                        nodes: doc.definition.nodes.map((node) =>
                            node.id === id ? { ...node, configText } : node,
                        ),
                    },
                }
                encodeGraph(document)
                commit(document)
            },
            select: (selectedNodes, selectedEdges) =>
                set({ selectedNodes, selectedEdges }),
            add: (kind, name, position) => {
                const doc = get().document
                if (doc.definition.nodes.length >= maxNodes)
                    throw new GraphEditError('limit')
                checkPosition(position)
                const id = nextId()
                commit({
                    ...doc,
                    definition: {
                        ...doc.definition,
                        nodes: [
                            ...doc.definition.nodes,
                            { id, kind, name, configText: defaultConfig(kind) },
                        ],
                    },
                    layout: {
                        positions: {
                            ...doc.layout.positions,
                            [id]: { ...position },
                        },
                    },
                })
                set({ selectedNodes: [id], selectedEdges: [] })
                return id
            },
            connect: (source, target) => {
                const doc = get().document
                const error = connectionError(doc, source, target)
                if (error) throw new GraphEditError(error)
                commit({
                    ...doc,
                    definition: {
                        ...doc.definition,
                        edges: [
                            ...doc.definition.edges,
                            { id: nextId(), source, target },
                        ],
                    },
                })
            },
            rename: (id, name) => {
                const doc = get().document
                commit({
                    ...doc,
                    definition: {
                        ...doc.definition,
                        nodes: doc.definition.nodes.map((n) =>
                            n.id === id ? { ...n, name } : n,
                        ),
                    },
                })
            },
            move: (positions) => {
                const doc = get().document
                const next = { ...doc.layout.positions }
                for (const [id, position] of Object.entries(positions)) {
                    checkPosition(position)
                    if (Object.hasOwn(next, id)) next[id] = { ...position }
                }
                commit({ ...doc, layout: { positions: next } })
            },
            remove: () => {
                const { document: doc, selectedNodes, selectedEdges } = get()
                const removed = new Set(selectedNodes)
                commit({
                    ...doc,
                    definition: {
                        nodes: doc.definition.nodes.filter(
                            (n) => !removed.has(n.id),
                        ),
                        edges: doc.definition.edges.filter(
                            (e) =>
                                !removed.has(e.source) &&
                                !removed.has(e.target) &&
                                !selectedEdges.includes(e.id),
                        ),
                    },
                    layout: {
                        positions: Object.fromEntries(
                            Object.entries(doc.layout.positions).filter(
                                ([id]) => !removed.has(id),
                            ),
                        ),
                    },
                })
                set({ selectedNodes: [], selectedEdges: [] })
            },
            copy: () => {
                const { document: doc, selectedNodes } = get()
                if (!selectedNodes.length) return
                const ids = new Set(selectedNodes)
                set({
                    clipboard: structuredClone({
                        ...doc,
                        definition: {
                            nodes: doc.definition.nodes.filter((n) =>
                                ids.has(n.id),
                            ),
                            edges: doc.definition.edges.filter(
                                (e) => ids.has(e.source) && ids.has(e.target),
                            ),
                        },
                        layout: {
                            positions: Object.fromEntries(
                                Object.entries(doc.layout.positions).filter(
                                    ([id]) => ids.has(id),
                                ),
                            ),
                        },
                    }),
                })
            },
            paste: () => {
                const { document: doc, clipboard } = get()
                if (!clipboard?.definition.nodes.length) return
                if (
                    doc.definition.nodes.length +
                        clipboard.definition.nodes.length >
                    maxNodes
                )
                    throw new GraphEditError('limit')
                const ids = new Map(
                    clipboard.definition.nodes.map((n) => [n.id, nextId()]),
                )
                const nodes = clipboard.definition.nodes.map((n) => ({
                    ...n,
                    id: ids.get(n.id)!,
                }))
                const edges = clipboard.definition.edges.map((e) => ({
                    id: nextId(),
                    source: ids.get(e.source)!,
                    target: ids.get(e.target)!,
                }))
                const positions = Object.fromEntries(
                    Object.entries(clipboard.layout.positions).map(
                        ([id, p]) => [
                            ids.get(id)!,
                            { x: p.x + 32, y: p.y + 32 },
                        ],
                    ),
                )
                for (const position of Object.values(positions))
                    checkPosition(position)
                const pasted = {
                    ...doc,
                    definition: {
                        nodes: [...doc.definition.nodes, ...nodes],
                        edges: [...doc.definition.edges, ...edges],
                    },
                    layout: {
                        positions: { ...doc.layout.positions, ...positions },
                    },
                }
                encodeGraph(pasted)
                commit(pasted)
                set({
                    selectedNodes: nodes.map((n) => n.id),
                    selectedEdges: [],
                })
            },
            undo: () => {
                const { past, document, future } = get()
                if (!past.length) return
                set({
                    document: past[past.length - 1],
                    past: past.slice(0, -1),
                    future: [document, ...future],
                    selectedNodes: [],
                    selectedEdges: [],
                })
            },
            redo: () => {
                const { past, document, future } = get()
                if (!future.length) return
                set({
                    document: future[0],
                    past: [...past, document].slice(-historyLimit),
                    future: future.slice(1),
                    selectedNodes: [],
                    selectedEdges: [],
                })
            },
        }
    })
}
