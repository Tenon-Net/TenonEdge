import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import { createUsersApi, userForm, type User, type UserAction } from './api'
const user: User = {
    id: 9007199254740991,
    accountName: 'operator',
    displayName: null,
    role: 'viewer',
    isEnabled: true,
    mustChangePassword: false,
    lockedUntil: null,
    lastLoginAt: null,
    lastLoginIp: null,
    version: 'a'.repeat(64),
}
function client(fetcher: typeof fetch) {
    return createUsersApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'csrf-test',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('lists bounded pages without writing and creates only with explicit confirmation', async () => {
    const requests: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        requests.push(request)
        if (request.method === 'GET')
            return Response.json({ items: [user], nextCursor: null })
        expect(await request.json()).toEqual({
            accountName: 'new-user',
            displayName: null,
            role: 'viewer',
            isEnabled: true,
            temporaryPassword: 'Temporary-test-1234',
        })
        expect(request.headers.get('X-CSRF-Token')).toBe('csrf-test')
        return Response.json({ user })
    })
    expect((await api.list(0)).items).toEqual([user])
    expect(new URL(requests[0].url).searchParams.get('limit')).toBe('50')
    expect(requests[0].headers.has('X-CSRF-Token')).toBe(false)
    const form = {
        ...userForm('create'),
        accountName: 'new-user',
        temporaryPassword: 'Temporary-test-1234',
    }
    await expect(api.write(form, false)).rejects.toThrow('user-confirmation')
    expect(requests).toHaveLength(1)
    await api.write(form, true)
    expect(requests).toHaveLength(2)
})
it.each<UserAction>(['update', 'password', 'unlock', 'end-sessions', 'delete'])(
    'binds %s to the captured user version, confirmation and CSRF without changing account name',
    async (action) => {
        const fetcher = vi.fn(async (input: RequestInfo | URL) => {
            const request = input as Request
            const url = new URL(request.url)
            expect(url.pathname).toBe(
                '/api/edge-admin/v1/users/9007199254740991' +
                    (action === 'update' || action === 'delete'
                        ? ''
                        : '/' + action),
            )
            expect(request.headers.get('X-CSRF-Token')).toBe('csrf-test')
            if (action === 'delete') {
                expect(request.method).toBe('DELETE')
                expect(url.searchParams.get('confirmed')).toBe('true')
                expect(url.searchParams.get('expectedVersion')).toBe(
                    user.version,
                )
            } else {
                expect(request.method).toBe(
                    action === 'update' ? 'PUT' : 'POST',
                )
                const body = await request.json()
                expect(body.expectedVersion).toBe(user.version)
                expect(body.confirmed).toBe(true)
                expect(body.accountName).toBeUndefined()
                expect(body.temporaryPassword).toBe(
                    action === 'password' ? 'Temporary-test-1234' : undefined,
                )
            }
            return Response.json({ user: action === 'delete' ? null : user })
        })
        await client(fetcher).write(
            {
                ...userForm(action, user),
                accountName: 'cannot-change-account',
                temporaryPassword: 'Temporary-test-1234',
            },
            true,
        )
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it.each([403, 409, 503])(
    'does not replay HTTP %i and leaves error classification to the page',
    async (status) => {
        const fetcher = vi.fn(async () =>
            Response.json({ code: 'user.changed' }, { status }),
        )
        await expect(
            client(fetcher).write(userForm('end-sessions', user), true),
        ).rejects.toThrow(`HTTP ${status}`)
        expect(fetcher).toHaveBeenCalledTimes(1)
    },
)
it('does not replay a lost reply or accept missing user result', async () => {
    const fetcher = vi.fn(async () => {
        throw new TypeError('response-lost')
    })
    await expect(
        client(fetcher).write(userForm('password', user), true),
    ).rejects.toThrow('response-lost')
    expect(fetcher).toHaveBeenCalledTimes(1)
    await expect(
        client(async () => Response.json({})).write(
            userForm('update', user),
            true,
        ),
    ).rejects.toThrow('missing-user-result')
})
it('new editors never inherit a password and reject invalid ID/version before accessing the network', async () => {
    expect(userForm('password', user).temporaryPassword).toBe('')
    const fetcher = vi.fn()
    await expect(
        client(fetcher).write(
            userForm('delete', { ...user, version: 'stale' }),
            true,
        ),
    ).rejects.toThrow('user-version')
    await expect(
        client(fetcher).write(
            userForm('delete', { ...user, id: '9007199254740992' }),
            true,
        ),
    ).rejects.toThrow('user-version')
    expect(fetcher).not.toHaveBeenCalled()
})
