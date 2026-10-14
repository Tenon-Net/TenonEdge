import { afterEach, expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createDiagnosticsApi } from '../diagnostics/api'
import {
    createSystemApi,
    editPolicy,
    policyRequest,
    policyRow,
    type OutboundView,
} from './api'

const version = '0123456789abcdef0123456789abcdef'
const view: OutboundView = {
    version,
    networks: [{ cidr: '203.0.113.0/24', port: 443 }],
    localPorts: [1883],
    deployment: {
        networks: [{ cidr: '198.51.100.0/24', port: 8883 }],
        localPorts: [2883],
    },
}
function gateway(fetcher: typeof fetch) {
    return createGatewayClient({
        baseUrl: 'https://gateway.invalid',
        csrf: () => 'csrf',
        epoch: () => 0,
        unauthorized: () => {},
        fetcher,
    })
}
afterEach(() => vi.unstubAllGlobals())

it('freezes the original version and edits only copied persistent grants', () => {
    const source = structuredClone(view)
    const form = editPolicy(source)
    source.version = 'f'.repeat(32)
    source.networks[0].cidr = 'changed'
    source.localPorts.push(1234)
    expect(policyRequest(form)).toEqual({
        expectedVersion: version,
        networks: view.networks,
        localPorts: [1883],
    })
    expect(form.networks).toHaveLength(1)
})
it('does not depend on secure-context Crypto APIs for component row identity', () => {
    vi.stubGlobal('crypto', undefined)
    const a = policyRow()
    const b = policyRow()
    expect(a.key).not.toBe(b.key)
    expect(
        policyRequest({
            expectedVersion: null,
            networks: [policyRow(' 2001:db8::/32 ', '65535')],
            localPorts: '1\r\n 65535 \n',
        }),
    ).toEqual({
        expectedVersion: null,
        networks: [{ cidr: '2001:db8::/32', port: 65535 }],
        localPorts: [1, 65535],
    })
})
it.each(['0', '-1', '65536', '1.5', '1e2', '+80', '0x50', '', 'NaN'])(
    'rejects invalid port %s for both kinds of rule',
    (port) => {
        expect(() =>
            policyRequest({
                expectedVersion: null,
                networks: [policyRow('203.0.113.0/24', port)],
                localPorts: '',
            }),
        ).toThrow('policy-port')
        if (port)
            expect(() =>
                policyRequest({
                    expectedVersion: null,
                    networks: [],
                    localPorts: port,
                }),
            ).toThrow('policy-port')
    },
)
it.each(['', '  ', 'x'.repeat(65)])(
    'rejects empty/overlong CIDR %s',
    (cidr) => {
        expect(() =>
            policyRequest({
                expectedVersion: null,
                networks: [policyRow(cidr)],
                localPorts: '',
            }),
        ).toThrow('policy-cidr')
    },
)
it('bounds both lists to 1000 and accepts clearing persistent rules', () => {
    const form = {
        expectedVersion: version,
        networks: Array.from({ length: 1000 }, () =>
            policyRow('203.0.113.0/24'),
        ),
        localPorts: Array(1000).fill('443').join('\n'),
    }
    expect(policyRequest(form).localPorts).toHaveLength(1000)
    expect(() =>
        policyRequest({ ...form, localPorts: form.localPorts + '\n443' }),
    ).toThrow('policy-limit')
    expect(() =>
        policyRequest({ ...form, networks: [...form.networks, policyRow()] }),
    ).toThrow('policy-limit')
    expect(
        policyRequest({
            expectedVersion: null,
            networks: [],
            localPorts: ' \n',
        }),
    ).toEqual({ expectedVersion: null, networks: [], localPorts: [] })
    expect(() =>
        policyRequest({ ...form, expectedVersion: 'bad-version' }),
    ).toThrow('policy-version')
})
it('requires confirmation, sends CSRF and only the three writable fields', async () => {
    const calls: Request[] = []
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        expect(input).toBeInstanceOf(Request)
        calls.push(input as Request)
        return Response.json(view)
    })
    const api = createSystemApi(gateway(fetcher))
    const write = {
        ...policyRequest(editPolicy(view)),
        deployment: view.deployment,
    }
    await expect(api.writePolicy(write, false)).rejects.toThrow(
        'policy-confirmation',
    )
    expect(fetcher).not.toHaveBeenCalled()
    await api.writePolicy(write, true)
    expect(calls[0].headers.get('X-CSRF-Token')).toBe('csrf')
    expect(await calls[0].json()).toEqual({
        expectedVersion: version,
        networks: view.networks,
        localPorts: [1883],
    })
})
it.each([400, 409, 500, 'lost'] as const)(
    'never replays failed write %s',
    async (failure) => {
        const fetcher = vi.fn(async () => {
            if (failure === 'lost') throw new TypeError('response-lost')
            return Response.json({ status: failure }, { status: failure })
        })
        await expect(
            createSystemApi(gateway(fetcher)).writePolicy(
                policyRequest(editPolicy(view)),
                true,
            ),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('preserves invalid-deployment null instead of representing it as no grants', async () => {
    const api = createSystemApi(
        gateway(async () => Response.json({ ...view, deployment: null })),
    )
    expect((await api.policy()).deployment).toBeNull()
})
it('uses the real system-info decoder and retains large byte counts exactly', async () => {
    const api = createDiagnosticsApi(
        gateway(
            async () =>
                new Response(
                    '{"dataDirectoryDisk":{"totalBytes":9007199254740993,"availableBytes":9007199254740992}}',
                ),
        ),
    )
    expect((await api.info()).dataDirectoryDisk).toEqual({
        totalBytes: '9007199254740993',
        availableBytes: '9007199254740992',
    })
})
