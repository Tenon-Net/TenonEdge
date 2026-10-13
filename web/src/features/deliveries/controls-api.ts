import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { revisionId } from '../configuration/api'
import { connectionFilter } from './api'

export type OutputControl = components['schemas']['OutputControlState']
export function createOutputControlsApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    const path = (id: string) => {
        const value = connectionFilter(id)
        if (!value) throw new Error('output-target')
        return { connectionId: value as unknown as number }
    }
    return {
        async read(id: string) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/connections/{connectionId}/delivery-control',
                { params: { path: path(id) } },
            )
            if (!data) throw new Error('output-control')
            return data
        },
        async write(
            id: string,
            paused: boolean,
            expectedVersion: string | null,
            confirmed: boolean,
        ) {
            if (!confirmed) throw new Error('output-confirmation')
            const { data } = await api.PUT(
                '/api/edge-admin/v1/connections/{connectionId}/delivery-control',
                {
                    params: { path: path(id) },
                    body: { paused, expectedVersion, confirmed: true },
                },
            )
            if (!data) throw new Error('output-control')
            return data
        },
        async retry(id: string, revision: string, backoffVersion: string) {
            if (!/^[0-9a-f]{32}$/.test(backoffVersion))
                throw new Error('output-backoff')
            const { response } = await api.POST(
                '/api/edge-admin/v1/connections/{connectionId}/retry-now',
                {
                    params: { path: path(id) },
                    body: { revision: revisionId(revision), backoffVersion },
                },
            )
            if (response.status !== 202)
                throw new Error('output-retry-response')
        },
    }
}
