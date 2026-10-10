import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { readRouteJson, RouteNumber, type RouteJson } from '../canvas/json'
import { routeObject } from '../canvas/codec'

type Schema = components['schemas']
export type DraftSummary = Schema['ConfigurationDraftSummary']
export type Operation = Schema['RuntimeApplicationStatus']
export type Review = Omit<Schema['ConfigurationReview'], 'changes'> & {
    changes: (Omit<Schema['ConfigurationChange'], 'before' | 'after'> & {
        beforeText: string
        afterText: string
    })[]
}
const maximumResponseBytes = 128 * 1024 * 1024

/** Display configuration values without converting JSON numbers through IEEE-754. */
export function exactJson(value: RouteJson, depth = 0): string {
    if (depth > 64) throw new Error('configuration-response-depth')
    if (value instanceof RouteNumber) return value.text
    if (value === null || typeof value !== 'object')
        return JSON.stringify(value)
    if (Array.isArray(value))
        return (
            '[' +
            value.map((item) => exactJson(item, depth + 1)).join(',') +
            ']'
        )
    return (
        '{' +
        Object.entries(value)
            .map(
                ([key, item]) =>
                    JSON.stringify(key) + ':' + exactJson(item, depth + 1),
            )
            .join(',') +
        '}'
    )
}

export function decodeReview(body: string): Review {
    // Metadata is bounded integers/strings. Only before/after carry arbitrary JSON numeric values.
    const raw = routeObject(readRouteJson(body, maximumResponseBytes, 64))
    const metadata: Schema['ConfigurationReview'] = JSON.parse(body)
    if (
        !Array.isArray(raw.changes) ||
        !Array.isArray(metadata.changes) ||
        typeof metadata.version !== 'string'
    )
        throw new Error('configuration-response')
    const rawChanges = raw.changes
    const changes = metadata.changes.map((change, index) => {
        const values = routeObject(rawChanges[index])
        const { before: _before, after: _after, ...fields } = change
        return {
            ...fields,
            beforeText: exactJson(values.before ?? null),
            afterText: exactJson(values.after ?? null),
        }
    })
    return { ...metadata, changes }
}

export function applicationRequest(
    review: Review,
    description: string,
    confirmed: boolean,
): Schema['ConfigurationApplicationRequest'] {
    if (
        !confirmed ||
        !review.preflightPassed ||
        !review.comparisonAvailable ||
        !review.planHash ||
        description.length > 1000
    )
        throw new Error('configuration-confirmation')
    return {
        expectedVersion: review.version,
        expectedEffectiveRevision: review.effectiveRevision,
        expectedPlanHash: review.planHash,
        confirmed: true,
        changeDescription: description || null,
    }
}
export function reviewIsCurrent(
    review: Review,
    summary: DraftSummary,
): boolean {
    return (
        review.version === summary.version &&
        String(review.effectiveRevision) === String(summary.effectiveRevision)
    )
}
export function operationOutcome(
    operation: Operation,
): 'running' | 'unknown' | 'successful' | 'committedFault' | 'notCommitted' {
    if (!operation.finishedAt) return 'running'
    if (operation.committed === null) return 'unknown'
    if (!operation.committed) return 'notCommitted'
    return operation.result?.activated === true
        ? 'successful'
        : 'committedFault'
}

export function createConfigurationApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async summary(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/draft',
                { signal },
            )
            if (!data) throw new Error('configuration-response')
            return data
        },
        async operations(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/operations',
                { signal },
            )
            if (!data) throw new Error('configuration-response')
            return data
        },
        async operation(id: string, signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/operations/{operationId}',
                { params: { path: { operationId: id } }, signal },
            )
            if (!data) throw new Error('configuration-response')
            return data
        },
        async review(version: string) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/review',
                { body: { expectedVersion: version }, parseAs: 'text' },
            )
            if (!data) throw new Error('configuration-response')
            return decodeReview(data)
        },
        async apply(review: Review, description: string, confirmed: boolean) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/apply',
                { body: applicationRequest(review, description, confirmed) },
            )
            if (!data) throw new Error('configuration-response')
            return data
        },
        async discard(summary: DraftSummary, confirmed: boolean) {
            if (!confirmed || !summary.comparisonAvailable)
                throw new Error('configuration-confirmation')
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/draft/discard',
                {
                    body: {
                        expectedVersion: summary.version,
                        expectedEffectiveRevision: summary.effectiveRevision,
                        confirmed: true,
                    },
                },
            )
            if (!data) throw new Error('configuration-response')
            return data
        },
    }
}

/** JSON-pointer field changes; bounded display, with full exact objects still available. */
export function fieldChanges(
    beforeText: string,
    afterText: string,
    maximum = 200,
) {
    const items: { path: string; before: string; after: string }[] = []
    let truncated = false
    const before = readRouteJson(beforeText, maximumResponseBytes, 64)
    const after = readRouteJson(afterText, maximumResponseBytes, 64)
    function object(
        value: RouteJson | undefined,
    ): value is { [key: string]: RouteJson } {
        return (
            value !== null &&
            typeof value === 'object' &&
            !(value instanceof RouteNumber) &&
            !Array.isArray(value)
        )
    }
    function visit(
        left: RouteJson | undefined,
        right: RouteJson | undefined,
        path: string,
    ): void {
        if (truncated) return
        if (object(left) && object(right)) {
            for (const key of new Set([
                ...Object.keys(left),
                ...Object.keys(right),
            ])) {
                visit(
                    Object.hasOwn(left, key) ? left[key] : undefined,
                    Object.hasOwn(right, key) ? right[key] : undefined,
                    path + '/' + key.replace(/~/g, '~0').replace(/\//g, '~1'),
                )
            }
            return
        }
        if (Array.isArray(left) && Array.isArray(right)) {
            for (
                let index = 0;
                index < Math.max(left.length, right.length);
                index++
            )
                visit(left[index], right[index], path + '/' + index)
            return
        }
        const old = left === undefined ? '—' : exactJson(left)
        const next = right === undefined ? '—' : exactJson(right)
        if (old === next) return
        if (items.length >= maximum) {
            truncated = true
            return
        }
        items.push({ path: path || '/', before: old, after: next })
    }
    visit(before, after, '')
    return { items, truncated }
}
