import { beforeEach, expect, it, vi } from 'vitest'
import { useDashboardExceptions, useDashboardTrends } from './dashboard-queries'

const hooks = vi.hoisted(() => ({
    query: vi.fn(),
    events: vi.fn(),
    trends: vi.fn(),
}))
vi.mock('@tanstack/react-query', () => ({ useQuery: hooks.query }))
vi.mock('./queries', () => ({
    diagnostics: { events: hooks.events, trends: hooks.trends },
}))
beforeEach(() => vi.clearAllMocks())

it('uses a separate trend cache shape, ten-second refresh and the route cancellation signal', async () => {
    hooks.trends.mockResolvedValue([])
    useDashboardTrends()
    const config = hooks.query.mock.calls[0][0]
    const signal = new AbortController().signal
    expect(config.queryKey).toEqual(['dashboard-trends'])
    expect(config.refetchInterval).toBe(10_000)
    expect(config.gcTime).toBe(0)
    expect(await config.queryFn({ signal })).toEqual([])
    expect(hooks.trends).toHaveBeenCalledExactlyOnceWith(signal)
})
it('fetches bounded warning/error pages with the same cancellation signal, never all info history', async () => {
    hooks.events.mockResolvedValue({
        items: [],
        nextCursor: '9007199254740993',
    })
    useDashboardExceptions()
    const config = hooks.query.mock.calls[0][0]
    const signal = new AbortController().signal
    expect(config.refetchInterval).toBe(10_000)
    expect(config.gcTime).toBe(0)
    expect(await config.queryFn({ signal })).toEqual([])
    expect(hooks.events.mock.calls).toEqual([
        [{ level: 'warning' }, undefined, signal],
        [{ level: 'error' }, undefined, signal],
    ])
})
it.each(['warning', 'error'])(
    'fails the whole exception summary when the %s page is unavailable, rather than presenting a partial healthy result',
    async (level) => {
        hooks.events.mockImplementation(async (filter) => {
            if (filter.level === level) throw new Error('fixture unavailable')
            return { items: [], nextCursor: null }
        })
        useDashboardExceptions()
        await expect(
            hooks.query.mock.calls[0][0].queryFn({
                signal: new AbortController().signal,
            }),
        ).rejects.toThrow('fixture unavailable')
    },
)
