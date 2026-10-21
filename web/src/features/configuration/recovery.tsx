import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { queryClient, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import type { Operation, RecoveryReview, VersionDetail } from './api'
import { operationsKey, runtimeKey } from './queries'
import { configuration } from './client'
import { ConfigurationError } from './view'

export function ConfigurationRecovery({
    detail,
    unavailable,
    onBusy,
    onAccepted,
}: {
    detail: VersionDetail
    unavailable: boolean
    onBusy: (value: boolean) => void
    onAccepted: (operation: Operation) => void
}) {
    const { t } = useTranslation()
    const admin = useSession((state) => state.value?.role) === 'administrator'
    const status = useQuery({
        queryKey: runtimeKey,
        queryFn: ({ signal }) => configuration.runtime(signal),
        staleTime: Infinity,
    })
    const [review, setReview] = useState<RecoveryReview | null>(null)
    const [confirmed, confirm] = useState(false)
    const [preserve, confirmPreserve] = useState(false)
    const [uncertain, setUncertain] = useState(false)
    const check = useMutation({
        mutationFn: async () => {
            setReview(null)
            confirm(false)
            confirmPreserve(false)
            const operations = await configuration.operations()
            if (operations.some((operation) => !operation.finishedAt))
                throw new Error('configuration-operation-busy')
            // The normal draft comparison intentionally fails on a damaged snapshot.
            // This dedicated preflight captures and returns the actual draft version.
            const value = await configuration.reviewRecovery(
                detail.version.revision,
                null,
            )
            if (value.targetContentHash !== detail.contentHash)
                throw new Error('configuration-review-stale')
            return value
        },
        onSuccess: (value) => {
            setReview(value)
            setUncertain(false)
        },
    })
    const recover = useMutation({
        mutationFn: () => {
            if (!review) throw new Error('configuration-confirmation')
            return configuration.recover(review, confirmed, preserve)
        },
        onMutate: () => onBusy(true),
        onSuccess: (operation) => {
            setReview(null)
            onAccepted(operation)
        },
        onError: () => {
            setReview(null)
            setUncertain(true)
        },
        onSettled: () => {
            confirm(false)
            confirmPreserve(false)
            onBusy(false)
            void queryClient.invalidateQueries({ queryKey: operationsKey })
            void queryClient.invalidateQueries({ queryKey: runtimeKey })
        },
    })
    if (
        !admin ||
        status.isError ||
        status.data?.state !== 'runFailed' ||
        status.data.errorCode !== 'configuration.invalidSnapshot' ||
        detail.version.state !== 'history' ||
        Number(detail.version.schemaVersion) !== 2
    )
        return null
    const disabled =
        unavailable || check.isPending || recover.isPending || status.isFetching
    return (
        <section
            aria-label={t('configuration.recovery.title')}
            className="space-y-3 rounded border border-destructive p-4"
        >
            <h3 className="font-semibold">
                {t('configuration.recovery.title')}
            </h3>
            <p>{t('configuration.recovery.notice')}</p>
            <ConfigurationError error={check.error || recover.error} />
            <Button
                variant="outline"
                disabled={disabled}
                onClick={() => {
                    recover.reset()
                    check.mutate()
                }}
            >
                {t('configuration.recovery.review')}
            </Button>
            {uncertain && <p role="alert">{t('configuration.uncertain')}</p>}
            {review && (
                <>
                    <p>
                        {t('configuration.recovery.target', {
                            effective: review.effectiveRevision,
                            target: review.targetRevision,
                            draft: review.draftVersion,
                        })}
                    </p>
                    <p>
                        {t('configuration.recovery.queue', {
                            count: Number(review.queue.checkedCount),
                            blocked: review.queue.blockedCount,
                        })}
                    </p>
                    {review.targets.map((target) => (
                        <pre
                            key={String(target.connectionId)}
                            className="whitespace-pre-wrap break-all text-xs"
                        >
                            {JSON.stringify(target, null, 2)}
                        </pre>
                    ))}
                    {!review.canConfirm && (
                        <p role="alert">
                            {t('configuration.recovery.blocked')}{' '}
                            {review.queue.blockedConnections.join(', ')}{' '}
                            {review.preparationProblems.join(', ')}
                        </p>
                    )}
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={confirmed}
                            disabled={disabled || !review.canConfirm}
                            onChange={(event) => confirm(event.target.checked)}
                        />
                        {t('configuration.recovery.confirm')}
                    </label>
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={preserve}
                            disabled={disabled || !review.canConfirm}
                            onChange={(event) =>
                                confirmPreserve(event.target.checked)
                            }
                        />
                        {t('configuration.recovery.preserve')}
                    </label>
                    <Button
                        disabled={
                            disabled ||
                            !review.canConfirm ||
                            !confirmed ||
                            !preserve ||
                            uncertain
                        }
                        onClick={() => recover.mutate()}
                    >
                        {t('configuration.recovery.submit')}
                    </Button>
                </>
            )}
        </section>
    )
}
