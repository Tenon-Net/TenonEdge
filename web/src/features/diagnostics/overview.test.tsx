import type { ReactNode } from 'react'
import { beforeEach, expect, it, vi } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { ApiError } from '../../api/errors'
import { decodeDiagnosticsJson, type Diagnostics } from './api'
import { OverviewPage } from './overview'
import { dateText } from './view'

const queries = vi.hoisted(() => ({
    runtime: vi.fn(),
    info: vi.fn(),
    devices: vi.fn(),
    outputs: vi.fn(),
    trends: vi.fn(),
    events: vi.fn(),
}))
const session = vi.hoisted(() => ({ role: 'viewer' }))
vi.mock('../../lib/session', () => ({ useSession: () => session }))
vi.mock('./queries', () => ({
    useRuntimeStatus: queries.runtime,
    useSystemInfo: queries.info,
    useRuntimeDevices: queries.devices,
    useRuntimeOutputs: queries.outputs,
}))
vi.mock('@tanstack/react-router', () => ({
    Link: ({ children }: { children: ReactNode }) => <span>{children}</span>,
}))
vi.mock('./dashboard-queries', () => ({
    useDashboardTrends: queries.trends,
    useDashboardExceptions: queries.events,
}))
vi.mock('./dashboard-chart', () => ({
    DashboardChart: () => <p>trends-component</p>,
}))

function query(data?: unknown, error: unknown = null) {
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
    session.role = 'viewer'
    queries.runtime.mockReturnValue(query(status()))
    queries.info.mockReturnValue(query())
    queries.devices.mockReturnValue(query())
    queries.outputs.mockReturnValue(query())
    queries.trends.mockReturnValue(query([]))
    queries.events.mockReturnValue(query([]))
})

it.each(['administrator', 'configurator', 'viewer'])(
    'first-use guidance respects %s permissions and never claims an unavailable configuration is empty',
    (role) => {
        session.role = role
        const data = status()
        data.configuration = {
            effectiveRevision: null,
            appliedBy: null,
            appliedAt: null,
        }
        data.runtime.activeRevision = null
        data.coordinator.state = 'idle'
        queries.runtime.mockReturnValue(query(data))
        expect(
            renderToStaticMarkup(<OverviewPage />).includes('开始使用'),
        ).toBe(role !== 'viewer')
        queries.runtime.mockReturnValue(query(data, new Error('unavailable')))
        expect(renderToStaticMarkup(<OverviewPage />)).not.toContain('开始使用')
        data.coordinator.state = 'runFailed'
        queries.runtime.mockReturnValue(query(data))
        expect(renderToStaticMarkup(<OverviewPage />)).not.toContain('开始使用')
    },
)

it('pairs exact durable revision with its own operator/time, not the active runtime revision; keeps independent chart and exception summary', () => {
    const html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toMatch(/持久有效版本<\/dt><dd[^>]*>r9007199254740993<\/dd>/)
    expect(html).toMatch(/当前运行版本<\/dt><dd[^>]*>r7<\/dd>/)
    expect(html).toContain('actual-operator')
    expect(html).toContain(dateText('2026-10-19T01:02:03Z'))
    expect(html).toContain('不表示全部运行单元已启动')
    expect(html).toContain('trends-component')
    expect(html).toContain('最近异常')
    expect(html).toContain('暂无警告与错误')
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
    expect(html).toContain('最近异常')
    expect(html).toContain('暂无警告与错误')
})

it('hides stale counts and the healthy exception message on independent query failures', () => {
    queries.devices.mockReturnValue(
        query(
            { devices: [{ health: 'normal' }] },
            new ApiError(500, { status: 500 }),
        ),
    )
    queries.outputs.mockReturnValue(
        query(
            { usage: { pendingCount: '9007199254740993' } },
            new ApiError(500, { status: 500 }),
        ),
    )
    queries.events.mockReturnValue(
        query([], new ApiError(500, { status: 500 })),
    )
    const html = renderToStaticMarkup(<OverviewPage />)
    expect(html).not.toContain('暂无警告与错误')
    expect(html).not.toContain('9,007,199,254,740,993')
    expect(html).toContain('暂时无法读取')
    expect(html).toContain('aria-valuetext="未知"')
    expect(html).toContain('trends-component')
    expect(html).toContain('actual-operator')
})

it('preserves exact pending counts and counts pending plus quarantine, not failed bytes, against global quota', () => {
    queries.outputs.mockReturnValue(
        query({
            metadataAvailable: false,
            usage: {
                pendingCount: '9007199254740993',
                pendingBytes: '20',
                quarantinedBytes: '30',
                failedBytes: '1000000',
                failedCount: '2',
                quarantinedCount: '3',
            },
            limits: { globalBytes: '100' },
            activeStates: {},
        }),
    )
    const html = renderToStaticMarkup(<OverviewPage />)
    expect(html).toContain('9,007,199,254,740,993')
    expect(html).toContain('aria-valuenow="50"')
    expect(html).toContain('不能据此判断输出正在运行')
    expect(html).not.toContain('成功率')
    expect(html).toContain('class="sr-only">总览</h1>')
})
