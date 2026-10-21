// Adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing). See web/THIRD_PARTY.md.
import { useTranslation } from 'react-i18next'

export function SkipToMain() {
    const { t } = useTranslation()
    return (
        <a
            className={`fixed inset-s-4 z-999 -translate-y-52 bg-primary px-4 py-2 text-sm font-medium whitespace-nowrap text-primary-foreground opacity-95 shadow-sm transition hover:bg-primary/90 focus:translate-y-3 focus:transform focus-visible:ring-1 focus-visible:ring-ring`}
            href="#content"
        >
            {t('skipToContent')}
        </a>
    )
}
