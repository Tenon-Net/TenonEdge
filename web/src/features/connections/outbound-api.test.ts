import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createSystemApi } from '../system/api'
import { connectionForm } from './model'
import {
    createOutboundInspectionApi,
    outboundGrantRequest,
    outboundTarget,
    type OutboundInspection,
} from './outbound-api'

const view: OutboundInspection = {
    host: 'fixture.invalid',
    port: 443,
    policy: {
        version: 'original',
        networks: [{ cidr: '198.51.100.5/32', port: 80 }],
        localPorts: [1883],
    },
    addresses: [
        {
            address: '203.0.113.5',
            decision: 'publicGrantRequired',
            grant: { cidr: '203.0.113.5/32', port: 443 },
        },
        {
            address: '2001:db8::5',
            decision: 'publicGrantRequired',
            grant: { cidr: '2001:db8::5/128', port: 443 },
        },
        { address: '127.0.0.1', decision: 'localPortRequired', grant: null },
        { address: '169.254.169.254', decision: 'forbidden', grant: null },
    ],
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
it.each(['modbus', 'mqtt'] as const)(
    'takes only the %s host and port, never credentials or other connection fields',
    (kind) => {
        const form = {
            ...connectionForm('version'),
            kind,
            host: 'broker.example',
            port: '8883',
            clientId: 'not-sent',
        }
        expect(outboundTarget(form)).toEqual({
            host: 'broker.example',
            port: 8883,
        })
    },
)
it.each([
    ['https://example.com/path', 'example.com', 443],
    ['http://example.com/path', 'example.com', 80],
    ['https://[2001:db8::5]:8443/path', '2001:db8::5', 8443],
])(
    'extracts HTTP authority without path or headers',
    (baseAddress, host, port) => {
        expect(
            outboundTarget({
                ...connectionForm('v'),
                kind: 'http',
                baseAddress: String(baseAddress),
                headers: [{ name: 'x-site', value: 'not-sent' }],
            }),
        ).toEqual({ host, port })
    },
)
it.each(['ftp://example.com', 'https://user:password@example.com', 'invalid'])(
    'refuses invalid or credential-bearing authority %s',
    (baseAddress) => {
        expect(() =>
            outboundTarget({
                ...connectionForm('v'),
                kind: 'http',
                baseAddress,
            }),
        ).toThrow()
    },
)
it('appends exact public hosts to the original version without changing existing networks or local exceptions', () => {
    const before = JSON.stringify(view)
    expect(outboundGrantRequest(view)).toEqual({
        expectedVersion: 'original',
        networks: [
            ...view.policy.networks,
            ...view.addresses.slice(0, 2).map((a) => a.grant),
        ],
        localPorts: [1883],
    })
    expect(JSON.stringify(view)).toBe(before)
    const duplicate = {
        ...view,
        addresses: [...view.addresses, view.addresses[0]],
    }
    expect(outboundGrantRequest(duplicate).networks).toHaveLength(3)
})
it('refuses empty or over-limit proposals, never partially authorizes or converts a local denial to CIDR', () => {
    expect(() =>
        outboundGrantRequest({ ...view, addresses: view.addresses.slice(2) }),
    ).toThrow('outbound-no-grants')
    expect(() =>
        outboundGrantRequest({
            ...view,
            policy: {
                ...view.policy,
                networks: Array.from({ length: 1000 }, (_, i) => ({
                    cidr: `198.51.${Math.floor(i / 256)}.${i % 256}/32`,
                    port: 443,
                })),
            },
        }),
    ).toThrow('outbound-grant-limit')
})
it('inspection posts only target fields with CSRF and cancellation, never writes rules', async () => {
    let call: Request | undefined
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        call = input as Request
        return new Promise<Response>((_resolve, reject) => {
            call!.signal.addEventListener(
                'abort',
                () => reject(new DOMException('Aborted', 'AbortError')),
                { once: true },
            )
        })
    })
    const controller = new AbortController()
    const target = { host: 'fixture.invalid', port: 443, secret: 'never-sent' }
    const pending = createOutboundInspectionApi(client(fetcher))(
        target,
        controller.signal,
    )
    await vi.waitFor(() => expect(call).toBeDefined())
    expect(call?.url).toContain('/connections/inspect-outbound')
    expect(call?.headers.get('X-CSRF-Token')).toBe('csrf')
    expect(await call?.json()).toEqual({ host: 'fixture.invalid', port: 443 })
    const rejected = expect(pending).rejects.toThrow('Aborted')
    controller.abort()
    await rejected
    expect(call?.signal.aborted).toBe(true)
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it.each([409, 500, 'lost'] as const)(
    'never replays public grant outcome %s',
    async (failure) => {
        const fetcher = vi.fn(async () => {
            if (failure === 'lost') throw new TypeError('lost')
            return Response.json({ status: failure }, { status: failure })
        })
        await expect(
            createSystemApi(client(fetcher)).writePolicy(
                outboundGrantRequest(view),
                true,
            ),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
