import { QueryClient } from '@tanstack/react-query'
import { create } from 'zustand'
import type { components } from '../api/schema'
import { createGatewayClient } from '../api/client'
import { ApiError } from '../api/errors'

type Session = components['schemas']['SessionResponse']
// Memory only: neither the session CSRF token nor credentials enter localStorage/cookies managed by JS.
export const useSession = create<{
    value: Session | null | undefined
    epoch: number
}>(() => ({ value: undefined, epoch: 0 }))
export const queryClient = new QueryClient({
    defaultOptions: {
        queries: {
            retry: false,
            refetchOnWindowFocus: false,
            gcTime: 5 * 60_000,
        },
        mutations: { retry: false },
    },
})
export function replaceSession(value: Session | null) {
    void queryClient.cancelQueries()
    queryClient.clear()
    useSession.setState((state) => ({ value, epoch: state.epoch + 1 }))
}
export const api = createGatewayClient({
    baseUrl: window.location.origin,
    csrf: () => useSession.getState().value?.csrfToken,
    epoch: () => useSession.getState().epoch,
    unauthorized: () => replaceSession(null),
})
export async function restoreSession(signal: AbortSignal) {
    try {
        const { data } = await api.GET('/api/edge-admin/v1/auth/session', {
            signal,
        })
        if (!data) throw new Error('missing-session')
        useSession.setState({ value: data })
        return data
    } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null
        throw error
    }
}
