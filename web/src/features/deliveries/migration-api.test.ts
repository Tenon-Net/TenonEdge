import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { ApiError } from '../../api/errors'
import {
    createDeliveryMigrationApi,
    migrationSelection,
    migrationQuota,
    validateMigrationReview,
    validateMigrationIdentity,
    type MigrationReview,
} from './migration-api'
import type { DeliveryItem } from './api'

const id = '10000000-0000-4000-8000-000000000001'
const deliveryId = '20000000-0000-4000-8000-000000000001'
const source = '9007199254740991'
const seq = '9223372036854775806'
const time = '2026-10-14T00:00:00Z'
function item(): DeliveryItem {
    return {
        sequence: seq,
        deliveryId,
        connectionId: source,
        state: 'pending',
        migrationId: '9223372036854775805',
        deviceKey: 'meter',
        routeId: null,
        nodeLabel: null,
        revision: '1',
        createdAt: time,
        failedAt: null,
        failureCount: '0',
        quarantineReason: null,
        lastError: null,
        byteSize: '10',
        clockAnomaly: false,
    }
}
function review(): MigrationReview {
    return {
        id,
        revision: seq,
        sourceConnectionId: source,
        targetConnectionId: '12',
        targetKey: 'target',
        targetName: 'Target',
        target: {
            protocol: 'http',
            address: 'https://target.test/',
            authentication: 'Basic',
            account: 'new-account',
            accountRecorded: true,
            clientCertificateSubject: null,
            clientCertificateRecorded: true,
        },
        sources: [
            {
                count: 1,
                recipient: {
                    protocol: 'http',
                    address: 'http://old.test/',
                    authentication: 'Basic',
                    account: null,
                    accountRecorded: false,
                    clientCertificateSubject: null,
                    clientCertificateRecorded: false,
                },
            },
        ],
        count: 1,
        pending: 1,
        failed: 0,
        quarantined: 0,
        firstCreatedAt: time,
        lastCreatedAt: time,
        expiresAt: '2026-10-14T00:15:00Z',
        quota: {
            pendingRecords: 0,
            pendingBytes: 0,
            globalBytes: 0,
            failedRecords: 0,
            exceeded: false,
        },
        canConfirm: true,
    }
}
function client(fetcher: typeof fetch) {
    return createDeliveryMigrationApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'fixture-csrf',
            epoch: () => 1,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('accepts same-protocol extension preparation without relaxing identity or protocol comparisons', async () => {
    const prepared = review()
    prepared.target = {
        ...prepared.target,
        protocol: 'authwire',
        address: 'authwire://new.test:9000',
        authentication: 'module',
    }
    prepared.sources[0].recipient = {
        ...prepared.sources[0].recipient,
        protocol: 'authwire',
        address: 'authwire://old.test:9000',
        authentication: 'module',
    }
    const api = client(async () => new Response(JSON.stringify(prepared)))
    expect((await api.prepare(source, '12', [item()])).target.protocol).toBe(
        'authwire',
    )
    prepared.sources[0].recipient.protocol = 'otherwire'
    expect(() => validateMigrationReview(prepared)).toThrow(
        'migration-protocol',
    )
})
it.each([
    '',
    'HTTP',
    '1wire',
    'wire/x',
    'wire_name',
    'wire\n',
    'wire\0',
    '无线',
    'a'.repeat(65),
])('rejects invalid migration protocol %j', (protocol) => {
    expect(() =>
        validateMigrationIdentity({ ...review().target, protocol }),
    ).toThrow('migration-identity')
})
it('keeps exact Int64 cursors and migration generations without ever loading body or posting untrusted fingerprints', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        if (request.method === 'GET')
            return new Response(
                JSON.stringify({
                    capturedAt: time,
                    items: [item()],
                    nextSequence: null,
                }).replace(`"${seq}"`, seq),
            )
        expect(await request.json()).toEqual({
            sourceConnectionId: source,
            targetConnectionId: '12',
            items: [
                {
                    sequence: seq,
                    deliveryId,
                    migrationId: '9223372036854775805',
                },
            ],
        })
        expect(request.headers.get('X-CSRF-Token')).toBe('fixture-csrf')
        return new Response(JSON.stringify(review()))
    })
    const page = await api.records(source, 'pending', '9223372036854775804')
    expect(page.items[0].sequence).toBe(seq)
    expect(new URL(requests[0].url).searchParams.get('after')).toBe(
        '9223372036854775804',
    )
    const prepared = await api.prepare(source, '12', page.items)
    expect(prepared.sources[0].recipient.account).toBeNull()
    expect(prepared.sources[0].recipient.accountRecorded).toBe(false)
    expect(prepared.target.account).toBe('new-account')
    expect(requests).toHaveLength(2)
    expect(requests.some((r) => r.url.includes('/body'))).toBe(false)
})
it('has read-only current/outcome recovery and explicit cancellation/confirmation with the same-owner declaration', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        if (request.method === 'DELETE')
            return new Response(null, { status: 204 })
        if (request.method === 'POST') {
            expect(await request.json()).toEqual({ sameOwnerConfirmed: true })
            return new Response(
                JSON.stringify({ operationId: id, migratedRecords: 1 }),
            )
        }
        return new Response(
            JSON.stringify(
                request.url.endsWith('/preparation')
                    ? { preparation: review() }
                    : {
                          operationId: id,
                          status: 'completed',
                          requestedRecords: 1,
                          affectedRecords: 1,
                          migrationId: seq,
                          errorCode: null,
                      },
            ),
        )
    })
    await expect(api.confirm(id, false)).rejects.toThrow()
    expect(requests).toHaveLength(0)
    await api.current()
    await api.cancel(id)
    await api.confirm(id, true)
    expect((await api.outcome(id))?.migrationId).toBe(seq)
    expect(requests.map((r) => r.method)).toEqual([
        'GET',
        'DELETE',
        'POST',
        'GET',
    ])
})
it.each([400, 403, 409, 503])(
    'never retries confirmation HTTP %i or fabricates success',
    async (status) => {
        const fetcher = vi.fn(
            async () =>
                new Response('{"code":"delivery.migrationQuotaExceeded"}', {
                    status,
                }),
        )
        await expect(client(fetcher).confirm(id, true)).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('does not replay a lost reply, treats outcome404 as unknown rather than failure, and forwards read cancellation', async () => {
    const fetcher = vi.fn(async () => {
        throw new TypeError('lost reply')
    })
    await expect(client(fetcher).confirm(id, true)).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
    expect(
        await client(async () => new Response('{}', { status: 404 })).outcome(
            id,
        ),
    ).toBeNull()
    const controller = new AbortController()
    await expect(
        client(async (input) => {
            controller.abort()
            expect((input as Request).signal.aborted).toBe(true)
            throw new DOMException('cancelled', 'AbortError')
        }).records(source, 'failed', undefined, controller.signal),
    ).rejects.toThrow()
})
it('rejects empty/oversize/duplicate/other-source/stale-shape selections before any request', async () => {
    const rows = [
        [],
        Array.from({ length: 1001 }, () => item()),
        [item(), item()],
        [{ ...item(), connectionId: '11' }],
        [{ ...item(), migrationId: undefined }],
        [{ ...item(), state: 'invalid' }],
    ]
    const fetcher = vi.fn(async () => new Response('{}'))
    for (const entries of rows)
        await expect(
            client(fetcher).prepare(source, '12', entries as DeliveryItem[]),
        ).rejects.toThrow()
    await expect(
        client(fetcher).prepare(source, source, [item()]),
    ).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    expect(() =>
        migrationSelection(source, [
            { ...item(), sequence: 9223372036854776000 },
        ]),
    ).toThrow()
})
it.each(['pending', 'failed', 'quarantined'] as const)(
    'accepts explicitly selected %s without replacing its migration generation',
    (state) => {
        expect(migrationSelection(source, [{ ...item(), state }])).toEqual([
            { sequence: seq, deliveryId, migrationId: '9223372036854775805' },
        ])
    },
)
it('refuses a malformed or nonadvancing metadata page instead of enabling preparation', async () => {
    for (const page of [
        { items: [item()], nextSequence: seq },
        { items: [{ ...item(), state: 'failed' }], nextSequence: null },
        { items: [item(), item()], nextSequence: null },
        { items: [{ ...item(), connectionId: '12' }], nextSequence: null },
    ])
        await expect(
            client(async () => new Response(JSON.stringify(page))).records(
                source,
                'pending',
            ),
        ).rejects.toThrow()
})
it('requires complete, consistent identity groups and quota numbers, and never fills missing old account or certificate identity', () => {
    const original = review()
    expect(validateMigrationReview(original)).toBe(original)
    for (const broken of [
        { ...review(), count: 2 },
        { ...review(), sources: [] },
        { ...review(), sources: [{ ...review().sources[0], count: 2 }] },
        {
            ...review(),
            sources: [
                {
                    ...review().sources[0],
                    recipient: {
                        ...review().sources[0].recipient,
                        protocol: 'mqtt',
                    },
                },
            ],
        },
        {
            ...review(),
            sources: [
                {
                    ...review().sources[0],
                    recipient: {
                        ...review().sources[0].recipient,
                        account: 'guessed',
                    },
                },
            ],
        },
        { ...review(), quota: { ...review().quota, pendingBytes: -1 } },
        { ...review(), quota: { ...review().quota, pendingRecords: 1 } },
        { ...review(), canConfirm: false },
        { ...review(), expiresAt: 'invalid' },
    ])
        expect(() => validateMigrationReview(broken)).toThrow()
    const denied = {
        ...review(),
        quota: { ...review().quota, pendingRecords: 3, exceeded: true },
        canConfirm: false,
    }
    expect(validateMigrationReview(denied).canConfirm).toBe(false)
})
it('displays exact final quota deficits only when valid, without rounding a malformed unsafe number', () => {
    const problem = {
        migrationQuota: {
            pendingRecords: 2,
            pendingBytes: 42,
            globalBytes: '9223372036854775806',
            failedRecords: 0,
        },
    }
    expect(migrationQuota(new ApiError(409, problem))).toEqual({
        pendingRecords: '2',
        pendingBytes: '42',
        globalBytes: seq,
        failedRecords: '0',
    })
    expect(
        migrationQuota(
            new ApiError(409, {
                migrationQuota: {
                    ...problem.migrationQuota,
                    globalBytes: 9223372036854776000,
                },
            }),
        ),
    ).toBeNull()
    expect(migrationQuota(new ApiError(409, {}))).toBeNull()
})
it('does not accept a preparation for different targets or fabricate an incomplete completed receipt', async () => {
    await expect(
        client(
            async () =>
                new Response(
                    JSON.stringify({ ...review(), targetConnectionId: '13' }),
                ),
        ).prepare(source, '12', [item()]),
    ).rejects.toThrow()
    await expect(
        client(async () => new Response('{"preparation":null}')).current(),
    ).resolves.toBeNull()
    await expect(
        client(async () => new Response('{}')).current(),
    ).rejects.toThrow()
    await expect(
        client(
            async () =>
                new Response(
                    JSON.stringify({
                        operationId: id,
                        status: 'completed',
                        requestedRecords: 1,
                        affectedRecords: null,
                        migrationId: null,
                        errorCode: null,
                    }),
                ),
        ).outcome(id),
    ).rejects.toThrow()
})
