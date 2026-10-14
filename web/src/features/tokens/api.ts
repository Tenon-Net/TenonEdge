import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
export type ApiToken = components['schemas']['ApiTokenSummary']
export type CreatorTokens = components['schemas']['CreatorTokenReview']
export type TokenForm = {
    name: string
    role: ApiToken['role']
    expiresAt: string
}
export function createTokensApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async list(after: number, signal?: AbortSignal) {
            const { data } = await api.GET('/api/edge-admin/v1/tokens', {
                params: { query: { after, limit: 50 } },
                signal,
            })
            if (!data) throw new Error('missing-tokens')
            return data
        },
        async creator(userId: number, signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/users/{userId}/tokens',
                {
                    params: { path: { userId } },
                    signal,
                },
            )
            if (!data) throw new Error('missing-creator-tokens')
            return data
        },
        // Call directly, never as a query or mutation returning this secret into its cache.
        async create(form: TokenForm) {
            const { data } = await api.POST('/api/edge-admin/v1/tokens', {
                body: {
                    name: form.name,
                    role: form.role,
                    expiresAt: form.expiresAt
                        ? new Date(form.expiresAt).toISOString()
                        : null,
                },
            })
            if (!data?.secret) throw new Error('missing-token-secret')
            return data
        },
        async revoke(id: number, confirmed: boolean) {
            if (!confirmed || !Number.isSafeInteger(id) || id < 1)
                throw new Error('token-confirmation')
            await api.POST('/api/edge-admin/v1/tokens/{id}/revoke', {
                params: { path: { id } },
                body: { confirmed: true },
            })
        },
    }
}
