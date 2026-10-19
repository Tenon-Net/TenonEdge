import { describe, expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import type { MigrationIdentity } from '../deliveries/migration-api'
import {
    createConfigurationMigrationApi,
    decodeConfigurationMigration,
    validateIntentPage,
    type IntentInspection,
    type IntentPage,
} from './migration-api'

const id = '12345678-1234-4321-9876-123456789abc'
const intent = '12345678-1234-4321-9876-123456789def'
const source: MigrationIdentity = {
    protocol: 'http',
    address: 'http://old.test/',
    authentication: 'Basic',
    accountRecorded: false,
    account: null,
    clientCertificateRecorded: true,
    clientCertificateSubject: null,
}
const target: MigrationIdentity = {
    ...source,
    address: 'http://new.test/',
    accountRecorded: true,
    account: 'operator',
}
const quota = {
    pendingRecords: '0',
    pendingBytes: '0',
    globalBytes: '0',
    failedRecords: '0',
    exceeded: false,
}
function review() {
    return {
        id,
        expiresAt: '2026-10-15T00:00:00Z',
        canConfirm: true,
        configuration: {
            version: 'draft',
            effectiveRevision: '9223372036854775806',
            targetRevision: null,
            comparisonAvailable: true,
            changes: [
                {
                    kind: 'route',
                    id: '1',
                    key: 'route',
                    change: 'modified',
                    before: null,
                    after: { value: 'EXACT' },
                },
            ],
            points: [],
            routeIssues: [],
            targetBlocks: [{ connectionId: '7' }],
            warnings: [],
            preflightPassed: false,
            planHash: 'hash',
        },
        targets: [
            {
                intentId: intent,
                connectionId: '7',
                key: 'out',
                name: 'Output',
                source,
                target,
                count: '2',
                quota: { ...quota },
            },
        ],
    }
}
function inspection(): IntentInspection {
    return {
        intentId: intent,
        status: 'rejected',
        effectiveRevision: '9223372036854775807',
        source,
        approvedTarget: target,
        currentTarget: target,
        targetMatchesConfirmation: true,
        remainingRecords: '0',
        currentQuota: { ...quota },
        problem: null,
        retryProblem: null,
        canPrepareRetry: true,
    }
}
function page(): IntentPage {
    return {
        items: [
            {
                id: intent,
                revision: '9223372036854775807',
                sourceRevision: '9223372036854775806',
                connectionId: '7',
                status: 'rejected',
                registeredBy: 'admin',
                registeredAt: '1790000000000',
                lastOperationId: id,
                hasConfirmedTarget: true,
            },
        ],
        next: null,
    }
}
function fixture(body: unknown, status = 200) {
    const sent: Request[] = []
    const fetcher = vi.fn(async (request: RequestInfo | URL) => {
        sent.push((request as Request).clone())
        return new Response(
            typeof body === 'string' ? body : JSON.stringify(body),
            { status, headers: { 'Content-Type': 'application/json' } },
        )
    })
    return {
        api: createConfigurationMigrationApi(
            createGatewayClient({
                baseUrl: 'http://gateway.test',
                csrf: () => 'csrf',
                epoch: () => 0,
                unauthorized: vi.fn(),
                fetcher,
            }),
        ),
        sent,
        fetcher,
    }
}
describe('configuration migration reviews and explicit intent retry', () => {
    it('accepts an extension protocol in the real form-A review envelope', () => {
        const value = review()
        value.targets[0] = {
            ...value.targets[0],
            source: {
                ...source,
                protocol: 'authwire',
                address: 'authwire://old.test:9000',
                authentication: 'module',
            },
            target: {
                ...target,
                protocol: 'authwire',
                address: 'authwire://new.test:9000',
                authentication: 'module',
            },
        }
        const parsed = decodeConfigurationMigration(JSON.stringify(value))
        expect(parsed.canConfirm).toBe(true)
        expect(parsed.targets[0].target.protocol).toBe('authwire')
        expect(parsed.targets[0].source.accountRecorded).toBe(false)
        expect(parsed.targets[0].source.account).toBeNull()
    })
    it('keeps embedded configuration JSON numeric lexemes and Int64 metadata exact', () => {
        const body = JSON.stringify(review())
            .replace('"EXACT"', '9223372036854775807')
            .replace('"9223372036854775806"', '9223372036854775806')
        const parsed = decodeConfigurationMigration(body)
        expect(parsed.configuration.effectiveRevision).toBe(
            '9223372036854775806',
        )
        expect(parsed.configuration.changes[0].afterText).toBe(
            '{"value":9223372036854775807}',
        )
        expect(parsed.configuration.preflightPassed).toBe(false)
        expect(parsed.canConfirm).toBe(true)
        expect(parsed.targets[0].source.accountRecorded).toBe(false)
        expect(parsed.targets[0].source.account).toBeNull()
    })
    it.each([
        'duplicates',
        'unselected',
        'wrongProtocol',
        'sameAddress',
        'unknownTarget',
        'quotaLie',
        'canConfirmLie',
        'invalidCount',
        'rollback',
        'routeIssue',
        'noComparison',
        'expires',
    ])('rejects malformed or contradictory form-A review: %s', (kind) => {
        const value = review()
        switch (kind) {
            case 'duplicates':
                value.targets.push(value.targets[0])
                break
            case 'unselected':
                value.targets[0].connectionId = '8'
                break
            case 'wrongProtocol':
                value.targets[0] = {
                    ...value.targets[0],
                    target: { ...target, protocol: 'mqtt' },
                }
                break
            case 'sameAddress':
                value.targets[0] = { ...value.targets[0], target: source }
                break
            case 'unknownTarget':
                value.targets[0] = {
                    ...value.targets[0],
                    target: {
                        ...target,
                        accountRecorded: false,
                        account: null,
                    },
                }
                break
            case 'quotaLie':
                value.targets[0].quota.globalBytes = '1'
                break
            case 'canConfirmLie':
                value.canConfirm = false
                break
            case 'invalidCount':
                value.targets[0].count = '-1'
                break
            case 'rollback':
                Object.assign(value.configuration, { targetRevision: '1' })
                break
            case 'routeIssue':
                Object.assign(value.configuration, { routeIssues: [{}] })
                break
            case 'noComparison':
                value.configuration.comparisonAvailable = false
                break
            case 'expires':
                value.expiresAt = 'unknown'
                break
        }
        expect(() =>
            decodeConfigurationMigration(JSON.stringify(value)),
        ).toThrow()
    })
    it('shows all quota deficits and disables an otherwise valid preparation', () => {
        const value = review()
        value.canConfirm = false
        value.targets[0].quota = {
            pendingRecords: '1',
            pendingBytes: '2',
            globalBytes: '9223372036854775807',
            failedRecords: '4',
            exceeded: true,
        }
        expect(
            decodeConfigurationMigration(JSON.stringify(value)).targets[0]
                .quota,
        ).toEqual(value.targets[0].quota)
    })
    it('prepares only distinct selected safe connection IDs and verifies the returned selection', async () => {
        const { api, sent } = fixture(review())
        for (const selection of [
            [],
            ['7', '7'],
            ['9007199254740992'],
            Array.from({ length: 501 }, (_, n) => String(n + 1)),
        ])
            await expect(api.prepare('draft', selection)).rejects.toThrow()
        expect(sent).toHaveLength(0)
        await api.prepare('draft', ['7'])
        expect(await sent[0].json()).toEqual({
            expectedVersion: 'draft',
            connectionIds: ['7'],
        })
        expect(sent[0].headers.get('X-CSRF-Token')).toBe('csrf')
        await expect(api.prepare('draft', ['8'])).rejects.toThrow(
            'migration-review-selection',
        )
    })
    it('recovers a preparation without granting declarations; null and malformed are distinct', async () => {
        expect(
            await fixture({ preparation: review() }).api.current(),
        ).toMatchObject({ id })
        expect(await fixture({ preparation: null }).api.current()).toBeNull()
        await expect(fixture({}).api.current()).rejects.toThrow()
    })
    it.each([
        [false, true],
        [true, false],
        [false, false],
    ])(
        'requires both form-A and retry declarations (%s, %s)',
        async (confirmed, owner) => {
            const { api, fetcher } = fixture({})
            await expect(api.confirm(id, confirmed, owner, '')).rejects.toThrow(
                'migration-confirmation',
            )
            await expect(
                api.confirmRetry(id, confirmed, owner),
            ).rejects.toThrow('migration-confirmation')
            expect(fetcher).not.toHaveBeenCalled()
        },
    )
    it('confirms once and verifies the known operation ID rather than guessing a new one', async () => {
        const { api, sent } = fixture(
            { id, revision: '9223372036854775807' },
            202,
        )
        expect(await api.confirm(id, true, true, 'approved')).toMatchObject({
            id,
            revision: '9223372036854775807',
        })
        expect(sent).toHaveLength(1)
        expect(await sent[0].json()).toEqual({
            confirmed: true,
            sameOwnerConfirmed: true,
            changeDescription: 'approved',
        })
        await expect(
            fixture({ id: intent }).api.confirm(id, true, true, ''),
        ).rejects.toThrow()
    })
    it('does not replay failed confirmation and preserves the durable lookup path', async () => {
        const { api, sent } = fixture(
            { code: 'configuration.migrationReviewChanged' },
            409,
        )
        await expect(api.confirm(id, true, true, '')).rejects.toThrow()
        expect(sent).toHaveLength(1)
        expect(new URL(sent[0].url).pathname).toBe(
            `/api/edge-admin/v1/configuration/migrations/${id}/confirm`,
        )
    })
    it('lists using paired exact cursors, filters, and bounded metadata only', async () => {
        const { api, sent } = fixture(page())
        await api.intents(
            { status: 'rejected', connectionId: '7' },
            { revision: '9223372036854775806', connectionId: '7' },
        )
        const url = new URL(sent[0].url)
        expect(Object.fromEntries(url.searchParams)).toEqual({
            status: 'rejected',
            connectionId: '7',
            afterRevision: '9223372036854775806',
            afterConnection: '7',
            limit: '50',
        })
        await expect(api.intents({ status: 'started' })).rejects.toThrow(
            'migration-filter',
        )
    })
    it.each(['order', 'filter', 'cursor', 'status', 'duplicate'])(
        'rejects inconsistent intent page: %s',
        (kind) => {
            const value = page()
            if (kind === 'status') value.items[0].status = 'started'
            if (kind === 'cursor')
                value.next = { revision: '1', connectionId: '7' }
            if (kind === 'duplicate') value.items.push(value.items[0])
            expect(() =>
                validateIntentPage(
                    value,
                    kind === 'filter' ? { connectionId: '8' } : {},
                    kind === 'order'
                        ? { revision: '9223372036854775807', connectionId: '7' }
                        : undefined,
                ),
            ).toThrow()
        },
    )
    it('inspects remaining zero records but forbids contradictory retryable unknown-result states', async () => {
        expect(await fixture(inspection()).api.inspect(intent)).toMatchObject({
            remainingRecords: '0',
            canPrepareRetry: true,
        })
        await expect(
            fixture({
                ...inspection(),
                retryProblem: 'configuration.migrationIntentOutcomeUnknown',
            }).api.inspect(intent),
        ).rejects.toThrow()
        expect(
            await fixture({
                ...inspection(),
                canPrepareRetry: false,
                retryProblem: 'configuration.migrationIntentOutcomeUnknown',
            }).api.inspect(intent),
        ).toMatchObject({ canPrepareRetry: false })
        await expect(fixture(inspection()).api.inspect(id)).rejects.toThrow(
            'migration-intent',
        )
    })
    it('binds retry preparation to the requested intent and its server preflight', async () => {
        const value = {
            id,
            inspection: inspection(),
            canConfirm: true,
            expiresAt: '2026-10-15T00:00:00Z',
        }
        expect(await fixture(value).api.prepareRetry(intent)).toEqual(value)
        await expect(fixture(value).api.prepareRetry(id)).rejects.toThrow(
            'migration-intent',
        )
        expect(
            await fixture({ preparation: value }).api.currentRetry(),
        ).toEqual(value)
        expect(
            await fixture({ preparation: null }).api.currentRetry(),
        ).toBeNull()
        await expect(
            fixture({ ...value, canConfirm: false }).api.prepareRetry(intent),
        ).rejects.toThrow()
    })
    it('accepts retries larger than form-B selection limits and zero-record completion without rounding', async () => {
        for (const migratedRecords of ['0', '2000']) {
            const { api, sent } = fixture({ operationId: id, migratedRecords })
            expect(await api.confirmRetry(id, true, true)).toEqual({
                operationId: id,
                migratedRecords,
            })
            expect(await sent[0].json()).toEqual({
                confirmed: true,
                sameOwnerConfirmed: true,
            })
        }
        await expect(
            fixture({
                operationId: intent,
                migratedRecords: '0',
            }).api.confirmRetry(id, true, true),
        ).rejects.toThrow()
    })
    it('cancels only the named preparation, never the background operation', async () => {
        const { api, sent } = fixture({})
        await api.cancel(id)
        await api.cancelRetry(id)
        expect(sent.map((r) => [r.method, new URL(r.url).pathname])).toEqual([
            [
                'DELETE',
                `/api/edge-admin/v1/configuration/migrations/preparation/${id}`,
            ],
            [
                'DELETE',
                `/api/edge-admin/v1/configuration/migrations/intent-retry/preparation/${id}`,
            ],
        ])
    })
})
