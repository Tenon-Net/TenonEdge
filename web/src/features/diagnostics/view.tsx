import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'

export function dateText(value: string | null | undefined) {
    return value
        ? new Date(value).toLocaleString('zh-CN', { hour12: false })
        : '—'
}
export function DiagnosticPanel({
    title,
    query,
    children,
}: {
    title: string
    query: {
        isPending: boolean
        isFetching: boolean
        error: unknown
        dataUpdatedAt: number
        refetch: () => unknown
    }
    children?: ReactNode
}) {
    const { t } = useTranslation()
    return (
        <section
            aria-label={title}
            className="space-y-4 rounded-lg border bg-card p-5"
        >
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="text-lg font-semibold">{title}</h2>
                <Button
                    variant="outline"
                    disabled={query.isFetching}
                    onClick={() => void query.refetch()}
                >
                    {t('diagnostics.refresh')}
                </Button>
            </div>
            <p className="text-xs text-muted-foreground">
                {query.dataUpdatedAt
                    ? t('diagnostics.lastRefresh', {
                          time: dateText(
                              new Date(query.dataUpdatedAt).toISOString(),
                          ),
                      })
                    : t('neverRefreshed')}
            </p>
            <ErrorMessage error={query.error} />
            {query.isPending && <p role="status">{t('loading')}</p>}
            {children}
        </section>
    )
}
export function Fact({ label, value }: { label: string; value: ReactNode }) {
    return (
        <div className="min-w-0 space-y-1">
            <dt className="text-sm text-muted-foreground">{label}</dt>
            <dd className="break-words font-medium">{value ?? '—'}</dd>
        </div>
    )
}
