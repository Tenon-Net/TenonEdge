import { useEffect, useRef, useState } from 'react'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { createRouteApi } from './api'

const routes = createRouteApi(api)
type Review = Awaited<ReturnType<typeof routes.prepareTestSend>>
type Result = Awaited<ReturnType<typeof routes.confirmTestSend>>
type State =
    | { phase: 'idle' | 'preparing' | 'sending' | 'unknown' }
    | { phase: 'review'; review: Review }
    | { phase: 'done'; result: Result }
    | { phase: 'error'; error: unknown }

/** Mounted only for a current, authorized preview. The parent keys by artifact id;
 * any graph/version/source/sample change unmounts it and aborts its pending request. */
export function RouteTestSend({ id }: { id: string }) {
    const { t } = useTranslation()
    const [state, setState] = useState<State>({ phase: 'idle' })
    const [acknowledged, setAcknowledged] = useState(false)
    const request = useRef<AbortController | null>(null)
    const consumed = useRef(false)
    useEffect(() => () => request.current?.abort(), [])

    async function prepare() {
        if (consumed.current || request.current) return
        const controller = new AbortController()
        request.current = controller
        setAcknowledged(false)
        setState({ phase: 'preparing' })
        try {
            const review = await routes.prepareTestSend(id, controller.signal)
            if (!controller.signal.aborted)
                setState({ phase: 'review', review })
        } catch (error) {
            if (!controller.signal.aborted) setState({ phase: 'error', error })
        } finally {
            if (request.current === controller) request.current = null
        }
    }
    async function confirm() {
        if (
            state.phase !== 'review' ||
            !acknowledged ||
            consumed.current ||
            request.current
        )
            return
        const review = state.review
        // Synchronous consumption, before fetch or a second click. Never persist/replay confirmation ids.
        consumed.current = true
        const controller = new AbortController()
        request.current = controller
        setAcknowledged(false)
        setState({ phase: 'sending' })
        try {
            const result = await routes.confirmTestSend(
                id,
                review.confirmationId,
                controller.signal,
            )
            if (!controller.signal.aborted) setState({ phase: 'done', result })
        } catch {
            // An HTTP failure, lost reply and a canceled fetch cannot prove the target received nothing.
            if (!controller.signal.aborted) setState({ phase: 'unknown' })
        } finally {
            if (request.current === controller) request.current = null
        }
    }
    function cancel() {
        request.current?.abort()
        request.current = null
        setAcknowledged(false)
        setState({ phase: consumed.current ? 'unknown' : 'idle' })
    }
    const review = state.phase === 'review' ? state.review : null
    return (
        <section
            className="space-y-2 rounded border p-3"
            aria-label={t('testSend.title')}
        >
            <h4 className="font-medium">{t('testSend.title')}</h4>
            <p className="text-sm">{t('testSend.notice')}</p>
            {(state.phase === 'idle' || state.phase === 'error') && (
                <Button
                    type="button"
                    variant="outline"
                    onClick={() => void prepare()}
                >
                    {t('testSend.prepare')}
                </Button>
            )}
            {state.phase === 'error' && (
                <>
                    <p role="alert">{t('testSend.failedPrepare')}</p>
                    <ErrorMessage error={state.error} />
                </>
            )}
            {state.phase === 'preparing' && (
                <p role="status">{t('testSend.preparing')}</p>
            )}
            {review && (
                <div className="space-y-2 break-all">
                    <p>
                        {t('testSend.recipient', {
                            id: review.connectionId,
                            recipient: review.recipient,
                        })}
                    </p>
                    <p>
                        {t('testSend.account', {
                            authentication: review.authentication,
                            username:
                                review.username ?? t('testSend.noUsername'),
                        })}
                    </p>
                    <p>
                        {t('testSend.address', { address: review.address })}
                        {review.qos !== null && ` · QoS ${review.qos}`}
                    </p>
                    <p>{t('testSend.bytes', { bytes: review.bodyBytes })}</p>
                    <p className="font-mono text-xs">
                        {t('testSend.digest', { sha256: review.sha256 })}
                    </p>
                    <p className="text-xs">
                        {t('testSend.delivery', { id: review.deliveryId })}
                    </p>
                    <p className="text-xs">
                        {t('testSend.source', { id: review.sourceMessageId })}
                    </p>
                    <p>
                        {t('testSend.collected', { time: review.collectedAt })}
                    </p>
                    <p>{t('testSend.expires', { time: review.expiresAt })}</p>
                    {review.previewTruncated && (
                        <p role="alert">{t('testSend.truncated')}</p>
                    )}
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={acknowledged}
                            onChange={(event) =>
                                setAcknowledged(event.target.checked)
                            }
                        />
                        {t('testSend.acknowledge')}
                    </label>
                    <div className="flex flex-wrap gap-2">
                        <Button
                            type="button"
                            disabled={!acknowledged}
                            onClick={() => void confirm()}
                        >
                            {t('testSend.confirm')}
                        </Button>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={cancel}
                        >
                            {t('testSend.discard')}
                        </Button>
                    </div>
                </div>
            )}
            {state.phase === 'sending' && (
                <p role="status">{t('testSend.sending')}</p>
            )}
            {(state.phase === 'preparing' || state.phase === 'sending') && (
                <Button type="button" variant="outline" onClick={cancel}>
                    {t('testSend.cancel')}
                </Button>
            )}
            {state.phase === 'unknown' && (
                <p role="alert">{t('testSend.unknown')}</p>
            )}
            {state.phase === 'done' && <TestSendResult result={state.result} />}
        </section>
    )
}

function TestSendResult({ result }: { result: Result }) {
    const { t } = useTranslation()
    const http = result.http
    const elapsed = http?.elapsedMs ?? result.mqtt?.elapsedMs
    return (
        <section className="space-y-2" aria-label={t('testSend.result')}>
            <p role="status">
                {t('testSend.codes.' + result.code, {
                    defaultValue: t('testSend.unknown'),
                })}
            </p>
            <p className="text-sm">{t('testSend.consumed')}</p>
            {http?.statusCode != null && (
                <>
                    <p>{t('testSend.status', { status: http.statusCode })}</p>
                    <p>{t('testSend.httpNotice')}</p>
                    <p>
                        {t('testSend.bodies.' + http.bodyState, {
                            defaultValue: t('testSend.bodies.unavailable'),
                        })}
                    </p>
                    {http.bodyState === 'captured' && http.body != null && (
                        <pre
                            className="max-h-96 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2 text-sm"
                            aria-label={t('testSend.response')}
                        >
                            {http.body}
                        </pre>
                    )}
                </>
            )}
            {elapsed != null && (
                <p className="text-xs">
                    {t('testSend.elapsed', { ms: Math.round(Number(elapsed)) })}
                </p>
            )}
        </section>
    )
}
