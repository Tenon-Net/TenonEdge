import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { validateOutputModules } from './output-module-model'

export function useOutputModules(enabled: boolean) {
    return useQuery({
        queryKey: ['output-modules'],
        enabled,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/output-modules',
                { signal },
            )
            if (!data) throw new Error('missing-output-modules')
            return validateOutputModules(data)
        },
    })
}
