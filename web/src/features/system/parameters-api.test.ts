import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import {
    createParameterApi,
    editParameters,
    parameterRequest,
    type RuntimeParameters,
} from './parameters-api'
const debug = {
    debugObjectBytes: 1048576,
    debugGlobalBytes: 33554432,
    debugObjectRecords: 100,
    inFlightBytes: 67108864,
    inputChannelCapacity: 100,
    planSources: 2000,
    planConnections: 500,
    planModbusConnections: 256,
    planPathsPerSource: 32,
    planTotalPaths: 20000,
}
const debugForm = {
    debugObjectBytes: '1048576',
    debugGlobalBytes: '33554432',
    debugObjectRecords: '100',
    inFlightBytes: '67108864',
    inputChannelCapacity: '100',
    planSources: '2000',
    planConnections: '500',
    planModbusConnections: '256',
    planPathsPerSource: '32',
    planTotalPaths: '20000',
}
const state: RuntimeParameters = {
    version: 'a'.repeat(32),
    deploymentAuditMinimumDays: 365,
    values: {
        ...debug,
        auditRetentionDays: 730,
        previewGlobalLimit: 8,
        previewSessionLimit: 3,
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
it('freezes values and version without mutating the server read', () => {
    const form = editParameters(state)
    form.values.auditRetentionDays = '1000'
    expect(state.values.auditRetentionDays).toBe(730)
    expect(parameterRequest(form, true)).toEqual({
        expectedVersion: state.version,
        confirmed: true,
        values: {
            ...debug,
            auditRetentionDays: 1000,
            previewGlobalLimit: 8,
            previewSessionLimit: 3,
        },
    })
    expect(() => parameterRequest(form, false)).toThrow(
        'parameters-confirmation',
    )
})
it.each([
    ['auditRetentionDays', '180'],
    ['auditRetentionDays', '36501'],
    ['auditRetentionDays', '1e3'],
    ['previewGlobalLimit', '0'],
    ['previewGlobalLimit', '65'],
    ['previewGlobalLimit', '1.5'],
    ['previewSessionLimit', '0'],
    ['previewSessionLimit', '9'],
    ['debugObjectBytes', '67583'],
    ['debugObjectBytes', '67108865'],
    ['debugGlobalBytes', '1048575'],
    ['debugGlobalBytes', '1073741825'],
    ['debugObjectRecords', '0'],
    ['debugObjectRecords', '101'],
    ['planSources', '0'],
    ['planSources', '2001'],
    ['planConnections', '0'],
    ['planConnections', '501'],
    ['planModbusConnections', '0'],
    ['planModbusConnections', '257'],
    ['planPathsPerSource', '0'],
    ['planPathsPerSource', '33'],
    ['planTotalPaths', '0'],
    ['planTotalPaths', '20001'],
    ['inputChannelCapacity', '0'],
    ['inputChannelCapacity', '10001'],
    ['inputChannelCapacity', '1.5'],
    ['inFlightBytes', '16777215'],
    ['inFlightBytes', '1073741825'],
] as const)('rejects %s=%s', (key, value) => {
    const form = editParameters(state)
    form.values[key] = value
    expect(() => parameterRequest(form, true)).toThrow('parameters-range')
})
it('accepts edges, including a first write, but refuses malformed version', () => {
    const form = editParameters({
        ...state,
        version: null,
        deploymentAuditMinimumDays: 180,
    })
    form.values = {
        ...debugForm,
        auditRetentionDays: '180',
        previewGlobalLimit: '1',
        previewSessionLimit: '1',
    }
    expect(parameterRequest(form, true).values).toEqual({
        ...debug,
        auditRetentionDays: 180,
        previewGlobalLimit: 1,
        previewSessionLimit: 1,
    })
    form.values = {
        ...debugForm,
        auditRetentionDays: '36500',
        previewGlobalLimit: '64',
        previewSessionLimit: '64',
    }
    expect(parameterRequest(form, true).values.previewGlobalLimit).toBe(64)
    expect(() =>
        parameterRequest({ ...form, expectedVersion: 'invalid' }, true),
    ).toThrow('parameters-version')
})
it('posts only declared writable fields with CSRF and explicit confirmation', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        const request = input as Request
        expect(request.method).toBe('PUT')
        expect(request.headers.get('X-CSRF-Token')).toBe('csrf')
        expect(await request.json()).toEqual(
            parameterRequest(editParameters(state), true),
        )
        return Response.json(state)
    })
    await expect(
        createParameterApi(client(fetcher)).write(editParameters(state), false),
    ).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    expect(
        await createParameterApi(client(fetcher)).write(
            editParameters(state),
            true,
        ),
    ).toEqual(state)
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it.each([409, 500, 'lost'] as const)(
    'never replays unknown/conflicting outcome %s',
    async (failure) => {
        const fetcher = vi.fn(async () => {
            if (failure === 'lost') throw new TypeError('lost')
            return Response.json({ status: failure }, { status: failure })
        })
        await expect(
            createParameterApi(client(fetcher)).write(
                editParameters(state),
                true,
            ),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
