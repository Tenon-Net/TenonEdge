import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { createConfigurationApi } from './api'
import { createConfigurationMigrationApi } from './migration-api'
export const configuration = createConfigurationApi(api)
export const configurationMigrations = createConfigurationMigrationApi(api)
export const summaryKey = ['configuration-summary']
export const operationsKey = ['configuration-operations']
export const runtimeKey = ['configuration-runtime']
export function useDraftSummary() {
    return useQuery({
        queryKey: summaryKey,
        queryFn: ({ signal }) => configuration.summary(signal),
        refetchInterval: 10_000,
    })
}
