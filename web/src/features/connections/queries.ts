import { useMutation, useQuery } from '@tanstack/react-query'
import { api, queryClient } from '../../lib/session'
import { connectionSnapshotCurrent } from './list-model'
import { connectionRequest, type ConnectionForm } from './model'
import { readConnectionList } from './module-model'

// This cache includes lexical module parameters; do not share the plain DTO query shape.
export const connectionDraftsKey = ['connection-drafts', 'exact']

function useParameterSchema(kind: 'modbus' | 'mqtt') {
    return useQuery({
        queryKey: ['parameter-schema', kind],
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/parameter-schemas/{id}',
                {
                    signal,
                    params: { path: { id: `connection.${kind}` } },
                },
            )
            if (
                !data ||
                data.id !== `connection.${kind}` ||
                data.scope !== 'connection' ||
                !data.parameters?.length
            )
                throw new Error('missing-parameter-schema')
            return data
        },
    })
}

// Bind each runtime snapshot to the draft version it describes. A failed read
// must not make cached runtime data look current or replace local editor state.
export function useConnectionData() {
    const query = useQuery({
        queryKey: connectionDraftsKey,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/connections', {
                signal,
                parseAs: 'text',
            })
            if (!data) throw new Error('missing-connections')
            return readConnectionList(data)
        },
    })
    const modules = useQuery({
        queryKey: ['connection-modules'],
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/connection-modules',
                { signal },
            )
            if (!data) throw new Error('missing-connection-modules')
            return data
        },
    })
    const version = query.data?.version
    const runtime = useQuery({
        queryKey: ['runtime-connections', version],
        enabled: version !== undefined,
        refetchInterval: 5000,
        refetchIntervalInBackground: false,
        queryFn: async ({ signal }) => {
            if (version === undefined) throw new Error('missing-draft-version')
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/connections',
                {
                    signal,
                    params: { query: { expectedDraftVersion: version } },
                },
            )
            if (!data) throw new Error('missing-runtime-connections')
            return data
        },
    })
    const current = connectionSnapshotCurrent(
        version,
        runtime.data,
        !!runtime.error || !!query.error,
    )
    const modbusSchema = useParameterSchema('modbus')
    const mqttSchema = useParameterSchema('mqtt')
    return { query, modules, runtime, current, modbusSchema, mqttSchema }
}

// The form retains the version captured when editing began. Invalidate cached
// drafts only after a successful write; failures leave the caller's edits intact.
export function useSaveConnection(onSaved: (form: ConnectionForm) => void) {
    return useMutation({
        retry: false,
        mutationFn: async (form: ConnectionForm) => {
            const body = connectionRequest(form)
            if (form.id !== undefined)
                await api.PUT('/api/edge-admin/v1/connections/{connectionId}', {
                    params: { path: { connectionId: Number(form.id) } },
                    body,
                })
            else await api.POST('/api/edge-admin/v1/connections', { body })
        },
        onSuccess: async (_, form) => {
            void queryClient.invalidateQueries({
                queryKey: ['configuration-summary'],
            })
            onSaved(form)
            await queryClient.invalidateQueries({
                queryKey: ['connection-drafts'],
            })
        },
    })
}
