import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { ApiError } from '../../api/errors'
import { revisionId } from '../configuration/api'
import { decodeDiagnosticsJson } from '../diagnostics/api'
import {
    connectionFilter,
    retrySelection,
    type DeliveryItem,
    type DeliveryPage,
} from './api'

type Schema = components['schemas']
export type MigrationReview = Schema['DeliveryMigrationReview']
export type MigrationOutcome = Schema['DeliveryMigrationOutcome']
export type MigrationIdentity = Schema['OutputRecipientIdentity']
export type MigrationQuota = Schema['DeliveryMigrationQuotaDeficit']
export type MigrationArea = 'pending' | 'failed' | 'quarantined'
export const migrationAreas: MigrationArea[] = [
    'pending',
    'failed',
    'quarantined',
]
const root = '/api/edge-admin/v1/deliveries/migrations' as const
export function migrationId(value: string) {
    if (
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
            value,
        ) ||
        value === '00000000-0000-0000-0000-000000000000'
    )
        throw new Error('migration-id')
    return value.toLowerCase()
}
function connection(value: string) {
    const id = connectionFilter(value)
    if (!id) throw new Error('migration-connection')
    return id
}
function count(value: number | string) {
    if (typeof value === 'number' && !Number.isSafeInteger(value))
        throw new Error('migration-count')
    const text = String(value)
    if (
        !/^(0|[1-9]\d*)$/.test(text) ||
        text.length > 19 ||
        BigInt(text) > 9223372036854775807n
    )
        throw new Error('migration-count')
    return BigInt(text)
}
function identity(value: MigrationIdentity) {
    if (
        !value ||
        !['http', 'mqtt'].includes(value.protocol) ||
        typeof value.address !== 'string' ||
        typeof value.authentication !== 'string' ||
        typeof value.accountRecorded !== 'boolean' ||
        typeof value.clientCertificateRecorded !== 'boolean' ||
        !(value.account === null || typeof value.account === 'string') ||
        !(
            value.clientCertificateSubject === null ||
            typeof value.clientCertificateSubject === 'string'
        ) ||
        (!value.accountRecorded && value.account !== null) ||
        (!value.clientCertificateRecorded &&
            value.clientCertificateSubject !== null)
    )
        throw new Error('migration-identity')
}
export function validateMigrationReview(value: MigrationReview) {
    migrationId(value.id)
    revisionId(value.revision)
    connection(String(value.sourceConnectionId))
    connection(String(value.targetConnectionId))
    identity(value.target)
    const total = count(value.count)
    if (
        total < 1n ||
        total > 1000n ||
        value.sourceConnectionId === value.targetConnectionId ||
        !Array.isArray(value.sources) ||
        value.sources.length < 1 ||
        value.sources.length > 1000 ||
        count(value.pending) +
            count(value.failed) +
            count(value.quarantined) !==
            total ||
        typeof value.canConfirm !== 'boolean' ||
        typeof value.quota?.exceeded !== 'boolean' ||
        ![value.expiresAt, value.firstCreatedAt, value.lastCreatedAt].every(
            (time) =>
                typeof time === 'string' && Number.isFinite(Date.parse(time)),
        )
    )
        throw new Error('migration-review')
    let sources = 0n
    for (const source of value.sources) {
        identity(source.recipient)
        if (
            source.recipient.protocol !== value.target.protocol ||
            count(source.count) < 1n
        )
            throw new Error('migration-protocol')
        sources += count(source.count)
    }
    const exceeded = [
        value.quota.pendingRecords,
        value.quota.pendingBytes,
        value.quota.globalBytes,
        value.quota.failedRecords,
    ]
        .map(count)
        .some((n) => n > 0n)
    if (
        sources !== total ||
        exceeded !== value.quota.exceeded ||
        value.canConfirm === exceeded
    )
        throw new Error('migration-review')
    return value
}
export function migrationSelection(
    sourceId: string,
    items: readonly DeliveryItem[],
) {
    const source = connection(sourceId)
    const selected = retrySelection(items)
    return selected.map((item, index): Schema['DeliveryMigrationKey'] => {
        const original = items[index]
        if (
            String(original.connectionId) !== source ||
            !migrationAreas.includes(original.state as MigrationArea) ||
            original.migrationId === undefined
        )
            throw new Error('migration-selection')
        return {
            ...item,
            migrationId:
                original.migrationId === null
                    ? null
                    : revisionId(original.migrationId),
        }
    })
}
export function migrationProblem(error: unknown) {
    if (
        error instanceof ApiError &&
        error.problem &&
        typeof error.problem === 'object' &&
        'code' in error.problem &&
        typeof error.problem.code === 'string'
    )
        return error.problem.code
    return 'unknown'
}
export function migrationQuota(error: unknown): MigrationQuota | null {
    if (
        !(error instanceof ApiError) ||
        !error.problem ||
        typeof error.problem !== 'object' ||
        !('migrationQuota' in error.problem)
    )
        return null
    const value = error.problem.migrationQuota
    if (!value || typeof value !== 'object') return null
    const values: Record<string, string> = {}
    for (const key of [
        'pendingRecords',
        'pendingBytes',
        'globalBytes',
        'failedRecords',
    ]) {
        const n = Reflect.get(value, key)
        if (typeof n !== 'number' && typeof n !== 'string') return null
        try {
            values[key] = count(n).toString()
        } catch {
            return null
        }
    }
    return {
        pendingRecords: values.pendingRecords,
        pendingBytes: values.pendingBytes,
        globalBytes: values.globalBytes,
        failedRecords: values.failedRecords,
    }
}
export function createDeliveryMigrationApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async records(
            source: string,
            state: MigrationArea,
            after?: string,
            signal?: AbortSignal,
        ) {
            if (!migrationAreas.includes(state))
                throw new Error('migration-area')
            const id = connection(source)
            const { data } = await api.GET(`${root}/records`, {
                params: {
                    query: {
                        connectionId: id as unknown as number,
                        state,
                        after:
                            after === undefined
                                ? undefined
                                : (revisionId(after) as unknown as number),
                        limit: 100,
                    },
                },
                parseAs: 'text',
                signal,
            })
            const page = decodeDiagnosticsJson<DeliveryPage>(data)
            if (!Array.isArray(page.items) || page.items.length > 100)
                throw new Error('migration-page')
            let previous = after ? BigInt(revisionId(after)) : 0n
            for (const item of page.items) {
                migrationSelection(id, [item])
                const seq = BigInt(revisionId(item.sequence))
                if (item.state !== state || seq <= previous)
                    throw new Error('migration-page')
                previous = seq
            }
            if (
                page.nextSequence !== null &&
                (page.items.length !== 100 ||
                    BigInt(revisionId(page.nextSequence)) !== previous)
            )
                throw new Error('migration-cursor')
            return page
        },
        async prepare(
            source: string,
            target: string,
            items: readonly DeliveryItem[],
        ) {
            if (connection(source) === connection(target))
                throw new Error('migration-same-target')
            const { data } = await api.POST(`${root}/prepare`, {
                body: {
                    sourceConnectionId: source,
                    targetConnectionId: target,
                    items: migrationSelection(source, items),
                },
                parseAs: 'text',
            })
            const review = validateMigrationReview(
                decodeDiagnosticsJson<MigrationReview>(data),
            )
            if (
                String(review.sourceConnectionId) !== source ||
                String(review.targetConnectionId) !== target ||
                count(review.count) !== BigInt(items.length)
            )
                throw new Error('migration-review-selection')
            return review
        },
        async current(signal?: AbortSignal) {
            const { data } = await api.GET(`${root}/preparation`, {
                parseAs: 'text',
                signal,
            })
            const current =
                decodeDiagnosticsJson<Schema['DeliveryMigrationCurrent']>(data)
            return current.preparation === null
                ? null
                : validateMigrationReview(current.preparation)
        },
        async cancel(id: string) {
            await api.DELETE(`${root}/preparation/{id}`, {
                params: { path: { id: migrationId(id) } },
            })
        },
        async confirm(id: string, sameOwnerConfirmed: boolean) {
            if (!sameOwnerConfirmed)
                throw new Error('migration-owner-confirmation')
            const { data } = await api.POST(`${root}/{id}/confirm`, {
                params: { path: { id: migrationId(id) } },
                body: { sameOwnerConfirmed: true },
                parseAs: 'text',
            })
            const result =
                decodeDiagnosticsJson<
                    Schema['DeliveryMigrationConfirmationResult']
                >(data)
            if (
                result.operationId !== id ||
                count(result.migratedRecords) > 1000n
            )
                throw new Error('migration-outcome-unknown')
            return result
        },
        async outcome(id: string, signal?: AbortSignal) {
            try {
                const { data } = await api.GET(`${root}/{id}`, {
                    params: { path: { id: migrationId(id) } },
                    parseAs: 'text',
                    signal,
                })
                const outcome = decodeDiagnosticsJson<MigrationOutcome>(data)
                if (
                    outcome.operationId !== id ||
                    ![
                        'started',
                        'completed',
                        'rejected',
                        'interrupted',
                    ].includes(outcome.status)
                )
                    throw new Error('migration-outcome')
                for (const n of [
                    outcome.requestedRecords,
                    outcome.affectedRecords,
                ])
                    if (n !== null) count(n)
                if (outcome.migrationId !== null)
                    revisionId(outcome.migrationId)
                if (
                    outcome.status === 'completed' &&
                    (outcome.affectedRecords === null ||
                        outcome.migrationId === null)
                )
                    throw new Error('migration-outcome')
                return outcome
            } catch (error) {
                if (error instanceof ApiError && error.status === 404)
                    return null // No durable start found is NOT evidence of failure.
                throw error
            }
        },
    }
}
