import { describe, expect, it } from 'vitest'
import {
    connectionRows,
    connectionSnapshotCurrent,
    draftTarget,
    type RuntimeConnection,
    type RuntimeConnectionList,
} from './list-model'
import type { Connection } from './model'

const draft: Connection = {
    id: '1',
    key: 'draft',
    name: 'Edited',
    kind: 'http',
    enabled: true,
    modbus: null,
    mqtt: null,
    http: {
        baseAddress: 'http://example.test/new/',
        timeoutMs: 5000,
        headers: null,
    },
}
const observed: RuntimeConnection = {
    id: 1,
    effective: {
        key: 'original',
        name: 'Frozen',
        kind: 'http',
        target: 'http://example.test/old/',
        enabled: true,
    },
    draft: null,
    draftChanged: true,
    state: 'connected',
    users: [],
    draftUsers: [],
    pendingCount: 0,
    lastSuccessAt: null,
    lastErrorCode: null,
    lastErrorAt: null,
}
const snapshot: RuntimeConnectionList = {
    revision: 1,
    draftVersion: 'v1',
    capturedAt: '2026-10-19T00:00:00Z',
    queueAvailable: true,
    connections: [observed],
}

describe('one list for actual drafts and effective connection summaries', () => {
    it('unions IDs, normalizes numeric IDs, and never fabricates an editable draft', () => {
        const rows = connectionRows(
            [draft, { ...draft, id: 3 }],
            [observed, { ...observed, id: 2 }],
        )
        expect(rows.map((row) => row.id)).toEqual([1, 2, 3])
        expect(rows[0].draft).toBe(draft)
        expect(rows[0].runtime).toBe(observed)
        expect(rows[1].draft).toBeUndefined()
        expect(rows[2].runtime).toBeUndefined()
    })
    it('searches both actual effective and draft name/key without conflating targets', () => {
        for (const search of ['Edited', 'Frozen', 'draft', 'ORIGINAL'])
            expect(
                connectionRows([draft], [observed], search, 'http'),
            ).toHaveLength(1)
        expect(connectionRows([draft], [observed], '', 'mqtt')).toEqual([])
        expect(connectionRows([draft], [observed], 'missing')).toEqual([])
        expect(draftTarget(draft)).toBe('http://example.test/new/')
        expect(observed.effective?.target).toBe('http://example.test/old/')
    })
    it('does not publish cached normal status after errors or mismatched/missing draft version', () => {
        expect(connectionSnapshotCurrent('v1', snapshot, false)).toBe(true)
        expect(connectionSnapshotCurrent('v1', snapshot, true)).toBe(false)
        expect(connectionSnapshotCurrent('v2', snapshot, false)).toBe(false)
        expect(connectionSnapshotCurrent(undefined, snapshot, false)).toBe(
            false,
        )
        expect(connectionSnapshotCurrent('v1', undefined, false)).toBe(false)
    })
    it('formats IPv6 ports and TLS scheme without losing exact host', () => {
        expect(
            draftTarget({
                ...draft,
                kind: 'mqtt',
                http: null,
                mqtt: {
                    host: '::1',
                    port: 8883,
                    tls: true,
                    timeoutMs: 5000,
                    pingIntervalMs: 15000,
                },
            }),
        ).toBe('mqtts://[::1]:8883')
    })
})
