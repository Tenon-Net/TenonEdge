import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import {
    createParameterApi,
    editParameters,
    parameterRequest,
    type RuntimeParameters,
} from './parameters-api'
const state: RuntimeParameters = {
    version: 'a'.repeat(32),
    deploymentAuditMinimumDays: 365,
    values: {
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
        auditRetentionDays: '180',
        previewGlobalLimit: '1',
        previewSessionLimit: '1',
    }
    expect(parameterRequest(form, true).values).toEqual({
        auditRetentionDays: 180,
        previewGlobalLimit: 1,
        previewSessionLimit: 1,
    })
    form.values = {
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
