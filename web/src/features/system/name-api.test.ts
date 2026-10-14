import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createSystemApi, gatewayNameRequest } from './api'

function client(fetcher: typeof fetch) {
    return createSystemApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'csrf',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('normalizes the new name but keeps the original-name precondition exact, including null', () => {
    expect(gatewayNameRequest('  现场😀  ', null)).toEqual({
        name: '现场😀',
        expectedName: null,
    })
    expect(gatewayNameRequest(' \t\n ', '  original  ')).toEqual({
        name: null,
        expectedName: '  original  ',
    })
    expect(gatewayNameRequest('x'.repeat(64), 'before').name).toHaveLength(64)
    expect(gatewayNameRequest('😀'.repeat(32), null).name).toHaveLength(64)
})
it.each([
    'x'.repeat(65),
    '😀'.repeat(33),
    'a\0b',
    'a\nb',
    'a\tb',
    'a\u007fb',
    'a\u0085b',
])('rejects invalid normalized name', (name) => {
    expect(() => gatewayNameRequest(name, null)).toThrow('gateway-name')
})
it('requires confirmation and sends only writable fields plus CSRF, never NodeId', async () => {
    let call: Request | undefined
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
        call = input as Request
        return Response.json({ name: 'after', changed: true })
    })
    const api = client(fetcher)
    const request = {
        ...gatewayNameRequest('after', 'before'),
        nodeId: 'never-change',
    }
    await expect(api.writeName(request, false)).rejects.toThrow(
        'name-confirmation',
    )
    expect(fetcher).not.toHaveBeenCalled()
    await expect(api.writeName(request, true)).resolves.toEqual({
        name: 'after',
        changed: true,
    })
    expect(call?.headers.get('X-CSRF-Token')).toBe('csrf')
    expect(await call?.json()).toEqual({
        name: 'after',
        expectedName: 'before',
    })
})
it.each([400, 409, 500, 'lost'] as const)(
    'never replays failed rename %s',
    async (failure) => {
        const fetcher = vi.fn(async () => {
            if (failure === 'lost') throw new TypeError('lost-response')
            return Response.json({ status: failure }, { status: failure })
        })
        await expect(
            client(fetcher).writeName(gatewayNameRequest('after', null), true),
        ).rejects.toThrow()
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('keeps an unchanged or cleared name as a successful explicit result', async () => {
    await expect(
        client(async () =>
            Response.json({ name: null, changed: false }),
        ).writeName(gatewayNameRequest('', null), true),
    ).resolves.toEqual({ name: null, changed: false })
})
