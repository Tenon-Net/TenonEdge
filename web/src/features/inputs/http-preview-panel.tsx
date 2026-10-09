import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { components } from '../../api/schema'
import { api } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'

type Source = components['schemas']['HttpSourceDraft']
type Result = components['schemas']['HttpDevicePreviewResult']
export function HttpPreviewPanel({
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
                '/api/edge-admin/v1/devices/{deviceId}/preview/http',
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
    return (
        <section
            className="space-y-3 rounded border p-3"
            aria-label={t(
                source
                    ? 'inputs.httpPreview.sourceTitle'
                    : 'inputs.httpPreview.deviceTitle',
            )}
        >
            <h2 className="font-semibold">
                {t(
                    source
                        ? 'inputs.httpPreview.sourceTitle'
                        : 'inputs.httpPreview.deviceTitle',
                )}
            </h2>
            <p className="text-sm text-muted-foreground">
                {t('inputs.httpPreview.notice')}
            </p>
            <Button
                type="button"
                disabled={blocked || pending}
                onClick={() => void read()}
            >
                {t(
                    pending
                        ? 'inputs.preview.running'
                        : 'inputs.httpPreview.run',
                )}
            </Button>
            <Button
                type="button"
                variant="outline"
                disabled={pending || !result}
                onClick={() => setResult(undefined)}
            >
                {t('inputs.httpPreview.clear')}
            </Button>
            <ErrorMessage error={error} />
            {result && (
                <>
                    <p role="status">
                        {t('inputs.httpPreview.completed', {
                            count: result.data.sources.length,
                            total: result.data.totalSources,
                        })}{' '}
                        · {Number(result.data.elapsedMs).toFixed(0)} ms
                    </p>
                    {(result.data.truncated ||
                        result.data.deadlineExceeded) && (
                        <p>{t('inputs.httpPreview.partial')}</p>
                    )}
                    {result.data.sources.map(({ key, preview }) => (
                        <div className="space-y-2" key={key}>
                            <h3 className="font-semibold">
                                {key} · {preview.code} · HTTP{' '}
                                {preview.statusCode ?? '—'}
                            </h3>
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
                        </div>
                    ))}
                </>
            )}
        </section>
    )
}
