import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import {
    createQueueParameterApi,
    editQueueParameters,
    queueParameterRequest,
    queueParameterFields,
    queueParameterRanges,
    type QueueParameters,
} from './queue-parameters-api'
const state: QueueParameters = {
    version: 'a'.repeat(32),
    values: {
        maximumPendingPerTarget: 10000,
        maximumPendingBytesPerTarget: 67108864,
        maximumQueueBytes: 268435456,
        pendingRetentionHours: 24,
        maximumFailedPerTarget: 100,
        maximumFailedGlobal: 1000,
        failedRetentionHours: 168,
        minimumDiskFreeBytes: 500000000,
        minimumDiskFreePercent: 5,
    },
}
function client(fetcher: typeof fetch) {
    return createGatewayClient({
        baseUrl: 'https://gateway.invalid',
        csrf: () => 'csrf',
        epoch: () => 0,
        unauthorized: () => {},
        fetcher,
    })
}
it('freezes the version and values, requires confirmation and excludes read-only fields', () => {
    const form = editQueueParameters(state)
    form.values.maximumQueueBytes = '1'
    expect(state.values.maximumQueueBytes).toBe(268435456)
    expect(queueParameterRequest(form, true)).toEqual({
        expectedVersion: state.version,
        confirmed: true,
        values: { ...state.values, maximumQueueBytes: 1 },
    })
    expect(() => queueParameterRequest(form, false)).toThrow('confirmation')
    expect(() =>
        queueParameterRequest({ ...form, expectedVersion: 'invalid' }, true),
    ).toThrow('version')
})
it.each(queueParameterFields)(
    'checks both bounds and invalid integers for %s',
    (key) => {
        const form = editQueueParameters({ ...state, version: null })
        const [low, high] = queueParameterRanges[key]
        for (const value of [low, high]) {
            form.values[key] = String(value)
            expect(queueParameterRequest(form, true).values[key]).toBe(value)
        }
        for (const value of [
            String(low - 1),
            String(high + 1),
            '',
            'NaN',
            'Infinity',
            '1.5',
            '1e3',
            '-1',
            '9'.repeat(80),
        ]) {
            form.values[key] = value
            expect(() => queueParameterRequest(form, true)).toThrow('range')
        }
    },
)
it('uses the declared endpoint with CSRF, never writes before confirmation', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        if (!(input instanceof Request)) throw new Error('expected request')
        expect(input.url).toBe(
            'https://gateway.invalid/api/edge-admin/v1/system/queue-parameters',
        )
        if (input.method === 'PUT') {
            expect(input.headers.get('X-CSRF-Token')).toBe('csrf')
            expect(await input.json()).toEqual(
                queueParameterRequest(editQueueParameters(state), true),
            )
        }
        return Response.json(state)
    })
    const api = createQueueParameterApi(client(fetcher))
    await expect(api.write(editQueueParameters(state), false)).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    expect(await api.read()).toEqual(state)
    expect(await api.write(editQueueParameters(state), true)).toEqual(state)
    expect(fetcher).toHaveBeenCalledTimes(2)
})
it.each([409, 500, 'lost'] as const)(
    'does not replay an unknown outcome %s',
    async (failure) => {
        const fetcher = vi.fn(async () => {
            if (failure === 'lost') throw new TypeError('lost')
            return Response.json({ status: failure }, { status: failure })
        })
        await expect(
            createQueueParameterApi(client(fetcher)).write(
                editQueueParameters(state),
                true,
            ),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
