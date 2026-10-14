import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
export type User = components['schemas']['UserSummary']
export type UserAction =
    'create' | 'update' | 'password' | 'unlock' | 'end-sessions' | 'delete'
export type UserForm = {
    action: UserAction
    user: User | null
    accountName: string
    displayName: string
    role: User['role']
    isEnabled: boolean
    temporaryPassword: string
}
export function userForm(
    action: UserAction,
    user: User | null = null,
): UserForm {
    return {
        action,
        user,
        accountName: user?.accountName ?? '',
        displayName: user?.displayName ?? '',
        role: user?.role ?? 'viewer',
        isEnabled: user?.isEnabled ?? true,
        temporaryPassword: '',
    }
}
export function createUsersApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async list(after: number, signal?: AbortSignal) {
            const { data } = await api.GET('/api/edge-admin/v1/users', {
                params: { query: { after, limit: 50 } },
                signal,
            })
            if (!data) throw new Error('missing-users')
            return data
        },
        async write(form: UserForm, confirmed: boolean) {
            if (!confirmed) throw new Error('user-confirmation')
            if (form.action === 'create') {
                const { data } = await api.POST('/api/edge-admin/v1/users', {
                    body: {
                        accountName: form.accountName,
                        displayName: form.displayName || null,
                        role: form.role,
                        isEnabled: form.isEnabled,
                        temporaryPassword: form.temporaryPassword,
                    },
                })
                if (!data?.user) throw new Error('missing-user-result')
                return
            }
            const userId = Number(form.user?.id)
            if (
                !Number.isSafeInteger(userId) ||
                userId < 1 ||
                !/^[0-9a-f]{64}$/.test(form.user?.version ?? '')
            )
                throw new Error('user-version')
            const params = { path: { userId } }
            const body = {
                expectedVersion: form.user!.version,
                confirmed: true,
            }
            let result
            switch (form.action) {
                case 'update':
                    result = await api.PUT(
                        '/api/edge-admin/v1/users/{userId}',
                        {
                            params,
                            body: {
                                ...body,
                                displayName: form.displayName || null,
                                role: form.role,
                                isEnabled: form.isEnabled,
                            },
                        },
                    )
                    break
                case 'password':
                    result = await api.POST(
                        '/api/edge-admin/v1/users/{userId}/password',
                        {
                            params,
                            body: {
                                ...body,
                                temporaryPassword: form.temporaryPassword,
                            },
                        },
                    )
                    break
                case 'unlock':
                    result = await api.POST(
                        '/api/edge-admin/v1/users/{userId}/unlock',
                        { params, body },
                    )
                    break
                case 'end-sessions':
                    result = await api.POST(
                        '/api/edge-admin/v1/users/{userId}/end-sessions',
                        { params, body },
                    )
                    break
                case 'delete':
                    result = await api.DELETE(
                        '/api/edge-admin/v1/users/{userId}',
                        { params: { ...params, query: body } },
                    )
                    break
            }
            if (!result.data || (form.action !== 'delete' && !result.data.user))
                throw new Error('missing-user-result')
        },
    }
}
