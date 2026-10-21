import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'

export function useRouteNodeRuntime(
    id: number | undefined,
    version: string | undefined,
    enabled: boolean,
) {
    return useQuery({
        queryKey: ['route-node-runtime', id, version],
        enabled: enabled && id !== undefined && version !== undefined,
        queryFn: async ({ signal }) => {
            if (id === undefined || version === undefined)
                throw new Error('route-runtime-query')
            const { data } = await api.GET(
                '/api/edge-admin/v1/routes/{routeId}/runtime',
                {
                    params: {
                        path: { routeId: id },
                        query: { expectedVersion: version },
                    },
                    signal,
                },
            )
            if (!data) throw new Error('route-runtime-response')
            return data
        },
        refetchInterval: 2000,
        gcTime: 0,
        retry: false,
    })
}
