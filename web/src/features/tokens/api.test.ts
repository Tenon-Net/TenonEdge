import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createTokensApi } from './api'
function client(fetcher: typeof fetch) {
    return createTokensApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'test-csrf',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('reads bounded metadata and creator version without an Authorization header', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        expect(request.method).toBe('GET')
        expect(request.headers.has('Authorization')).toBe(false)
        return Response.json({
            items: [],
            version: 'test-version',
            nextCursor: null,
        })
    })
    await api.list(17)
    await api.creator(42)
    expect(new URL(requests[0].url).searchParams.get('after')).toBe('17')
    expect(new URL(requests[0].url).searchParams.get('limit')).toBe('50')
    expect(new URL(requests[1].url).pathname).toBe(
        '/api/edge-admin/v1/users/42/tokens',
    )
})
it('returns the once-only secret to its direct caller and binds creation to Cookie CSRF', async () => {
    const api = client(async (input) => {
        const request = input as Request
        expect(request.headers.get('X-CSRF-Token')).toBe('test-csrf')
        expect(await request.json()).toEqual({
            name: 'MES',
            role: 'viewer',
            expiresAt: '2030-01-01T00:00:00.000Z',
        })
        return Response.json({
            token: { id: 42 },
            secret: 'test-opaque-secret',
        })
    })
    expect(
        (
            await api.create({
                name: 'MES',
                role: 'viewer',
                expiresAt: '2030-01-01T00:00:00Z',
            })
        ).secret,
    ).toBe('test-opaque-secret')
})
it('requires explicit revocation and never retries an unknown result', async () => {
    const fetcher = vi.fn(async () => {
        throw new TypeError('lost response')
    })
    const api = client(fetcher)
    await expect(api.revoke(42, false)).rejects.toThrow('token-confirmation')
    expect(fetcher).not.toHaveBeenCalled()
    await expect(api.revoke(42, true)).rejects.toThrow('lost response')
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it.each([403, 409, 503])(
    'does not replay creation on HTTP %i',
    async (status) => {
        const fetcher = vi.fn(async () =>
            Response.json({ code: 'token.test' }, { status }),
        )
        await expect(
            client(fetcher).create({
                name: 'MES',
                role: 'viewer',
                expiresAt: '',
            }),
        ).rejects.toMatchObject({ status })
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
