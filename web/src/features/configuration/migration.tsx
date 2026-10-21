import { PageHeading } from '../../components/layout/page'
import { useState } from 'react'
import { create } from 'zustand'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { useSession } from '../../lib/session'
import { dateText } from '../diagnostics/view'
import { MigrationQuotaPanel } from '../deliveries/migration-review'
import { migrationQuota } from '../deliveries/migration-api'
import { ConfigurationError, OperationCard, ReviewContents } from './view'
import { MigrationDeclarations, Recipient } from './migration-view'
import {
    summaryKey,
    operationsKey,
    runtimeKey,
    useDraftSummary,
} from './queries'
import { configuration, configurationMigrations } from './client'
import { reviewIsCurrent, type Review } from './api'

const useAttempt = create<{ epoch: number; id?: string }>(() => ({ epoch: -1 }))
export function ConfigurationMigrationPage() {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    return role === 'administrator' ? (
        <Migration />
    ) : (
        <p role="alert">{t('migration.adminOnly')}</p>
    )
}
function Migration() {
    const { t } = useTranslation()
    const { operationId } = useSearch({ from: '/configuration/migrations' })
    const navigate = useNavigate({ from: '/configuration/migrations' })
    const epoch = useSession((s) => s.epoch)
    const last = useAttempt()
    const attempt = operationId ?? (last.epoch === epoch ? last.id : undefined)
    const queries = useQueryClient()
    const summary = useDraftSummary()
    const key = ['configuration-migration-preparation', epoch]
    const current = useQuery({
        queryKey: key,
        queryFn: ({ signal }) => configurationMigrations.current(signal),
        gcTime: 0,
        refetchInterval: (q) =>
            q.state.data && !q.state.error ? 10000 : false,
    })
    const operation = useQuery({
        queryKey: ['configuration-operation', attempt],
        queryFn: ({ signal }) => configuration.operation(attempt!, signal),
        enabled: !!attempt,
        refetchInterval: (q) =>
            !q.state.error && !q.state.data?.finishedAt ? 1000 : false,
    })
    const [draft, setDraft] = useState<Review | null>(null)
    const [selected, setSelected] = useState<string[]>([])
    const [description, setDescription] = useState('')
    const [declarationVersion, resetDeclarations] = useState(0)
    const [uncertain, setUncertain] = useState(false)
    type Action =
        | { kind: 'review' | 'prepare' | 'restart' }
        | { kind: 'confirm' | 'cancel'; id: string }
    const command = useMutation({
        retry: false,
        mutationFn: async (action: Action) => {
            if (action.kind === 'review') {
                const value = await configuration.review(summary.data!.version)
                setDraft(value)
                setSelected([])
                return
            }
            if (action.kind === 'prepare') {
                const value = await configurationMigrations.prepare(
                    draft!.version,
                    selected,
                )
                queries.setQueryData(key, value)
                setDraft(null)
                return
            }
            if (action.kind === 'cancel') {
                await configurationMigrations.cancel(action.id)
                queries.setQueryData(key, null)
                return
            }
            if (action.kind === 'confirm') {
                useAttempt.setState({ epoch, id: action.id })
                await navigate({
                    search: { operationId: action.id },
                    replace: true,
                })
                const value = await configurationMigrations.confirm(
                    action.id,
                    true,
                    true,
                    description,
                )
                queries.setQueryData(
                    ['configuration-operation', value.id],
                    value,
                )
                return
            }
            const prepared = await configurationMigrations.current()
            if (prepared) await configurationMigrations.cancel(prepared.id)
            queries.setQueryData(key, null)
            await navigate({ search: {}, replace: true })
            useAttempt.setState({ epoch, id: undefined })
            setDraft(null)
            setSelected([])
            setDescription('')
        },
        onMutate: async () => {
            resetDeclarations((n) => n + 1)
            await queries.cancelQueries({ queryKey: key })
        },
        onSuccess: () => setUncertain(false),
        onError: () => setUncertain(true),
        onSettled: () => {
            for (const queryKey of [
                key,
                summaryKey,
                operationsKey,
                runtimeKey,
                ['configuration-history'],
                ['configuration-operation', attempt],
            ])
                void queries.invalidateQueries({ queryKey })
        },
    })
    const review = current.data
    const locked =
        current.isPending ||
        current.isFetching ||
        current.isError ||
        command.isPending ||
        uncertain ||
        summary.isFetching ||
        summary.isError ||
        !summary.data ||
        summary.data.pendingRevision != null
    const fresh =
        review &&
        summary.data &&
        reviewIsCurrent(review.configuration, summary.data)
    const deficit = migrationQuota(command.error)
    async function refresh() {
        resetDeclarations((n) => n + 1)
        const results = await Promise.all([
            current.refetch(),
            summary.refetch(),
            ...(attempt ? [operation.refetch()] : []),
        ])
        if (results.every((r) => !r.error)) {
            setUncertain(false)
            command.reset()
        }
    }
    return (
        <div className="product-page">
            <PageHeading title={t('configurationMigration.title')} />
            <nav className="flex flex-wrap gap-4">
                <Link to="/configuration" className="underline">
                    {t('configurationMigration.back')}
                </Link>
                <Link
                    to="/configuration/migration-intents"
                    className="underline"
                >
                    {t('configurationMigration.intents')}
                </Link>
            </nav>
            <p>{t('configurationMigration.boundary')}</p>
            <p>{t('configurationMigration.safety')}</p>
            <p className="text-sm text-muted-foreground">
                {t('configurationMigration.lifetime')}
            </p>
            <Button
                variant="outline"
                disabled={command.isPending || current.isFetching}
                onClick={() => void refresh()}
            >
                {t('configurationMigration.refresh')}
            </Button>
            <ConfigurationError
                error={current.error || summary.error || command.error}
            />
            {current.isError && <p role="alert">{t('migration.readError')}</p>}
            {uncertain && <p role="alert">{t('migration.uncertain')}</p>}
            {command.isPending && (
                <p role="status">{t('configuration.pending')}</p>
            )}
            {deficit && <MigrationQuotaPanel quota={deficit} />}
            {!attempt && !review && (
                <>
                    <Button
                        disabled={locked}
                        onClick={() => command.mutate({ kind: 'review' })}
                    >
                        {t('configurationMigration.readDraft')}
                    </Button>
                    {draft && (
                        <section className="product-panel space-y-4">
                            <ReviewContents review={draft} />
                            {[
                                ...new Set(
                                    draft.targetBlocks.map((b) =>
                                        String(b.connectionId),
                                    ),
                                ),
                            ].map((id) => (
                                <label
                                    key={id}
                                    className="flex items-start gap-2"
                                >
                                    <input
                                        type="checkbox"
                                        disabled={
                                            locked ||
                                            !summary.data ||
                                            !reviewIsCurrent(
                                                draft,
                                                summary.data,
                                            )
                                        }
                                        checked={selected.includes(id)}
                                        onChange={(e) =>
                                            setSelected(
                                                e.target.checked
                                                    ? [...selected, id]
                                                    : selected.filter(
                                                          (v) => v !== id,
                                                      ),
                                            )
                                        }
                                    />
                                    {t('configurationMigration.select', { id })}
                                </label>
                            ))}
                            <p>
                                {t('configurationMigration.selected', {
                                    count: selected.length,
                                })}
                            </p>
                            <Button
                                disabled={
                                    locked ||
                                    !summary.data ||
                                    !reviewIsCurrent(draft, summary.data) ||
                                    !draft.comparisonAvailable ||
                                    draft.routeIssues.length > 0 ||
                                    !selected.length ||
                                    selected.length > 500
                                }
                                onClick={() =>
                                    command.mutate({ kind: 'prepare' })
                                }
                            >
                                {t('configurationMigration.prepare')}
                            </Button>
                        </section>
                    )}
                </>
            )}
            {review && (
                <section
                    aria-label={t('configurationMigration.prepared')}
                    className="product-panel space-y-5"
                >
                    <h2 className="text-base font-semibold">
                        {t('configurationMigration.prepared')}
                    </h2>
                    <p>
                        {t('configurationMigration.expires', {
                            time: dateText(review.expiresAt),
                        })}
                    </p>
                    <p>{t('configurationMigration.blocked')}</p>
                    <ReviewContents review={review.configuration} />
                    {review.targets.map((target) => (
                        <section
                            key={target.intentId}
                            className="product-panel space-y-3"
                        >
                            <h3 className="font-semibold">
                                {t('configurationMigration.target', {
                                    name: target.name,
                                    key: target.key,
                                    id: target.connectionId,
                                })}
                            </h3>
                            <p className="break-all">
                                {t('configurationMigration.intentId', {
                                    id: target.intentId,
                                })}
                            </p>
                            <p>
                                {t('configurationMigration.count', {
                                    count: target.count,
                                })}
                            </p>
                            <div className="grid gap-3 md:grid-cols-2">
                                <Recipient
                                    title={t('migration.original')}
                                    value={target.source}
                                />
                                <Recipient
                                    title={t(
                                        'configurationMigration.targetIdentity',
                                    )}
                                    value={target.target}
                                />
                            </div>
                            <MigrationQuotaPanel quota={target.quota} />
                        </section>
                    ))}
                    {!fresh && <p role="alert">{t('configuration.stale')}</p>}
                    {!attempt && (
                        <>
                            <label className="block">
                                {t('configuration.description')}
                                <textarea
                                    className="mt-2 block min-h-20 w-full rounded border bg-background p-2"
                                    value={description}
                                    maxLength={1000}
                                    disabled={locked}
                                    onChange={(e) =>
                                        setDescription(e.target.value)
                                    }
                                />
                            </label>
                            <MigrationDeclarations
                                key={`${review.id}/${declarationVersion}`}
                                disabled={
                                    locked || !fresh || !review.canConfirm
                                }
                                onConfirm={() =>
                                    command.mutate({
                                        kind: 'confirm',
                                        id: review.id,
                                    })
                                }
                            />
                        </>
                    )}
                    <Button
                        variant="outline"
                        disabled={locked}
                        onClick={() =>
                            command.mutate({ kind: 'cancel', id: review.id })
                        }
                    >
                        {t('configurationMigration.cancel')}
                    </Button>
                </section>
            )}
            {attempt && (
                <section className="product-panel space-y-3">
                    <p className="break-all font-mono">
                        {t('configurationMigration.operation', { id: attempt })}
                    </p>
                    <ConfigurationError error={operation.error} />
                    {operation.error && (
                        <p role="alert">
                            {t('configurationMigration.unknown')}
                        </p>
                    )}
                    {operation.data && (
                        <OperationCard operation={operation.data} />
                    )}
                    <Link
                        className="underline"
                        to="/configuration/migration-intents"
                    >
                        {t('configurationMigration.intents')}
                    </Link>
                </section>
            )}
            {(attempt || uncertain) && (
                <section className="space-y-3">
                    <p>{t('configurationMigration.nextNotice')}</p>
                    <Button
                        variant="outline"
                        disabled={command.isPending}
                        onClick={() => command.mutate({ kind: 'restart' })}
                    >
                        {t('configurationMigration.next')}
                    </Button>
                </section>
            )}
        </div>
    )
}
