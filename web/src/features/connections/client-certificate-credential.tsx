import { useEffect, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import type { components } from '../../api/schema'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { credentialRecipient, type Connection } from './model'
import {
    ClientCertificateFields,
    useClientCertificateUpload,
} from './client-certificate-fields'

type Review = components['schemas']['ClientCertificateReview']
export function ClientCertificateCredential({
    connection,
    draftVersion,
    version,
    blocked,
    refresh,
    onBusy,
    onDirty,
}: {
    connection: Connection
    draftVersion: string
    version: string | null
    blocked: boolean
    onBusy: (value: boolean) => void
    onDirty: (value: boolean) => void
    refresh: () => Promise<unknown>
}) {
    const { t } = useTranslation()
    const material = useClientCertificateUpload()
    const [review, setReview] = useState<Review | null>(null)
    const [account, setAccount] = useState('')
    const [acknowledged, setAcknowledged] = useState(false)
    const [notice, setNotice] = useState('')
    const [needsRefresh, setNeedsRefresh] = useState(false)
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const recipient = credentialRecipient(connection)
    useEffect(() => {
        onDirty(
            material.format !== 'pkcs12' ||
                Object.keys(material.upload).length > 0 ||
                !!review ||
                !!account ||
                acknowledged,
        )
    }, [
        material.format,
        material.upload,
        review,
        account,
        acknowledged,
        onDirty,
    ])
    useEffect(() => () => onDirty(false), [onDirty])
    const tls =
        recipient.startsWith('https://') || recipient.startsWith('mqtts://')
    const operation = useMutation({
        retry: false,
        gcTime: 0,
        // Neither uploaded material nor requests containing secrets enter the mutation cache.
        mutationFn: async (action: 'review' | 'save' | 'refresh') => {
            if (action === 'refresh') {
                await refresh()
                setNeedsRefresh(false)
                setNotice('')
                return
            }
            controller.current = new AbortController()
            if (action === 'review') {
                const { data } = await api.POST(
                    '/api/edge-admin/v1/connections/{connectionId}/client-certificate-review',
                    {
                        params: {
                            path: { connectionId: Number(connection.id) },
                        },
                        signal: controller.current.signal,
                        body: {
                            kind: connection.kind,
                            recipient,
                            expectedDraftVersion: draftVersion,
                            expectedVersion: version,
                            certificate: material.upload,
                        },
                    },
                )
                if (!data) throw new Error('missing-certificate-review')
                setReview(data)
                setAccount('')
                setAcknowledged(false)
                return
            }
            if (
                !review ||
                !acknowledged ||
                (review.accountDeclarationRequired && account === '')
            )
                throw new Error('certificate-review-required')
            const body = {
                recipient: review.recipient,
                expectedDraftVersion: review.expectedDraftVersion,
                expectedVersion: review.expectedVersion,
                kind: 'clientCertificate' as const,
                retainExisting: false,
                certificate: material.upload,
                accountChanged: account === '' ? null : account === 'changed',
            }
            try {
                const options = {
                    params: { path: { connectionId: Number(connection.id) } },
                    signal: controller.current.signal,
                    body,
                }
                if (connection.kind === 'mqtt')
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}/mqtt-credential',
                        options,
                    )
                else
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}/http-credential',
                        options,
                    )
                setNotice('clientCertificate.saved')
            } catch (error) {
                setNeedsRefresh(true)
                setNotice('clientCertificate.unknown')
                throw error
            } finally {
                material.clear()
                setReview(null)
                setAccount('')
                setAcknowledged(false)
            }
            setNeedsRefresh(true)
            await refresh()
            setNeedsRefresh(false)
        },
    })
    useEffect(() => {
        onBusy(operation.isPending)
        return () => onBusy(false)
    }, [onBusy, operation.isPending])
    const busy = blocked || operation.isPending
    function cancel() {
        setReview(null)
        setAccount('')
        setAcknowledged(false)
        material.clear()
        operation.reset()
    }
    return (
        <div className="space-y-3">
            {!tls && <p role="alert">{t('clientCertificate.tlsRequired')}</p>}
            <ClientCertificateFields
                value={material}
                disabled={busy || !!review || needsRefresh || !tls}
            />
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
                <Button
                    type="button"
                    disabled={busy || needsRefresh || !tls || !material.ready}
                    onClick={() => {
                        setNotice('')
                        operation.mutate('review')
                    }}
                >
                    {t('clientCertificate.review')}
                </Button>
            )}
            {review && (
                <section
                    className="space-y-3 rounded border p-3"
                    aria-label={t('clientCertificate.reviewTitle')}
                >
                    <h3 className="font-semibold">
                        {t('clientCertificate.reviewTitle')}
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
                            <h4>{t('clientCertificate.proposed')}</h4>
                            <p className="break-all">
                                {t('clientCertificate.recipient')}:{' '}
                                {review.recipient}
                            </p>
                            <dl className="break-all">
                                <dt>{t('trust.subject')}</dt>
                                <dd>{review.certificate.subject}</dd>
                                <dt>{t('trust.issuer')}</dt>
                                <dd>{review.certificate.issuer}</dd>
                                <dt>{t('trust.notBefore')}</dt>
                                <dd>{review.certificate.notBefore}</dd>
                                <dt>{t('trust.notAfter')}</dt>
                                <dd>{review.certificate.notAfter}</dd>
                                <dt>{t('trust.fingerprint')}</dt>
                                <dd>{review.certificate.sha256Fingerprint}</dd>
                            </dl>
                        </div>
                    </div>
                    <p>
                        {t('clientCertificate.count', {
                            pending: review.pendingCount,
                            failed: review.failedCount,
                            time: review.capturedAt,
                        })}
                    </p>
                    <p>{t('clientCertificate.liveNotice')}</p>
                    <p>
                        {t(
                            review.identityChanged
                                ? 'clientCertificate.changedNotice'
                                : 'clientCertificate.sameNotice',
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
                            {t('clientCertificate.acknowledge')}
                        </label>
                        <div className="flex flex-wrap gap-2">
                            <Button
                                type="button"
                                disabled={
                                    !acknowledged ||
                                    (review.accountDeclarationRequired &&
                                        account === '')
                                }
                                onClick={() => operation.mutate('save')}
                            >
                                {t('clientCertificate.confirm')}
                            </Button>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={cancel}
                            >
                                {t('clientCertificate.cancel')}
                            </Button>
                        </div>
                    </fieldset>
                </section>
            )}
        </div>
    )
}
