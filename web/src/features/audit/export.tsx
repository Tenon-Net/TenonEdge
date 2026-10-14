import { useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { api, useSession } from '../../lib/session'
import { dateText } from '../diagnostics/view'
import { auditDownloadHref, createAuditApi, type AuditFilter } from './api'
const audit = createAuditApi(api)
export function AuditExportPanel({
    filter,
    unavailable,
}: {
    filter: AuditFilter
    unavailable: boolean
}) {
    const { t } = useTranslation()
    const epoch = useSession((s) => s.epoch)
    const queries = useQueryClient()
    const key = ['audit-export', epoch]
    const fingerprint = JSON.stringify(filter)
    const [confirmed, confirm] = useState<string>()
    const [downloaded, markDownloaded] = useState<string>()
    const [uncertain, setUncertain] = useState(false)
    const sending = useRef(false)
    const query = useQuery({
        queryKey: key,
        queryFn: ({ signal }) => audit.current(signal),
        gcTime: 0,
        retry: false,
        refetchInterval: (q) =>
            !q.state.error && q.state.data?.export?.busy ? 1000 : false,
    })
    const command = useMutation({
        retry: false,
        mutationFn: (action: { filter: AuditFilter } | { id: string }) =>
            'filter' in action
                ? audit.start(action.filter, true)
                : audit.cancel(action.id),
        onMutate: async () => {
            confirm(undefined)
            await queries.cancelQueries({ queryKey: key })
        },
        onSuccess: (value) => queries.setQueryData(key, { export: value }),
        onError: () => setUncertain(true),
        onSettled: () => {
            sending.current = false
            void queries.invalidateQueries({ queryKey: key })
        },
    })
    const current = query.data?.export
    const inaccessible =
        query.isPending || !!query.error || command.isPending || uncertain
    function scope(value: AuditFilter) {
        return t('audit.scope', {
            account: value.account || t('audit.all'),
            action: value.action || t('audit.all'),
            target: value.target || t('audit.all'),
            from: value.from ? dateText(value.from) : t('audit.all'),
            to: value.to ? dateText(value.to) : t('audit.all'),
        })
    }
    function execute(action: { filter: AuditFilter } | { id: string }) {
        if (sending.current || inaccessible) return
        sending.current = true
        command.mutate(action)
    }
    async function refresh() {
        const result = await query.refetch()
        if (!result.error) {
            setUncertain(false)
            command.reset()
        }
    }
    const canStart =
        !inaccessible &&
        !unavailable &&
        !current?.busy &&
        current?.error !== 'audit.exportCleanupFailed'
    return (
        <section
            aria-label={t('audit.exportTitle')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">{t('audit.exportTitle')}</h2>
            <p className="text-sm">{t('audit.exportNotice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('audit.lifetime')}
            </p>
            <p className="text-sm">{scope(filter)}</p>
            <label className="flex items-center gap-2 text-sm">
                <input
                    type="checkbox"
                    checked={confirmed === fingerprint && !unavailable}
                    disabled={!canStart}
                    onChange={(e) =>
                        confirm(e.target.checked ? fingerprint : undefined)
                    }
                />
                {t('audit.confirm')}
            </label>
            <div className="flex flex-wrap gap-3">
                <Button
                    disabled={!canStart || confirmed !== fingerprint}
                    onClick={() => execute({ filter: { ...filter } })}
                >
                    {t('audit.start')}
                </Button>
                <Button
                    variant="outline"
                    disabled={query.isFetching || command.isPending}
                    onClick={() => void refresh()}
                >
                    {t('audit.readExport')}
                </Button>
                {current?.busy && (
                    <Button
                        variant="outline"
                        disabled={
                            inaccessible ||
                            !['ready', 'generating', 'downloading'].includes(
                                current.state,
                            )
                        }
                        onClick={() => execute({ id: current.id })}
                    >
                        {t('audit.cancel')}
                    </Button>
                )}
            </div>
            {query.error && <p role="alert">{t('audit.exportReadFailed')}</p>}
            {uncertain && <p role="alert">{t('audit.noReplay')}</p>}
            {!current && !query.isPending && !query.error && (
                <p>{t('audit.noExport')}</p>
            )}
            {current && (
                <>
                    <p role="status">{t(`audit.states.${current.state}`)}</p>
                    <p className="text-sm">
                        {t('audit.frozen')} {scope(current.filter)}
                    </p>
                    <p className="text-sm">
                        {t('audit.summary', {
                            count: current.count ?? '—',
                            bytes: current.fileBytes ?? '—',
                            expires: dateText(current.expiresAt),
                        })}
                    </p>
                    {current.error && (
                        <p role="alert">
                            {t(`audit.errors.${current.error}`, {
                                defaultValue: t('audit.genericError'),
                            })}
                        </p>
                    )}
                    {current.state === 'ready' &&
                        !inaccessible &&
                        !current.error &&
                        downloaded !== current.id && (
                            <a
                                className="inline-block rounded border px-4 py-2 underline"
                                href={auditDownloadHref(current.id)}
                                download
                                onClick={() => markDownloaded(current.id)}
                            >
                                {t('audit.download')}
                            </a>
                        )}
                    {(current.state === 'responseWritten' ||
                        downloaded === current.id) && (
                        <p className="text-sm">{t('audit.response')}</p>
                    )}
                </>
            )}
        </section>
    )
}
