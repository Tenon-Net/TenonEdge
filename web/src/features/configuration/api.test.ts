import { describe, expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { ApiError } from '../../api/errors'
import {
    applicationRequest,
    fieldChanges,
    createConfigurationApi,
    decodeReview,
    operationOutcome,
    reviewIsCurrent,
    type DraftSummary,
    type Operation,
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
