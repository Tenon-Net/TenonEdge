import { useQuery } from '@tanstack/react-query'
import { diagnostics } from './queries'
import { dashboardSamples, recentExceptions } from './dashboard-model'

export function useDashboardTrends() {
    return useQuery({
        queryKey: ['dashboard-trends'],
        queryFn: async ({ signal }) =>
            dashboardSamples(await diagnostics.trends(signal)),
        refetchInterval: 10_000,
        gcTime: 0,
    })
}
export function useDashboardExceptions() {
    return useQuery({
        queryKey: ['dashboard-exceptions'],
        queryFn: async ({ signal }) =>
            recentExceptions(
                await Promise.all([
                    diagnostics.events({ level: 'warning' }, undefined, signal),
                    diagnostics.events({ level: 'error' }, undefined, signal),
                ]),
            ),
        refetchInterval: 10_000,
        gcTime: 0,
    })
}
