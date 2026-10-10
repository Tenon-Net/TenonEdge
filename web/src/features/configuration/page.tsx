import { useEffect, useState } from 'react'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { queryClient, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { reviewIsCurrent, type Review } from './api'
import {
    configuration,
    operationsKey,
    summaryKey,
    useDraftSummary,
} from './queries'
import { OperationCard, ReviewContents } from './view'

type Form = {
    review: Review | null
    description: string
    confirmed: boolean
    operationId: string | null
    uncertain: boolean
    notice: string
}
export function ConfigurationPage() {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const canEdit = role === 'administrator' || role === 'configurator'
    const summary = useDraftSummary()
    const operations = useQuery({
        queryKey: operationsKey,
        queryFn: ({ signal }) => configuration.operations(signal),
        refetchInterval: 2000,
    })
    const [store] = useState(() =>
        createStore<Form>(() => ({
            review: null,
            description: '',
            confirmed: false,
            operationId: null,
            uncertain: false,
            notice: '',
        })),
    )
    const form = useStore(store)
    const operation = useQuery({
        queryKey: ['configuration-operation', form.operationId],
        queryFn: ({ signal }) =>
            configuration.operation(form.operationId!, signal),
        enabled: form.operationId !== null,
        refetchInterval: (query) =>
            query.state.data?.finishedAt ? false : 1000,
    })
    const activeOperation = operations.data?.find((item) => !item.finishedAt)
    const busy =
        operations.data?.some((item) => !item.finishedAt) ||
        summary.data?.pendingRevision != null
    const fresh =
        !!form.review &&
        !!summary.data &&
        !summary.isError &&
        reviewIsCurrent(form.review, summary.data)
    const review = useMutation({
        mutationFn: () => configuration.review(summary.data!.version),
        onSuccess: (value) =>
            store.setState({
                review: value,
                confirmed: false,
                uncertain: false,
                notice: '',
            }),
    })
    const apply = useMutation({
        mutationFn: () => {
            if (!form.review || !fresh || busy)
                throw new Error('configuration-review-stale')
            return configuration.apply(
                form.review,
                form.description,
                form.confirmed,
            )
        },
        onSuccess: (value) => {
            store.setState({
                operationId: value.id,
                review: null,
                confirmed: false,
                description: '',
                notice: 'accepted',
                uncertain: false,
            })
            queryClient.setQueryData(
                ['configuration-operation', value.id],
                value,
            )
            void queryClient.invalidateQueries({ queryKey: operationsKey })
        },
        onError: () => store.setState({ confirmed: false, uncertain: true }),
    })
    const discard = useMutation({
        mutationFn: () => configuration.discard(summary.data!, true),
        onSuccess: () => {
            store.setState({
                review: null,
                confirmed: false,
                notice: 'discarded',
                uncertain: false,
            })
            void queryClient.invalidateQueries()
        },
        onError: () => store.setState({ uncertain: true }),
    })
    useEffect(() => {
        if (!operation.data?.finishedAt) return
        void queryClient.invalidateQueries({ queryKey: summaryKey })
        void queryClient.invalidateQueries({ queryKey: operationsKey })
    }, [operation.data?.finishedAt])
    const waiting = apply.isPending || discard.isPending || review.isPending
    const unavailable =
        !canEdit ||
        waiting ||
        summary.isFetching ||
        !summary.data ||
        summary.isError ||
        operations.isPending ||
        operations.isError ||
        !!busy
    async function refresh() {
        await Promise.all([
            summary.refetch(),
            operations.refetch(),
            ...(form.operationId ? [operation.refetch()] : []),
        ])
    }
    return (
        <div className="mx-auto max-w-6xl space-y-6">
            <h1 className="text-2xl font-semibold">
                {t('configuration.title')}
            </h1>
            <p className="text-sm text-muted-foreground">
                {t('configuration.historyBoundary')}
            </p>
            <ErrorMessage error={summary.error || operations.error} />
            <section className="space-y-3 rounded border p-4">
                <p>
                    {summary.data?.changeCount == null || summary.isError
                        ? t('configuration.unknownSummary')
                        : t('configuration.summary', {
                              count: Number(summary.data.changeCount),
                          })}
                </p>
                <p>
                    {t('configuration.effective', {
                        revision:
                            summary.data?.effectiveRevision == null
                                ? t('configuration.none')
                                : `r${summary.data.effectiveRevision}`,
                    })}
                </p>
                {busy && (
                    <p role="status">
                        {activeOperation
                            ? t('configuration.applicationBy', {
                                  account: activeOperation.account,
                                  id: activeOperation.id,
                              })
                            : t('configuration.pending')}
                    </p>
                )}
                <div className="flex flex-wrap gap-3">
                    <Button
                        variant="outline"
                        disabled={waiting}
                        onClick={() => void refresh()}
                    >
                        {t('configuration.refresh')}
                    </Button>
                    {canEdit && (
                        <>
                            <Button
                                disabled={unavailable}
                                onClick={() => {
                                    apply.reset()
                                    discard.reset()
                                    store.setState({ confirmed: false })
                                    review.mutate()
                                }}
                            >
                                {t('configuration.review')}
                            </Button>
                            <Button
                                variant="outline"
                                disabled={
                                    unavailable ||
                                    !summary.data?.comparisonAvailable ||
                                    Number(summary.data.changeCount) === 0 ||
                                    form.uncertain
                                }
                                onClick={() => {
                                    if (
                                        window.confirm(
                                            t('configuration.discardConfirm'),
                                        )
                                    )
                                        discard.mutate()
                                }}
                            >
                                {t('configuration.discard')}
                            </Button>
                        </>
                    )}
                </div>
            </section>
            <ErrorMessage
                error={review.error || apply.error || discard.error}
            />
            {form.uncertain && (
                <p
                    role="alert"
                    className="rounded border border-destructive p-3"
                >
                    {t('configuration.uncertain')}
                </p>
            )}
            {form.notice && (
                <p role="status">{t(`configuration.${form.notice}`)}</p>
            )}
            {form.review && canEdit && (
                <section
                    aria-label={t('configuration.reviewTitle')}
                    className="space-y-4 rounded border p-4"
                >
                    <h2 className="text-xl font-semibold">
                        {t('configuration.reviewTitle')}
                    </h2>
                    {!fresh && <p role="alert">{t('configuration.stale')}</p>}
                    <ReviewContents review={form.review} />
                    <label className="block">
                        {t('configuration.description')}
                        <textarea
                            className="mt-2 block min-h-20 w-full rounded border bg-background p-2"
                            maxLength={1000}
                            value={form.description}
                            disabled={waiting}
                            onChange={(event) =>
                                store.setState({
                                    description: event.target.value,
                                })
                            }
                        />
                    </label>
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={form.confirmed}
                            disabled={
                                unavailable ||
                                !fresh ||
                                !form.review.preflightPassed ||
                                review.isError ||
                                form.uncertain
                            }
                            onChange={(event) =>
                                store.setState({
                                    confirmed: event.target.checked,
                                })
                            }
                        />
                        {t('configuration.confirm')}
                    </label>
                    <Button
                        disabled={
                            unavailable ||
                            !fresh ||
                            !form.review.preflightPassed ||
                            review.isError ||
                            !form.confirmed ||
                            form.uncertain
                        }
                        onClick={() => apply.mutate()}
                    >
                        {t('configuration.apply')}
                    </Button>
                </section>
            )}
            {form.operationId && (
                <section
                    className="space-y-3"
                    aria-label={t('configuration.currentOperation')}
                >
                    <h2 className="text-xl font-semibold">
                        {t('configuration.currentOperation')}
                    </h2>
                    {operation.error && (
                        <>
                            <ErrorMessage error={operation.error} />
                            <p role="alert">
                                {t('configuration.unavailableOperation')}
                            </p>
                            {operation.data && (
                                <p>{t('configuration.lastKnown')}</p>
                            )}
                        </>
                    )}
                    {operation.data && (
                        <OperationCard operation={operation.data} />
                    )}
                </section>
            )}
            <section className="space-y-3">
                <h2 className="text-xl font-semibold">
                    {t('configuration.operations')}
                </h2>
                {operations.data?.length === 0 && (
                    <p>{t('configuration.noOperations')}</p>
                )}
                {operations.isError && operations.data && (
                    <p>{t('configuration.lastKnown')}</p>
                )}
                {operations.data
                    ?.filter((item) => item.id !== form.operationId)
                    .map((item) => (
                        <OperationCard key={item.id} operation={item} />
                    ))}
            </section>
        </div>
    )
}
