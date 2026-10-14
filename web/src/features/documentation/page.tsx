import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { readDocumentation } from './model'

export function DocumentationPage() {
    const { t } = useTranslation()
    const [search, setSearch] = useState('')
    const [selected, select] = useState<string | null>(null)
    const [complete, showComplete] = useState(false)
    const query = useQuery({
        queryKey: ['api-documentation'],
        queryFn: async ({ signal }) =>
            readDocumentation(await api.readOpenApiDocument(signal)),
    })
    const document = query.error ? undefined : query.data
    const term = search.trim().toLowerCase()
    const operations =
        document?.operations.filter((op) =>
            [op.key, op.summary, op.operationId].some((s) =>
                s.toLowerCase().includes(term),
            ),
        ) ?? []
    return (
        <div className="space-y-5">
            <h1 className="text-2xl font-semibold">
                {t('documentation.title')}
            </h1>
            <p className="max-w-4xl text-sm text-muted-foreground">
                {t('documentation.notice')}
            </p>
            <div className="flex flex-wrap items-center gap-4">
                <Button
                    disabled={query.isFetching}
                    onClick={() => void query.refetch()}
                >
                    {t('documentation.refresh')}
                </Button>
                <a
                    className="text-sm underline"
                    href="/api/edge-admin/v1/openapi/v1.json"
                    target="_blank"
                    rel="noopener noreferrer"
                >
                    {t('documentation.raw')}
                </a>
            </div>
            {query.isFetching && (
                <p role="status">{t('documentation.loading')}</p>
            )}
            <ErrorMessage error={query.error} />
            {query.error && <p role="alert">{t('documentation.failure')}</p>}
            {document && (
                <>
                    <p>
                        {document.title} · {document.version} · OpenAPI{' '}
                        {document.openapi}
                    </p>
                    <label className="block text-sm">
                        {t('documentation.search')}
                        <input
                            className="mt-1 block w-full max-w-xl rounded border bg-background p-2"
                            value={search}
                            onChange={(e) => setSearch(e.target.value)}
                        />
                    </label>
                    <p role="status">
                        {t('documentation.count', {
                            shown: operations.length,
                            total: document.operations.length,
                        })}
                    </p>
                    {!operations.length && <p>{t('documentation.empty')}</p>}
                    <div className="space-y-2">
                        {operations.map((op) => (
                            <details
                                key={op.key}
                                open={selected === op.key}
                                onToggle={(e) => {
                                    if (e.currentTarget.open) select(op.key)
                                    else
                                        select((current) =>
                                            current === op.key ? null : current,
                                        )
                                }}
                                className="rounded border p-3"
                            >
                                <summary className="cursor-pointer break-all text-sm">
                                    <strong>{op.method}</strong>{' '}
                                    <code>{op.path}</code> {op.summary}
                                </summary>
                                {selected === op.key && (
                                    <pre
                                        className="mt-3 max-h-[32rem] overflow-auto whitespace-pre-wrap break-all text-xs"
                                        data-testid="operation-document"
                                    >
                                        {op.detail}
                                    </pre>
                                )}
                            </details>
                        ))}
                    </div>
                    <details
                        open={complete}
                        onToggle={(e) => showComplete(e.currentTarget.open)}
                        className="rounded border p-3"
                    >
                        <summary className="cursor-pointer">
                            {t('documentation.complete')}
                        </summary>
                        {complete && (
                            <pre
                                data-testid="complete-document"
                                className="mt-3 max-h-[36rem] overflow-auto whitespace-pre-wrap break-all text-xs"
                            >
                                {document.complete}
                            </pre>
                        )}
                    </details>
                </>
            )}
        </div>
    )
}
