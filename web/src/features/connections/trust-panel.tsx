import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import type { components } from '../../api/schema'
import { ApiError } from '../../api/errors'
import { api, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import {
    credentialRecipient,
    type Connection,
    type ConnectionForm,
} from './model'
import { certificateDerBase64, maximumTrustFileBytes } from './trust-file'

type Certificate = components['schemas']['ServerCertificateInfo']
type Write = components['schemas']['ConnectionTrustRequest']
type Clear = components['schemas']['ConnectionTrustClearRequest']
type Prepared =
    | { type: 'write'; body: Write; certificate: Certificate }
    | { type: 'clear'; body: Clear }

export function CertificateDetails({
    certificate,
}: {
    certificate: Pick<
        Certificate,
        'subject' | 'issuer' | 'notBefore' | 'notAfter' | 'sha256Fingerprint'
    >
}) {
    const { t } = useTranslation()
    return (
        <dl className="grid gap-1 break-all text-sm">
            {(['subject', 'issuer', 'notBefore', 'notAfter'] as const).map(
                (key) => (
                    <div key={key}>
                        <dt className="font-medium">{t(`trust.${key}`)}</dt>
                        <dd>{certificate[key] ?? '—'}</dd>
                    </div>
                ),
            )}
            <dt className="font-medium">{t('trust.fingerprint')}</dt>
            <dd className="font-mono">{certificate.sha256Fingerprint}</dd>
        </dl>
    )
}

/** All certificate bytes and pending confirmations stay in this mounted panel. */
export function ConnectionTrustPanel({
    form,
    savedConnection,
    tested,
}: {
    form: ConnectionForm
    savedConnection?: Connection
    tested?: { certificate?: Certificate | null; formSnapshot: string }
}) {
    const { t } = useTranslation()
    const administrator = useSession(
        (state) => state.value?.role === 'administrator',
    )
    const id = Number(savedConnection?.id ?? 0)
    const recipient = savedConnection
        ? credentialRecipient(savedConnection)
        : ''
    const tls =
        savedConnection?.http?.baseAddress.startsWith('https:') ||
        savedConnection?.mqtt?.tls === true
    const current = useQuery({
        queryKey: ['connection-trust', id, recipient],
        enabled: !!savedConnection && tls,
        retry: false,
        refetchOnWindowFocus: false,
        queryFn: async ({ signal }) => {
            try {
                const { data } = await api.GET(
                    '/api/edge-admin/v1/connections/{connectionId}/tls-trust',
                    { params: { path: { connectionId: id } }, signal },
                )
                if (!data) throw new Error('missing-trust')
                return data
            } catch (error) {
                if (error instanceof ApiError && error.status === 404)
                    return null
                throw error
            }
        },
    })
    const [file, setFile] = useState<File | null>(null)
    const [uploaded, setUploaded] = useState<Certificate | null>(null)
    const [prepared, setPrepared] = useState<Prepared | null>(null)
    const [acknowledged, setAcknowledged] = useState(false)
    const [submitted, setSubmitted] = useState(false)
    const [notice, setNotice] = useState('')
    const inspectController = useRef<AbortController | null>(null)
    useEffect(() => () => inspectController.current?.abort(), [])
    const inspect = useMutation({
        retry: false,
        gcTime: 0,
        mutationFn: async () => {
            if (!file || !file.size || file.size > maximumTrustFileBytes)
                throw new Error('trust.fileSize')
            const encoded = certificateDerBase64(
                new Uint8Array(await file.arrayBuffer()),
            )
            inspectController.current = new AbortController()
            const { data } = await api.POST(
                '/api/edge-admin/v1/connections/tls-trust/inspect',
                {
                    body: { certificateDerBase64: encoded },
                    signal: inspectController.current.signal,
                },
            )
            if (!data) throw new Error('missing-certificate')
            return data
        },
        onSuccess: setUploaded,
    })
    const change = useMutation({
        retry: false,
        gcTime: 0,
        mutationFn: async (candidate: Prepared) => {
            const params = { path: { connectionId: id } }
            if (candidate.type === 'write')
                await api.PUT(
                    '/api/edge-admin/v1/connections/{connectionId}/tls-trust',
                    { params, body: candidate.body },
                )
            else
                await api.POST(
                    '/api/edge-admin/v1/connections/{connectionId}/tls-trust/clear',
                    { params, body: candidate.body },
                )
            return candidate.type
        },
        onSuccess: (type) => {
            setNotice(type === 'write' ? 'trust.saved' : 'trust.cleared')
            void current.refetch()
        },
        onError: () => setNotice('trust.unknown'),
        onSettled: () => {
            setPrepared(null)
            setAcknowledged(false)
            setUploaded(null)
        },
    })
    // A late connection test may still display its old result, but can never authorize a different form.
    const observed =
        tested?.formSnapshot === JSON.stringify(form)
            ? tested.certificate
            : null
    const canChange =
        administrator &&
        tls &&
        !!savedConnection &&
        current.isSuccess &&
        !current.isFetching &&
        !submitted &&
        !change.isPending
    const busy = change.isPending || inspect.isPending
    function prepare(certificate: Certificate, mode: Write['mode']) {
        if (!canChange) return
        setAcknowledged(false)
        setPrepared({
            type: 'write',
            certificate,
            body: {
                expectedDraftVersion: form.version,
                recipient,
                expectedVersion: current.data?.version ?? null,
                mode,
                certificateDerBase64: certificate.certificateDerBase64,
                confirmedFingerprint: certificate.sha256Fingerprint,
                confirmed: true,
            },
        })
    }
    if (!tested?.certificate && !tls) return null
    return (
        <section
            className="space-y-3 rounded border p-4"
            aria-label={t('trust.title')}
        >
            <h3 className="font-semibold">{t('trust.title')}</h3>
            <p className="text-sm">{t('trust.notice')}</p>
            {tested?.certificate && (
                <div className="space-y-2">
                    <h4>{t('trust.observed')}</h4>
                    <CertificateDetails certificate={tested.certificate} />
                    <p className="text-sm">{t('trust.pinNotice')}</p>
                    {administrator && (
                        <Button
                            type="button"
                            variant="outline"
                            disabled={!canChange || !observed || busy}
                            onClick={() =>
                                observed &&
                                prepare(observed, 'pinnedCertificate')
                            }
                        >
                            {t('trust.pin')}
                        </Button>
                    )}
                </div>
            )}
            {!savedConnection && <p>{t('trust.saveFirst')}</p>}
            {!administrator && <p>{t('trust.adminOnly')}</p>}
            {tls && (
                <>
                    <h4 className="font-semibold">{t('trust.current')}</h4>
                    <ErrorMessage error={current.error} />
                    {current.isSuccess &&
                        (current.data ? (
                            <div className="space-y-2 text-sm">
                                <p>
                                    {t(`trust.${current.data.mode}`)} ·{' '}
                                    {t('trust.version')}: {current.data.version}
                                </p>
                                <p className="break-all">
                                    {t('trust.recipient')}:{' '}
                                    {current.data.recipient}
                                </p>
                                {current.data.recipient !== recipient && (
                                    <p role="alert">{t('trust.mismatch')}</p>
                                )}
                                {current.data.available ? (
                                    <CertificateDetails
                                        certificate={{
                                            subject: current.data.subject!,
                                            issuer: current.data.issuer!,
                                            notBefore: current.data.notBefore!,
                                            notAfter: current.data.notAfter!,
                                            sha256Fingerprint:
                                                current.data.sha256Fingerprint!,
                                        }}
                                    />
                                ) : (
                                    <p role="alert">{t('trust.unavailable')}</p>
                                )}
                            </div>
                        ) : (
                            <p>{t('trust.none')}</p>
                        ))}
                    <Button
                        type="button"
                        variant="outline"
                        disabled={busy || current.isFetching}
                        onClick={async () => {
                            setPrepared(null)
                            setAcknowledged(false)
                            setUploaded(null)
                            setSubmitted(false)
                            setNotice('')
                            change.reset()
                            await current.refetch()
                        }}
                    >
                        {t('trust.reload')}
                    </Button>
                </>
            )}
            {administrator && savedConnection && tls && (
                <fieldset disabled={!canChange || busy} className="space-y-3">
                    <p className="text-sm">{t('trust.caNotice')}</p>
                    <label className="block">
                        {t('trust.upload')}
                        <input
                            type="file"
                            accept=".pem,.crt,.cer,.der"
                            className="block"
                            onChange={(event) => {
                                setFile(event.target.files?.[0] ?? null)
                                setUploaded(null)
                                setPrepared(null)
                                setAcknowledged(false)
                                inspect.reset()
                            }}
                        />
                    </label>
                    <Button
                        type="button"
                        variant="outline"
                        disabled={!file || !canChange || busy}
                        onClick={() => inspect.mutate()}
                    >
                        {t('trust.inspect')}
                    </Button>
                    {uploaded && (
                        <div className="space-y-2">
                            <h4>{t('trust.inspected')}</h4>
                            <CertificateDetails certificate={uploaded} />
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() =>
                                    prepare(uploaded, 'certificateAuthority')
                                }
                            >
                                {t('trust.prepareCa')}
                            </Button>
                        </div>
                    )}
                    {current.data && (
                        <>
                            <p className="text-sm">{t('trust.clearNotice')}</p>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() => {
                                    setAcknowledged(false)
                                    setPrepared({
                                        type: 'clear',
                                        body: {
                                            expectedDraftVersion: form.version,
                                            recipient,
                                            expectedVersion:
                                                current.data!.version,
                                            confirmed: true,
                                        },
                                    })
                                }}
                            >
                                {t('trust.clear')}
                            </Button>
                        </>
                    )}
                </fieldset>
            )}
            {inspect.error instanceof Error &&
            inspect.error.message.startsWith('trust.') ? (
                <p role="alert">{t(inspect.error.message)}</p>
            ) : (
                <ErrorMessage error={inspect.error} />
            )}
            {prepared && (
                <div
                    className="space-y-3 rounded border p-3"
                    role="region"
                    aria-label={t('trust.confirmTitle')}
                >
                    <h4 className="font-semibold">{t('trust.confirmTitle')}</h4>
                    <p className="break-all">
                        {t('trust.recipient')}: {prepared.body.recipient}
                    </p>
                    {prepared.type === 'write' ? (
                        <>
                            <p>{t(`trust.${prepared.body.mode}`)}</p>
                            <CertificateDetails
                                certificate={prepared.certificate}
                            />
                        </>
                    ) : (
                        <p>{t('trust.clearNotice')}</p>
                    )}
                    <label className="flex gap-2">
                        <input
                            type="checkbox"
                            checked={acknowledged}
                            disabled={busy}
                            onChange={(event) =>
                                setAcknowledged(event.target.checked)
                            }
                        />
                        {t(
                            prepared.type === 'write'
                                ? 'trust.acknowledge'
                                : 'trust.acknowledgeClear',
                        )}
                    </label>
                    <div className="flex gap-2">
                        <Button
                            type="button"
                            disabled={!acknowledged || !canChange || busy}
                            onClick={() => {
                                setSubmitted(true)
                                setNotice('')
                                change.mutate(prepared)
                            }}
                        >
                            {t('trust.confirm')}
                        </Button>
                        <Button
                            type="button"
                            variant="outline"
                            disabled={busy}
                            onClick={() => {
                                setPrepared(null)
                                setAcknowledged(false)
                            }}
                        >
                            {t('trust.cancel')}
                        </Button>
                    </div>
                </div>
            )}
            <ErrorMessage error={change.error} />
            {notice && <p role="status">{t(notice)}</p>}
        </section>
    )
}
