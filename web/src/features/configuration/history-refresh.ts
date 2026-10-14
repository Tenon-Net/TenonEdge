import type { QueryClient } from '@tanstack/react-query'

// Invalidate only the current group. Marking every group stale up front lets
// React re-enable comparison (after summary/detail) and fetch it ahead of its
// turn, competing with other reads for the bounded per-session preview budget.
export async function refreshConfigurationHistory(client: QueryClient) {
    for (const kind of ['list', 'detail', 'comparison']) {
        await client.invalidateQueries(
            { queryKey: ['configuration-history', kind] },
            // Join an observer's in-flight read instead of cancelling/restarting
            // it: cancellation does not immediately release the server's budget.
            { cancelRefetch: false },
        )
    }
}
