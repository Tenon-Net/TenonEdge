import type { QueryClient } from '@tanstack/react-query'

// A broad invalidate refetches every active observer synchronously. React has not
// disabled comparison yet when detail starts, so both can enter the one-user
// preview budget together. Mark stale without fetching, then await each group.
export async function refreshConfigurationHistory(client: QueryClient) {
    await client.invalidateQueries({
        queryKey: ['configuration-history'],
        refetchType: 'none',
    })
    for (const kind of ['list', 'detail', 'comparison']) {
        await client.refetchQueries({
            queryKey: ['configuration-history', kind],
            type: 'active',
        })
    }
}
