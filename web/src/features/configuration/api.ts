import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { readRouteJson, RouteNumber, type RouteJson } from '../canvas/json'
import { routeObject } from '../canvas/codec'

type Schema = components['schemas']
export type DraftSummary = Schema['ConfigurationDraftSummary']
export type Operation = Schema['RuntimeApplicationStatus']
export type RuntimeState = Schema['ConfigurationRuntimeState']
export type RecoveryReview = Schema['ConfigurationRecoveryReview']
export type Review = Omit<Schema['ConfigurationReview'], 'changes'> & {
    changes: (Omit<Schema['ConfigurationChange'], 'before' | 'after'> & {
        beforeText: string
        afterText: string
    })[]
}
export type VersionSummary = Schema['RuntimeVersionSummary']
export type VersionDetail = {
    version: VersionSummary
    contentHash: string
    planHash: string
    draftText: string
    planText: string
    warnings: string[]
}
export type Comparison = Omit<
    Schema['ConfigurationVersionComparison'],
    'changes'
> & { changes: Review['changes'] }
export type ImportReview = Omit<
    Schema['ConfigurationImportReview'],
    'changes'
> & {
    changes: Review['changes']
}

function importReview(value: RouteJson): ImportReview {
    const { changes, ...fields } = routeObject(value)
    const envelope = metadata(fields) as Schema['ConfigurationImportReview']
    if (
        typeof envelope.id !== 'string' ||
        typeof envelope.version !== 'string' ||
        !['merge', 'replace'].includes(envelope.mode) ||
        !Array.isArray(envelope.credentials) ||
        typeof envelope.expiresAt !== 'string' ||
        !Number.isFinite(Date.parse(envelope.expiresAt))
    )
        throw new Error('configuration-import-response')
    return { ...envelope, changes: decodeChanges(changes) }
}
export function decodeImportReview(body: string): ImportReview {
    return importReview(readRouteJson(body, maximumResponseBytes, 64))
}
export function decodeCurrentImport(body: string): ImportReview | null {
    const raw = routeObject(readRouteJson(body, maximumResponseBytes, 64))
    return raw.review === null ? null : importReview(raw.review)
}

/** Generated integer metadata permits strings, so large revision numbers need not be rounded. */
function metadata(value: RouteJson): unknown {
    if (value instanceof RouteNumber) return value.text
    if (Array.isArray(value)) return value.map(metadata)
    if (value !== null && typeof value === 'object')
        return Object.fromEntries(
            Object.entries(value).map(([key, item]) => [key, metadata(item)]),
        )
    return value
}
export function revisionId(value: number | string): string {
    if (typeof value === 'number' && !Number.isSafeInteger(value))
        throw new Error('configuration-revision')
    const text = String(value)
    if (
        !/^[1-9]\d*$/.test(text) ||
        text.length > 19 ||
        (text.length === 19 && text > '9223372036854775807')
    )
        throw new Error('configuration-revision')
    return text
}
// OpenAPI emits numeric path parameters, but openapi-fetch serializes them as text.
// Keep the validated Int64 decimal at this transport boundary rather than rounding it.
function revisionPath(value: number | string): number {
    return revisionId(value) as unknown as number
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

function decodeChanges(rawChanges: RouteJson): Review['changes'] {
    if (!Array.isArray(rawChanges)) throw new Error('configuration-response')
    return rawChanges.map((item) => {
        const raw = routeObject(item)
        const { before: _before, after: _after, ...rest } = raw
        const fields = metadata(rest) as Omit<
            Schema['ConfigurationChange'],
            'before' | 'after'
        >
        return {
            ...fields,
            beforeText: exactJson(raw.before ?? null),
            afterText: exactJson(raw.after ?? null),
        }
    })
}
export function decodeReview(body: string): Review {
    const raw = routeObject(readRouteJson(body, maximumResponseBytes, 64))
    const { changes, ...fields } = raw
    const envelope = metadata(fields) as Schema['ConfigurationReview']
    if (
        typeof envelope.version !== 'string' ||
        typeof envelope.preflightPassed !== 'boolean' ||
        typeof envelope.comparisonAvailable !== 'boolean' ||
        !Array.isArray(envelope.points) ||
        !Array.isArray(envelope.routeIssues) ||
        !Array.isArray(envelope.targetBlocks) ||
        !Array.isArray(envelope.warnings)
    )
        throw new Error('configuration-response')
    if (envelope.effectiveRevision != null)
        revisionId(envelope.effectiveRevision)
    if (envelope.targetRevision != null) revisionId(envelope.targetRevision)
    return { ...envelope, changes: decodeChanges(changes) }
}
export function decodeComparison(body: string): Comparison {
    const raw = routeObject(readRouteJson(body, maximumResponseBytes, 64))
    const { changes, ...fields } = raw
    const envelope = metadata(
        fields,
    ) as Schema['ConfigurationVersionComparison']
    revisionId(envelope.beforeRevision)
    if (envelope.afterRevision != null) revisionId(envelope.afterRevision)
    if (!Array.isArray(envelope.points))
        throw new Error('configuration-response')
    return { ...envelope, changes: decodeChanges(changes) }
}
function decodeVersionSummary(value: RouteJson): VersionSummary {
    const version = metadata(routeObject(value)) as VersionSummary
    revisionId(version.revision)
    if (
        typeof version.state !== 'string' ||
        typeof version.origin !== 'string' ||
        typeof version.createdBy !== 'string' ||
        typeof version.isStable !== 'boolean' ||
        !Number.isSafeInteger(Number(version.schemaVersion)) ||
        Number(version.schemaVersion) < 1
    )
        throw new Error('configuration-response')
    return version
}
export function decodeVersion(body: string): VersionDetail {
    const raw = routeObject(readRouteJson(body, maximumResponseBytes, 64))
    const version = decodeVersionSummary(raw.version)
    if (
        typeof raw.contentHash !== 'string' ||
        typeof raw.planHash !== 'string' ||
        !Array.isArray(raw.warnings) ||
        !raw.warnings.every((item) => typeof item === 'string') ||
        !Object.hasOwn(raw, 'draft') ||
        !Object.hasOwn(raw, 'plan')
    )
        throw new Error('configuration-response')
    return {
        version,
        contentHash: raw.contentHash,
        planHash: raw.planHash,
        warnings: raw.warnings,
        draftText: exactJson(raw.draft),
        planText: exactJson(raw.plan),
    }
}

export function decodeOperation(body: string, expectedId?: string): Operation {
    const operation = metadata(
        routeObject(readRouteJson(body, maximumResponseBytes, 64)),
    ) as Operation
    if (
        typeof operation.id !== 'string' ||
        (expectedId != null && operation.id !== expectedId)
    )
        throw new Error('configuration-response')
    return operation
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
):
    | 'running'
    | 'unknown'
    | 'successful'
    | 'committedFault'
    | 'notCommitted'
    | 'retrySuccessful'
    | 'retryFailed' {
    if (!operation.finishedAt) return 'running'
    if (operation.kind === 'retry')
        return operation.result?.activated === true
            ? 'retrySuccessful'
            : 'retryFailed'
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
        async reviewRecovery(
            revision: number | string,
            version: string | null,
        ) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/versions/{revision}/recovery-review',
                {
                    params: { path: { revision: revisionPath(revision) } },
                    body: { expectedVersion: version },
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            const review = metadata(
                routeObject(readRouteJson(data, maximumResponseBytes, 64)),
            ) as RecoveryReview
            if (
                revisionId(review.targetRevision) !== revisionId(revision) ||
                typeof review.draftVersion !== 'string' ||
                review.preservesDraft !== true ||
                typeof review.canConfirm !== 'boolean' ||
                !/^sha256:[a-f0-9]{64}$/.test(review.confirmationHash) ||
                !Array.isArray(review.targets) ||
                !Array.isArray(review.preparationProblems) ||
                !review.queue
            )
                throw new Error('configuration-response')
            revisionId(review.effectiveRevision)
            return review
        },
        async recover(
            review: RecoveryReview,
            confirmed: boolean,
            preserve: boolean,
        ) {
            if (
                !confirmed ||
                !preserve ||
                !review.canConfirm ||
                !review.preservesDraft
            )
                throw new Error('configuration-confirmation')
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/versions/{revision}/recover',
                {
                    params: {
                        path: { revision: revisionPath(review.targetRevision) },
                    },
                    body: {
                        expectedVersion: review.draftVersion,
                        confirmationHash: review.confirmationHash,
                        confirmed: true,
                        preserveDraftConfirmed: true,
                    },
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            return decodeOperation(data)
        },
        async runtime(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/runtime',
                { signal, parseAs: 'text' },
            )
            if (!data) throw new Error('configuration-response')
            const state = metadata(
                routeObject(readRouteJson(data, 16 * 1024, 16)),
            ) as RuntimeState
            if (
                typeof state.state !== 'string' ||
                typeof state.canRetry !== 'boolean' ||
                (state.planHash !== null && typeof state.planHash !== 'string')
            )
                throw new Error('configuration-response')
            if (state.effectiveRevision !== null)
                revisionId(state.effectiveRevision)
            return state
        },
        async retry(state: RuntimeState, confirmed: boolean) {
            if (
                !confirmed ||
                !state.canRetry ||
                state.state !== 'runFailed' ||
                state.effectiveRevision == null ||
                !state.planHash
            )
                throw new Error('configuration-confirmation')
            revisionId(state.effectiveRevision)
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/runtime/retry',
                {
                    body: {
                        expectedEffectiveRevision: state.effectiveRevision,
                        expectedPlanHash: state.planHash,
                        confirmed: true,
                    },
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            return decodeOperation(data)
        },
        async summary(signal?: AbortSignal) {
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
            if (summary.pendingRevision != null)
                revisionId(summary.pendingRevision)
            if (
                typeof summary.version !== 'string' ||
                typeof summary.comparisonAvailable !== 'boolean'
            )
                throw new Error('configuration-response')
            return summary
        },
        async operations(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/operations',
                { signal, parseAs: 'text' },
            )
            if (!data) throw new Error('configuration-response')
            const raw = readRouteJson(data, maximumResponseBytes, 64)
            if (!Array.isArray(raw)) throw new Error('configuration-response')
            return metadata(raw) as Operation[]
        },
        async operation(id: string, signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/operations/{operationId}',
                {
                    params: { path: { operationId: id } },
                    signal,
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            return decodeOperation(data, id)
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
            if (review.targetRevision != null)
                throw new Error('configuration-wrong-operation')
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/apply',
                {
                    body: applicationRequest(review, description, confirmed),
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            return decodeOperation(data)
        },
        async versions(beforeRevision?: string, signal?: AbortSignal) {
            if (beforeRevision != null) revisionId(beforeRevision)
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/versions',
                {
                    params: { query: { beforeRevision, limit: 20 } },
                    signal,
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            const raw = readRouteJson(data, maximumResponseBytes, 64)
            if (!Array.isArray(raw)) throw new Error('configuration-response')
            return raw.map(decodeVersionSummary)
        },
        async version(revision: number | string, signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/versions/{revision}',
                {
                    params: { path: { revision: revisionPath(revision) } },
                    parseAs: 'text',
                    signal,
                },
            )
            if (!data) throw new Error('configuration-response')
            const detail = decodeVersion(data)
            if (revisionId(detail.version.revision) !== revisionId(revision))
                throw new Error('configuration-response')
            return detail
        },
        async compare(
            before: number | string,
            after: number | string,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/versions/{revision}/compare/{otherRevision}',
                {
                    params: {
                        path: {
                            revision: revisionPath(before),
                            otherRevision: revisionPath(after),
                        },
                    },
                    parseAs: 'text',
                    signal,
                },
            )
            if (!data) throw new Error('configuration-response')
            const comparison = decodeComparison(data)
            if (
                revisionId(comparison.beforeRevision) !== revisionId(before) ||
                comparison.afterRevision == null ||
                revisionId(comparison.afterRevision) !== revisionId(after) ||
                comparison.draftVersion != null
            )
                throw new Error('configuration-response')
            return comparison
        },
        async compareDraft(
            revision: number | string,
            version: string,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/configuration/versions/{revision}/compare-draft',
                {
                    params: {
                        path: { revision: revisionPath(revision) },
                        query: { expectedVersion: version },
                    },
                    parseAs: 'text',
                    signal,
                },
            )
            if (!data) throw new Error('configuration-response')
            const comparison = decodeComparison(data)
            if (
                revisionId(comparison.beforeRevision) !==
                    revisionId(revision) ||
                comparison.afterRevision != null ||
                comparison.draftVersion !== version
            )
                throw new Error('configuration-response')
            return comparison
        },
        async reviewRollback(revision: number | string, version: string) {
            const target = revisionId(revision)
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/versions/{revision}/review',
                {
                    params: { path: { revision: revisionPath(target) } },
                    body: { expectedVersion: version },
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            const review = decodeReview(data)
            if (
                review.targetRevision == null ||
                revisionId(review.targetRevision) !== target
            )
                throw new Error('configuration-response')
            return review
        },
        async rollback(
            review: Review,
            description: string,
            confirmed: boolean,
        ) {
            if (review.targetRevision == null)
                throw new Error('configuration-wrong-operation')
            const { data } = await api.POST(
                '/api/edge-admin/v1/configuration/versions/{revision}/rollback',
                {
                    params: {
                        path: { revision: revisionPath(review.targetRevision) },
                    },
                    body: applicationRequest(review, description, confirmed),
                    parseAs: 'text',
                },
            )
            if (!data) throw new Error('configuration-response')
            return decodeOperation(data)
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
