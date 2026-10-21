import { Suspense, type ReactNode } from 'react'
import { CatchBoundary } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Button } from './ui/button'

export function PageLoading() {
    const { t } = useTranslation()
    return (
        <p role="status" className="p-4">
            {t('pageLoading')}
        </p>
    )
}

export function PageLoadError() {
    const { t } = useTranslation()
    return (
        <div role="alert" className="space-y-3 p-4">
            <p>{t('pageUnavailable')}</p>
            <Button onClick={() => window.location.reload()}>
                {t('reloadPage')}
            </Button>
        </div>
    )
}

// A failed import is reported without automatically refreshing the document,
// clearing edits or replaying the write that led to the next screen.
export function PageBoundary({ children }: { children: ReactNode }) {
    return (
        <CatchBoundary getResetKey={() => 0} errorComponent={PageLoadError}>
            <Suspense fallback={<PageLoading />}>{children}</Suspense>
        </CatchBoundary>
    )
}
