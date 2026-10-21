import { useEffect, useRef } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { connectionRequest, type ConnectionForm } from './model'
import { ConnectionTestSteps } from './test-steps'

export function ModbusTestPanel({ form }: { form: ConnectionForm }) {
    const { t } = useTranslation()
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const test = useMutation({
        retry: false,
        gcTime: 0,
        mutationFn: async () => {
            const connection = connectionRequest(form).modbus!
            controller.current = new AbortController()
            const { data } = await api.POST(
                '/api/edge-admin/v1/connections/test/modbus',
                {
                    signal: controller.current.signal,
                    body: { connection, connectionId: form.id ?? null },
                },
            )
            if (!data) throw new Error('missing-test-result')
            return { ...data, target: `${connection.host}:${connection.port}` }
        },
    })
    return (
        <section
            className="space-y-3 rounded-lg border p-4"
            aria-label={t('connections.modbusTest.title')}
        >
            <h2 className="font-semibold">
                {t('connections.modbusTest.title')}
            </h2>
            <p className="text-sm text-muted-foreground">
                {t('connections.modbusTest.notice')}
            </p>
            <Button
                type="button"
                disabled={test.isPending}
                onClick={() => test.mutate()}
            >
                {t(
                    test.isPending
                        ? 'connections.modbusTest.running'
                        : 'connections.modbusTest.run',
                )}
            </Button>
            <ErrorMessage error={test.error} />
            {test.data && (
                <>
                    <p role="status">
                        {test.data.target} —{' '}
                        {t(`connections.modbusTest.results.${test.data.code}`, {
                            defaultValue: t('connections.modbusTest.failed'),
                        })}
                        {' · '}
                        {Number(test.data.elapsedMs).toFixed(0)} ms
                    </p>
                    <ConnectionTestSteps
                        steps={test.data.steps}
                        protocol="modbus"
                    />
                </>
            )}
        </section>
    )
}
