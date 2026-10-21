// Layout composition adapted from shadcn-admin authenticated-layout.tsx at e16c87f.
// MIT, Copyright (c) 2024 Sat Naing. See web/THIRD_PARTY.md.
import { useEffect, type ReactNode } from 'react'
import { Outlet } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { ConfigurationHeader } from '../../features/configuration/header'
import { useSystemInfo } from '../../features/diagnostics/queries'
import { Main } from './main'
import { AppSidebar } from './app-sidebar'
import { Header } from './header'
import { SidebarInset, SidebarProvider } from '../ui/sidebar'
import { SkipToMain } from '../skip-to-main'
import { DeferredThemeSwitch as ThemeSwitch } from '../deferred-theme-switch'

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
            title={name || t('shell.unnamed')}
        >
            {t('shell.gateway')}：{name || t('shell.unnamed')}
        </p>
    )
}

export function Workspace({ sessionNotice }: { sessionNotice?: ReactNode }) {
    return (
        <SidebarProvider className="has-data-[variant=inset]:bg-muted dark:has-data-[variant=inset]:bg-background">
            <SkipToMain />
            <AppSidebar />
            <SidebarInset className="@container/content bg-card has-data-[layout=fixed]:h-svh md:border md:peer-data-[variant=inset]:m-3 md:peer-data-[variant=inset]:ms-0 peer-data-[variant=inset]:has-data-[layout=fixed]:h-[calc(100svh-(var(--spacing)*6))]">
                <Header fixed className="bg-card md:rounded-t-xl">
                    <GatewayName />
                    <ThemeSwitch />
                    <ConfigurationHeader />
                </Header>
                {sessionNotice}
                <Main
                    fluid
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
