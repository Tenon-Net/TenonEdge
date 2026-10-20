// Adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing). See web/THIRD_PARTY.md.
import { useTranslation } from 'react-i18next'
import { ThemeSwitch } from '../../components/theme-switch'

export function AuthLayout({ children }: { children: React.ReactNode }) {
    const { t } = useTranslation()
    return (
        <main className="container grid min-h-svh max-w-none grid-cols-1 items-center justify-items-center px-4">
            <div className="absolute end-4 top-4">
                <ThemeSwitch />
            </div>
            <div className="mx-auto flex w-full max-w-md flex-col justify-center space-y-2 py-12 sm:p-8">
                <div className="mb-6 flex items-center justify-center gap-3">
                    <img src="/icon-128.png" className="size-10" alt="" />
                    <div>
                        <h1 className="text-xl font-medium">{t('product')}</h1>
                        <p className="text-xs text-muted-foreground">
                            {t('subtitle')}
                        </p>
                    </div>
                </div>
                {children}
            </div>
        </main>
    )
}
