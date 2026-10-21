// Theme trigger adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing).
// See web/THIRD_PARTY.md. The on-demand loading and local failure boundary are product code.
import { useTranslation } from 'react-i18next'
import { lazy, Suspense, useState } from 'react'
import { CatchBoundary } from '@tanstack/react-router'
import { Moon, Sun } from 'lucide-react'
import { Button } from './ui/button'

const ThemeSwitch = lazy(() =>
    import('./theme-switch').then(({ ThemeSwitch }) => ({
        default: ThemeSwitch,
    })),
)

function ThemeUnavailable() {
    const { t } = useTranslation()
    return (
        <p role="alert" className="max-w-48 text-xs">
            {t('theme.unavailable')}
        </p>
    )
}

// Preserve the product control, but fetch its menu only when requested. A failed
// optional menu must not unmount the login form or discard entered material.
export function DeferredThemeSwitch() {
    const { t } = useTranslation()
    const [requested, setRequested] = useState(false)
    const trigger = (
        <Button
            variant="ghost"
            size="icon"
            className="scale-95 rounded-full"
            aria-haspopup="menu"
            aria-expanded={false}
            onClick={() => setRequested(true)}
            onKeyDown={(event) => {
                if (event.key === 'ArrowDown') {
                    event.preventDefault()
                    setRequested(true)
                }
            }}
        >
            <Sun className="size-[1.2rem] scale-100 rotate-0 transition-all dark:scale-0 dark:-rotate-90" />
            <Moon className="absolute size-[1.2rem] scale-0 rotate-90 transition-all dark:scale-100 dark:rotate-0" />
            <span className="sr-only">{t('theme.title')}</span>
        </Button>
    )
    return (
        <CatchBoundary getResetKey={() => 0} errorComponent={ThemeUnavailable}>
            {requested ? (
                <Suspense fallback={trigger}>
                    <ThemeSwitch initialOpen />
                </Suspense>
            ) : (
                trigger
            )}
        </CatchBoundary>
    )
}
