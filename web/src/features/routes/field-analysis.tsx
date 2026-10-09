import { useEffect, useId, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { GraphDocument } from '../canvas/model'
import { createRouteApi, newRoute } from './api'

const routes = createRouteApi(api)
type Page = Awaited<ReturnType<typeof routes.fields>>
type Result = {
    document: GraphDocument
    nodeId: string
    version: string
    page?: Page
    error?: unknown
    pending: boolean
    signal: AbortSignal
}

/** Retain only one bounded page; never put whole graphs in the query cache. */
export function useFieldAnalysis(
    document: GraphDocument,
    nodeId: string | undefined,
    version: string | undefined,
) {
    const listId = useId()
    const controller = useRef<AbortController | null>(null)
    const [result, setResult] = useState<Result>()
    useEffect(
        () => () => controller.current?.abort(),
        [document, nodeId, version],
    )
    // Identity-gating is synchronous, including the render before effect cleanup.
    const current =
        result?.document === document &&
        !result.signal.aborted &&
        result.nodeId === nodeId &&
        result.version === version
            ? result
            : undefined
    async function analyze(search: string, afterKey?: string) {
        if (!nodeId || !version) return
        controller.current?.abort()
        const request = new AbortController()
        controller.current = request
        const captured = { document, nodeId, version, signal: request.signal }
        setResult({ ...captured, pending: true })
        try {
            const page = await routes.fields(
                { ...newRoute(version), document },
                nodeId,
                {
                    search,
                    afterKey,
                    signal: request.signal,
                },
            )
            if (!request.signal.aborted)
                setResult({ ...captured, page, pending: false })
        } catch (error) {
            if (!request.signal.aborted)
                setResult({ ...captured, error, pending: false })
        }
    }
    return { listId, current, analyze }
}

export function FieldAnalysis({
    analysis,
    disabled,
}: {
    analysis: ReturnType<typeof useFieldAnalysis>
    disabled: boolean
}) {
    const { t } = useTranslation()
    const [search, setSearch] = useState('')
    const [appliedSearch, setAppliedSearch] = useState('')
    const { current, listId } = analysis
    const page = current?.page
    return (
        <section className="space-y-2" aria-label={t('routes.fields.title')}>
            <h3 className="font-medium">{t('routes.fields.title')}</h3>
            <p className="text-xs">{t('routes.fields.notice')}</p>
            <label>
                {t('routes.fields.search')}
                <input
                    className="w-full rounded border p-1"
                    value={search}
                    maxLength={64}
                    onChange={(event) => setSearch(event.target.value)}
                />
            </label>
            <Button
                type="button"
                variant="outline"
                disabled={disabled || current?.pending}
                onClick={() => {
                    setAppliedSearch(search)
                    void analysis.analyze(search)
                }}
            >
                {t('routes.fields.analyze')}
            </Button>
            {current?.error instanceof ApiError &&
            current.error.status === 409 ? (
                <p role="alert">{t('routes.conflict')}</p>
            ) : (
                <ErrorMessage error={current?.error} />
            )}
            {current?.pending && (
                <p role="status">{t('routes.fields.pending')}</p>
            )}
            {!current && (
                <p className="text-xs">{t('routes.fields.refresh')}</p>
            )}
            {page && (
                <>
                    <p role="status">
                        {page.analyzed
                            ? t('routes.fields.summary', {
                                  count: page.commonCount,
                                  upstream: page.upstreamCount,
                              })
                            : t('routes.fields.invalid')}
                    </p>
                    {page.unitsDiffer && (
                        <p>{t('routes.fields.unitsDiffer')}</p>
                    )}
                    {!!page.excludedFields.length && (
                        <p>
                            {t('routes.fields.excluded')}:{' '}
                            {page.excludedFields.join('、')}
                        </p>
                    )}
                    {page.differencesTruncated && (
                        <p>{t('routes.fields.truncated')}</p>
                    )}
                    <ul className="max-h-48 overflow-auto text-xs">
                        {page.issues.map((issue, index) => (
                            <li key={index}>
                                {issue.nodeId ?? '—'}: {issue.code}
                            </li>
                        ))}
                        {page.analyzed &&
                            page.fields.map((field) => (
                                <li key={field.key}>
                                    {field.key} · {field.type}{' '}
                                    {field.unit ?? ''}
                                </li>
                            ))}
                    </ul>
                    <Button
                        type="button"
                        variant="outline"
                        disabled={disabled || !page.nextAfterKey}
                        onClick={() =>
                            void analysis.analyze(
                                appliedSearch,
                                page.nextAfterKey ?? undefined,
                            )
                        }
                    >
                        {t('routes.catalogNext')}
                    </Button>
                </>
            )}
            <datalist id={listId}>
                {page?.analyzed &&
                    page.fields.map((field) => (
                        <option key={field.key} value={field.key}>
                            {field.type} {field.unit ?? ''}
                        </option>
                    ))}
            </datalist>
        </section>
    )
}
