import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { createConfigurationApi } from './api'
export const configuration = createConfigurationApi(api)
export const summaryKey = ['configuration-summary']
export const operationsKey = ['configuration-operations']
export function useDraftSummary() {
    return useQuery({
        queryKey: summaryKey,
        queryFn: ({ signal }) => configuration.summary(signal),
        refetchInterval: 10_000,
    })
}
