import { createStore } from 'zustand/vanilla'
import type { GraphDocument, NodeKind } from '../canvas/model'
import { selectedReferences } from './references'

export const createPreviewDraft = () =>
    createStore(() => ({ source: '', sample: '' }))
export type PreviewDraft = ReturnType<typeof createPreviewDraft>
export type PreviewSource = {
    key: string
    id: number
    kind: NodeKind
    nodeId: string
    nodeName: string
    nodeCount: number
}
/** Only references already in the graph can be selected; constants never pass through JS numbers. */
export function previewSources(document: GraphDocument) {
    const sources = new Map<string, PreviewSource>()
    const invalidNodes: string[] = []
    let overLimit = false
    for (const node of document.definition.nodes) {
        if (!node.kind.startsWith('input.')) continue
        try {
            for (const id of new Set(
                selectedReferences(node.kind, node.configText),
            )) {
                const key = `${node.kind}:${id}`
                const existing = sources.get(key)
                if (existing) existing.nodeCount++
                else if (sources.size < 2000)
                    sources.set(key, {
                        key,
                        id,
                        kind: node.kind,
                        nodeId: node.id,
                        nodeName: node.name,
                        nodeCount: 1,
                    })
                else overLimit = true
            }
        } catch {
            invalidNodes.push(node.id)
        }
    }
    return { sources: [...sources.values()], invalidNodes, overLimit }
}
