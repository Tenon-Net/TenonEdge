import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { isInputModuleKind } from '../canvas/model'

export function useInputModules(enabled = true) {
    return useQuery({
        queryKey: ['input-modules'],
        enabled,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/input-modules', {
                signal,
            })
            if (!Array.isArray(data)) throw new Error('missing-input-modules')
            const seen = new Set<string>()
            for (const module of data) {
                const version = Number(module.version)
                const key = `${module.kind}@${version}`
                if (
                    !isInputModuleKind(module.kind) ||
                    !Number.isInteger(version) ||
                    version < 1 ||
                    version > 2147483647 ||
                    !module.displayName ||
                    seen.has(key) ||
                    module.parameters?.scope !== 'input' ||
                    !Array.isArray(module.parameters.parameters) ||
                    module.parameters.parameters.some(
                        (field) =>
                            !['text', 'number', 'switch', 'choice'].includes(
                                field.type,
                            ),
                    )
                )
                    throw new Error('invalid-input-module')
                seen.add(key)
            }
            return data
        },
    })
}
