import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { createDraftSummaryApi } from './summary-api'
const readSummary = createDraftSummaryApi(api)
export const summaryKey = ['configuration-summary']
export const operationsKey = ['configuration-operations']
export const runtimeKey = ['configuration-runtime']
export function useDraftSummary() {
    return useQuery({
        queryKey: summaryKey,
        queryFn: ({ signal }) => readSummary(signal),
        refetchInterval: 10_000,
    })
}
