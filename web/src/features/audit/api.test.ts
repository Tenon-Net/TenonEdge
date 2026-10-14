import { expect, it, vi } from 'vitest'
import { createGatewayClient } from '../../api/client'
import {
    auditDownloadHref,
    createAuditApi,
    emptyFilter,
    readFilter,
} from './api'
const guid = '12345678-1234-4321-8123-123456789012'
const ready = {
    id: guid,
    state: 'ready',
    busy: true,
    expiresAt: '2030-01-01T00:00:00Z',
    filter: {},
    count: '0',
    fileBytes: '83',
    upperId: 0,
    error: null,
}
function client(fetcher: typeof fetch) {
    return createAuditApi(
        createGatewayClient({
            baseUrl: 'https://gateway.invalid',
            csrf: () => 'csrf',
            epoch: () => 0,
            unauthorized: () => {},
            fetcher,
        }),
    )
}
it('sends exact applied filters with paging only to the list, not the complete export', async () => {
    const calls: Request[] = []
    const api = client(async (input) => {
        const request = input as Request
        calls.push(request)
        return Response.json(
            request.method === 'GET'
                ? { items: [], nextCursor: null }
                : {
                      ...ready,
                      state: 'generating',
                      count: null,
                      fileBytes: null,
                      upperId: null,
                  },
        )
    })
    const filter = readFilter({
        ...emptyFilter,
        account: 'alice',
        action: 'auth.login',
        target: 'object',
        from: '2026-01-01T00:00:00Z',
        to: '2026-02-01T00:00:00Z',
    })
    await api.list(filter, 123)
    const url = new URL(calls[0].url)
    expect(url.searchParams.get('account')).toBe('alice')
    expect(url.searchParams.get('before')).toBe('123')
    expect(url.searchParams.get('limit')).toBe('50')
    await api.start(filter, true)
    expect(calls[1].headers.get('X-CSRF-Token')).toBe('csrf')
    expect(await calls[1].json()).toEqual({ filter, confirmed: true })
    expect(calls).toHaveLength(2)
})
it.each(['from', 'to', 'account', 'action', 'target'] as const)(
    'rejects malformed %s before sending',
    (key) => {
        expect(() =>
            readFilter({
                ...emptyFilter,
                [key]:
                    key === 'from' || key === 'to'
                        ? 'invalid'
                        : 'x'.repeat(129),
            }),
        ).toThrow('audit-filter')
    },
)
it('rejects reversed or equal timestamps and keeps exact text filters', () => {
    for (const to of ['2026-01-01', '2025-01-01'])
        expect(() =>
            readFilter({ ...emptyFilter, from: '2026-01-01', to }),
        ).toThrow()
    expect(readFilter({ ...emptyFilter, target: ' exact ' }).target).toBe(
        ' exact ',
    )
})
it('requires explicit confirmation and never replays a lost response', async () => {
    const fetcher = vi.fn(async () => {
        throw new TypeError('response-lost')
    })
    const api = client(fetcher)
    await expect(api.start({}, false)).rejects.toThrow('audit-confirmation')
    expect(fetcher).not.toHaveBeenCalled()
    await expect(api.start({}, true)).rejects.toThrow('response-lost')
    expect(fetcher).toHaveBeenCalledTimes(1)
})
it('accepts a complete empty export and exposes only a same-origin native download URL', async () => {
    const api = client(async () => Response.json({ export: ready }))
    expect((await api.current()).export?.count).toBe('0')
    expect(auditDownloadHref(guid)).toBe(
        '/api/edge-admin/v1/audit/export/' + guid + '/download',
    )
    for (const id of [
        'https://evil.invalid',
        '../download',
        '00000000-0000-0000-0000-000000000000',
    ])
        expect(() => auditDownloadHref(id)).toThrow()
})
it.each([{ count: '9007199254740993' }, { fileBytes: '999999999999999999' }])(
    'keeps large export counts as exact strings',
    async (extra) => {
        const api = client(async () =>
            Response.json({ export: { ...ready, ...extra } }),
        )
        expect((await api.current()).export).toMatchObject(extra)
    },
)
it.each([
    { count: '1e2' },
    { fileBytes: '0' },
    { count: null },
    { upperId: '9007199254740993' },
    { state: 'unknown' },
])('does not allow a malformed ready file', async (extra) => {
    const api = client(async () =>
        Response.json({ export: { ...ready, ...extra } }),
    )
    await expect(api.current()).rejects.toThrow()
})
it('rejects unsafe audit row IDs rather than exposing rounded paging cursors', async () => {
    const api = client(async () =>
        Response.json({ items: [], nextCursor: '9007199254740993' }),
    )
    await expect(api.list({}, undefined)).rejects.toThrow('audit-id')
})
