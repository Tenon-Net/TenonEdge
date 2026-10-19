import type { ReactNode } from 'react'
import { beforeEach, expect, it, vi } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { ApiError } from '../../api/errors'
import { decodeDiagnosticsJson, type Diagnostics } from './api'
import { OverviewPage } from './overview'
import { dateText } from './view'

const queries = vi.hoisted(() => ({ runtime: vi.fn(), info: vi.fn() }))
vi.mock('../../lib/session', () => ({ useSession: () => ({ role: 'viewer' }) }))
vi.mock('./queries', () => ({
    useRuntimeStatus: queries.runtime,
    useSystemInfo: queries.info,
}))
vi.mock('@tanstack/react-router', () => ({
    Link: ({ children }: { children: ReactNode }) => <span>{children}</span>,
}))
vi.mock('./current-values', () => ({ DeviceSummary: () => null }))
vi.mock('./outputs', () => ({ OutputSummary: () => null }))
vi.mock('./loss-counters', () => ({ LossCounters: () => null }))
vi.mock('./events', () => ({ RuntimeEvents: () => <p>events-component</p> }))
vi.mock('./trends', () => ({ RuntimeTrends: () => <p>trends-component</p> }))

function query(data?: Diagnostics, error: unknown = null) {
    return {
        data,
        error,
        isPending: false,
        isFetching: false,
        dataUpdatedAt: 1792368000000,
        refetch: vi.fn(),
    }
}
function status() {
    return decodeDiagnosticsJson<Diagnostics>(`{
        "configuration":{"effectiveRevision":9007199254740993,"appliedBy":"actual-operator","appliedAt":"2026-10-19T01:02:03Z"},
        "coordinator":{"state":"active","reason":null,"activeRevision":7,"queueOperationInProgress":false},
        "runtime":{"activeRevision":7,"units":[]},
        "queue":{"storageFault":false,"lastError":null},
        "storageProtection":{"rejectNewRecords":false,"space":null},
        "clockSyncState":"unknown","inFlight":{"reservedBytes":0,"capacityBytes":1024}
    }`)
}
beforeEach(() => {
    queries.runtime.mockReturnValue(query(status()))
    queries.info.mockReturnValue(query())
})

it('pairs exact durable revision with its own operator/time, not the active runtime revision; mounts the shared trends and events', () => {
    const html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toMatch(/持久有效版本<\/dt><dd[^>]*>r9007199254740993<\/dd>/)
    expect(html).toMatch(/当前运行版本<\/dt><dd[^>]*>r7<\/dd>/)
    expect(html).toContain('actual-operator')
    expect(html).toContain(dateText('2026-10-19T01:02:03Z'))
    expect(html).toContain('不表示全部运行单元已启动')
    expect(html).toContain('trends-component')
    expect(html).toContain('events-component')
})

it('retains the durable identity when runtime startup failed and no units are active', () => {
    const data = status()
    data.runtime.activeRevision = null
    data.coordinator.state = 'runFailed'
    data.coordinator.reason = 'fixture-snapshot-fault'
    queries.runtime.mockReturnValue(query(data))
    const html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toContain('r9007199254740993')
    expect(html).toContain('actual-operator')
    expect(html).toContain('尚无运行版本')
    expect(html).toContain('fixture-snapshot-fault')
})

it('distinguishes no effective configuration from unavailable metadata without inventing an operator or timestamp', () => {
    const data = status()
    data.configuration = {
        effectiveRevision: null,
        appliedBy: null,
        appliedAt: null,
    }
    queries.runtime.mockReturnValue(query(data))
    expect(renderToStaticMarkup(<OverviewPage />)).toContain('尚无有效配置')
    data.configuration = {
        effectiveRevision: '9',
        appliedBy: null,
        appliedAt: null,
    }
    let html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toMatch(/持久有效版本<\/dt><dd[^>]*>r9<\/dd>/)
    expect(html).toMatch(/有效版本应用人<\/dt><dd[^>]*>未知<\/dd>/)
    expect(html).toMatch(/有效版本应用时间<\/dt><dd[^>]*>未知<\/dd>/)
    data.configuration = null
    html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toMatch(/持久有效版本<\/dt><dd[^>]*>未知<\/dd>/)
    expect(html).not.toContain('尚无有效配置')
    expect(html).not.toContain('actual-operator')
})

it('does not show cached version metadata as current after a failed status refresh', () => {
    queries.runtime.mockReturnValue(
        query(status(), new ApiError(500, { status: 500 })),
    )
    const html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toContain('请求失败')
    expect(html).toContain('当前运行状态与有效版本未知')
    expect(html).not.toContain('r9007199254740993')
    expect(html).not.toContain('actual-operator')
    expect(html).toContain('trends-component')
    expect(html).toContain('events-component')
})
