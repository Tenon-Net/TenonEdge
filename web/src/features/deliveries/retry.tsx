import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { api } from '../../lib/session'
import { revisionId } from '../configuration/api'
import { createDiagnosticsApi } from '../diagnostics/api'
import { dateText } from '../diagnostics/view'
import {
    createDeliveryApi,
    readFailedBatch,
    retrySelection,
    retryProblem,
    type DeliveryFilter,
    type DeliveryItem,
} from './api'

const deliveries = createDeliveryApi(api)
const diagnostics = createDiagnosticsApi(api)

export function FailedRetryControls({
    filter,
    selected,
    unavailable,
    lock,
    reset,
}: {
    filter: DeliveryFilter
    selected: DeliveryItem[]
    unavailable: boolean
    lock: (value: boolean) => void
    reset: () => void
}) {
    const { t } = useTranslation()
    const queries = useQueryClient()
    const [confirmed, confirm] = useState(false)
    const prepare = useMutation({
        retry: false,
        mutationFn: async (byFilter: boolean) => {
            const batch = byFilter
                ? await readFailedBatch(deliveries, filter)
                : { items: [...selected], hasMore: false }
            retrySelection(batch.items)
            const { coordinator } = await diagnostics.status()
            if (
                coordinator.state !== 'active' ||
                coordinator.queueOperationInProgress ||
                coordinator.activeRevision === null
            )
                throw new Error('delivery-retry-unavailable')
            return {
                ...batch,
                revision: revisionId(coordinator.activeRevision),
                byFilter,
            }
        },
        onMutate: () => {
            lock(true)
            confirm(false)
        },
    })
    const write = useMutation({
        retry: false,
        mutationFn: (plan: NonNullable<typeof prepare.data>) =>
            deliveries.retry(plan.revision, plan.items),
        onMutate: () => confirm(false),
        onSettled: async () => {
            await Promise.all(
                [
                    'delivery-records',
                    'delivery-body',
                    'runtime-outputs',
                    'runtime-output',
                    'runtime-diagnostics',
                ].map((key) => queries.invalidateQueries({ queryKey: [key] })),
            )
        },
    })
    const plan = prepare.data
    const started = prepare.status !== 'idle'
    const pending = prepare.isPending || write.isPending
    const finished = write.status !== 'idle'
    function close() {
        prepare.reset()
        write.reset()
        confirm(false)
        lock(false)
        reset()
    }
    return (
        <section
            aria-label={t('deliveries.retry.title')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('deliveries.retry.title')}
            </h2>
            <p className="text-sm">{t('deliveries.retry.notice')}</p>
            {!started && (
                <div className="flex flex-wrap gap-3">
                    <Button
                        disabled={unavailable || selected.length === 0}
                        onClick={() => prepare.mutate(false)}
                    >
                        {t('deliveries.retry.selected', {
                            count: selected.length,
                        })}
                    </Button>
                    <Button
                        variant="outline"
                        disabled={unavailable}
                        onClick={() => prepare.mutate(true)}
                    >
                        {t('deliveries.retry.filtered')}
                    </Button>
                </div>
            )}
            {prepare.isPending && (
                <p role="status">{t('deliveries.retry.preparing')}</p>
            )}
            {prepare.error && (
                <p role="alert">{t('deliveries.retry.prepareFailed')}</p>
            )}
            {plan && (
                <>
                    <p className="font-semibold">
                        {t('deliveries.retry.summary', {
                            count: plan.items.length,
                            revision: plan.revision,
                        })}
                    </p>
                    {plan.byFilter && (
                        <p>
                            {t(
                                plan.hasMore
                                    ? 'deliveries.retry.more'
                                    : 'deliveries.retry.allRead',
                            )}
                        </p>
                    )}
                    <p>{t('deliveries.retry.snapshot')}</p>
                    <div className="max-h-60 overflow-auto rounded border p-3 text-xs">
                        <ul className="space-y-2">
                            {plan.items.map((item) => (
                                <li key={item.sequence} className="break-all">
                                    {t('deliveries.retry.record', {
                                        id: item.deliveryId,
                                        sequence: item.sequence,
                                        target: item.connectionId,
                                        device: item.deviceKey,
                                        time: dateText(item.createdAt),
                                    })}
                                </li>
                            ))}
                        </ul>
                    </div>
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={confirmed}
                            disabled={finished}
                            onChange={(event) => confirm(event.target.checked)}
                        />
                        {t('deliveries.retry.confirmation')}
                    </label>
                    <Button
                        disabled={!confirmed || finished}
                        onClick={() => write.mutate(plan)}
                    >
                        {t('deliveries.retry.execute')}
                    </Button>
                </>
            )}
            {write.isPending && (
                <p role="status">{t('deliveries.retry.writing')}</p>
            )}
            {write.error && (
                <div role="alert" className="space-y-2">
                    <p>
                        {t(
                            `deliveries.retry.errors.${retryProblem(write.error)}`,
                        )}
                    </p>
                    <p>{t('deliveries.retry.failed')}</p>
                </div>
            )}
            {write.data && (
                <p role="status">
                    {t('deliveries.retry.success', {
                        count: write.data.requeuedRecords,
                    })}
                </p>
            )}
            {started && (
                <Button variant="outline" disabled={pending} onClick={close}>
                    {t(
                        finished || prepare.error
                            ? 'deliveries.retry.reselect'
                            : 'deliveries.retry.cancel',
                    )}
                </Button>
            )}
        </section>
    )
}
