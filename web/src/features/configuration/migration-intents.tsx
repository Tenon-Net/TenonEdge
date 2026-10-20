import { PageHeading } from '../../components/layout/page'
import { useState } from 'react'
import { create } from 'zustand'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { api, useSession } from '../../lib/session'
import { connectionFilter } from '../deliveries/api'
import {
    createDeliveryMigrationApi,
    migrationQuota,
} from '../deliveries/migration-api'
import { MigrationQuotaPanel } from '../deliveries/migration-review'
import { dateText } from '../diagnostics/view'
import { configurationMigrations } from './queries'
import type { IntentCursor, IntentFilter } from './migration-api'
import { ConfigurationError } from './view'
import { IntentInspectionPanel, MigrationDeclarations } from './migration-view'

const migrations = createDeliveryMigrationApi(api)
const useAttempt = create<{ epoch: number; id?: string }>(() => ({ epoch: -1 }))
export function ConfigurationMigrationIntentsPage() {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    return role === 'administrator' ? (
        <Intents />
    ) : (
        <p role="alert">{t('migration.adminOnly')}</p>
    )
}
function Intents() {
    const { t } = useTranslation()
    const { intentId, attemptId } = useSearch({
        from: '/configuration/migration-intents',
    })
    const navigate = useNavigate({ from: '/configuration/migration-intents' })
    const epoch = useSession((s) => s.epoch)
    const last = useAttempt()
    const attempt = attemptId ?? (last.epoch === epoch ? last.id : undefined)
    const queries = useQueryClient()
    const [status, setStatus] = useState('')
    const [connection, setConnection] = useState('')
    const [filter, setFilter] = useState<IntentFilter>({})
    const [after, setAfter] = useState<IntentCursor>()
    const [invalid, setInvalid] = useState(false)
    const [uncertain, setUncertain] = useState(false)
    const [declarationVersion, resetDeclarations] = useState(0)
    const key = ['configuration-intent-retry-preparation', epoch]
    const current = useQuery({
        queryKey: key,
        queryFn: ({ signal }) => configurationMigrations.currentRetry(signal),
        gcTime: 0,
        refetchInterval: (q) =>
            q.state.data && !q.state.error ? 10000 : false,
    })
    const list = useQuery({
        queryKey: ['configuration-intents', epoch, filter, after],
        queryFn: ({ signal }) =>
            configurationMigrations.intents(filter, after, signal),
        gcTime: 0,
    })
    const detailKey = ['configuration-intent', epoch, intentId]
    const detail = useQuery({
        queryKey: detailKey,
        queryFn: ({ signal }) =>
            configurationMigrations.outcome(intentId!, signal),
        enabled: !!intentId,
        gcTime: 0,
        refetchInterval: (q) =>
            !q.state.error && q.state.data?.status === 'waiting' ? 2000 : false,
    })
    const inspectionKey = ['configuration-intent-inspection', epoch, intentId]
    const inspection = useQuery({
        queryKey: inspectionKey,
        queryFn: ({ signal }) =>
            configurationMigrations.inspect(intentId!, signal),
        enabled: !!intentId,
        gcTime: 0,
    })
    const outcome = useQuery({
        queryKey: ['configuration-intent-attempt', epoch, attempt],
        queryFn: ({ signal }) => migrations.outcome(attempt!, signal),
        enabled: !!attempt,
        gcTime: 0,
        refetchInterval: (q) =>
            !q.state.error &&
            (!q.state.data || q.state.data.status === 'started')
                ? 2000
                : false,
    })
    type Action =
        | { kind: 'prepare' | 'confirm' | 'cancel'; id: string }
        | { kind: 'restart' }
    const command = useMutation({
        retry: false,
        mutationFn: async (action: Action) => {
            if (action.kind === 'prepare')
                return configurationMigrations.prepareRetry(action.id)
            if (action.kind === 'cancel') {
                await configurationMigrations.cancelRetry(action.id)
                return null
            }
            if (action.kind === 'confirm') {
                useAttempt.setState({ epoch, id: action.id })
                await navigate({
                    search: {
                        intentId: current.data!.inspection.intentId,
                        attemptId: action.id,
                    },
                    replace: true,
                })
                await configurationMigrations.confirmRetry(
                    action.id,
                    true,
                    true,
                )
                return null
            }
            const prepared = await configurationMigrations.currentRetry()
            if (prepared) await configurationMigrations.cancelRetry(prepared.id)
            await navigate({ search: { intentId }, replace: true })
            useAttempt.setState({ epoch, id: undefined })
            return null
        },
        onMutate: async () => {
            resetDeclarations((n) => n + 1)
            await queries.cancelQueries({ queryKey: key })
        },
        onSuccess: (value) => {
            queries.setQueryData(key, value)
            setUncertain(false)
        },
        onError: () => setUncertain(true),
        onSettled: () => {
            for (const queryKey of [
                key,
                detailKey,
                inspectionKey,
                ['configuration-intents'],
                ['configuration-intent-attempt'],
            ])
                void queries.invalidateQueries({ queryKey })
        },
    })
    const locked =
        command.isPending ||
        uncertain ||
        current.isPending ||
        current.isFetching ||
        current.isError
    const review = current.data
    const deficit = migrationQuota(command.error)
    async function refresh() {
        resetDeclarations((n) => n + 1)
        const result = await current.refetch()
        if (!result.error) {
            setUncertain(false)
            command.reset()
        }
    }
    return (
        <div className="product-page">
            <PageHeading title={t('configurationMigration.intents')} />
            <nav className="flex flex-wrap gap-4">
                <Link className="underline" to="/configuration">
                    {t('configurationMigration.back')}
                </Link>
                <Link className="underline" to="/configuration/migrations">
                    {t('configurationMigration.open')}
                </Link>
            </nav>
            <p>{t('configurationMigration.listBoundary')}</p>
            <form
                className="product-toolbar"
                onSubmit={(e) => {
                    e.preventDefault()
                    try {
                        const connectionId = connectionFilter(connection.trim())
                        setFilter({ status: status || undefined, connectionId })
                        setAfter(undefined)
                        setInvalid(false)
                    } catch {
                        setInvalid(true)
                    }
                }}
            >
                <label>
                    {t('configurationMigration.status')}
                    <select
                        aria-label={t('configurationMigration.status')}
                        className="block rounded border bg-background p-2"
                        value={status}
                        onChange={(e) => setStatus(e.target.value)}
                    >
                        <option value="">
                            {t('configurationMigration.all')}
                        </option>
                        {['waiting', 'rejected', 'completed'].map((s) => (
                            <option key={s} value={s}>
                                {t(`configurationMigration.states.${s}`)}
                            </option>
                        ))}
                    </select>
                </label>
                <label>
                    {t('configurationMigration.connection')}
                    <Input
                        value={connection}
                        maxLength={16}
                        onChange={(e) => setConnection(e.target.value)}
                    />
                </label>
                <Button type="submit">
                    {t('configurationMigration.filter')}
                </Button>
                {invalid && (
                    <p role="alert">{t('configurationMigration.invalid')}</p>
                )}
            </form>
            <ConfigurationError error={list.error} />
            {list.isError && <p role="alert">{t('migration.readError')}</p>}
            {list.isPending && <p role="status">{t('loading')}</p>}
            {!list.isError && list.data && (
                <section className="space-y-3">
                    {list.data.items.length === 0 && (
                        <p>{t('configurationMigration.empty')}</p>
                    )}
                    {list.data.items.map((item) => (
                        <article
                            key={item.id}
                            className="space-y-2 rounded border p-3"
                        >
                            <p>
                                {t('configurationMigration.item', {
                                    revision: item.revision,
                                    source: item.sourceRevision,
                                    connection: item.connectionId,
                                    account: item.registeredBy,
                                })}
                            </p>
                            <p>
                                {t(
                                    `configurationMigration.states.${item.status}`,
                                )}
                            </p>
                            <Link
                                className="break-all underline"
                                to="/configuration/migration-intents"
                                search={{ intentId: item.id, attemptId }}
                            >
                                {t('configurationMigration.inspect', {
                                    id: item.id,
                                })}
                            </Link>
                        </article>
                    ))}
                </section>
            )}
            <div className="flex flex-wrap gap-3">
                <Button
                    variant="outline"
                    disabled={!after || list.isFetching}
                    onClick={() => setAfter(undefined)}
                >
                    {t('configurationMigration.first')}
                </Button>
                <Button
                    variant="outline"
                    disabled={
                        list.isFetching || list.isError || !list.data?.next
                    }
                    onClick={() => setAfter(list.data!.next!)}
                >
                    {t('configurationMigration.nextPage')}
                </Button>
                <Button
                    variant="outline"
                    disabled={list.isFetching}
                    onClick={() => void list.refetch()}
                >
                    {t('configurationMigration.refreshList')}
                </Button>
            </div>
            {intentId && (
                <section
                    aria-label={t('configurationMigration.detail')}
                    className="product-panel space-y-4"
                >
                    <h2 className="text-base font-semibold">
                        {t('configurationMigration.detail')}
                    </h2>
                    <p className="break-all">
                        {t('configurationMigration.intentId', { id: intentId })}
                    </p>
                    <ConfigurationError
                        error={detail.error || inspection.error}
                    />
                    {(detail.error || inspection.error) && (
                        <p role="alert">{t('migration.readError')}</p>
                    )}
                    {detail.data && !detail.isError && (
                        <>
                            <p role="status">
                                {t(
                                    `configurationMigration.states.${detail.data.status}`,
                                )}
                            </p>
                            {detail.data.operationId && (
                                <p className="break-all">
                                    {t('configurationMigration.lastOperation', {
                                        id: detail.data.operationId,
                                    })}
                                </p>
                            )}
                            {detail.data.attemptStatus && (
                                <p>
                                    {t(
                                        `migration.states.${detail.data.attemptStatus}`,
                                    )}
                                </p>
                            )}
                            {detail.data.affectedRecords !== null && (
                                <p>
                                    {t('migration.resultCount', {
                                        count: detail.data.affectedRecords,
                                    })}
                                </p>
                            )}
                            {detail.data.errorCode && (
                                <p>
                                    {t(
                                        `migration.errors.${detail.data.errorCode}`,
                                        { defaultValue: detail.data.errorCode },
                                    )}
                                </p>
                            )}
                        </>
                    )}
                    {inspection.data && !inspection.isError && (
                        <IntentInspectionPanel
                            value={inspection.data}
                            showStatus={false}
                        />
                    )}
                    <Button
                        variant="outline"
                        disabled={
                            command.isPending ||
                            inspection.isFetching ||
                            detail.isFetching
                        }
                        onClick={() => {
                            resetDeclarations((n) => n + 1)
                            void inspection.refetch()
                            void detail.refetch()
                        }}
                    >
                        {t('configurationMigration.refreshDetail')}
                    </Button>
                    {!attempt && !review && (
                        <Button
                            disabled={
                                locked ||
                                list.isError ||
                                inspection.isFetching ||
                                inspection.isError ||
                                detail.isError ||
                                detail.isFetching ||
                                !inspection.data?.canPrepareRetry
                            }
                            onClick={() =>
                                command.mutate({
                                    kind: 'prepare',
                                    id: intentId,
                                })
                            }
                        >
                            {t('configurationMigration.prepareRetry')}
                        </Button>
                    )}
                </section>
            )}
            <p>{t('configurationMigration.retryBoundary')}</p>
            <p className="text-sm">
                {t('configurationMigration.retryLifetime')}
            </p>
            <Button
                variant="outline"
                disabled={command.isPending || current.isFetching}
                onClick={() => void refresh()}
            >
                {t('configurationMigration.retryRefresh')}
            </Button>
            <ConfigurationError error={current.error || command.error} />
            {current.isError && <p role="alert">{t('migration.readError')}</p>}
            {uncertain && <p role="alert">{t('migration.uncertain')}</p>}
            {command.isPending && <p role="status">{t('migration.pending')}</p>}
            {deficit && <MigrationQuotaPanel quota={deficit} />}
            {review && (
                <section
                    aria-label={t('configurationMigration.retryReview')}
                    className="product-panel space-y-4"
                >
                    <h2 className="text-base font-semibold">
                        {t('configurationMigration.retryReview')}
                    </h2>
                    <p>
                        {t('configurationMigration.expires', {
                            time: dateText(review.expiresAt),
                        })}
                    </p>
                    <IntentInspectionPanel value={review.inspection} />
                    {!attempt && (
                        <MigrationDeclarations
                            retry
                            key={`${review.id}/${declarationVersion}`}
                            disabled={
                                locked ||
                                !review.canConfirm ||
                                list.isError ||
                                (!!intentId &&
                                    (detail.isError ||
                                        detail.isFetching ||
                                        inspection.isError ||
                                        inspection.isFetching))
                            }
                            onConfirm={() =>
                                command.mutate({
                                    kind: 'confirm',
                                    id: review.id,
                                })
                            }
                        />
                    )}
                    <Button
                        variant="outline"
                        disabled={locked}
                        onClick={() =>
                            command.mutate({ kind: 'cancel', id: review.id })
                        }
                    >
                        {t('configurationMigration.retryCancel')}
                    </Button>
                </section>
            )}
            {attempt && (
                <section
                    aria-label={t('migration.lookup')}
                    className="product-panel space-y-3"
                >
                    <h2 className="font-semibold">{t('migration.lookup')}</h2>
                    <p className="break-all font-mono">
                        {t('migration.operation', { id: attempt })}
                    </p>
                    {outcome.isPending && <p role="status">{t('loading')}</p>}
                    {outcome.error && (
                        <p role="alert">{t('migration.readError')}</p>
                    )}
                    {outcome.data === null && (
                        <p role="status">{t('migration.notFound')}</p>
                    )}
                    {outcome.data && !outcome.isError && (
                        <>
                            <p role="status">
                                {t(`migration.states.${outcome.data.status}`)}
                            </p>
                            {outcome.data.affectedRecords !== null && (
                                <p>
                                    {t('migration.resultCount', {
                                        count: outcome.data.affectedRecords,
                                    })}
                                </p>
                            )}
                            {outcome.data.errorCode && (
                                <p>
                                    {t(
                                        `migration.errors.${outcome.data.errorCode}`,
                                        {
                                            defaultValue:
                                                outcome.data.errorCode,
                                        },
                                    )}
                                </p>
                            )}
                        </>
                    )}
                    <Button
                        variant="outline"
                        disabled={outcome.isFetching}
                        onClick={() => void outcome.refetch()}
                    >
                        {t('migration.queryAgain')}
                    </Button>
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
                        {t('configurationMigration.retryNext')}
                    </Button>
                </section>
            )}
        </div>
    )
}
