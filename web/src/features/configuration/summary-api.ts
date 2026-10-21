import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { readRouteJson } from '../canvas/json'
import { routeObject } from '../canvas/codec'
import { metadata, revisionId, maximumResponseBytes } from './response'

export type DraftSummary = components['schemas']['ConfigurationDraftSummary']

// The workspace header only needs this read; it must not import apply, import or migration clients.
export function createDraftSummaryApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return async (signal?: AbortSignal) => {
        const { data } = await api.GET(
            '/api/edge-admin/v1/configuration/draft',
            { signal, parseAs: 'text' },
        )
        if (!data) throw new Error('configuration-response')
        const summary = metadata(
            routeObject(readRouteJson(data, maximumResponseBytes, 64)),
        ) as DraftSummary
        if (summary.effectiveRevision != null)
            revisionId(summary.effectiveRevision)
        if (summary.pendingRevision != null) revisionId(summary.pendingRevision)
        if (
            typeof summary.version !== 'string' ||
            typeof summary.comparisonAvailable !== 'boolean'
        )
            throw new Error('configuration-response')
        return summary
    }
}
