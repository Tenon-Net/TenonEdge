// Layout composition adapted from shadcn-admin authenticated-layout.tsx at e16c87f.
// MIT, Copyright (c) 2024 Sat Naing. See web/THIRD_PARTY.md.
import { useEffect, type ReactNode } from 'react'
import { Outlet } from '@tanstack/react-router'
import { ShieldAlert } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { ConfigurationHeader } from '../../features/configuration/header'
import { useSystemInfo } from '../../features/diagnostics/queries'
import { Main } from './main'
import { AppSidebar } from './app-sidebar'
import { Header } from './header'
import { SidebarInset, SidebarProvider } from '../ui/sidebar'
import { SkipToMain } from '../skip-to-main'
import { ThemeSwitch } from '../theme-switch'

function GatewayName() {
    const { t } = useTranslation()
    const info = useSystemInfo()
    const name = info.data?.gatewayName
    useEffect(() => {
        document.title = name ? `${name} · ${t('product')}` : t('product')
        return () => {
            document.title = t('product')
        }
    }, [name, t])
    return (
        <p
            className="min-w-0 flex-1 truncate text-sm font-semibold"
            title={name || t('systemSettings.unnamed')}
        >
            {t('systemSettings.gateway')}：{name || t('systemSettings.unnamed')}
        </p>
    )
}

export function Workspace({ sessionNotice }: { sessionNotice?: ReactNode }) {
    const { t } = useTranslation()
    return (
        <SidebarProvider>
            <SkipToMain />
            <AppSidebar />
            <SidebarInset className="@container/content has-data-[layout=fixed]:h-svh peer-data-[variant=inset]:has-data-[layout=fixed]:h-[calc(100svh-(var(--spacing)*4))]">
                <Header fixed>
                    <GatewayName />
                    <ThemeSwitch />
                    <ConfigurationHeader />
                </Header>
                {sessionNotice}
                {location.protocol === 'http:' && (
                    <div
                        role="note"
                        className="flex items-start gap-2 border-b bg-muted/40 px-4 py-2 text-xs text-muted-foreground"
                    >
                        <ShieldAlert
                            aria-hidden="true"
                            className="mt-0.5 size-4 shrink-0"
                        />
                        <details className="min-w-0">
                            <summary className="cursor-pointer">
                                {t('httpWarning')}
                            </summary>
                            <p className="mt-2 max-w-3xl leading-relaxed">
                                {t('httpsInstructions')}
                            </p>
                        </details>
                    </div>
                )}
                <Main
                    id="content"
                    tabIndex={-1}
                    className="outline-none md:px-6"
                >
                    <Outlet />
                </Main>
            </SidebarInset>
        </SidebarProvider>
    )
}
