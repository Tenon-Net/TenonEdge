import {
    ClientCertificateFields,
    useClientCertificateUpload,
} from './client-certificate-fields'
import { useEffect, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { components } from '../../api/schema'
import {
    connectionRequest,
    type Connection,
    type ConnectionForm,
} from './model'
import { ConnectionTestSteps } from './test-steps'
import { ConnectionTrustPanel } from './trust-panel'

type Authentication = components['schemas']['ConnectionTestAuthentication']
type Kind = components['schemas']['HttpCredentialKind']
export function HttpTestPanel({
    form,
    savedConnection,
}: {
    form: ConnectionForm
    savedConnection?: Connection
}) {
    const { t } = useTranslation()
    const [authentication, setAuthentication] = useState<Authentication>('none')
    const [kind, setKind] = useState<Kind>('bearer')
    const [username, setUsername] = useState('')
    const [name, setName] = useState('')
    const [secret, setSecret] = useState('')
    const certificate = useClientCertificateUpload()
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const test = useMutation({
        retry: false,
        gcTime: 0,
        mutationFn: async () => {
            let connection
            try {
                connection = connectionRequest(form).http!
            } catch {
                throw new ApiError(400, {
                    errors: { headers: [t('connections.duplicateHeaders')] },
                })
            }
            controller.current = new AbortController()
            const { data } = await api.POST(
                '/api/edge-admin/v1/connections/test/http',
                {
                    signal: controller.current.signal,
                    body: {
                        connection,
                        connectionId: form.id ?? null,
                        authentication,
                        credential:
                            authentication === 'provided'
                                ? kind === 'clientCertificate'
                                    ? { kind, certificate: certificate.upload }
                                    : {
                                          kind,
                                          secret,
                                          username:
                                              kind === 'basic'
                                                  ? username
                                                  : null,
                                          name:
                                              kind === 'apiKeyHeader' ||
                                              kind === 'apiKeyQuery'
                                                  ? name
                                                  : null,
                                      }
                                : null,
                    },
                },
            )
            if (!data) throw new Error('missing-test-result')
            return {
                ...data,
                target: connection.baseAddress,
                formSnapshot: JSON.stringify(form),
            }
        },
        onSettled: () => {
            setSecret('')
            certificate.clear()
        },
    })
    return (
        <section
            className="space-y-3 rounded-lg border p-4"
            aria-label={t('connections.httpTest.title')}
        >
            <h2 className="font-semibold">{t('connections.httpTest.title')}</h2>
            <p className="text-sm text-muted-foreground">
                {t('connections.httpTest.notice')}
            </p>
            <fieldset
                disabled={test.isPending}
                className="flex flex-wrap gap-3"
            >
                <label>
                    {t('connections.httpTest.authentication')}
                    <select
                        className="ml-2 rounded border bg-background p-2"
                        value={authentication}
                        onChange={(event) => {
                            setAuthentication(
                                event.target.value as Authentication,
                            )
                            setSecret('')
                            certificate.clear()
                            test.reset()
                        }}
                    >
                        <option value="none">
                            {t('connections.httpTest.none')}
                        </option>
                        {form.id !== undefined && (
                            <option value="stored">
                                {t('connections.httpTest.stored')}
                            </option>
                        )}
                        <option value="provided">
                            {t('connections.httpTest.provided')}
                        </option>
                    </select>
                </label>
                {authentication === 'provided' && (
                    <>
                        <label>
                            {t('connections.httpTest.credentialKind')}
                            <select
                                className="ml-2 rounded border bg-background p-2"
                                value={kind}
                                onChange={(event) => {
                                    setKind(event.target.value as Kind)
                                    setSecret('')
                                    certificate.clear()
                                    test.reset()
                                }}
                            >
                                {(
                                    [
                                        'basic',
                                        'bearer',
                                        'apiKeyHeader',
                                        'apiKeyQuery',
                                        'clientCertificate',
                                    ] as const
                                ).map((value) => (
                                    <option key={value} value={value}>
                                        {value === 'clientCertificate'
                                            ? t(
                                                  'clientCertificate.kinds.clientCertificate',
                                              )
                                            : t(
                                                  `connections.httpTest.kinds.${value}`,
                                              )}
                                    </option>
                                ))}
                            </select>
                        </label>
                        {kind === 'basic' && (
                            <label>
                                {t('connections.httpTest.username')}
                                <input
                                    className="ml-2 rounded border p-2"
                                    value={username}
                                    maxLength={256}
                                    autoComplete="off"
                                    onChange={(event) =>
                                        setUsername(event.target.value)
                                    }
                                />
                            </label>
                        )}
                        {(kind === 'apiKeyHeader' ||
                            kind === 'apiKeyQuery') && (
                            <label>
                                {t('connections.httpTest.name')}
                                <input
                                    className="ml-2 rounded border p-2"
                                    value={name}
                                    maxLength={128}
                                    autoComplete="off"
                                    onChange={(event) =>
                                        setName(event.target.value)
                                    }
                                />
                            </label>
                        )}
                        {kind === 'clientCertificate' ? (
                            <ClientCertificateFields value={certificate} />
                        ) : (
                            <label>
                                {t('connections.httpTest.password')}
                                <input
                                    className="ml-2 rounded border p-2"
                                    type="password"
                                    value={secret}
                                    maxLength={4096}
                                    autoComplete="new-password"
                                    onChange={(event) =>
                                        setSecret(event.target.value)
                                    }
                                />
                            </label>
                        )}
                    </>
                )}
            </fieldset>
            <Button
                type="button"
                disabled={
                    test.isPending ||
                    (authentication === 'provided' &&
                        kind === 'clientCertificate' &&
                        !certificate.ready)
                }
                onClick={() => test.mutate()}
            >
                {t(
                    test.isPending
                        ? 'connections.httpTest.running'
                        : 'connections.httpTest.run',
                )}
            </Button>
            <ErrorMessage error={test.error} />
            {test.data && (
                <>
                    <p role="status">
                        {test.data.target} —{' '}
                        {t(`connections.httpTest.results.${test.data.code}`, {
                            defaultValue: t('connections.httpTest.failed'),
                        })}
                        {' · '}
                        {Number(test.data.elapsedMs).toFixed(0)} ms
                        {test.data.statusCode != null &&
                            ` · HTTP ${test.data.statusCode}`}
                    </p>
                    <ConnectionTestSteps
                        steps={test.data.steps}
                        protocol="http"
                    />
                </>
            )}
            <ConnectionTrustPanel
                form={form}
                savedConnection={savedConnection}
                tested={test.data}
            />
        </section>
    )
}
