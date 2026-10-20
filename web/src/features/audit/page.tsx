import { PageHeading } from '../../components/layout/page'
import {
    Table,
    TableHeader,
    TableBody,
    TableRow,
    TableHead,
    TableCell,
    TableCaption,
} from '../../components/ui/table'
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { api, useSession } from '../../lib/session'
import { dateText } from '../diagnostics/view'
import { createAuditApi, emptyFilter, readFilter } from './api'
import { AuditExportPanel } from './export'
const audit = createAuditApi(api)
const columns = [
    'id',
    'occurredAt',
    'accountName',
    'tokenName',
    'sourceIp',
    'action',
    'target',
    'result',
    'detail',
] as const
export function AuditPage() {
    const { t } = useTranslation()
    const allowed = useSession((s) => s.value?.role) === 'administrator'
    const epoch = useSession((s) => s.epoch)
    const [form, setForm] = useState(emptyFilter)
    const [applied, setApplied] = useState(emptyFilter)
    const [cursors, setCursors] = useState<number[]>([])
    const [invalid, setInvalid] = useState(false)
    const before = cursors.at(-1)
    const filter = readFilter(applied)
    const dirty = JSON.stringify(form) !== JSON.stringify(applied)
    const query = useQuery({
        queryKey: ['audit', epoch, filter, before],
        enabled: allowed,
        queryFn: ({ signal }) => audit.list(filter, before, signal),
        gcTime: 0,
        retry: false,
    })
    if (!allowed) return <p role="alert">{t('audit.adminOnly')}</p>
    return (
        <div className="product-page">
            <PageHeading
                hideTitle
                description={t('audit.notice')}
                title={t('audit.title')}
            />

            <form
                className="product-toolbar"
                onSubmit={(event) => {
                    event.preventDefault()
                    try {
                        readFilter(form)
                        setApplied({ ...form })
                        setCursors([])
                        setInvalid(false)
                    } catch {
                        setInvalid(true)
                    }
                }}
            >
                {(['from', 'to', 'account', 'action', 'target'] as const).map(
                    (key) => (
                        <label key={key} className="text-sm">
                            {t(`audit.${key}`)}
                            <input
                                className="mt-1 block rounded border bg-background p-2"
                                type={
                                    key === 'from' || key === 'to'
                                        ? 'datetime-local'
                                        : 'text'
                                }
                                maxLength={key === 'action' ? 128 : 64}
                                value={form[key]}
                                onChange={(e) =>
                                    setForm({ ...form, [key]: e.target.value })
                                }
                            />
                        </label>
                    ),
                )}
                <Button type="submit">{t('audit.apply')}</Button>
                <Button
                    type="button"
                    variant="outline"
                    disabled={query.isFetching}
                    onClick={() => void query.refetch()}
                >
                    {t('audit.refresh')}
                </Button>
            </form>
            {invalid && <p role="alert">{t('audit.invalid')}</p>}
            {dirty && <p role="status">{t('audit.dirty')}</p>}
            {query.isPending && <p role="status">{t('audit.loading')}</p>}
            {query.error && <p role="alert">{t('audit.readFailed')}</p>}
            {!query.error && query.data && (
                <>
                    <div className="overflow-x-auto rounded-lg border">
                        <Table className="w-full text-left text-sm">
                            <TableCaption className="sr-only">
                                {t('audit.title')}
                            </TableCaption>
                            <TableHeader className="bg-muted">
                                <TableRow>
                                    {columns.map((c) => (
                                        <TableHead key={c} className="p-3">
                                            {t(`audit.columns.${c}`)}
                                        </TableHead>
                                    ))}
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                {query.data.items.map((row) => (
                                    <TableRow
                                        key={row.id}
                                        className="border-t align-top"
                                    >
                                        {columns.map((c) => (
                                            <TableCell
                                                key={c}
                                                className="max-w-sm break-words p-3"
                                            >
                                                {c === 'detail'
                                                    ? row.detail && (
                                                          <details>
                                                              <summary className="cursor-pointer">
                                                                  {t(
                                                                      'audit.columns.detail',
                                                                  )}
                                                              </summary>
                                                              <pre className="max-h-64 overflow-auto whitespace-pre-wrap">
                                                                  {row.detail}
                                                              </pre>
                                                          </details>
                                                      )
                                                    : c === 'occurredAt'
                                                      ? dateText(row.occurredAt)
                                                      : (row[c] ?? '—')}
                                            </TableCell>
                                        ))}
                                    </TableRow>
                                ))}
                            </TableBody>
                        </Table>
                    </div>
                    {!query.data.items.length && <p>{t('audit.empty')}</p>}
                    <div className="flex gap-3">
                        <Button
                            variant="outline"
                            disabled={!cursors.length || query.isFetching}
                            onClick={() => setCursors(cursors.slice(0, -1))}
                        >
                            {t('audit.previous')}
                        </Button>
                        <Button
                            variant="outline"
                            disabled={
                                !query.data.nextCursor || query.isFetching
                            }
                            onClick={() =>
                                query.data?.nextCursor &&
                                setCursors([...cursors, query.data.nextCursor])
                            }
                        >
                            {t('audit.next')}
                        </Button>
                    </div>
                </>
            )}
            <AuditExportPanel
                filter={filter}
                unavailable={dirty || query.isPending || !!query.error}
            />
        </div>
    )
}
