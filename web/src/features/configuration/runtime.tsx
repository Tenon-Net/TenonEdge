import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { queryClient } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { type Operation, type RuntimeState } from './api'
import { configuration, operationsKey, runtimeKey } from './queries'
import { ConfigurationError } from './view'

export function ConfigurationRuntime({
    ready,
    canEdit,
    unavailable,
    onBusy,
    onAccepted,
}: {
    ready: boolean
    canEdit: boolean
    unavailable: boolean
    onBusy: (busy: boolean) => void
    onAccepted: (operation: Operation) => void
}) {
    const { t } = useTranslation()
    const status = useQuery({
        queryKey: runtimeKey,
        queryFn: ({ signal }) => configuration.runtime(signal),
        enabled: ready,
        staleTime: Infinity,
    })
    const [checkpoint, setCheckpoint] = useState<RuntimeState | null>(null)
    const [confirmed, setConfirmed] = useState(false)
    const [uncertain, setUncertain] = useState(false)
    const check = useMutation({
        mutationFn: async () => {
            // Explicit fresh reads after a lost response, never replay the write.
            const operations = await configuration.operations()
            const state = await configuration.runtime()
            if (operations.some((operation) => !operation.finishedAt))
                throw new Error('configuration-operation-busy')
            return state
        },
        onSuccess: (state) => {
            queryClient.setQueryData(runtimeKey, state)
            setCheckpoint(state)
            setConfirmed(false)
            setUncertain(false)
        },
    })
    const fresh =
        !status.isError &&
        !status.isFetching &&
        status.data?.canRetry === true &&
        checkpoint?.canRetry === true &&
        String(checkpoint.effectiveRevision) ===
            String(status.data.effectiveRevision) &&
        checkpoint.planHash === status.data.planHash
    const retry = useMutation({
        mutationFn: () => {
            // The parent becomes busy in onMutate to disable other writes.
            // That is this operation's own admission, not a reason to reject it.
            if (!checkpoint || !fresh)
                throw new Error('configuration-retry-stale')
            return configuration.retry(checkpoint, confirmed)
        },
        onMutate: () => onBusy(true),
        onSuccess: (operation) => {
            setCheckpoint(null)
            setConfirmed(false)
            setUncertain(false)
            onAccepted(operation)
        },
        onError: () => {
            setConfirmed(false)
            setUncertain(true)
        },
        onSettled: () => {
            onBusy(false)
            void queryClient.invalidateQueries({ queryKey: operationsKey })
            void queryClient.invalidateQueries({ queryKey: runtimeKey })
        },
    })
    const disabled = unavailable || check.isPending || retry.isPending
    return (
        <section
            aria-label={t('configuration.runtimeTitle')}
            className="product-panel space-y-3"
        >
            <h2 className="text-base font-semibold">
                {t('configuration.runtimeTitle')}
            </h2>
            <ConfigurationError
                error={status.error || check.error || retry.error}
            />
            {status.isError && status.data && (
                <p>{t('configuration.lastKnown')}</p>
            )}
            {status.data && (
                <>
                    <p>
                        {t(`configuration.runtimeStates.${status.data.state}`)}
                        {' · '}
                        {status.data.effectiveRevision == null
                            ? t('configuration.none')
                            : `r${status.data.effectiveRevision}`}
                    </p>
                    {status.data.reason && (
                        <p role="alert" className="break-words">
                            {status.data.reason}
                        </p>
                    )}
                    {status.data.errorCode ===
                        'configuration.invalidSnapshot' && (
                        <p role="alert">
                            {t('configuration.damagedRecoveryBoundary')}
                        </p>
                    )}
                </>
            )}
            {canEdit && status.data?.state === 'runFailed' && (
                <>
                    <p>{t('configuration.retryBoundary')}</p>
                    <Button
                        variant="outline"
                        disabled={
                            disabled ||
                            status.isFetching ||
                            status.isError ||
                            !status.data.canRetry
                        }
                        onClick={() => {
                            retry.reset()
                            setConfirmed(false)
                            check.mutate()
                        }}
                    >
                        {t('configuration.checkRetry')}
                    </Button>
                    {checkpoint && (
                        <>
                            <p>
                                {t('configuration.retryVersion', {
                                    revision: checkpoint.effectiveRevision,
                                })}
                            </p>
                            <label className="flex items-start gap-2">
                                <input
                                    type="checkbox"
                                    checked={confirmed}
                                    disabled={disabled || !fresh || uncertain}
                                    onChange={(event) =>
                                        setConfirmed(event.target.checked)
                                    }
                                />
                                {t('configuration.retryConfirm')}
                            </label>
                            <Button
                                disabled={
                                    disabled ||
                                    !fresh ||
                                    !confirmed ||
                                    uncertain
                                }
                                onClick={() => retry.mutate()}
                            >
                                {t('configuration.retry')}
                            </Button>
                        </>
                    )}
                </>
            )}
            {uncertain && <p role="alert">{t('configuration.uncertain')}</p>}
        </section>
    )
}
