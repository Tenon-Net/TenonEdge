// Adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing). See web/THIRD_PARTY.md.
import { Link } from '@tanstack/react-router'
import {
    Activity,
    BookOpen,
    Cable,
    ClipboardList,
    KeyRound,
    LayoutDashboard,
    Network,
    Route,
    Send,
    Settings,
    Users,
} from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useSession } from '../../lib/session'
import {
    Sidebar,
    SidebarContent,
    SidebarFooter,
    SidebarHeader,
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
    SidebarRail,
    SidebarSeparator,
    useSidebar,
} from '../ui/sidebar'
import { NavGroup, type NavItem } from './nav-group'
import { NavUser } from './nav-user'

export function AppSidebar() {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const { setOpenMobile } = useSidebar()
    const management: NavItem[] = [
        { title: t('systemSettings.title'), url: '/system', icon: Settings },
    ]
    if (role === 'administrator')
        management.push(
            { title: t('users.title'), url: '/users', icon: Users },
            { title: t('tokens.title'), url: '/tokens', icon: KeyRound },
            { title: t('audit.title'), url: '/audit', icon: ClipboardList },
        )
    management.push({
        title: t('documentation.title'),
        url: '/api-docs',
        icon: BookOpen,
    })
    return (
        <Sidebar collapsible="icon" variant="inset">
            <SidebarHeader>
                <SidebarMenu>
                    <SidebarMenuItem>
                        <SidebarMenuButton
                            asChild
                            size="lg"
                            className="hover:bg-transparent"
                        >
                            <Link
                                to="/"
                                onClick={() => setOpenMobile(false)}
                                aria-label={t('product')}
                            >
                                <img
                                    src="/icon-128.png"
                                    className="size-8 shrink-0"
                                    alt=""
                                />
                                <div className="grid min-w-0 flex-1 text-start leading-tight group-data-[collapsible=icon]:hidden">
                                    <span className="truncate font-semibold">
                                        {t('product')}
                                    </span>
                                    <span className="truncate text-xs text-muted-foreground">
                                        {t('subtitle')}
                                    </span>
                                </div>
                            </Link>
                        </SidebarMenuButton>
                    </SidebarMenuItem>
                </SidebarMenu>
            </SidebarHeader>
            <SidebarContent>
                <nav aria-label={t('navigation')}>
                    <NavGroup
                        title={t('navigationGroups.configuration')}
                        items={[
                            {
                                title: t('workspace'),
                                url: '/',
                                icon: LayoutDashboard,
                            },
                            {
                                title: t('connections.title'),
                                url: '/connections',
                                icon: Cable,
                            },
                            {
                                title: t('devices.title'),
                                url: '/devices',
                                icon: Network,
                            },
                            {
                                title: t('routes.title'),
                                url: '/routes',
                                icon: Route,
                            },
                        ]}
                    />
                    <NavGroup
                        title={t('navigationGroups.operations')}
                        items={[
                            {
                                title: t('diagnostics.title'),
                                url: '/diagnostics',
                                icon: Activity,
                            },
                            {
                                title: t('deliveries.title'),
                                url: '/deliveries',
                                icon: Send,
                            },
                        ]}
                    />
                    <NavGroup
                        title={t('navigationGroups.management')}
                        items={management}
                    />
                </nav>
            </SidebarContent>
            <SidebarSeparator />
            <SidebarFooter>
                <NavUser />
            </SidebarFooter>
            <SidebarRail />
        </Sidebar>
    )
}
