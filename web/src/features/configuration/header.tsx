import { Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useDraftSummary } from './queries'

export function ConfigurationHeader() {
    const { t } = useTranslation()
    const query = useDraftSummary()
    return (
        <Link
            to="/configuration"
            className="rounded border px-3 py-2 text-sm hover:bg-accent"
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
