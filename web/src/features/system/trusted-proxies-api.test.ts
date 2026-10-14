import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import {
    createTrustedProxyApi,
    editTrustedProxies,
    trustedProxyRequest,
    type TrustedProxyState,
} from './trusted-proxies-api'
const state: TrustedProxyState = {
    version: 'a'.repeat(32),
    networks: ['192.0.2.1/32'],
    deployment: ['198.51.100.1/32'],
}
it('freezes the editable rules, never round-trips deployment and requires confirmation', () => {
    const form = editTrustedProxies(state)
    form.networks = ' 2001:db8::/64\n\n192.0.2.2\r\n'
    expect(state.networks).toEqual(['192.0.2.1/32'])
    expect(trustedProxyRequest(form, true)).toEqual({
        expectedVersion: state.version,
        networks: ['2001:db8::/64', '192.0.2.2'],
        confirmed: true,
    })
    expect(() => trustedProxyRequest(form, false)).toThrow('confirmation')
    expect(
        trustedProxyRequest({ networks: '', expectedVersion: null }, true)
            .networks,
    ).toEqual([])
})
it.each([
    'x'.repeat(81),
    Array(1001).fill('192.0.2.1').join('\n'),
    '192.0.2.1\u0000',
])('rejects excessive or controlled rules', (networks) => {
    expect(() =>
        trustedProxyRequest({ networks, expectedVersion: null }, true),
    ).toThrow('range')
})
it('accepts the declared maximum count and validates version', () => {
    expect(
        trustedProxyRequest(
            {
                networks: Array(1000).fill('192.0.2.1').join('\n'),
                expectedVersion: null,
            },
            true,
        ).networks,
    ).toHaveLength(1000)
    expect(() =>
        trustedProxyRequest({ networks: '', expectedVersion: '' }, true),
    ).toThrow('version')
})
it.each([409, 500, 'lost'] as const)(
    'never replays trust writes after %s',
    async (failure) => {
        const fetcher = vi.fn(async (input: RequestInfo | URL) => {
            if (!(input instanceof Request)) throw new Error('request')
            expect(input.method).toBe('PUT')
            expect(input.headers.get('X-CSRF-Token')).toBe('csrf')
            expect(input.url).toBe(
                'https://gateway.invalid/api/edge-admin/v1/system/trusted-proxies',
            )
            expect(await input.json()).toEqual(
                trustedProxyRequest(editTrustedProxies(state), true),
            )
            if (failure === 'lost') throw new TypeError('lost')
            return Response.json({ status: failure }, { status: failure })
        })
        const api = createTrustedProxyApi(
            createGatewayClient({
                baseUrl: 'https://gateway.invalid',
                csrf: () => 'csrf',
                epoch: () => 0,
                unauthorized: () => {},
                fetcher,
            }),
        )
        await expect(
            api.write(editTrustedProxies(state), false),
        ).rejects.toThrow('confirmation')
        expect(fetcher).not.toHaveBeenCalled()
        await expect(
            api.write(editTrustedProxies(state), true),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
