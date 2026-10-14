import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'

export type QueueParameters = components['schemas']['QueueParameterState']
export type QueueParameterValues = components['schemas']['QueueParameterValues']
export type QueueParameterForm = {
    expectedVersion: string | null
    values: Record<keyof QueueParameterValues, string>
}
const maximumBytes = 1099511627776
export const queueParameterRanges: Record<
    keyof QueueParameterValues,
    readonly [number, number]
> = {
    maximumPendingPerTarget: [1, 1000000],
    maximumPendingBytesPerTarget: [1, maximumBytes],
    maximumQueueBytes: [1, maximumBytes],
    pendingRetentionHours: [1, 87600],
    maximumFailedPerTarget: [1, 1000000],
    maximumFailedGlobal: [1, 1000000],
    failedRetentionHours: [1, 87600],
    minimumDiskFreeBytes: [0, maximumBytes],
    minimumDiskFreePercent: [0, 99],
}
export const queueParameterFields = Object.keys(
    queueParameterRanges,
) as (keyof QueueParameterValues)[]

export function editQueueParameters(
    state: QueueParameters,
): QueueParameterForm {
    return {
        expectedVersion: state.version,
        values: Object.fromEntries(
            queueParameterFields.map((key) => [key, String(state.values[key])]),
        ) as QueueParameterForm['values'],
    }
}
export function queueParameterRequest(
    form: QueueParameterForm,
    confirmed: boolean,
) {
    if (!confirmed) throw new Error('queue-parameters-confirmation')
    if (
        form.expectedVersion !== null &&
        !/^[0-9a-f]{32}$/.test(form.expectedVersion)
    )
        throw new Error('queue-parameters-version')
    const values = Object.fromEntries(
        queueParameterFields.map((key) => {
            const text = form.values[key].trim()
            const value = Number(text)
            const [min, max] = queueParameterRanges[key]
            if (
                !/^\d+$/.test(text) ||
                !Number.isSafeInteger(value) ||
                value < min ||
                value > max
            )
                throw new Error('queue-parameters-range')
            return [key, value]
        }),
    ) as QueueParameterValues
    return { expectedVersion: form.expectedVersion, confirmed: true, values }
}
export function createQueueParameterApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async read(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/system/queue-parameters',
                { signal },
            )
            if (!data) throw new Error('missing-queue-parameters')
            return data
        },
        async write(form: QueueParameterForm, confirmed: boolean) {
            const { data } = await api.PUT(
                '/api/edge-admin/v1/system/queue-parameters',
                { body: queueParameterRequest(form, confirmed) },
            )
            if (!data) throw new Error('missing-queue-parameters')
            return data
        },
    }
}
