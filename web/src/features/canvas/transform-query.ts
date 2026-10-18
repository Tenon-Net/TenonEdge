import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { isNodeKind } from './model'
import { builtInMap, moduleKey } from './transform-model'

export function useTransformModules(enabled: boolean) {
    return useQuery({
        queryKey: ['transform-modules'],
        enabled,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/transform-modules',
                { signal },
            )
            if (!Array.isArray(data))
                throw new Error('missing-transform-modules')
            const seen = new Set<string>()
            for (const module of data) {
                const version = Number(module.version)
                const key = moduleKey(module.kind, version)
                if (
                    !isNodeKind(module.kind) ||
                    !module.kind.startsWith('transform.') ||
                    !Number.isInteger(version) ||
                    version < 1 ||
                    version > 2147483647 ||
                    !module.displayName ||
                    seen.has(key) ||
                    (module.parameters &&
                        (module.parameters.scope !== 'transform' ||
                            !module.parameters.parameters?.length ||
                            module.parameters.parameters.some(
                                (field) =>
                                    ![
                                        'text',
                                        'number',
                                        'switch',
                                        'choice',
                                    ].includes(field.type),
                            ))) ||
                    (module.canConfigure &&
                        !builtInMap(module.kind, version) &&
                        !module.parameters)
                )
                    throw new Error('invalid-transform-module')
                seen.add(key)
            }
            return data
        },
    })
}
