import { QueryClient, QueryObserver } from '@tanstack/react-query'
import { expect, it, vi } from 'vitest'
import { refreshConfigurationHistory } from './history-refresh'

it('refreshes history reads sequentially even before React updates comparison enabled state', async () => {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    })
    const events: string[] = []
    let release!: () => void
    const held = new Promise<void>((resolve) => {
        release = resolve
    })
    const observers = ['list', 'detail', 'comparison'].map((kind) => {
        const queryKey = ['configuration-history', kind, '1']
        client.setQueryData(queryKey, 0)
        return new QueryObserver(client, {
            queryKey,
            queryFn: async () => {
                events.push(kind + '-start')
                if (kind === 'detail') await held
                events.push(kind + '-end')
                return 1
            },
        })
    })
    const unsubscribe = observers.map((observer) =>
        observer.subscribe(() => {}),
    )
    const refreshing = refreshConfigurationHistory(client)
    try {
        await vi.waitFor(() => expect(events).toContain('detail-start'))
        expect(events).not.toContain('comparison-start')
        release()
        await refreshing
        expect(events).toEqual([
            'list-start',
            'list-end',
            'detail-start',
            'detail-end',
            'comparison-start',
            'comparison-end',
        ])
    } finally {
        release()
        await refreshing
        for (const stop of unsubscribe) stop()
        client.clear()
    }
})

it('joins a comparison already started when its detail observer re-enables it', async () => {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    })
    const detailKey = ['configuration-history', 'detail', '1']
    const comparisonKey = [
        'configuration-history',
        'comparison',
        '1',
        'draft',
        'v1',
    ]
    client.setQueryData(detailKey, 0)
    client.setQueryData(comparisonKey, 0)
    let starts = 0
    let cancellations = 0
    let release!: () => void
    const held = new Promise<void>((resolve) => {
        release = resolve
    })
    const comparisonOptions = {
        queryKey: comparisonKey,
        queryFn: async ({ signal }: { signal: AbortSignal }) => {
            starts++
            signal.addEventListener(
                'abort',
                () => {
                    cancellations++
                },
                { once: true },
            )
            await held
            return 1
        },
    }
    const comparison = new QueryObserver(client, comparisonOptions)
    const detail = new QueryObserver(client, {
        queryKey: detailKey,
        queryFn: async () => 1,
    })
    const stopComparison = comparison.subscribe(() => {})
    // Match ConfigurationHistory: comparison is disabled during the detail read,
    // then re-enabled as soon as the detail finishes. That can itself fetch a
    // stale comparison before refreshConfigurationHistory reaches its next group.
    const stopDetail = detail.subscribe((result) => {
        comparison.setOptions({
            ...comparisonOptions,
            enabled: !result.isFetching,
        })
    })
    // Another caller may already have marked this pair stale. Re-enabling it
    // must still share its request with the explicit refresh group.
    await client.invalidateQueries({
        queryKey: comparisonKey,
        refetchType: 'none',
    })
    const refreshing = refreshConfigurationHistory(client)
    try {
        await vi.waitFor(() => expect(starts).toBeGreaterThan(0))
        expect(starts).toBe(1)
        expect(cancellations).toBe(0)
        release()
        await refreshing
        expect(comparison.getCurrentResult().data).toBe(1)
    } finally {
        release()
        await refreshing
        stopDetail()
        stopComparison()
        client.clear()
    }
})

it('does not make a gated comparison stale before earlier read groups finish', async () => {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    })
    const events: string[] = []
    let release!: () => void
    const held = new Promise<void>((resolve) => {
        release = resolve
    })
    const observers = ['list', 'detail', 'comparison'].map((kind) => {
        const queryKey = ['configuration-history', kind, '1']
        client.setQueryData(queryKey, 0)
        return new QueryObserver(client, {
            queryKey,
            enabled: kind !== 'comparison',
            queryFn: async () => {
                events.push(kind)
                if (kind === 'list') await held
                return 1
            },
        })
    })
    const unsubscribe = observers.map((observer) =>
        observer.subscribe(() => {}),
    )
    const refreshing = refreshConfigurationHistory(client)
    try {
        await vi.waitFor(() => expect(events).toContain('list'))
        // A finished summary read re-enables this observer while list/runtime
        // are still pending. A broad invalidation must not start comparison now.
        observers[2].setOptions({ ...observers[2].options, enabled: true })
        expect(events).toEqual(['list'])
        release()
        await refreshing
        expect(events).toEqual(['list', 'detail', 'comparison'])
    } finally {
        release()
        await refreshing
        for (const stop of unsubscribe) stop()
        client.clear()
    }
})
