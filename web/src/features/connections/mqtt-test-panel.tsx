import {
    ClientCertificateFields,
    useClientCertificateUpload,
} from './client-certificate-fields'
import { useEffect, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/session'
import { ConnectionTestSteps } from './test-steps'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { components } from '../../api/schema'
import {
    connectionRequest,
    type Connection,
    type ConnectionForm,
} from './model'
import { ConnectionTrustPanel } from './trust-panel'

type Authentication = components['schemas']['ConnectionTestAuthentication']
export function MqttTestPanel({
    form,
    savedConnection,
}: {
    form: ConnectionForm
    savedConnection?: Connection
}) {
    const { t } = useTranslation()
    const [authentication, setAuthentication] = useState<Authentication>('none')
    const [username, setUsername] = useState('')
    const [password, setPassword] = useState('')
    const [kind, setKind] = useState<'password' | 'clientCertificate'>(
        'password',
    )
    const certificate = useClientCertificateUpload()
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const test = useMutation({
        retry: false,
        gcTime: 0,
        // No secret or form contents enter mutation variables or persistent stores.
        mutationFn: async () => {
            controller.current = new AbortController()
            const connection = connectionRequest(form).mqtt!
            const { data } = await api.POST(
                '/api/edge-admin/v1/connections/test/mqtt',
                {
                    signal: controller.current.signal,
                    body: {
                        connection,
                        connectionId: form.id ?? null,
                        authentication,
                        credentialConnectionId:
                            authentication === 'stored' ? form.id : null,
                        ...(authentication === 'provided'
                            ? kind === 'clientCertificate'
                                ? { certificate: certificate.upload }
                                : { username, password }
                            : {}),
                    },
                },
            )
            if (!data) throw new Error('missing-test-result')
            return {
                ...data,
                target: `${connection.host}:${connection.port}`,
                formSnapshot: JSON.stringify(form),
            }
        },
        onSettled: () => {
            setPassword('')
            certificate.clear()
        },
    })
    return (
        <section
            className="space-y-3 rounded-lg border p-4"
            aria-label={t('connections.mqttTest.title')}
        >
            <h2 className="font-semibold">{t('connections.mqttTest.title')}</h2>
            <p className="text-sm text-muted-foreground">
                {t('connections.mqttTest.notice')}
            </p>
            <fieldset
                disabled={test.isPending}
                className="flex flex-wrap gap-3"
            >
                <label>
                    {t('connections.mqttTest.authentication')}
                    <select
                        className="ml-2 rounded border bg-background p-2"
                        value={authentication}
                        onChange={(event) => {
                            setAuthentication(
                                event.target.value as Authentication,
                            )
                            setPassword('')
                            certificate.clear()
                            test.reset()
                        }}
                    >
                        <option value="none">
                            {t('connections.mqttTest.none')}
                        </option>
                        {form.id !== undefined && (
                            <option value="stored">
                                {t('connections.mqttTest.stored')}
                            </option>
                        )}
                        <option value="provided">
                            {t('connections.mqttTest.provided')}
                        </option>
                    </select>
                </label>
                {authentication === 'provided' && (
                    <>
                        <label>
                            {t('credentials.kind')}
                            <select
                                className="ml-2 rounded border p-2"
                                value={kind}
                                onChange={(event) => {
                                    setKind(
                                        event.target.value as
                                            'password' | 'clientCertificate',
                                    )
                                    setPassword('')
                                    setUsername('')
                                    certificate.clear()
                                    test.reset()
                                }}
                            >
                                <option value="password">
                                    {t('clientCertificate.kinds.password')}
                                </option>
                                <option value="clientCertificate">
                                    {t(
                                        'clientCertificate.kinds.clientCertificate',
                                    )}
                                </option>
                            </select>
                        </label>
                        {kind === 'clientCertificate' ? (
                            <ClientCertificateFields value={certificate} />
                        ) : (
                            <>
                                <label>
                                    {t('connections.mqttTest.username')}
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
                                <label>
                                    {t('connections.mqttTest.password')}
                                    <input
                                        className="ml-2 rounded border p-2"
                                        type="password"
                                        value={password}
                                        maxLength={4096}
                                        autoComplete="new-password"
                                        onChange={(event) =>
                                            setPassword(event.target.value)
                                        }
                                    />
                                </label>
                            </>
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
                        ? 'connections.mqttTest.running'
                        : 'connections.mqttTest.run',
                )}
            </Button>
            <ErrorMessage error={test.error} />
            {test.data && (
                <p role="status">
                    {test.data.target} —{' '}
                    {t(`connections.mqttTest.results.${test.data.code}`, {
                        defaultValue: t('connections.mqttTest.failed'),
                    })}
                    {' · '}
                    {Number(test.data.elapsedMs).toFixed(0)} ms
                    {test.data.connAckCode != null &&
                        ` · CONNACK ${test.data.connAckCode}`}
                </p>
            )}
            {test.data && (
                <ConnectionTestSteps steps={test.data.steps} protocol="mqtt" />
            )}
            <ConnectionTrustPanel
                form={form}
                savedConnection={savedConnection}
                tested={test.data}
            />
        </section>
    )
}
