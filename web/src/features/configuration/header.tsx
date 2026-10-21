import { Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useDraftSummary } from './queries'

export function ConfigurationHeader() {
    const { t } = useTranslation()
    const query = useDraftSummary()
    return (
        <Link
            to="/configuration"
            className="inline-flex min-h-8 items-center rounded-md border bg-background px-3 py-1.5 text-xs font-medium shadow-xs hover:bg-accent max-sm:basis-full"
        >
            {query.isError || query.data?.changeCount == null
                ? t('shell.unknownSummary')
                : t('shell.summary', {
                      count: Number(query.data.changeCount),
                  })}
            {' · '}
            {t('shell.configuration')}
        </Link>
    )
}
