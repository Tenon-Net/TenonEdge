// Layout composition adapted from shadcn-admin authenticated-layout.tsx at e16c87f.
// MIT, Copyright (c) 2024 Sat Naing. See web/THIRD_PARTY.md.
import { useEffect, type ReactNode } from 'react'
import { Outlet, useRouterState } from '@tanstack/react-router'
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
    const isOverview = useRouterState({
        select: (state) => state.location.pathname === '/',
    })
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
                <Main
                    fluid={isOverview}
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
