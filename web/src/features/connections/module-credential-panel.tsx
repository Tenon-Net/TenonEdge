import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api, queryClient, useSession } from '../../lib/session'
import type { components } from '../../api/schema'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { ParameterFields } from '../parameters/fields'
import { parameterIssues } from '../parameters/model'
import { readRouteJson, writeRouteJson } from '../canvas/json'
import { routeObject } from '../canvas/codec'
import type { ConnectionModule } from './module-model'

type Review = components['schemas']['ModuleCredentialReview']
export function ModuleCredentialPanel({
    id,
    draftVersion,
    definition,
    blocked: externalBlocked,
    onBusy,
    onDirty,
}: {
    id: number
    draftVersion: string
    definition: ConnectionModule
    blocked: boolean
    onBusy: (value: boolean) => void
    onDirty: (value: boolean) => void
}) {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const bound = !!definition.recipientParameters?.length
    const canEdit =
        bound && (role === 'administrator' || role === 'configurator')
    const key = ['module-credential', id]
    const query = useQuery({
        queryKey: key,
        retry: false,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        queryFn: async ({ signal }) => {
            try {
                const { data } = await api.GET(
                    '/api/edge-admin/v1/connections/{connectionId}/module-credential',
                    { params: { path: { connectionId: id } }, signal },
                )
                if (!data) throw new Error('missing-module-credential')
                return data
            } catch (error) {
                if (error instanceof ApiError && error.status === 404)
                    return null
                throw error
            }
        },
    })
    const [account, setAccount] = useState('')
    const [secrets, setSecrets] = useState<Record<string, string>>({})
    const [generation, setGeneration] = useState(0)
    const [review, setReview] = useState<{
        data: Review
        parameters: string
    } | null>(null)
    const [declaration, setDeclaration] = useState('')
    const [confirmed, setConfirmed] = useState(false)
    const [needsRefresh, setNeedsRefresh] = useState(false)
    const [notice, setNotice] = useState('')
    const [showIssues, setShowIssues] = useState(false)
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const schema = {
        ...definition.parameters,
        parameters: definition.parameters.parameters?.filter(
            (field) => field.type === 'credential',
        ),
    }
    const issues = parameterIssues(
        schema,
        {},
        [],
        new Set(Object.keys(secrets)),
    )
    function clear() {
        setSecrets({})
        setAccount('')
        setReview(null)
        setDeclaration('')
        setConfirmed(false)
        setShowIssues(false)
        // Credential inputs are deliberately uncontrolled. Remount to clear their DOM values, too.
        setGeneration((value) => value + 1)
    }
    async function refresh() {
        await query.refetch({ throwOnError: true })
        await Promise.all([
            queryClient.invalidateQueries({ queryKey: ['connection-drafts'] }),
            queryClient.invalidateQueries({
                queryKey: ['configuration-summary'],
            }),
        ])
    }
    const operation = useMutation({
        retry: false,
        gcTime: 0,
        // Only an action name reaches the mutation cache. Never pass secrets as variables/results.
        mutationFn: async (
            action: 'review' | 'retain' | 'save' | 'clear' | 'refresh',
        ) => {
            controller.current = new AbortController()
            const common = {
                params: { path: { connectionId: id } },
                signal: controller.current.signal,
            }
            if (action === 'refresh') {
                clear()
                await refresh()
                setNeedsRefresh(false)
                setNotice('')
                return
            }
            if (action === 'review' || action === 'retain') {
                const { data } = await api.POST(
                    '/api/edge-admin/v1/connections/{connectionId}/module-credential/review',
                    {
                        ...common,
                        parseAs: 'text',
                        body: {
                            expectedDraftVersion: draftVersion,
                            expectedVersion:
                                query.data?.identity.version ?? null,
                            account: action === 'retain' ? null : account,
                            retainExisting: action === 'retain',
                        },
                    },
                )
                if (!data) throw new Error('missing-module-credential-review')
                const raw = routeObject(readRouteJson(data, 128 * 1024))
                setReview({
                    data: JSON.parse(data),
                    parameters: writeRouteJson(
                        routeObject(raw.recipientParameters),
                    ),
                })
                setDeclaration('')
                setConfirmed(false)
                return
            }
            if (action === 'clear') {
                await api.DELETE(
                    '/api/edge-admin/v1/connections/{connectionId}/module-credential',
                    {
                        ...common,
                        params: {
                            path: { connectionId: id },
                            query: {
                                expectedDraftVersion: draftVersion,
                                expectedVersion: query.data!.identity.version,
                            },
                        },
                    },
                )
            } else {
                const reviewed = review?.data
                if (
                    !reviewed ||
                    !confirmed ||
                    (reviewed.accountDeclarationRequired && !declaration)
                )
                    throw new Error('module-credential-review-required')
                await api.PUT(
                    '/api/edge-admin/v1/connections/{connectionId}/module-credential',
                    {
                        ...common,
                        body: {
                            expectedDraftVersion: reviewed.expectedDraftVersion,
                            expectedVersion: reviewed.expectedVersion,
                            retainExisting: reviewed.retainExisting,
                            account: reviewed.retainExisting
                                ? null
                                : reviewed.account,
                            secrets: reviewed.retainExisting ? null : secrets,
                            accountChanged:
                                declaration === ''
                                    ? null
                                    : declaration === 'changed',
                        },
                    },
                )
            }
            clear()
            setNeedsRefresh(true)
            setNotice(
                action === 'clear'
                    ? 'credentials.cleared'
                    : 'credentials.saved',
            )
            await refresh()
            setNeedsRefresh(false)
        },
        onError: () => {
            clear()
            setNeedsRefresh(true)
            setNotice('moduleConnections.credentialUnknown')
        },
    })
    useEffect(() => {
        onBusy(operation.isPending)
        return () => onBusy(false)
    }, [onBusy, operation.isPending])
    const dirty = !!account || Object.keys(secrets).length > 0 || !!review
    useEffect(() => {
        onDirty(dirty)
        return () => onDirty(false)
    }, [onDirty, dirty])
    const busy = operation.isPending || query.isFetching
    const blocked =
        externalBlocked ||
        busy ||
        query.isPending ||
        query.isError ||
        needsRefresh
    return (
        <section
            className="space-y-3 rounded-md border p-4"
            aria-label={t('moduleConnections.credentialTitle')}
        >
            <h2 className="font-semibold">
                {t('moduleConnections.credentialTitle')}
            </h2>
            <p>{t('moduleConnections.credentialNotice')}</p>
            {!bound && <p role="alert">{t('moduleConnections.unbound')}</p>}
            <p>
                {query.isPending
                    ? t('credentials.loading')
                    : query.isError
                      ? '—'
                      : t(
                            query.data?.configured
                                ? 'credentials.configured'
                                : 'credentials.empty',
                        )}
            </p>
            {query.data && (
                <>
                    <p className="break-all">{query.data.identity.recipient}</p>
                    <p>{query.data.identity.account}</p>
                    {!query.data.matchesDraft && (
                        <p role="alert">{t('credentials.mismatch')}</p>
                    )}
                </>
            )}
            <ErrorMessage error={query.error ?? operation.error} />
            {notice && <p role="status">{t(notice)}</p>}
            <Button
                type="button"
                variant="outline"
                disabled={busy}
                onClick={() => {
                    if (!dirty || window.confirm(t('connections.discard')))
                        operation.mutate('refresh')
                }}
            >
                {t('credentials.refresh')}
            </Button>
            {canEdit && (
                <form
                    className="space-y-3"
                    onSubmit={(event) => {
                        event.preventDefault()
                        setShowIssues(true)
                        if (!blocked && !review && !issues.length) {
                            setNotice('')
                            operation.mutate('review')
                        }
                    }}
                >
                    <fieldset
                        className="grid gap-3 md:grid-cols-2"
                        disabled={blocked || !!review}
                    >
                        <label>
                            {t('moduleConnections.account')}
                            <input
                                className="mt-1 w-full rounded border p-2"
                                value={account}
                                required
                                maxLength={128}
                                autoComplete="off"
                                onChange={(event) =>
                                    setAccount(event.target.value)
                                }
                            />
                        </label>
                        <ParameterFields
                            key={generation}
                            schema={schema}
                            values={{}}
                            onChange={() => {}}
                            issues={showIssues ? issues : []}
                            onCredentialChange={(name, value) =>
                                setSecrets((current) => {
                                    const next = { ...current }
                                    if (value) next[name] = value
                                    else delete next[name]
                                    return next
                                })
                            }
                        />
                    </fieldset>
                    {!review && (
                        <div className="flex gap-2 flex-wrap">
                            <Button type="submit" disabled={blocked}>
                                {t('credentials.review')}
                            </Button>
                            <Button
                                type="button"
                                variant="outline"
                                disabled={busy || !dirty}
                                onClick={clear}
                            >
                                {t('credentials.reviewCancel')}
                            </Button>
                            <Button
                                type="button"
                                variant="outline"
                                disabled={blocked || !query.data || dirty}
                                onClick={() => {
                                    if (
                                        window.confirm(
                                            t('credentials.confirmClear'),
                                        )
                                    )
                                        operation.mutate('clear')
                                }}
                            >
                                {t('credentials.clear')}
                            </Button>
                            {role === 'administrator' &&
                                query.data?.configured &&
                                !query.data.matchesDraft && (
                                    <Button
                                        type="button"
                                        variant="outline"
                                        disabled={blocked || dirty}
                                        onClick={() =>
                                            operation.mutate('retain')
                                        }
                                    >
                                        {t('credentials.retain')}
                                    </Button>
                                )}
                        </div>
                    )}
                    {review && (
                        <section
                            className="space-y-3 rounded border p-3"
                            aria-label={t('credentials.reviewTitle')}
                        >
                            <h3 className="font-semibold">
                                {t('credentials.reviewTitle')}
                            </h3>
                            <div className="grid gap-3 md:grid-cols-2">
                                <div>
                                    <h4>{t('clientCertificate.current')}</h4>
                                    {review.data.current ? (
                                        <>
                                            <p className="break-all">
                                                {review.data.current.recipient}
                                            </p>
                                            <p>{review.data.current.account}</p>
                                            <p>
                                                {review.data.current.kind} v
                                                {
                                                    review.data.current
                                                        .moduleVersion
                                                }
                                            </p>
                                            {!review.data.currentConfigured && (
                                                <p>
                                                    {t(
                                                        'clientCertificate.notConfigured',
                                                    )}
                                                </p>
                                            )}
                                        </>
                                    ) : (
                                        <p>{t('clientCertificate.absent')}</p>
                                    )}
                                </div>
                                <div>
                                    <h4>{t('credentials.proposed')}</h4>
                                    <p className="break-all">
                                        {review.data.recipient}
                                    </p>
                                    <p>{review.data.account}</p>
                                    <p>
                                        {review.data.kind} v
                                        {review.data.moduleVersion}
                                    </p>
                                    <pre className="overflow-auto whitespace-pre-wrap">
                                        {review.parameters}
                                    </pre>
                                </div>
                            </div>
                            <p>
                                {t('clientCertificate.count', {
                                    pending: review.data.pendingCount,
                                    failed: review.data.failedCount,
                                    time: review.data.capturedAt,
                                })}
                            </p>
                            <p>{t('credentials.reviewLive')}</p>
                            <p>
                                {t(
                                    review.data.identityChanged
                                        ? 'clientCertificate.changedNotice'
                                        : 'credentials.accountHelp',
                                )}
                            </p>
                            {review.data.retainExisting && (
                                <p>{t('credentials.confirmRetain')}</p>
                            )}
                            <fieldset disabled={blocked} className="space-y-3">
                                {review.data.accountDeclarationRequired && (
                                    <label>
                                        {t('credentials.account')}
                                        <select
                                            className="ml-2 rounded border p-2"
                                            value={declaration}
                                            onChange={(event) =>
                                                setDeclaration(
                                                    event.target.value,
                                                )
                                            }
                                        >
                                            <option value="">
                                                {t('credentials.choose')}
                                            </option>
                                            <option value="same">
                                                {t('credentials.same')}
                                            </option>
                                            <option value="changed">
                                                {t('credentials.changed')}
                                            </option>
                                        </select>
                                    </label>
                                )}
                                <label className="flex gap-2">
                                    <input
                                        type="checkbox"
                                        checked={confirmed}
                                        onChange={(event) =>
                                            setConfirmed(event.target.checked)
                                        }
                                    />
                                    {t('credentials.reviewAcknowledge')}
                                </label>
                                <div className="flex flex-wrap gap-2">
                                    <Button
                                        type="button"
                                        disabled={
                                            !confirmed ||
                                            (review.data
                                                .accountDeclarationRequired &&
                                                !declaration)
                                        }
                                        onClick={() => operation.mutate('save')}
                                    >
                                        {t('credentials.save')}
                                    </Button>
                                    <Button
                                        type="button"
                                        variant="outline"
                                        onClick={clear}
                                    >
                                        {t('credentials.reviewCancel')}
                                    </Button>
                                </div>
                            </fieldset>
                        </section>
                    )}
                </form>
            )}
        </section>
    )
}
