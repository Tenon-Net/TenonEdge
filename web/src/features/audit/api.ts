import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'

export type AuditFilter = components['schemas']['AuditFilter']
export type AuditExport = components['schemas']['AuditExportStatus']
export type FilterForm = Record<
    'from' | 'to' | 'account' | 'action' | 'target',
    string
>
export const emptyFilter: FilterForm = {
    from: '',
    to: '',
    account: '',
    action: '',
    target: '',
}
const root = '/api/edge-admin/v1/audit/export' as const
export function readFilter(form: FilterForm): AuditFilter {
    function date(value: string) {
        if (!value) return null
        if (!Number.isFinite(Date.parse(value))) throw new Error('audit-filter')
        return new Date(value).toISOString()
    }
    const from = date(form.from)
    const to = date(form.to)
    if (
        (from && to && from >= to) ||
        form.account.length > 64 ||
        form.action.length > 128 ||
        form.target.length > 64
    )
        throw new Error('audit-filter')
    return {
        from,
        to,
        account: form.account || null,
        action: form.action || null,
        target: form.target || null,
    }
}
function exportId(id: string) {
    if (
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
            id,
        ) ||
        id === '00000000-0000-0000-0000-000000000000'
    )
        throw new Error('audit-export-id')
    return id
}
function validate(value: AuditExport) {
    exportId(value.id)
    if (
        ![
            'generating',
            'ready',
            'downloading',
            'responseWritten',
            'failed',
            'cancelled',
            'expired',
        ].includes(value.state) ||
        typeof value.busy !== 'boolean' ||
        !Number.isFinite(Date.parse(value.expiresAt))
    )
        throw new Error('audit-export-status')
    for (const number of [value.count, value.fileBytes])
        if (
            number !== null &&
            (typeof number !== 'string' ||
                !/^(0|[1-9]\d*)$/.test(number) ||
                BigInt(number) > 9223372036854775807n)
        )
            throw new Error('audit-export-count')
    if (
        value.state === 'ready' &&
        (value.count === null ||
            value.fileBytes === null ||
            value.fileBytes === '0' ||
            value.upperId === null ||
            !Number.isSafeInteger(Number(value.upperId)) ||
            Number(value.upperId) < 0)
    )
        throw new Error('audit-export-file')
    return value
}
/** A native same-origin download, never a fetch/Blob containing the entire CSV. */
export function auditDownloadHref(id: string) {
    return `${root}/${exportId(id)}/download`
}
export function createAuditApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async list(
            filter: AuditFilter,
            before: number | undefined,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET('/api/edge-admin/v1/audit', {
                params: {
                    query: {
                        ...Object.fromEntries(
                            Object.entries(filter).filter(
                                ([, v]) => v !== null,
                            ),
                        ),
                        before,
                        limit: 50,
                    },
                },
                signal,
            })
            if (!data) throw new Error('audit-missing')
            function id(value: string | number) {
                const n = Number(value)
                if (!Number.isSafeInteger(n) || n < 1)
                    throw new Error('audit-id')
                return n
            }
            return {
                ...data,
                nextCursor:
                    data.nextCursor === null ? null : id(data.nextCursor),
                items: data.items.map((row) => ({ ...row, id: id(row.id) })),
            }
        },
        async current(signal?: AbortSignal) {
            const { data } = await api.GET(root, { signal })
            if (!data) throw new Error('audit-missing')
            return {
                export: data.export === null ? null : validate(data.export),
            }
        },
        async start(filter: AuditFilter, confirmed: boolean) {
            if (!confirmed) throw new Error('audit-confirmation')
            const { data } = await api.POST(root, {
                body: { filter, confirmed: true },
            })
            if (!data) throw new Error('audit-missing')
            return validate(data)
        },
        async cancel(id: string) {
            const { data } = await api.POST(
                '/api/edge-admin/v1/audit/export/{id}/cancel',
                { params: { path: { id: exportId(id) } } },
            )
            if (!data) throw new Error('audit-missing')
            return validate(data)
        },
    }
}
