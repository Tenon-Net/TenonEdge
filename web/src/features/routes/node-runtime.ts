import type { components } from '../../api/schema'
import type { GraphDocument } from '../canvas/model'

export type NodeRuntime = components['schemas']['RouteNodeRuntimeInfo']
export type NodeRuntimePage = components['schemas']['RouteRuntimePage']

/** Only attach metadata to the exact effective graph, never to a reused id in
 * an unsaved draft, another module version, or an earlier runtime revision. */
export function bindNodeRuntime(
    document: GraphDocument,
    version: string,
    revision: number | string | null | undefined,
    available: boolean,
    page?: NodeRuntimePage,
): Map<string, NodeRuntime> {
    const result = new Map<string, NodeRuntime>()
    if (
        !available ||
        !page?.applied ||
        page.draftVersion !== version ||
        revision == null ||
        String(page.revision) !== String(revision)
    )
        return result
    for (const node of document.definition.nodes) {
        const matches = page.nodes.filter((value) => value.id === node.id)
        if (matches.length !== 1) continue
        const value = matches[0]
        if (
            value.kind === node.kind &&
            String(value.version) === String(node.version ?? 1)
        )
            result.set(node.id, value)
    }
    return result
}
