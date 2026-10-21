import type { components } from '../../api/schema'
import { topologyHints, type GraphDocument } from '../canvas/model'
import { selectedReferences } from './references'

export function localNodeErrors(document: GraphDocument) {
    const result = new Set(topologyHints(document))
    for (const node of document.definition.nodes) {
        if (!node.kind.startsWith('input.') && !node.kind.startsWith('output.'))
            continue
        try {
            if (!selectedReferences(node.kind, node.configText).length)
                result.add(node.id)
        } catch {
            result.add(node.id)
        }
    }
    return result
}

export type PreviewNodeIssues = {
    document: GraphDocument
    version: string
    source: string
    sample: string
    signal: AbortSignal
    nodes: string[]
}
export function previewErrorNodes(
    document: GraphDocument,
    data: components['schemas']['RoutePreviewResponse'],
) {
    const ids = new Set(
        data.issues.flatMap((issue) => (issue.nodeId ? [issue.nodeId] : [])),
    )
    for (const path of data.result?.paths ?? []) {
        for (const issue of path.issues) if (issue.nodeId) ids.add(issue.nodeId)
        for (const stage of path.stages)
            if (!stage.succeeded) ids.add(stage.nodeId)
    }
    // At most one id per graph node; no values, exception text or preview bodies.
    return document.definition.nodes
        .filter((node) => ids.has(node.id))
        .map((node) => node.id)
}
