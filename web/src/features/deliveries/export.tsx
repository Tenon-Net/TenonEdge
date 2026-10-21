import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { api, useSession } from '../../lib/session'
import { dateText } from '../diagnostics/view'
import type { DeliveryFilter } from './api'
import {
    createDeliveryExportApi,
    exportDownloadHref,
    exportProblem,
    type ExportStatus,
} from './export-api'

const exportsApi = createDeliveryExportApi(api)
export function DeliveryExport({
    filter,
    unavailable,
}: {
    filter: DeliveryFilter
    unavailable: boolean
}) {
    const { t } = useTranslation()
    const epoch = useSession((s) => s.epoch)
    const queries = useQueryClient()
    const key = ['delivery-export', epoch]
    const [confirmedId, confirm] = useState<string>()
    const [attemptedId, attempted] = useState<string>()
    const [downloadedId, downloaded] = useState<string>()
    const [uncertain, setUncertain] = useState(false)
    const query = useQuery({
        queryKey: key,
        queryFn: ({ signal }) => exportsApi.current(signal),
        gcTime: 0,
        retry: false,
        refetchInterval: (q) =>
            !q.state.error && q.state.data?.export?.busy ? 1000 : false,
    })
    const command = useMutation({
        retry: false,
        mutationFn: async (
            action:
                | { kind: 'prepare'; filter: DeliveryFilter }
                | { kind: 'confirm' | 'cancel'; id: string },
        ) => {
            if (action.kind === 'prepare')
                return exportsApi.prepare(action.filter)
            return action.kind === 'confirm'
                ? exportsApi.confirm(action.id)
                : exportsApi.cancel(action.id)
        },
        onMutate: async (action) => {
            confirm(undefined)
            if (action.kind === 'confirm') attempted(action.id)
            await queries.cancelQueries({ queryKey: key })
        },
        onSuccess: (result) => queries.setQueryData(key, { export: result }),
        onError: () => setUncertain(true),
        onSettled: () => {
            void queries.invalidateQueries({ queryKey: key })
        },
    })
    const current = query.data?.export
    const inaccessible =
        query.isPending || !!query.error || command.isPending || uncertain
    const terminal =
        current &&
        ['responseWritten', 'failed', 'cancelled', 'expired'].includes(
            current.state,
        )
    function scope(
        value: DeliveryFilter | NonNullable<ExportStatus['review']>['filter'],
    ) {
        return t('deliveries.export.scope', {
            area: t(`deliveries.${value.state}`),
            target: value.connectionId || t('deliveries.all'),
            device: value.deviceKey || t('deliveries.all'),
            reason: value.reason || t('deliveries.all'),
            from: dateText(value.from),
            to: dateText(value.to),
        })
    }
    async function refresh() {
        const result = await query.refetch()
        if (!result.error) {
            setUncertain(false)
            command.reset()
        }
    }
    return (
        <section
            aria-label={t('deliveries.export.title')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('deliveries.export.title')}
            </h2>
            <p className="text-sm">{t('deliveries.export.notice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('deliveries.export.lifetime')}
            </p>
            <p className="text-sm">
                {t('deliveries.export.applied')} {scope(filter)}
            </p>
            <div className="flex flex-wrap gap-3">
                <Button
                    disabled={
                        unavailable ||
                        inaccessible ||
                        !!current?.busy ||
                        current?.error === 'delivery.exportCleanupFailed'
                    }
                    onClick={() =>
                        command.mutate({
                            kind: 'prepare',
                            filter: { ...filter },
                        })
                    }
                >
                    {t('deliveries.export.prepare')}
                </Button>
                <Button
                    variant="outline"
                    disabled={query.isFetching || command.isPending}
                    onClick={() => void refresh()}
                >
                    {t('deliveries.export.refresh')}
                </Button>
            </div>
            {query.isPending && (
                <p role="status">{t('deliveries.export.loading')}</p>
            )}
            {query.error && (
                <p role="alert">{t('deliveries.export.readFailed')}</p>
            )}
            {command.error && (
                <p role="alert">
                    {t('deliveries.export.unknownResult')}{' '}
                    {t(
                        `deliveries.export.errors.${exportProblem(command.error)}`,
                        { defaultValue: '' },
                    )}
                </p>
            )}
            {command.isPending && (
                <p role="status">{t('deliveries.export.sending')}</p>
            )}
            {!current && !query.isPending && !query.error && (
                <p>{t('deliveries.export.none')}</p>
            )}
            {current && (
                <>
                    <p role="status" className="font-semibold">
                        {t(`deliveries.export.states.${current.state}`)}
                    </p>
                    <p className="text-sm">
                        {t('deliveries.export.expires', {
                            time: dateText(current.expiresAt),
                        })}
                    </p>
                    {current.review && (
                        <div className="space-y-2 rounded border bg-background p-3 text-sm">
                            <p className="font-semibold">
                                {t('deliveries.export.frozen')}{' '}
                                {scope(current.review.filter)}
                            </p>
                            <p>
                                {t('deliveries.export.summary', {
                                    count: current.review.count,
                                    bytes: current.review.payloadBytes,
                                    maximum: current.review.maximumFileBytes,
                                })}
                            </p>
                            <p>
                                {t('deliveries.export.timeRange', {
                                    from: dateText(
                                        current.review.firstCreatedAt,
                                    ),
                                    to: dateText(current.review.lastCreatedAt),
                                })}
                            </p>
                            <p>{t('deliveries.export.snapshot')}</p>
                        </div>
                    )}
                    {current.error && (
                        <p role="alert">
                            {t(`deliveries.export.errors.${current.error}`, {
                                defaultValue: t(
                                    'deliveries.export.genericError',
                                ),
                            })}
                        </p>
                    )}
                    {current.state === 'awaitingConfirmation' && (
                        <>
                            {current.review?.count === '0' && (
                                <p>{t('deliveries.export.empty')}</p>
                            )}
                            <label className="flex items-start gap-2">
                                <input
                                    type="checkbox"
                                    checked={confirmedId === current.id}
                                    disabled={
                                        inaccessible ||
                                        current.review?.count === '0' ||
                                        attemptedId === current.id
                                    }
                                    onChange={(event) =>
                                        confirm(
                                            event.target.checked
                                                ? current.id
                                                : undefined,
                                        )
                                    }
                                />
                                {t('deliveries.export.confirmation')}
                            </label>
                            <Button
                                disabled={
                                    inaccessible ||
                                    confirmedId !== current.id ||
                                    attemptedId === current.id ||
                                    current.review?.count === '0'
                                }
                                onClick={() =>
                                    command.mutate({
                                        kind: 'confirm',
                                        id: current.id,
                                    })
                                }
                            >
                                {t('deliveries.export.confirm')}
                            </Button>
                        </>
                    )}
                    {current.state === 'ready' && (
                        <>
                            <p>
                                {t('deliveries.export.fileReady', {
                                    bytes: current.fileBytes,
                                })}
                            </p>
                            {!inaccessible && downloadedId !== current.id ? (
                                <Button asChild>
                                    <a
                                        href={exportDownloadHref(current.id)}
                                        download
                                        onClick={() => downloaded(current.id)}
                                    >
                                        {t('deliveries.export.download')}
                                    </a>
                                </Button>
                            ) : (
                                <Button disabled>
                                    {t('deliveries.export.download')}
                                </Button>
                            )}
                            {downloadedId === current.id && (
                                <p role="status">
                                    {t('deliveries.export.downloadRequested')}
                                </p>
                            )}
                        </>
                    )}
                    {!terminal && (
                        <Button
                            variant="outline"
                            disabled={inaccessible}
                            onClick={() =>
                                command.mutate({
                                    kind: 'cancel',
                                    id: current.id,
                                })
                            }
                        >
                            {t('deliveries.export.cancel')}
                        </Button>
                    )}
                    {terminal && current.busy && (
                        <p>{t('deliveries.export.releasing')}</p>
                    )}
                </>
            )}
        </section>
    )
}
