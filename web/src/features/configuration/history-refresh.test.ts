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
