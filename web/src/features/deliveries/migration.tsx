import { PageHeading } from '../../components/layout/page'
import { useState } from 'react'
import { create } from 'zustand'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { api, useSession } from '../../lib/session'
import {
    createDeliveryMigrationApi,
    migrationId,
    migrationProblem,
    migrationQuota,
} from './migration-api'
import { MigrationSelection } from './migration-selection'
import { MigrationReviewPanel, MigrationQuotaPanel } from './migration-review'
import type { DeliveryItem } from './api'

const migrations = createDeliveryMigrationApi(api)
// One last attempt per login, memory only. Navigation cannot re-enable its confirmation;
// the URL is set before sending so a reload can query the same durable operation.
const useAttempt = create<{ epoch: number; id?: string }>(() => ({ epoch: -1 }))
export function DeliveryMigrationPage() {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    return role === 'administrator' ? (
        <Migration />
    ) : (
        <div className="product-page">
            <PageHeading title={t('migration.title')} />
            <p role="alert">{t('migration.adminOnly')}</p>
            <Link className="underline" to="/runtime/outputs">
                {t('migration.back')}
            </Link>
        </div>
    )
}
function Migration() {
    const { t } = useTranslation()
    const { connectionId, operationId } = useSearch({
        from: '/deliveries/migrate',
    })
    const navigate = useNavigate({ from: '/deliveries/migrate' })
    const epoch = useSession((s) => s.epoch)
    const last = useAttempt()
    const attempt = operationId ?? (last.epoch === epoch ? last.id : undefined)
    const [lookup, setLookup] = useState('')
    const [invalidLookup, setInvalidLookup] = useState(false)
    const [confirmed, confirm] = useState<string>()
    const [uncertain, setUncertain] = useState(false)
    const [selectionVersion, resetSelection] = useState(0)
    const queries = useQueryClient()
    const key = ['delivery-migration-preparation', epoch]
    const current = useQuery({
        queryKey: key,
        queryFn: ({ signal }) => migrations.current(signal),
        gcTime: 0,
        refetchInterval: (q) =>
            q.state.data && !q.state.error ? 10000 : false,
    })
    const outcome = useQuery({
        queryKey: ['delivery-migration-outcome', epoch, attempt],
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
        | { kind: 'prepare'; target: string; items: DeliveryItem[] }
        | { kind: 'confirm' | 'cancel'; id: string }
        | { kind: 'restart' }
    const command = useMutation({
        retry: false,
        mutationFn: async (action: Action) => {
            if (action.kind === 'prepare')
                return migrations.prepare(
                    connectionId!,
                    action.target,
                    action.items,
                )
            if (action.kind === 'cancel') {
                await migrations.cancel(action.id)
                return null
            }
            if (action.kind === 'confirm') {
                useAttempt.setState({ epoch, id: action.id })
                await navigate({
                    search: { connectionId, operationId: action.id },
                    replace: true,
                })
                await migrations.confirm(action.id, true)
                return null
            }
            // Explicitly abandon any unconsumed preparation, never cancel a background migration.
            const prepared = await migrations.current()
            if (prepared) await migrations.cancel(prepared.id)
            await navigate({ search: { connectionId }, replace: true })
            useAttempt.setState({ epoch, id: undefined })
            resetSelection((n) => n + 1)
            return null
        },
        onMutate: async () => {
            confirm(undefined)
            await queries.cancelQueries({ queryKey: key })
        },
        onSuccess: (review) => {
            queries.setQueryData(key, review)
            setUncertain(false)
        },
        onError: () => setUncertain(true),
        onSettled: () => {
            void queries.invalidateQueries({ queryKey: key })
            void queries.invalidateQueries({
                queryKey: ['delivery-migration-outcome', epoch],
            })
        },
    })
    const review = current.data
    const deficit = migrationQuota(command.error)
    const locked =
        current.isPending ||
        current.isError ||
        current.isFetching ||
        command.isPending ||
        uncertain
    async function refresh() {
        confirm(undefined)
        const result = await current.refetch()
        if (!result.error) {
            setUncertain(false)
            command.reset()
        }
    }
    return (
        <div className="product-page">
            <PageHeading title={t('migration.title')} />
            <Link className="underline" to="/runtime/outputs">
                {t('migration.back')}
            </Link>
            <p>{t('migration.boundary')}</p>
            <p>{t('migration.safety')}</p>
            <p className="text-sm text-muted-foreground">
                {t('migration.lifetime')}
            </p>
            <Button
                variant="outline"
                disabled={current.isFetching || command.isPending}
                onClick={() => void refresh()}
            >
                {t('migration.refresh')}
            </Button>
            {current.isPending && <p role="status">{t('loading')}</p>}
            {current.error && <p role="alert">{t('migration.readError')}</p>}
            {command.isPending && (
                <p role="status">
                    {t(
                        command.variables.kind === 'confirm'
                            ? 'migration.pending'
                            : 'migration.preparing',
                    )}
                </p>
            )}
            {command.error && (
                <p role="alert">
                    {t('migration.uncertain')}{' '}
                    {t(`migration.errors.${migrationProblem(command.error)}`, {
                        defaultValue: '',
                    })}
                </p>
            )}
            {deficit && <MigrationQuotaPanel quota={deficit} />}
            {!attempt &&
                !review &&
                !current.isPending &&
                !current.error &&
                (connectionId ? (
                    <MigrationSelection
                        key={`${connectionId}/${selectionVersion}`}
                        source={connectionId}
                        disabled={locked}
                        prepare={(target, items) =>
                            command.mutate({ kind: 'prepare', target, items })
                        }
                    />
                ) : (
                    <p>{t('migration.enter')}</p>
                ))}
            {review && (
                <>
                    <MigrationReviewPanel key={review.id} review={review} />
                    {!attempt && (
                        <>
                            <label className="flex items-start gap-2">
                                <input
                                    type="checkbox"
                                    checked={confirmed === review.id}
                                    disabled={locked || !review.canConfirm}
                                    onChange={(e) =>
                                        confirm(
                                            e.target.checked
                                                ? review.id
                                                : undefined,
                                        )
                                    }
                                />
                                {t('migration.owner')}
                            </label>
                            <Button
                                disabled={
                                    locked ||
                                    !review.canConfirm ||
                                    confirmed !== review.id
                                }
                                onClick={() =>
                                    command.mutate({
                                        kind: 'confirm',
                                        id: review.id,
                                    })
                                }
                            >
                                {t('migration.confirm')}
                            </Button>
                        </>
                    )}
                    <Button
                        variant="outline"
                        disabled={locked}
                        onClick={() =>
                            command.mutate({ kind: 'cancel', id: review.id })
                        }
                    >
                        {t('migration.cancel')}
                    </Button>
                </>
            )}
            {attempt && (
                <section
                    className="product-panel space-y-3"
                    aria-label={t('migration.lookup')}
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
                    {outcome.data && (
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
            {(attempt || review || uncertain) && (
                <section className="space-y-2">
                    <p className="text-sm text-muted-foreground">
                        {t('migration.restartNotice')}
                    </p>
                    <Button
                        variant="outline"
                        disabled={command.isPending}
                        onClick={() => command.mutate({ kind: 'restart' })}
                    >
                        {t('migration.restart')}
                    </Button>
                </section>
            )}
            <form
                className="flex flex-wrap items-end gap-3"
                onSubmit={(e) => {
                    e.preventDefault()
                    try {
                        const id = migrationId(lookup.trim())
                        setInvalidLookup(false)
                        confirm(undefined)
                        void navigate({
                            search: { connectionId, operationId: id },
                            replace: true,
                        })
                    } catch {
                        setInvalidLookup(true)
                    }
                }}
            >
                <label>
                    {t('migration.lookupId')}
                    <Input
                        value={lookup}
                        maxLength={36}
                        onChange={(e) => setLookup(e.target.value)}
                    />
                </label>
                <Button
                    variant="outline"
                    disabled={command.isPending}
                    type="submit"
                >
                    {t('migration.lookupButton')}
                </Button>
                {invalidLookup && (
                    <p role="alert">{t('migration.readError')}</p>
                )}
            </form>
        </div>
    )
}
