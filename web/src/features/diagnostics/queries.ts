import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { createDiagnosticsApi } from './api'

export const diagnostics = createDiagnosticsApi(api)
export function useRuntimeStatus() {
    return useQuery({
        queryKey: ['runtime-diagnostics'],
        queryFn: ({ signal }) => diagnostics.status(signal),
        refetchInterval: 2000,
        gcTime: 0,
    })
}
export function useSystemInfo() {
    return useQuery({
        queryKey: ['system-info'],
        queryFn: ({ signal }) => diagnostics.info(signal),
        refetchInterval: 30_000,
        gcTime: 0,
    })
}

export function useRuntimeDevices() {
    return useQuery({
        queryKey: ['runtime-devices'],
        queryFn: ({ signal }) => diagnostics.devices(signal),
        refetchInterval: 2000,
        gcTime: 0,
    })
}

export function useRuntimeOutputs(after?: string, revision?: string) {
    return useQuery({
        queryKey: ['runtime-outputs', after, revision],
        queryFn: ({ signal }) => diagnostics.outputs(after, revision, signal),
        refetchInterval: 2000,
        gcTime: 0,
    })
}
