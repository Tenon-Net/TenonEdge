import { describe, expect, it } from 'vitest'
import { eventTime, runtimeHealth, trendGeometry } from './model'
import { decodeDiagnosticsJson, type Diagnostics, type TrendPoint } from './api'

const point: TrendPoint = {
    at: '2026-10-13T00:00:00Z',
    uptimeSeconds: '10',
    durationSeconds: '10',
    received: '20',
    sent: '10',
    readFailures: '1',
    sendFailures: '4',
    rejectedPaths: '0',
    storageFailedRecordAttempts: '0',
}
const status: Diagnostics = {
    coordinator: { state: 'active', reason: null, activeRevision: '1' },
    runtime: { activeRevision: '1', units: [] },
    queue: {
        storageFault: false,
        lastError: null,
        lastErrorAt: null,
        committedBatches: '0',
        committedRecords: '0',
        deletedRecords: '0',
        failedBatches: '0',
        failedRecords: '0',
        rejectedRecords: '0',
        store: null,
    },
    storageProtection: {
        rejectNewRecords: false,
        spaceUnknown: true,
        space: null,
    },
    clockSyncState: 'unknown',
    persistentCounters: null,
    inFlight: {
        capacityBytes: '1',
        reservedBytes: '0',
        peakReservedBytes: '0',
    },
    unpersistedCounters: {},
}
describe('diagnostic metadata and display', () => {
    it('retains Int64 counters, event cursors and null/unknown instead of rounding or inventing zero', () => {
        expect(
            decodeDiagnosticsJson(
                '{"id":9223372036854775807,"counters":null,"nested":[0,1.2,"42"]}',
            ),
        ).toEqual({
            id: '9223372036854775807',
            counters: null,
            nested: ['0', '1.2', '42'],
        })
        expect(() => decodeDiagnosticsJson(undefined)).toThrow()
        expect(() => decodeDiagnosticsJson('{"id":1,"id":2}')).toThrow()
    })
    it('uses actual sampling duration and uptime, without filling gaps or rounding the displayed counts', () => {
        const points = [
            point,
            {
                ...point,
                uptimeSeconds: '50',
                durationSeconds: '40',
                received: '40',
            },
        ]
        const geometry = trendGeometry(points)
        expect(geometry.peak).toBe(2)
        expect(geometry.lines[0]).toBe('20,20 780,100')
        expect(geometry.lines[2]).toBe('20,140 780,170')
        expect(points[0].received).toBe('20')
        expect(trendGeometry([]).lines).toEqual(['', '', ''])
        for (const durationSeconds of ['0', '-1', 'NaN'])
            expect(() =>
                trendGeometry([{ ...point, durationSeconds }]),
            ).toThrow()
    })
    it('keeps failed startup and unknown clocks distinct from active loops', () => {
        expect(runtimeHealth(status)).toBe('active')
        expect(
            runtimeHealth({
                ...status,
                coordinator: {
                    state: 'runFailed',
                    reason: 'invalid',
                    activeRevision: '1',
                },
            }),
        ).toBe('runFailed')
        expect(
            runtimeHealth({
                ...status,
                queue: { ...status.queue, storageFault: true },
            }),
        ).toBe('degraded')
        expect(
            runtimeHealth({
                ...status,
                runtime: {
                    activeRevision: '1',
                    units: [
                        {
                            id: { kind: 'input.http', key: 'meter/poll' },
                            state: 'buildFailed',
                            restartCount: 0,
                            lastError: 'failed',
                            lastErrorAt: null,
                            nextRestartAt: null,
                            reads: null,
                        },
                    ],
                },
            }),
        ).toBe('degraded')
    })
    it('converts explicit local filter dates to UTC and rejects invalid input', () => {
        expect(eventTime('')).toBeUndefined()
        expect(eventTime('2026-10-13T08:00:00+08:00')).toBe(
            '2026-10-13T00:00:00.000Z',
        )
        expect(() => eventTime('not-a-date')).toThrow()
    })
})
