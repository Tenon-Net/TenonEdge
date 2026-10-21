// Adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing). See web/THIRD_PARTY.md.
import { Link, useLocation } from '@tanstack/react-router'
import type { LucideIcon } from 'lucide-react'
import {
    SidebarGroup,
    SidebarGroupLabel,
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
    useSidebar,
} from '../ui/sidebar'

export type NavItem = {
    title: string
    url:
        | '/'
        | '/connections'
        | '/devices'
        | '/routes'
        | '/diagnostics'
        | '/deliveries'
        | '/system'
        | '/users'
        | '/tokens'
        | '/audit'
        | '/api-docs'
    icon: LucideIcon
}

export function NavGroup({
    title,
    items,
}: {
    title: string
    items: NavItem[]
}) {
    const pathname = useLocation({ select: (location) => location.pathname })
    const { setOpenMobile } = useSidebar()
    return (
        <SidebarGroup>
            <SidebarGroupLabel>{title}</SidebarGroupLabel>
            <SidebarMenu>
                {items.map((item) => (
                    <SidebarMenuItem key={item.url}>
                        <SidebarMenuButton
                            asChild
                            tooltip={item.title}
                            isActive={
                                pathname === item.url ||
                                (item.url !== '/' &&
                                    pathname.startsWith(`${item.url}/`))
                            }
                        >
                            <Link
                                to={item.url}
                                search={{}}
                                activeOptions={{ exact: item.url === '/' }}
                                onClick={() => setOpenMobile(false)}
                            >
                                <item.icon aria-hidden="true" />
                                <span>{item.title}</span>
                            </Link>
                        </SidebarMenuButton>
                    </SidebarMenuItem>
                ))}
            </SidebarMenu>
        </SidebarGroup>
    )
}
