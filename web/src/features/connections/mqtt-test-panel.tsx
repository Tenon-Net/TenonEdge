import { useEffect, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { components } from '../../api/schema'
import { connectionRequest, type ConnectionForm } from './model'

type Authentication = components['schemas']['MqttTestAuthentication']
export function MqttTestPanel({ form }: { form: ConnectionForm }) {
    const { t } = useTranslation()
    const [authentication, setAuthentication] = useState<Authentication>('none')
    const [username, setUsername] = useState('')
    const [password, setPassword] = useState('')
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
                        authentication,
                        credentialConnectionId:
                            authentication === 'stored' ? form.id : null,
                        username:
                            authentication === 'provided' ? username : null,
                        password:
                            authentication === 'provided' ? password : null,
                    },
                },
            )
            if (!data) throw new Error('missing-test-result')
            return { ...data, target: `${connection.host}:${connection.port}` }
        },
        onSettled: () => setPassword(''),
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
            </fieldset>
            <Button
                type="button"
                disabled={test.isPending}
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
                <div className="overflow-x-auto">
                    <table
                        className="w-full text-left text-sm"
                        aria-label={t('connections.mqttTest.stepsTitle')}
                    >
                        <thead>
                            <tr>
                                {['stage', 'status', 'elapsed', 'reason'].map(
                                    (column) => (
                                        <th
                                            className="border-b p-2"
                                            key={column}
                                        >
                                            {t(
                                                `connections.mqttTest.${column}`,
                                            )}
                                        </th>
                                    ),
                                )}
                            </tr>
                        </thead>
                        <tbody>
                            {test.data.steps.map((step) => (
                                <tr key={step.stage}>
                                    <th
                                        scope="row"
                                        className="border-b p-2 font-medium"
                                    >
                                        {t(
                                            `connections.mqttTest.stages.${step.stage}`,
                                        )}
                                    </th>
                                    <td className="border-b p-2">
                                        {t(
                                            `connections.mqttTest.statuses.${step.status}`,
                                        )}
                                    </td>
                                    <td className="border-b p-2">
                                        {step.elapsedMs == null
                                            ? '—'
                                            : `${Number(step.elapsedMs).toFixed(1)} ms`}
                                    </td>
                                    <td className="border-b p-2">
                                        {step.code && step.code !== 'success'
                                            ? t(
                                                  `connections.mqttTest.results.${step.code}`,
                                                  {
                                                      defaultValue: t(
                                                          'connections.mqttTest.failed',
                                                      ),
                                                  },
                                              )
                                            : '—'}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
        </section>
    )
}
