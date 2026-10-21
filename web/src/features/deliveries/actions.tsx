import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { api } from '../../lib/session'
import { revisionId } from '../configuration/api'
import { createDiagnosticsApi } from '../diagnostics/api'
import { dateText } from '../diagnostics/view'
import {
    createDeliveryApi,
    readDeliveryBatch,
    retrySelection,
    retryProblem,
    cleanupSelection,
    cleanupProblem,
    type DeliveryFilter,
    type DeliveryItem,
} from './api'

const deliveries = createDeliveryApi(api)
const diagnostics = createDiagnosticsApi(api)

export function DeliveryRecordActions({
    filter,
    selected,
    unavailable,
    canRetry,
    canCleanup,
    lock,
    reset,
}: {
    filter: DeliveryFilter
    selected: DeliveryItem[]
    unavailable: boolean
    canRetry: boolean
    canCleanup: boolean
    lock: (value: boolean) => void
    reset: () => void
}) {
    const { t } = useTranslation()
    const queries = useQueryClient()
    const [confirmed, confirm] = useState(false)
    const prepare = useMutation({
        retry: false,
        mutationFn: async ({
            action,
            byFilter,
        }: {
            action: 'retry' | 'cleanup'
            byFilter: boolean
        }) => {
            const batch = byFilter
                ? await readDeliveryBatch(deliveries, filter)
                : { items: [...selected], hasMore: false }
            const frozen = { ...batch, filter: { ...filter }, action, byFilter }
            if (action === 'cleanup') {
                if (!canCleanup) throw new Error('forbidden')
                cleanupSelection(filter.state, batch.items)
                return { ...frozen, revision: null }
            }
            if (!canRetry || filter.state !== 'failed')
                throw new Error('forbidden')
            retrySelection(batch.items)
            const { coordinator } = await diagnostics.status()
            if (
                coordinator.state !== 'active' ||
                coordinator.queueOperationInProgress ||
                coordinator.activeRevision === null
            )
                throw new Error('delivery-retry-unavailable')
            return {
                ...frozen,
                revision: revisionId(coordinator.activeRevision),
            }
        },
        onMutate: () => {
            lock(true)
            confirm(false)
        },
    })
    const write = useMutation({
        retry: false,
        mutationFn: async (plan: NonNullable<typeof prepare.data>) => {
            if (plan.action === 'cleanup') {
                const result = await deliveries.cleanup(
                    plan.filter.state,
                    plan.items,
                )
                return { count: result.clearedRecords }
            }
            if (plan.revision === null)
                throw new Error('delivery-retry-version')
            const result = await deliveries.retry(plan.revision, plan.items)
            return { count: result.requeuedRecords }
        },
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
    const copy =
        plan?.action === 'cleanup' ||
        (!plan && prepare.variables?.action === 'cleanup')
            ? 'deliveries.cleanup'
            : 'deliveries.retry'
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
            aria-label={t('deliveries.actions')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">{t('deliveries.actions')}</h2>
            {canRetry && (
                <p className="text-sm">{t('deliveries.retry.notice')}</p>
            )}
            {canCleanup && (
                <p className="text-sm">{t('deliveries.cleanup.notice')}</p>
            )}
            {!started && (
                <div className="flex flex-wrap gap-3">
                    {canRetry && (
                        <>
                            <Button
                                disabled={unavailable || selected.length === 0}
                                onClick={() =>
                                    prepare.mutate({
                                        action: 'retry',
                                        byFilter: false,
                                    })
                                }
                            >
                                {t('deliveries.retry.selected', {
                                    count: selected.length,
                                })}
                            </Button>
                            <Button
                                variant="outline"
                                disabled={unavailable}
                                onClick={() =>
                                    prepare.mutate({
                                        action: 'retry',
                                        byFilter: true,
                                    })
                                }
                            >
                                {t('deliveries.retry.filtered')}
                            </Button>
                        </>
                    )}
                    {canCleanup && (
                        <>
                            <Button
                                variant="outline"
                                disabled={unavailable || selected.length === 0}
                                onClick={() =>
                                    prepare.mutate({
                                        action: 'cleanup',
                                        byFilter: false,
                                    })
                                }
                            >
                                {t('deliveries.cleanup.selected', {
                                    count: selected.length,
                                })}
                            </Button>
                            <Button
                                variant="outline"
                                disabled={unavailable}
                                onClick={() =>
                                    prepare.mutate({
                                        action: 'cleanup',
                                        byFilter: true,
                                    })
                                }
                            >
                                {t('deliveries.cleanup.filtered')}
                            </Button>
                        </>
                    )}
                </div>
            )}
            {prepare.isPending && <p role="status">{t(`${copy}.preparing`)}</p>}
            {prepare.error && <p role="alert">{t(`${copy}.prepareFailed`)}</p>}
            {plan && (
                <>
                    <p className="font-semibold">
                        {t(`${copy}.summary`, {
                            count: plan.items.length,
                            revision: plan.revision,
                            area: t(`deliveries.${plan.filter.state}`),
                        })}
                    </p>
                    {plan.byFilter && (
                        <p>
                            {t('deliveries.batchScope', {
                                area: t(`deliveries.${plan.filter.state}`),
                                target:
                                    plan.filter.connectionId ||
                                    t('deliveries.all'),
                                device:
                                    plan.filter.deviceKey ||
                                    t('deliveries.all'),
                                reason:
                                    plan.filter.reason || t('deliveries.all'),
                                from: dateText(plan.filter.from),
                                to: dateText(plan.filter.to),
                            })}
                        </p>
                    )}
                    {plan.byFilter && (
                        <p>
                            {t(
                                plan.hasMore
                                    ? 'deliveries.retry.more'
                                    : 'deliveries.retry.allRead',
                            )}
                        </p>
                    )}
                    <p>{t(`${copy}.snapshot`)}</p>
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
                        {t(`${copy}.confirmation`)}
                    </label>
                    <Button
                        disabled={!confirmed || finished}
                        onClick={() => write.mutate(plan)}
                    >
                        {t(`${copy}.execute`)}
                    </Button>
                </>
            )}
            {write.isPending && <p role="status">{t(`${copy}.writing`)}</p>}
            {write.error && (
                <div role="alert" className="space-y-2">
                    <p>
                        {t(
                            `${copy}.errors.${plan?.action === 'cleanup' ? cleanupProblem(write.error) : retryProblem(write.error)}`,
                        )}
                    </p>
                    <p>{t(`${copy}.failed`)}</p>
                </div>
            )}
            {write.data && (
                <p role="status">
                    {t(`${copy}.success`, {
                        count: write.data.count,
                    })}
                </p>
            )}
            {started && (
                <Button variant="outline" disabled={pending} onClick={close}>
                    {t(
                        finished || prepare.error
                            ? `${copy}.reselect`
                            : `${copy}.cancel`,
                    )}
                </Button>
            )}
        </section>
    )
}
