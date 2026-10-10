import { describe, expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { ApiError } from '../../api/errors'
import {
    applicationRequest,
    fieldChanges,
    createConfigurationApi,
    decodeReview,
    decodeVersion,
    decodeComparison,
    revisionId,
    operationOutcome,
    reviewIsCurrent,
    type DraftSummary,
    type Operation,
    type RuntimeState,
} from './api'

const hash = 'sha256:' + 'a'.repeat(64)
const reviewBody = `{"version":"draft-1","effectiveRevision":null,"comparisonAvailable":true,"changes":[{"kind":"route","id":42,"key":"route","deviceId":null,"change":"added","before":null,"after":{"constant":9223372036854775807,"decimal":0.1234567890123456789012345678,"scientific":1e-20},"updatedBy":null,"updatedAt":null}],"points":[],"routeIssues":[],"targetBlocks":[],"planHash":"${hash}","warnings":[],"preflightPassed":true}`
const summary: DraftSummary = {
    version: 'draft-1',
    effectiveRevision: null,
    pendingRevision: null,
    comparisonAvailable: true,
    changeCount: 1,
    objects: [],
    points: [],
    warnings: [],
}
const operation: Operation = {
    id: 'a1a86620-3a11-4931-a431-182349919559',
    kind: 'apply',
    account: 'operator',
    draftVersion: 'draft-1',
    targetRevision: null,
    revision: null,
    stage: 'validating',
    startedAt: '2026-10-10T00:00:00Z',
    finishedAt: null,
    committed: false,
    result: null,
    errorCode: null,
}
function fixture(fetcher: typeof fetch) {
    let epoch = 0
    const unauthorized = vi.fn()
    return {
        api: createConfigurationApi(
            createGatewayClient({
                baseUrl: 'http://gateway.test',
                csrf: () => 'fixture-csrf',
                epoch: () => epoch,
                unauthorized,
                fetcher,
            }),
        ),
        advance: () => epoch++,
        unauthorized,
    }
}

describe('configuration confirmation and owned operations', () => {
    it('retries only an explicitly confirmed failed effective checkpoint and keeps large revision metadata exact', async () => {
        const state: RuntimeState = {
            state: 'runFailed',
            reason: 'Startup protection',
            effectiveRevision: '9223372036854775807',
            pendingRevision: null,
            planHash: hash,
            canRetry: true,
            errorCode: null,
        }
        const sent: Request[] = []
        const fetcher = vi.fn(async (input: RequestInfo | URL) => {
            const request =
                input instanceof Request ? input : new Request(input)
            sent.push(request.clone())
            return new Response(
                JSON.stringify(
                    request.method === 'GET'
                        ? state
                        : { ...operation, kind: 'retry' },
                ),
                { status: request.method === 'GET' ? 200 : 202 },
            )
        })
        const { api } = fixture(fetcher)
        const read = await api.runtime()
        expect(read.effectiveRevision).toBe('9223372036854775807')
        for (const invalid of [
            { ...read, canRetry: false },
            { ...read, state: 'active' },
            { ...read, planHash: null },
            { ...read, effectiveRevision: null },
        ] as RuntimeState[])
            await expect(api.retry(invalid, true)).rejects.toThrow()
        await expect(api.retry(read, false)).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
        const admitted = await api.retry(read, true)
        expect(admitted.id).toBe(operation.id)
        expect(await sent[1].json()).toEqual({
            expectedEffectiveRevision: '9223372036854775807',
            expectedPlanHash: hash,
            confirmed: true,
        })
        expect(sent[1].headers.get('X-CSRF-Token')).toBe('fixture-csrf')
        expect(sent[1].url).toMatch(/configuration\/runtime\/retry$/)
    })
    it('does not replay runtime retries after response loss and describes retry outcomes without claiming a new configuration commit', async () => {
        const fetcher = vi.fn(async () => {
            throw new Error('lost-retry-response')
        })
        const { api } = fixture(fetcher)
        await expect(
            api.retry(
                {
                    state: 'runFailed',
                    reason: null,
                    effectiveRevision: 1,
                    pendingRevision: null,
                    planHash: hash,
                    canRetry: true,
                    errorCode: null,
                },
                true,
            ),
        ).rejects.toThrow('lost-retry-response')
        expect(fetcher).toHaveBeenCalledTimes(1)
        const retry = {
            ...operation,
            kind: 'retry',
            finishedAt: operation.startedAt,
            committed: false,
        }
        expect(operationOutcome(retry)).toBe('retryFailed')
        expect(
            operationOutcome({
                ...retry,
                result: {
                    revision: 1,
                    committed: false,
                    activated: true,
                    code: 'runtime.retried',
                    recoveryPending: false,
                },
            }),
        ).toBe('retrySuccessful')
    })
    it('keeps large integers, decimals and exponent tokens exact in change details', () => {
        const review = decodeReview(reviewBody)
        expect(review.changes[0].afterText).toBe(
            '{"constant":9223372036854775807,"decimal":0.1234567890123456789012345678,"scientific":1e-20}',
        )
        expect(review.changes[0].beforeText).toBe('null')
        expect(review.changes[0]).not.toHaveProperty('after')
        expect(review.changes[0].updatedBy).toBeNull()
    })
    it('rejects malformed or duplicate-key review bodies', () => {
        expect(() => decodeReview('{"changes":{},"version":"v1"}')).toThrow()
        expect(() =>
            decodeReview(
                reviewBody.replace(
                    '"constant":9223372036854775807',
                    '"constant":1,"constant":2',
                ),
            ),
        ).toThrow()
    })
    it('requires explicit confirmation and a passed preflight without submitting the reviewed configuration itself', () => {
        const review = decodeReview(reviewBody)
        expect(() => applicationRequest(review, '', false)).toThrow()
        expect(() =>
            applicationRequest({ ...review, preflightPassed: false }, '', true),
        ).toThrow()
        expect(() =>
            applicationRequest(
                { ...review, comparisonAvailable: false },
                '',
                true,
            ),
        ).toThrow()
        expect(() =>
            applicationRequest({ ...review, planHash: null }, '', true),
        ).toThrow()
        expect(() =>
            applicationRequest(review, 'a'.repeat(1001), true),
        ).toThrow()
        expect(applicationRequest(review, 'Explanation', true)).toEqual({
            expectedVersion: 'draft-1',
            expectedEffectiveRevision: null,
            expectedPlanHash: hash,
            confirmed: true,
            changeDescription: 'Explanation',
        })
    })
    it('invalidates confirmation when either the draft or effective version changes', () => {
        const review = decodeReview(reviewBody)
        expect(reviewIsCurrent(review, summary)).toBe(true)
        expect(
            reviewIsCurrent(review, { ...summary, version: 'draft-2' }),
        ).toBe(false)
        expect(
            reviewIsCurrent(review, { ...summary, effectiveRevision: 1 }),
        ).toBe(false)
        expect(
            reviewIsCurrent(
                { ...review, effectiveRevision: '1' },
                { ...summary, effectiveRevision: 1 },
            ),
        ).toBe(true)
    })
    it('does not confuse accepted, committed, activated and unknown outcomes', () => {
        expect(operationOutcome(operation)).toBe('running')
        const finished = { ...operation, finishedAt: operation.startedAt }
        expect(operationOutcome(finished)).toBe('notCommitted')
        expect(operationOutcome({ ...finished, committed: null })).toBe(
            'unknown',
        )
        expect(operationOutcome({ ...finished, committed: true })).toBe(
            'committedFault',
        )
        expect(
            operationOutcome({
                ...finished,
                committed: true,
                result: {
                    revision: 1,
                    committed: true,
                    activated: true,
                    code: 'ok',
                    recoveryPending: false,
                },
            }),
        ).toBe('successful')
    })
    it('uses real OpenAPI transport, CSRF and exact admitted operation ID without another write', async () => {
        const requests: {
            path: string
            method: string
            body: string
            csrf: string | null
        }[] = []
        const f = fixture(async (input) => {
            const request = new Request(input)
            const path = new URL(request.url).pathname
            requests.push({
                path,
                method: request.method,
                body: await request.text(),
                csrf: request.headers.get('X-CSRF-Token'),
            })
            return new Response(
                path.endsWith('/review')
                    ? reviewBody
                    : JSON.stringify(operation),
                { status: path.endsWith('/apply') ? 202 : 200 },
            )
        })
        const review = await f.api.review('draft-1')
        const accepted = await f.api.apply(review, 'Fixture', true)
        await f.api.operation(accepted.id)
        expect(requests.map((item) => item.method)).toEqual([
            'POST',
            'POST',
            'GET',
        ])
        expect(requests[1].csrf).toBe('fixture-csrf')
        expect(JSON.parse(requests[1].body)).toEqual(
            applicationRequest(review, 'Fixture', true),
        )
        expect(requests[2].path).toBe(
            '/api/edge-admin/v1/configuration/operations/' + operation.id,
        )
    })
    it('does not replay a failed application and retains safe busy details', async () => {
        const fetcher = vi.fn(
            async () =>
                new Response(
                    JSON.stringify({
                        code: 'runtime.applicationInProgress',
                        operationId: operation.id,
                        account: 'other',
                    }),
                    { status: 409 },
                ),
        )
        const f = fixture(fetcher)
        await expect(
            f.api.apply(decodeReview(reviewBody), '', true),
        ).rejects.toMatchObject({
            status: 409,
            problem: { operationId: operation.id, account: 'other' },
        })
        expect(fetcher).toHaveBeenCalledTimes(1)
    })
    it('does not replay a response-lost write', async () => {
        const fetcher = vi.fn(async () => {
            throw new TypeError('network')
        })
        await expect(
            fixture(fetcher).api.apply(decodeReview(reviewBody), '', true),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    })
    it('rejects late review responses after account replacement', async () => {
        let release!: (response: Response) => void
        const f = fixture(
            () =>
                new Promise<Response>((resolve) => {
                    release = resolve
                }),
        )
        const pending = f.api.review('draft-1')
        await vi.waitFor(() => expect(release).toBeTypeOf('function'))
        f.advance()
        release(new Response(reviewBody))
        await expect(pending).rejects.toThrow('stale-session-response')
    })
    it('requires explicit safe discard and does not let a failed discard mutate the supplied summary', async () => {
        const fetcher = vi.fn(async () => new Response('{}', { status: 409 }))
        const f = fixture(fetcher)
        await expect(f.api.discard(summary, false)).rejects.toThrow()
        await expect(
            f.api.discard({ ...summary, comparisonAvailable: false }, true),
        ).rejects.toThrow()
        expect(fetcher).not.toHaveBeenCalled()
        await expect(f.api.discard(summary, true)).rejects.toBeInstanceOf(
            ApiError,
        )
        expect(fetcher).toHaveBeenCalledTimes(1)
        expect(summary.version).toBe('draft-1')
    })
})

describe('field-level differences', () => {
    it('shows precise changed field paths, missing values and array positions', () => {
        expect(
            fieldChanges(
                '{"a/b":1,"same":true,"array":[1],"removed":null}',
                '{"a/b":1.0,"same":true,"array":[9223372036854775807],"added":false}',
            ),
        ).toEqual({
            truncated: false,
            items: [
                { path: '/a~1b', before: '1', after: '1.0' },
                { path: '/array/0', before: '1', after: '9223372036854775807' },
                { path: '/removed', before: 'null', after: '—' },
                { path: '/added', before: '—', after: 'false' },
            ],
        })
    })
    it('reports truncation rather than silently treating a partial field list as complete', () => {
        expect(fieldChanges('[1,2,3]', '[4,5,6]', 2)).toEqual({
            truncated: true,
            items: [
                { path: '/0', before: '1', after: '4' },
                { path: '/1', before: '2', after: '5' },
            ],
        })
        expect(fieldChanges('[1,2]', '[4,5]', 2).truncated).toBe(false)
    })
    it('does not compare prototype properties or flag property-order-only changes', () => {
        expect(fieldChanges('{"x":1,"y":2}', '{"y":2,"x":1}').items).toEqual([])
        expect(fieldChanges('{"__proto__":1}', '{}').items).toEqual([
            { path: '/__proto__', before: '1', after: '—' },
        ])
    })
})

const largeRevision = '9223372036854775807'
const versionMetadata = {
    revision: largeRevision,
    schemaVersion: 2,
    state: 'history',
    origin: 'apply',
    originDetail: null,
    createdBy: 'operator',
    createdAt: 1791590400000,
    activatedAt: 1791590401000,
    isStable: true,
    notEffectiveReason: null,
    changeDescription: 'Exact retained version',
}
const versionBody = JSON.stringify({
    version: versionMetadata,
    contentHash: 'sha256:' + 'b'.repeat(64),
    planHash: hash,
    draft: { constant: 'EXACT' },
    plan: { constant: 'EXACT' },
    warnings: [],
}).replaceAll('"EXACT"', '9223372036854775807')
const comparisonBody =
    '{"beforeRevision":9223372036854775807,"afterRevision":2,"draftVersion":null,"changes":' +
    reviewBody.slice(
        reviewBody.indexOf('"changes":') + 10,
        reviewBody.indexOf(',"points":'),
    ) +
    ',"points":[]}'
const rollbackReviewBody = reviewBody.replace(
    '"effectiveRevision":null',
    '"effectiveRevision":2,"targetRevision":9223372036854775807',
)

describe('persistent history and confirmed rollback', () => {
    it('validates canonical positive Int64 revisions without rounding or accepting alternate encodings', () => {
        expect(revisionId(1)).toBe('1')
        expect(revisionId(largeRevision)).toBe(largeRevision)
        for (const invalid of [
            0,
            -1,
            1.2,
            Number(largeRevision),
            '',
            '01',
            '1e2',
            '1.0',
            '+1',
            ' 1',
            '9223372036854775808',
        ]) {
            expect(() => revisionId(invalid)).toThrow('configuration-revision')
        }
    })
    it('preserves exact full checkpoint and compiled plan values while keeping their separate hashes', () => {
        const detail = decodeVersion(versionBody)
        expect(detail.version.revision).toBe(largeRevision)
        expect(detail.draftText).toBe('{"constant":9223372036854775807}')
        expect(detail.planText).toBe('{"constant":9223372036854775807}')
        expect(detail.contentHash).toBe('sha256:' + 'b'.repeat(64))
        expect(detail.planHash).toBe(hash)
        expect(detail.version.changeDescription).toBe('Exact retained version')
        const legacy = decodeVersion(
            versionBody
                .replace('"schemaVersion":2', '"schemaVersion":1')
                .replace(
                    '"draft":{"constant":9223372036854775807}',
                    '"draft":null',
                )
                .replace(
                    '"warnings":[]',
                    '"warnings":["configuration.legacyComparisonUnavailable"]',
                ),
        )
        expect(legacy.draftText).toBe('null')
        expect(legacy.warnings).toEqual([
            'configuration.legacyComparisonUnavailable',
        ])
    })
    it('rejects malformed history contents and unavailable full objects instead of inventing them', () => {
        expect(() =>
            decodeVersion(versionBody.replace('"plan":', '"other":')),
        ).toThrow()
        expect(() =>
            decodeVersion(
                versionBody.replace(
                    '"schemaVersion":2',
                    '"schemaVersion":null',
                ),
            ),
        ).toThrow()
        expect(() =>
            decodeVersion(
                versionBody.replace('"warnings":[]', '"warnings":[1]'),
            ),
        ).toThrow()
        expect(() =>
            decodeVersion(
                versionBody.replace(
                    '"constant":9223372036854775807',
                    '"constant":1,"constant":2',
                ),
            ),
        ).toThrow()
        expect(() =>
            decodeComparison(
                '{"beforeRevision":1,"afterRevision":2,"changes":[],"points":{}}',
            ),
        ).toThrow()
    })
    it('decodes comparison as read-only content rather than a fabricated preflight', () => {
        const comparison = decodeComparison(comparisonBody)
        expect(comparison.beforeRevision).toBe(largeRevision)
        expect(comparison.afterRevision).toBe('2')
        expect(comparison.changes[0].afterText).toContain(
            '"constant":9223372036854775807',
        )
        expect(comparison).not.toHaveProperty('preflightPassed')
        expect(comparison).not.toHaveProperty('version')
    })
    it('uses exact cursor and revision strings in real OpenAPI transport without loading contents in the list', async () => {
        const requests: string[] = []
        const f = fixture(async (input) => {
            const url = new URL(new Request(input).url)
            requests.push(url.pathname + url.search)
            return new Response(
                url.pathname.endsWith('/versions')
                    ? JSON.stringify([versionMetadata])
                    : versionBody,
            )
        })
        const rows = await f.api.versions(largeRevision)
        expect(rows[0].revision).toBe(largeRevision)
        await f.api.version(largeRevision)
        expect(requests).toEqual([
            '/api/edge-admin/v1/configuration/versions?beforeRevision=9223372036854775807&limit=20',
            '/api/edge-admin/v1/configuration/versions/9223372036854775807',
        ])
    })
    it('binds comparison results to both requested versions or the precise draft version', async () => {
        const requests: string[] = []
        const f = fixture(async (input) => {
            const url = new URL(new Request(input).url)
            requests.push(url.pathname + url.search)
            return new Response(
                url.pathname.endsWith('/compare-draft')
                    ? comparisonBody.replace(
                          '"afterRevision":2,"draftVersion":null',
                          '"afterRevision":null,"draftVersion":"draft-1"',
                      )
                    : comparisonBody,
            )
        })
        await f.api.compare(largeRevision, 2)
        await f.api.compareDraft(largeRevision, 'draft-1')
        expect(requests).toEqual([
            '/api/edge-admin/v1/configuration/versions/9223372036854775807/compare/2',
            '/api/edge-admin/v1/configuration/versions/9223372036854775807/compare-draft?expectedVersion=draft-1',
        ])
    })
    it.each(['detail', 'comparison', 'draft', 'review'] as const)(
        'rejects a mismatched %s response instead of labelling it as the selected version',
        async (kind) => {
            const f = fixture(
                async () =>
                    new Response(
                        kind === 'detail'
                            ? versionBody
                            : kind === 'review'
                              ? rollbackReviewBody
                              : comparisonBody,
                    ),
            )
            const call =
                kind === 'detail'
                    ? f.api.version(1)
                    : kind === 'comparison'
                      ? f.api.compare(1, 2)
                      : kind === 'draft'
                        ? f.api.compareDraft(largeRevision, 'draft-1')
                        : f.api.reviewRollback(1, 'draft-1')
            await expect(call).rejects.toThrow('configuration-response')
        },
    )
    it('freezes the reviewed target and routes the explicit confirmed request to rollback, with CSRF and the returned operation ID', async () => {
        const requests: { path: string; body: unknown; csrf: string | null }[] =
            []
        const f = fixture(async (input) => {
            const request = new Request(input)
            const path = new URL(request.url).pathname
            requests.push({
                path,
                body: JSON.parse(await request.text()),
                csrf: request.headers.get('X-CSRF-Token'),
            })
            return new Response(
                path.endsWith('/review')
                    ? rollbackReviewBody
                    : JSON.stringify({ ...operation, kind: 'rollback' }),
                { status: path.endsWith('/rollback') ? 202 : 200 },
            )
        })
        const review = await f.api.reviewRollback(largeRevision, 'draft-1')
        const accepted = await f.api.rollback(
            review,
            'Restore retained configuration',
            true,
        )
        expect(accepted.id).toBe(operation.id)
        expect(requests).toEqual([
            {
                path: '/api/edge-admin/v1/configuration/versions/9223372036854775807/review',
                body: { expectedVersion: 'draft-1' },
                csrf: 'fixture-csrf',
            },
            {
                path: '/api/edge-admin/v1/configuration/versions/9223372036854775807/rollback',
                body: {
                    expectedVersion: 'draft-1',
                    expectedEffectiveRevision: '2',
                    expectedPlanHash: hash,
                    confirmed: true,
                    changeDescription: 'Restore retained configuration',
                },
                csrf: 'fixture-csrf',
            },
        ])
    })
    it('cannot apply a rollback review or rollback a draft review, or write without explicit confirmation', async () => {
        const fetcher = vi.fn(async () => new Response('{}'))
        const f = fixture(fetcher)
        const rollback = decodeReview(rollbackReviewBody)
        await expect(f.api.apply(rollback, '', true)).rejects.toThrow(
            'configuration-wrong-operation',
        )
        await expect(
            f.api.rollback(decodeReview(reviewBody), '', true),
        ).rejects.toThrow('configuration-wrong-operation')
        await expect(f.api.rollback(rollback, '', false)).rejects.toThrow(
            'configuration-confirmation',
        )
        expect(fetcher).not.toHaveBeenCalled()
    })
    it.each(['network', 'conflict'] as const)(
        'never replays a rollback after %s failure',
        async (kind) => {
            const fetcher = vi.fn(async () => {
                if (kind === 'network') throw new TypeError('response-lost')
                return new Response(
                    '{"code":"configuration.rollbackCredentialMissing"}',
                    { status: 409 },
                )
            })
            await expect(
                fixture(fetcher).api.rollback(
                    decodeReview(rollbackReviewBody),
                    '',
                    true,
                ),
            ).rejects.toThrow()
            expect(fetcher).toHaveBeenCalledTimes(1)
        },
    )
    it('rejects a late retained-version response after session replacement', async () => {
        let release!: (response: Response) => void
        const f = fixture(
            () =>
                new Promise<Response>((resolve) => {
                    release = resolve
                }),
        )
        const pending = f.api.version(largeRevision)
        await vi.waitFor(() => expect(release).toBeTypeOf('function'))
        f.advance()
        release(new Response(versionBody))
        await expect(pending).rejects.toThrow('stale-session-response')
    })
    it('keeps summary, review and owned-operation revisions exact as well as configuration values', async () => {
        const f = fixture(async (input) => {
            const path = new URL(new Request(input).url).pathname
            return new Response(
                path.endsWith('/draft')
                    ? JSON.stringify(summary).replace(
                          '"effectiveRevision":null',
                          '"effectiveRevision":9223372036854775807',
                      )
                    : JSON.stringify(operation).replace(
                          '"revision":null',
                          '"revision":9223372036854775807',
                      ),
            )
        })
        const current = await f.api.summary()
        expect(current.effectiveRevision).toBe(largeRevision)
        expect(
            reviewIsCurrent(
                decodeReview(
                    reviewBody.replace(
                        '"effectiveRevision":null',
                        '"effectiveRevision":9223372036854775807',
                    ),
                ),
                current,
            ),
        ).toBe(true)
        expect((await f.api.operation(operation.id)).revision).toBe(
            largeRevision,
        )
        await expect(f.api.operation('incorrect-id')).rejects.toThrow(
            'configuration-response',
        )
    })
})
