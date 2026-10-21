import { useEffect, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import type { components } from '../../api/schema'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { credentialRecipient, type Connection } from './model'

type Kind = Exclude<
    components['schemas']['HttpCredentialKind'],
    'clientCertificate'
>
type Review = components['schemas']['CredentialChangeReview']
export function SecretCredential({
    connection,
    draftVersion,
    version,
    kind,
    blocked,
    refresh,
    onBusy,
    onDirty,
}: {
    connection: Connection
    draftVersion: string
    version: string | null
    kind: Kind
    blocked: boolean
    refresh: () => Promise<unknown>
    onBusy: (value: boolean) => void
    onDirty: (value: boolean) => void
}) {
    const { t } = useTranslation()
    const mqtt = connection.kind === 'mqtt'
    const [username, setUsername] = useState('')
    const [name, setName] = useState('')
    const [secret, setSecret] = useState('')
    const [review, setReview] = useState<Review | null>(null)
    const [account, setAccount] = useState('')
    const [acknowledged, setAcknowledged] = useState(false)
    const [needsRefresh, setNeedsRefresh] = useState(false)
    const [notice, setNotice] = useState('')
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const dirty = !!username || !!name || !!secret || review !== null
    useEffect(() => {
        onDirty(dirty)
        return () => onDirty(false)
    }, [onDirty, dirty])
    function clear() {
        setUsername('')
        setName('')
        setSecret('')
        setReview(null)
        setAccount('')
        setAcknowledged(false)
    }
    useEffect(() => {
        onDirty(
            !!(username || name || secret || review || account || acknowledged),
        )
    }, [username, name, secret, review, account, acknowledged, onDirty])
    useEffect(() => () => onDirty(false), [onDirty])
    const operation = useMutation({
        retry: false,
        gcTime: 0,
        // Only action names enter mutation variables; never the secret or the final request.
        mutationFn: async (action: 'review' | 'save' | 'refresh') => {
            if (action === 'refresh') {
                await refresh()
                setNeedsRefresh(false)
                setNotice('')
                return
            }
            controller.current = new AbortController()
            const params = { path: { connectionId: Number(connection.id) } }
            if (action === 'review') {
                const { data } = await api.POST(
                    '/api/edge-admin/v1/connections/{connectionId}/credential-review',
                    {
                        params,
                        signal: controller.current.signal,
                        body: {
                            kind: connection.kind,
                            recipient: credentialRecipient(connection),
                            expectedDraftVersion: draftVersion,
                            expectedVersion: version,
                            authenticationKind: mqtt ? 'password' : kind,
                            username:
                                mqtt || kind === 'basic' ? username : null,
                            name:
                                kind === 'apiKeyHeader' ||
                                kind === 'apiKeyQuery'
                                    ? name
                                    : null,
                        },
                    },
                )
                if (!data) throw new Error('missing-credential-review')
                setReview(data)
                setAccount('')
                setAcknowledged(false)
                return
            }
            if (
                !review ||
                !acknowledged ||
                (review.accountDeclarationRequired && !account)
            )
                throw new Error('credential-review-required')
            const common = {
                retainExisting: false,
                recipient: review.recipient,
                expectedDraftVersion: review.expectedDraftVersion,
                expectedVersion: review.expectedVersion,
                accountChanged: account === '' ? null : account === 'changed',
            }
            try {
                if (mqtt)
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}/mqtt-credential',
                        {
                            params,
                            signal: controller.current.signal,
                            body: {
                                ...common,
                                username: review.account,
                                password: secret,
                            },
                        },
                    )
                else
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}/http-credential',
                        {
                            params,
                            signal: controller.current.signal,
                            body: {
                                ...common,
                                kind,
                                username: review.account,
                                name: review.name,
                                secret,
                            },
                        },
                    )
                setNotice('credentials.saved')
            } catch (error) {
                setNotice('credentials.reviewUnknown')
                throw error
            } finally {
                clear()
                setNeedsRefresh(true)
            }
            await refresh()
            setNeedsRefresh(false)
        },
    })
    useEffect(() => {
        onBusy(operation.isPending)
        return () => onBusy(false)
    }, [onBusy, operation.isPending])
    const busy = blocked || operation.isPending
    return (
        <form
            className="space-y-3"
            onSubmit={(event) => {
                event.preventDefault()
                if (!busy && !needsRefresh && !review) {
                    setNotice('')
                    operation.mutate('review')
                }
            }}
        >
            <fieldset
                disabled={busy || !!review || needsRefresh}
                className="grid gap-3 md:grid-cols-2"
            >
                {(mqtt || kind === 'basic') && (
                    <label>
                        {t('credentials.username')}
                        <input
                            className="mt-1 w-full rounded border p-2"
                            value={username}
                            onChange={(event) =>
                                setUsername(event.target.value)
                            }
                            autoComplete="off"
                            required
                            maxLength={256}
                        />
                    </label>
                )}
                {(kind === 'apiKeyHeader' || kind === 'apiKeyQuery') && (
                    <label>
                        {t('credentials.name')}
                        <input
                            className="mt-1 w-full rounded border p-2"
                            value={name}
                            onChange={(event) => setName(event.target.value)}
                            autoComplete="off"
                            required
                            maxLength={128}
                        />
                    </label>
                )}
                <label>
                    {t('credentials.secret')}
                    <input
                        className="mt-1 w-full rounded border p-2"
                        type="password"
                        value={secret}
                        onChange={(event) => setSecret(event.target.value)}
                        autoComplete="new-password"
                        required
                        maxLength={4096}
                    />
                </label>
            </fieldset>
            <ErrorMessage error={operation.error} />
            {notice && <p role="status">{t(notice)}</p>}
            {needsRefresh && (
                <Button
                    type="button"
                    variant="outline"
                    disabled={busy}
                    onClick={() => operation.mutate('refresh')}
                >
                    {t('clientCertificate.refresh')}
                </Button>
            )}
            {!review && (
                <Button type="submit" disabled={busy || needsRefresh}>
                    {t('credentials.review')}
                </Button>
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
                            {review.current ? (
                                <>
                                    <p className="break-all">
                                        {review.current.recipient}
                                    </p>
                                    <p>
                                        {t(
                                            `clientCertificate.kinds.${review.current.kind}`,
                                            {
                                                defaultValue:
                                                    review.current.kind,
                                            },
                                        )}
                                    </p>
                                    <p className="break-all">
                                        {review.current.account ??
                                            t(
                                                'clientCertificate.hiddenAccount',
                                            )}
                                    </p>
                                    {!review.current.configured && (
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
                            <p className="break-all">{review.recipient}</p>
                            <p>
                                {t(
                                    `clientCertificate.kinds.${review.authenticationKind}`,
                                )}
                            </p>
                            <p className="break-all">
                                {review.account ??
                                    t('clientCertificate.hiddenAccount')}
                            </p>
                            {review.name && (
                                <p className="break-all">
                                    {t('credentials.name')}: {review.name}
                                </p>
                            )}
                        </div>
                    </div>
                    <p>
                        {t('clientCertificate.count', {
                            pending: review.pendingCount,
                            failed: review.failedCount,
                            time: review.capturedAt,
                        })}
                    </p>
                    <p>{t('credentials.reviewLive')}</p>
                    <p>
                        {t(
                            review.identityChanged
                                ? 'clientCertificate.changedNotice'
                                : 'credentials.accountHelp',
                        )}
                    </p>
                    <fieldset className="space-y-3" disabled={busy}>
                        {review.accountDeclarationRequired && (
                            <label className="block">
                                {t('credentials.account')}
                                <select
                                    className="ml-2 rounded border p-2"
                                    value={account}
                                    onChange={(event) =>
                                        setAccount(event.target.value)
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
                                checked={acknowledged}
                                onChange={(event) =>
                                    setAcknowledged(event.target.checked)
                                }
                            />
                            {t('credentials.reviewAcknowledge')}
                        </label>
                        <div className="flex flex-wrap gap-2">
                            <Button
                                type="button"
                                disabled={
                                    !acknowledged ||
                                    (review.accountDeclarationRequired &&
                                        !account)
                                }
                                onClick={() => operation.mutate('save')}
                            >
                                {t('credentials.save')}
                            </Button>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() => {
                                    clear()
                                    operation.reset()
                                }}
                            >
                                {t('credentials.reviewCancel')}
                            </Button>
                        </div>
                    </fieldset>
                </section>
            )}
        </form>
    )
}
