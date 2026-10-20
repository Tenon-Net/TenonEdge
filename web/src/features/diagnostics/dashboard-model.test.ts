import { describe, expect, it } from 'vitest'
import {
    chartPaths,
    chartWindow,
    countText,
    dashboardDeviceCounts,
    dashboardSamples,
    queuePressure,
    rateText,
    recentExceptions,
} from './dashboard-model'
import type { EventPage, RuntimeDeviceList, TrendPoint } from './api'

const point: TrendPoint = {
    at: '2026-10-20T00:00:10Z',
    uptimeSeconds: '10',
    durationSeconds: '10',
    received: '20',
    sent: '40',
    readFailures: '0',
    sendFailures: '0',
    rejectedPaths: '0',
    storageFailedRecordAttempts: '0',
}
describe('real dashboard metrics', () => {
    it('uses actual duration and allows fan-out sends to exceed source receives without inventing a success percentage', () => {
        const samples = dashboardSamples([point])
        expect(samples[0].rates).toEqual([2, 4])
        expect(samples[0].point).toBe(point)
        expect(dashboardSamples([])).toEqual([])
        expect(countText(undefined)).toBe('—')
        expect(rateText(1 / 60)).toBe('0.02')
        expect(rateText(1 / 1000)).toBe('<0.01')
        expect(rateText(undefined)).toBe('—')
    })
    it.each(['0', '-1', 'NaN', 'Infinity'])(
        'rejects invalid duration %s rather than making zero data',
        (durationSeconds) => {
            expect(() =>
                dashboardSamples([{ ...point, durationSeconds }]),
            ).toThrow('diagnostics-trends')
        },
    )
    it('rejects invalid counts, times and out-of-order monotonic samples', () => {
        expect(() => dashboardSamples([{ ...point, sent: '-1' }])).toThrow()
        expect(() => dashboardSamples([{ ...point, at: 'invalid' }])).toThrow()
        expect(() => dashboardSamples([point, point])).toThrow()
    })
    it('keeps real gaps as separate SVG subpaths and selects only the requested window', () => {
        const samples = dashboardSamples([
            point,
            { ...point, uptimeSeconds: '20' },
            { ...point, uptimeSeconds: '100' },
            { ...point, uptimeSeconds: '110' },
        ])
        const full = chartWindow(samples, 3600)
        const paths = chartPaths(full, 800, 232)
        expect(paths.paths[0].match(/M/g)).toHaveLength(2)
        expect(paths.paths[0].match(/L/g)).toHaveLength(2)
        expect(
            chartWindow(samples, 90).samples.map((sample) => sample.uptime),
        ).toEqual([20, 100, 110])
        expect(chartWindow([], 900).samples).toEqual([])
        expect(full.ceiling).toBeGreaterThanOrEqual(4)
    })
    it('preserves Int64 counts and sums bytes before calculating pressure; zero limit stays unknown', () => {
        expect(countText('9223372036854775807')).toBe(
            '9,223,372,036,854,775,807',
        )
        expect(
            queuePressure(
                '9007199254740993',
                '9007199254740992',
                '36028797018963970',
            ),
        ).toEqual({ used: '18014398509481985', percent: 50 })
        expect(queuePressure(1, 2, 0)).toEqual({
            used: '3',
            percent: undefined,
        })
        expect(queuePressure(120, 0, 100).percent).toBe(120)
    })
    it('does not classify silent push, disabled, draft-only or unrecognized devices as offline', () => {
        const healths = [
            'normal',
            'partialFailure',
            'offline',
            'push',
            'disabled',
            'notRead',
            'inactive',
            'notApplied',
            'future-state',
        ]
        const devices: RuntimeDeviceList['devices'] = healths.map((health) => ({
            key: health,
            name: health,
            kind: 'fixture',
            health,
            draftOnly: health === 'notApplied',
            removedFromDraft: false,
            sources: [],
        }))
        const groups = dashboardDeviceCounts(devices)
        expect(groups.map((group) => group.count)).toEqual([1, 2, 1, 1, 4])
        expect(groups.reduce((sum, group) => sum + group.count, 0)).toBe(
            healths.length,
        )
    })
    it('merges bounded severity pages by last occurrence with exact id tie-breaks and at most five items', () => {
        const event = (
            id: string,
            at: string,
            level: 'warning' | 'error' | 'info' = 'warning',
        ): EventPage['items'][number] => ({
            id,
            firstOccurredAt: point.at,
            lastOccurredAt: at,
            level,
            type: 'fixture',
            target: null,
            message: 'fixture event',
            occurrenceCount: '1',
        })
        const warning: EventPage = {
            nextCursor: '5',
            items: [
                event('1', '2026-10-20T00:00:50Z'),
                ...['2', '3', '4', '5', '6'].map((id) => event(id, point.at)),
            ],
        }
        const error: EventPage = {
            nextCursor: null,
            items: [
                event('9007199254740992', point.at, 'error'),
                event('9007199254740993', point.at, 'error'),
                event('9007199254740994', point.at, 'info'),
            ],
        }
        expect(
            recentExceptions([warning, error]).map((item) => item.id),
        ).toEqual(['1', '9007199254740993', '9007199254740992', '6', '5'])
        expect(recentExceptions([{ nextCursor: null, items: [] }])).toEqual([])
    })
})
