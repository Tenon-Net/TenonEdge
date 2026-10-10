import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { GraphDocument } from '../canvas/model'
import { createRouteApi, newRoute } from './api'

const routes = createRouteApi(api)
type Listing = Awaited<ReturnType<typeof routes.recentMessages>>
export type RecentMessage = Listing['records'][number]
type State = {
    document: GraphDocument
    version: string
    sourceId: number
    signal: AbortSignal
    pending: boolean
    choice: string
    data?: Listing
    error?: unknown
}
export function RecentSamples({
    document,
    version,
    sourceId,
    disabled,
    busy,
    onPreview,
    onInvalidate,
}: {
    document: GraphDocument
    version: string
    sourceId: number
    disabled: boolean
    busy: boolean
    onPreview: (record: RecentMessage) => void
    onInvalidate: () => void
}) {
    const { t } = useTranslation()
    const [state, setState] = useState<State>()
    const request = useRef<AbortController | null>(null)
    useEffect(
        () => () => request.current?.abort(),
        [document, version, sourceId, disabled],
    )
    const current =
        !disabled &&
        state?.document === document &&
        state.version === version &&
        state.sourceId === sourceId &&
        !state.signal.aborted
            ? state
            : undefined
    const selected = current?.data?.records.find(
        (item) => item.sequence === current.choice,
    )
    async function load() {
        onInvalidate()
        request.current?.abort()
        const controller = new AbortController()
        request.current = controller
        const captured = {
            document,
            version,
            sourceId,
            signal: controller.signal,
            choice: '',
        }
        setState({ ...captured, pending: true })
        try {
            const data = await routes.recentMessages(
                { ...newRoute(version), document },
                sourceId,
                controller.signal,
            )
            if (!controller.signal.aborted)
                setState({ ...captured, pending: false, data })
        } catch (error) {
            if (!controller.signal.aborted)
                setState({ ...captured, pending: false, error })
        }
    }
    return (
        <div
            className="space-y-2 rounded border p-3"
            aria-label={t('routes.preview.recentTitle')}
        >
            <p className="text-sm">{t('routes.preview.recentNotice')}</p>
            <Button
                type="button"
                variant="outline"
                disabled={disabled || busy || current?.pending}
                onClick={() => void load()}
            >
                {t('routes.preview.recentLoad')}
            </Button>
            {current?.pending && (
                <>
                    <p role="status">{t('routes.preview.recentLoading')}</p>
                    <Button
                        type="button"
                        variant="outline"
                        onClick={() => {
                            request.current?.abort()
                            setState(undefined)
                        }}
                    >
                        {t('routes.preview.cancel')}
                    </Button>
                </>
            )}
            <ErrorMessage error={current?.error} />
            {current?.data && (
                <>
                    {!current.data.running && (
                        <p role="status">{t('routes.preview.recentStopped')}</p>
                    )}
                    {!current.data.records.length && (
                        <p>{t('routes.preview.recentEmpty')}</p>
                    )}
                    <label className="block">
                        {t('routes.preview.recentSelect')}
                        <select
                            aria-label={t('routes.preview.recentSelect')}
                            className="block w-full rounded border bg-background p-2"
                            value={current.choice}
                            disabled={disabled || busy}
                            onChange={(event) => {
                                onInvalidate()
                                setState({
                                    ...current,
                                    choice: event.target.value,
                                })
                            }}
                        >
                            <option value="">
                                {t('routes.preview.recentChoose')}
                            </option>
                            {current.data.records.map((record) => (
                                <option
                                    key={record.sequence}
                                    value={record.sequence}
                                >
                                    #{record.sequence} · {record.capturedAt} ·{' '}
                                    {t('routes.preview.recentRevision', {
                                        revision: record.revision,
                                    })}
                                    {' · '}
                                    {record.storedBytes}/{record.originalBytes}{' '}
                                    B
                                    {record.privacyBlocked
                                        ? ' · ' +
                                          t(
                                              'routes.preview.recentPrivacyBlocked',
                                          )
                                        : ''}
                                    {record.truncated
                                        ? ' · ' +
                                          t('routes.preview.recentTruncated')
                                        : ''}
                                </option>
                            ))}
                        </select>
                    </label>
                    {selected && !selected.executable && (
                        <p role="alert">
                            {t('routes.preview.recentUnavailable')}
                        </p>
                    )}
                    <Button
                        type="button"
                        disabled={disabled || busy || !selected?.executable}
                        onClick={() => {
                            if (selected?.executable) onPreview(selected)
                        }}
                    >
                        {t('routes.preview.recentRun')}
                    </Button>
                </>
            )}
        </div>
    )
}
