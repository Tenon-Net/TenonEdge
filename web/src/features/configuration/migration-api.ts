import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { readRouteJson } from '../canvas/json'
import { routeObject } from '../canvas/codec'
import { decodeDiagnosticsJson } from '../diagnostics/api'
import { connectionFilter } from '../deliveries/api'
import {
    migrationId,
    migrationCount,
    validateMigrationIdentity,
    type MigrationQuota,
} from '../deliveries/migration-api'
import {
    decodeOperation,
    decodeReview,
    exactJson,
    revisionId,
    type Review,
} from './api'

type Schema = components['schemas']
export type ConfigurationMigrationReview = Omit<
    Schema['ConfigurationMigrationReview'],
    'configuration'
> & { configuration: Review }
export type IntentPage = Schema['DeliveryMigrationIntentPage']
export type IntentCursor = Schema['DeliveryMigrationIntentCursor']
export type IntentInspection = Schema['ConfigurationMigrationIntentInspection']
export type IntentRetryReview =
    Schema['ConfigurationMigrationIntentRetryReview']
export type IntentFilter = { status?: string; connectionId?: string }
const root = '/api/edge-admin/v1/configuration/migrations' as const
const statuses = ['waiting', 'completed', 'rejected']
function connection(value: number | string) {
    const result = connectionFilter(String(value))
    if (!result) throw new Error('migration-connection')
    return result
}
function expiry(value: string) {
    if (typeof value !== 'string' || !Number.isFinite(Date.parse(value)))
        throw new Error('migration-expiry')
}
function quota(value: MigrationQuota) {
    const exceeded = [
        'pendingRecords',
        'pendingBytes',
        'globalBytes',
        'failedRecords',
    ] as const
    const actual = exceeded
        .map((key) => migrationCount(value[key]))
        .some((n) => n > 0n)
    if (value.exceeded !== actual) throw new Error('migration-quota')
    return actual
}
export function decodeConfigurationMigration(
    body: string | undefined,
): ConfigurationMigrationReview {
    if (!body) throw new Error('migration-response')
    const { configuration: raw, ...rest } = routeObject(
        readRouteJson(body, 128 * 1024 * 1024, 64),
    )
    const result = {
        ...decodeDiagnosticsJson<
            Omit<ConfigurationMigrationReview, 'configuration'>
        >(exactJson(rest)),
        configuration: decodeReview(exactJson(raw)),
    }
    migrationId(result.id)
    expiry(result.expiresAt)
    const review = result.configuration
    if (
        !review.comparisonAvailable ||
        !review.planHash ||
        review.effectiveRevision == null ||
        review.targetRevision != null ||
        review.routeIssues.length ||
        !Array.isArray(result.targets) ||
        result.targets.length < 1 ||
        result.targets.length > 500
    )
        throw new Error('migration-review')
    const connections = new Set<string>()
    const intents = new Set<string>()
    let canConfirm = true
    for (const item of result.targets) {
        const id = connection(item.connectionId)
        migrationId(item.intentId)
        validateMigrationIdentity(item.source)
        validateMigrationIdentity(item.target)
        if (
            connections.has(id) ||
            intents.has(item.intentId) ||
            !review.targetBlocks.some(
                (block) => String(block.connectionId) === id,
            ) ||
            typeof item.name !== 'string' ||
            typeof item.key !== 'string' ||
            item.source.protocol !== item.target.protocol ||
            item.source.address === item.target.address ||
            !item.target.accountRecorded
        )
            throw new Error('migration-target')
        connections.add(id)
        intents.add(item.intentId)
        const exceeded = quota(item.quota)
        if (migrationCount(item.count) === 0n || exceeded) canConfirm = false
    }
    if (result.canConfirm !== canConfirm) throw new Error('migration-review')
    return result
}
function inspection(value: IntentInspection) {
    migrationId(value.intentId)
    if (
        !statuses.includes(value.status) ||
        typeof value.targetMatchesConfirmation !== 'boolean' ||
        !(value.problem === null || typeof value.problem === 'string') ||
        !(value.retryProblem === null || typeof value.retryProblem === 'string')
    )
        throw new Error('migration-inspection')
    if (value.effectiveRevision !== null) revisionId(value.effectiveRevision)
    for (const recipient of [
        value.source,
        value.approvedTarget,
        value.currentTarget,
    ])
        if (recipient !== null) validateMigrationIdentity(recipient)
    if (value.remainingRecords !== null) migrationCount(value.remainingRecords)
    const exceeded =
        value.currentQuota === null ? true : quota(value.currentQuota)
    const canPrepare =
        value.problem === null &&
        value.retryProblem === null &&
        value.remainingRecords !== null &&
        !exceeded
    if (
        value.canPrepareRetry !== canPrepare ||
        (canPrepare &&
            (!value.source ||
                !value.currentTarget ||
                value.effectiveRevision === null))
    )
        throw new Error('migration-inspection')
    return value
}
function retryReview(value: IntentRetryReview) {
    migrationId(value.id)
    expiry(value.expiresAt)
    inspection(value.inspection)
    if (value.canConfirm !== value.inspection.canPrepareRetry)
        throw new Error('migration-review')
    return value
}
function cursor(value: IntentCursor) {
    revisionId(value.revision)
    connection(value.connectionId)
    return [BigInt(value.revision), BigInt(value.connectionId)] as const
}
export function validateIntentPage(
    value: IntentPage,
    filter: IntentFilter,
    after?: IntentCursor,
) {
    if (!Array.isArray(value.items) || value.items.length > 50)
        throw new Error('migration-page')
    let previous = after ? cursor(after) : ([0n, 0n] as const)
    const ids = new Set<string>()
    for (const item of value.items) {
        migrationId(item.id)
        revisionId(item.sourceRevision)
        migrationCount(item.registeredAt)
        if (item.lastOperationId !== null) migrationId(item.lastOperationId)
        const next = cursor(item)
        if (
            ids.has(item.id) ||
            next[0] < previous[0] ||
            (next[0] === previous[0] && next[1] <= previous[1]) ||
            !statuses.includes(item.status) ||
            (filter.status && filter.status !== item.status) ||
            (filter.connectionId &&
                filter.connectionId !== String(item.connectionId)) ||
            typeof item.registeredBy !== 'string' ||
            typeof item.hasConfirmedTarget !== 'boolean'
        )
            throw new Error('migration-page')
        ids.add(item.id)
        previous = next
    }
    if (value.next !== null) {
        const next = cursor(value.next)
        if (
            value.items.length !== 50 ||
            next[0] !== previous[0] ||
            next[1] !== previous[1]
        )
            throw new Error('migration-cursor')
    }
    return value
}
export function createConfigurationMigrationApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async prepare(expectedVersion: string, connectionIds: string[]) {
            if (
                !expectedVersion ||
                connectionIds.length < 1 ||
                connectionIds.length > 500 ||
                new Set(connectionIds).size !== connectionIds.length
            )
                throw new Error('migration-selection')
            connectionIds.forEach(connection)
            const { data } = await api.POST(`${root}/prepare`, {
                body: { expectedVersion, connectionIds },
                parseAs: 'text',
            })
            const review = decodeConfigurationMigration(data)
            if (
                review.configuration.version !== expectedVersion ||
                review.targets.length !== connectionIds.length ||
                review.targets.some(
                    (t) => !connectionIds.includes(String(t.connectionId)),
                )
            )
                throw new Error('migration-review-selection')
            return review
        },
        async current(signal?: AbortSignal) {
            const { data } = await api.GET(`${root}/preparation`, {
                signal,
                parseAs: 'text',
            })
            if (!data) throw new Error('migration-response')
            const raw = routeObject(readRouteJson(data, 128 * 1024 * 1024, 64))
            return raw.preparation === null
                ? null
                : decodeConfigurationMigration(exactJson(raw.preparation))
        },
        async cancel(id: string) {
            await api.DELETE(`${root}/preparation/{id}`, {
                params: { path: { id: migrationId(id) } },
            })
        },
        async confirm(
            id: string,
            confirmed: boolean,
            sameOwnerConfirmed: boolean,
            description: string,
        ) {
            if (!confirmed || !sameOwnerConfirmed || description.length > 1000)
                throw new Error('migration-confirmation')
            const { data } = await api.POST(`${root}/{id}/confirm`, {
                params: { path: { id: migrationId(id) } },
                body: {
                    confirmed: true,
                    sameOwnerConfirmed: true,
                    changeDescription: description || null,
                },
                parseAs: 'text',
            })
            if (!data) throw new Error('migration-response')
            return decodeOperation(data, id)
        },
        async intents(
            filter: IntentFilter,
            after?: IntentCursor,
            signal?: AbortSignal,
        ) {
            if (filter.status && !statuses.includes(filter.status))
                throw new Error('migration-filter')
            if (after) cursor(after)
            const { data } = await api.GET(`${root}/intents`, {
                params: {
                    query: {
                        status: filter.status || undefined,
                        connectionId: filter.connectionId
                            ? (connection(
                                  filter.connectionId,
                              ) as unknown as number)
                            : undefined,
                        afterRevision: after
                            ? (revisionId(after.revision) as unknown as number)
                            : undefined,
                        afterConnection: after
                            ? (connection(
                                  after.connectionId,
                              ) as unknown as number)
                            : undefined,
                        limit: 50,
                    },
                },
                signal,
                parseAs: 'text',
            })
            return validateIntentPage(
                decodeDiagnosticsJson<IntentPage>(data),
                filter,
                after,
            )
        },
        async inspect(id: string, signal?: AbortSignal) {
            const { data } = await api.GET(`${root}/intents/{id}/preflight`, {
                params: { path: { id: migrationId(id) } },
                signal,
                parseAs: 'text',
            })
            const result = inspection(
                decodeDiagnosticsJson<IntentInspection>(data),
            )
            if (result.intentId !== id) throw new Error('migration-intent')
            return result
        },
        async outcome(id: string, signal?: AbortSignal) {
            const { data } = await api.GET(`${root}/intents/{id}`, {
                params: { path: { id: migrationId(id) } },
                signal,
                parseAs: 'text',
            })
            const result =
                decodeDiagnosticsJson<
                    Schema['ConfigurationMigrationIntentOutcome']
                >(data)
            if (result.id !== id || !statuses.includes(result.status))
                throw new Error('migration-intent')
            revisionId(result.revision)
            revisionId(result.sourceRevision)
            connection(result.connectionId)
            if (result.operationId !== null) migrationId(result.operationId)
            if (result.affectedRecords !== null)
                migrationCount(result.affectedRecords)
            for (const recipient of [result.source, result.target])
                if (recipient !== null) validateMigrationIdentity(recipient)
            return result
        },
        async prepareRetry(id: string) {
            const { data } = await api.POST(
                `${root}/intents/{id}/retry/prepare`,
                { params: { path: { id: migrationId(id) } }, parseAs: 'text' },
            )
            const result = retryReview(
                decodeDiagnosticsJson<IntentRetryReview>(data),
            )
            if (result.inspection.intentId !== id)
                throw new Error('migration-intent')
            return result
        },
        async currentRetry(signal?: AbortSignal) {
            const { data } = await api.GET(`${root}/intent-retry/preparation`, {
                signal,
                parseAs: 'text',
            })
            const result =
                decodeDiagnosticsJson<
                    Schema['ConfigurationMigrationIntentRetryCurrent']
                >(data)
            return result.preparation === null
                ? null
                : retryReview(result.preparation)
        },
        async cancelRetry(id: string) {
            await api.DELETE(`${root}/intent-retry/preparation/{id}`, {
                params: { path: { id: migrationId(id) } },
            })
        },
        async confirmRetry(
            id: string,
            confirmed: boolean,
            sameOwnerConfirmed: boolean,
        ) {
            if (!confirmed || !sameOwnerConfirmed)
                throw new Error('migration-confirmation')
            const { data } = await api.POST(
                `${root}/intent-retry/{id}/confirm`,
                {
                    params: { path: { id: migrationId(id) } },
                    body: { confirmed: true, sameOwnerConfirmed: true },
                    parseAs: 'text',
                },
            )
            const result =
                decodeDiagnosticsJson<
                    Schema['DeliveryMigrationConfirmationResult']
                >(data)
            if (result.operationId !== id) throw new Error('migration-outcome')
            migrationCount(result.migratedRecords)
            return result
        },
    }
}
