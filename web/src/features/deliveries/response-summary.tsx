import { useTranslation } from 'react-i18next'
import type { BodyPreview } from './api'

/** This is response body data, fetched only by the existing editor-only body request. */
export function ResponseSummary({
    summary,
}: {
    summary: BodyPreview['responseSummary']
}) {
    const { t } = useTranslation()
    return (
        <section
            aria-label={t('deliveries.response.title')}
            className="space-y-2 border-t pt-4"
        >
            <h3 className="font-medium">{t('deliveries.response.title')}</h3>
            <p className="text-sm text-muted-foreground">
                {t('deliveries.response.notice')}
            </p>
            {summary ? (
                <>
                    <p>
                        {t('deliveries.response.status', {
                            status: summary.statusCode,
                        })}
                    </p>
                    <p>
                        {t(`deliveries.response.states.${summary.state}`, {
                            defaultValue: t('deliveries.response.unknown'),
                        })}
                    </p>
                    {summary.state === 'captured' && (
                        <>
                            {summary.truncated && (
                                <p role="note">
                                    {t('deliveries.response.truncated')}
                                </p>
                            )}
                            <pre className="max-h-64 overflow-auto rounded border bg-background p-4 text-xs break-all whitespace-pre-wrap">
                                {summary.text}
                            </pre>
                        </>
                    )}
                </>
            ) : (
                <p>{t('deliveries.response.missing')}</p>
            )}
        </section>
    )
}
