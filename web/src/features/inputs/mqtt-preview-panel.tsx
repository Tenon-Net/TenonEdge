import { useEffect, useRef, useState } from 'react'
import { useTranslation } from './i18n'
import type { components } from '../../api/schema'
import { api } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'

type Source = components['schemas']['MqttSourceDraft']
type Result = components['schemas']['MqttReadPreviewResult']
export function MqttPreviewPanel({
    deviceId,
    version,
    inputId,
    source,
    onSample,
    blocked = false,
}: {
    deviceId: number
    version: string
    inputId?: number
    source?: () => Source
    onSample?: (text: string) => void
    blocked?: boolean
}) {
    const { t } = useTranslation()
    const controller = useRef<AbortController | null>(null)
    // Captured payload stays in this component only, never in a query cache or storage.
    const [result, setResult] = useState<{ data: Result; source: string }>()
    const [error, setError] = useState<unknown>(null)
    const [pending, setPending] = useState(false)
    useEffect(() => () => controller.current?.abort(), [])
    const read = async () => {
        if (pending || blocked) return
        const abort = new AbortController()
        controller.current = abort
        setPending(true)
        setResult(undefined)
        setError(null)
        try {
            let current: Source | undefined
            try {
                current = source?.()
            } catch {
                throw new ApiError(400, {
                    errors: { preview: [t('inputs.invalid')] },
                })
            }
            const { data } = await api.POST(
                '/api/edge-admin/v1/devices/{deviceId}/preview/mqtt',
                {
                    params: { path: { deviceId } },
                    signal: abort.signal,
                    body: {
                        expectedVersion: version,
                        inputId: inputId ?? null,
                        source: current ?? null,
                    },
                },
            )
            if (!data) throw new Error('missing-preview')
            if (!abort.signal.aborted)
                setResult({ data, source: JSON.stringify(current) ?? '' })
        } catch (failure) {
            if (!abort.signal.aborted) setError(failure)
        } finally {
            if (!abort.signal.aborted) setPending(false)
        }
    }
    let unchanged = false
    try {
        unchanged = result?.source === (JSON.stringify(source?.()) ?? '')
    } catch {
        /* invalid edits cannot adopt an old sample */
    }
    const preview = result?.data.preview
    return (
        <section
            className="space-y-3 rounded border p-3"
            aria-label={t('inputs.mqttPreview.title')}
        >
            <h2 className="font-semibold">{t('inputs.mqttPreview.title')}</h2>
            <p className="text-sm text-muted-foreground">
                {t('inputs.mqttPreview.notice')}
            </p>
            <div className="flex flex-wrap gap-2">
                <Button
                    type="button"
                    disabled={blocked || pending}
                    onClick={() => void read()}
                >
                    {t(
                        pending
                            ? 'inputs.mqttPreview.waiting'
                            : 'inputs.mqttPreview.run',
                    )}
                </Button>
                {pending && (
                    <Button
                        type="button"
                        variant="outline"
                        onClick={() => {
                            controller.current?.abort()
                            setPending(false)
                        }}
                    >
                        {t('inputs.mqttPreview.cancel')}
                    </Button>
                )}
                <Button
                    type="button"
                    variant="outline"
                    disabled={pending || !result}
                    onClick={() => setResult(undefined)}
                >
                    {t('inputs.httpPreview.clear')}
                </Button>
            </div>
            <ErrorMessage error={error} />
            {result && (
                <>
                    <p role="status">
                        {result.data.code} ·{' '}
                        {Number(result.data.elapsedMs).toFixed(0)} ms
                    </p>
                    {result.data.code === 'timeout' && (
                        <p>{t('inputs.mqttPreview.timeout')}</p>
                    )}
                    {result.data.retained && (
                        <p className="font-semibold">
                            {t('inputs.mqttPreview.retained')}
                        </p>
                    )}
                    {result.data.duplicate && (
                        <p>{t('inputs.mqttPreview.duplicate')}</p>
                    )}
                    {result.data.topicOmitted ? (
                        <p>{t('inputs.mqttPreview.topicOmitted')}</p>
                    ) : (
                        <p className="break-all font-mono">
                            {result.data.topic}
                        </p>
                    )}
                    {result.data.qos != null && <p>QoS {result.data.qos}</p>}
                    {preview && (
                        <>
                            <p className="text-sm">
                                {t('inputs.mqttPreview.receivedAt')}:{' '}
                                {preview.receivedAt ?? '—'}
                            </p>
                            {(preview.redacted ||
                                preview.rawTruncated ||
                                preview.fieldsTruncated) && (
                                <p>{t('inputs.httpPreview.unusable')}</p>
                            )}
                            {preview.rawJson != null && (
                                <pre className="max-h-64 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2">
                                    {preview.rawJson}
                                </pre>
                            )}
                            <ul className="max-h-64 overflow-auto font-mono text-sm">
                                {preview.fields.map((field) => (
                                    <li className="break-all" key={field.key}>
                                        {field.key}: {field.text ?? '—'} (
                                        {field.type ?? '—'}, {field.quality}
                                        {field.reason
                                            ? `: ${field.reason}`
                                            : ''}
                                        )
                                        {field.unsafeJavaScriptInteger && (
                                            <span>
                                                {' '}
                                                ·{' '}
                                                {t('inputs.preview.precision')}
                                            </span>
                                        )}
                                    </li>
                                ))}
                            </ul>
                            {onSample && (
                                <Button
                                    type="button"
                                    variant="outline"
                                    disabled={
                                        blocked ||
                                        pending ||
                                        !unchanged ||
                                        !preview.canUseAsSample ||
                                        preview.rawJson == null
                                    }
                                    onClick={() => {
                                        if (
                                            unchanged &&
                                            preview.canUseAsSample &&
                                            preview.rawJson != null
                                        )
                                            onSample(preview.rawJson)
                                    }}
                                >
                                    {t('inputs.httpPreview.use')}
                                </Button>
                            )}
                        </>
                    )}
                </>
            )}
        </section>
    )
}
