import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
export type RuntimeParameters = components['schemas']['RuntimeParameterState']
export type ParameterValues = components['schemas']['RuntimeParameterValues']
export type ParameterForm = {
    expectedVersion: string | null
    minimumAuditDays: number
    values: Record<keyof ParameterValues, string>
}
export function editParameters(state: RuntimeParameters): ParameterForm {
    return {
        expectedVersion: state.version,
        minimumAuditDays: Number(state.deploymentAuditMinimumDays),
        values: {
            auditRetentionDays: String(state.values.auditRetentionDays),
            previewGlobalLimit: String(state.values.previewGlobalLimit),
            previewSessionLimit: String(state.values.previewSessionLimit),
        },
    }
}
export function parameterRequest(form: ParameterForm, confirmed: boolean) {
    if (!confirmed) throw new Error('parameters-confirmation')
    function integer(value: string, min: number, max: number) {
        if (
            !/^\d+$/.test(value.trim()) ||
            Number(value) < min ||
            Number(value) > max
        )
            throw new Error('parameters-range')
        return Number(value)
    }
    const global = integer(form.values.previewGlobalLimit, 1, 64)
    if (
        form.expectedVersion !== null &&
        !/^[0-9a-f]{32}$/.test(form.expectedVersion)
    )
        throw new Error('parameters-version')
    return {
        expectedVersion: form.expectedVersion,
        confirmed: true,
        values: {
            auditRetentionDays: integer(
                form.values.auditRetentionDays,
                Math.max(180, form.minimumAuditDays),
                36500,
            ),
            previewGlobalLimit: global,
            previewSessionLimit: integer(
                form.values.previewSessionLimit,
                1,
                global,
            ),
        },
    }
}
export function createParameterApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async read(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/system/runtime-parameters',
                { signal },
            )
            if (!data) throw new Error('missing-parameters')
            return data
        },
        async write(form: ParameterForm, confirmed: boolean) {
            const { data } = await api.PUT(
                '/api/edge-admin/v1/system/runtime-parameters',
                { body: parameterRequest(form, confirmed) },
            )
            if (!data) throw new Error('missing-parameters')
            return data
        },
    }
}
