// Adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing). See web/THIRD_PARTY.md.
import { Link } from '@tanstack/react-router'
import { UserRound } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useSession } from '../../lib/session'
import { LogoutButton } from '../../features/auth/logout-button'
import {
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
    useSidebar,
} from '../ui/sidebar'

export function NavUser() {
    const { t } = useTranslation()
    const session = useSession((state) => state.value)
    const { setOpenMobile } = useSidebar()
    return (
        <>
            <SidebarMenu>
                <SidebarMenuItem>
                    <SidebarMenuButton
                        asChild
                        size="lg"
                        tooltip={t('security')}
                    >
                        <Link
                            to="/account"
                            aria-label={t('security')}
                            onClick={() => setOpenMobile(false)}
                        >
                            <UserRound
                                aria-hidden="true"
                                className="rounded-md"
                            />
                            <div className="grid min-w-0 flex-1 text-start text-sm leading-tight group-data-[collapsible=icon]:hidden">
                                <span className="truncate font-semibold">
                                    {session?.displayName ||
                                        session?.accountName}
                                </span>
                                <span className="truncate text-xs text-muted-foreground">
                                    {session && t(`roles.${session.role}`)}
                                </span>
                            </div>
                        </Link>
                    </SidebarMenuButton>
                </SidebarMenuItem>
            </SidebarMenu>
            <p className="px-2 text-xs leading-relaxed text-muted-foreground group-data-[collapsible=icon]:hidden">
                {session?.previousLoginAt
                    ? t('previousLogin', {
                          time: new Date(
                              session.previousLoginAt,
                          ).toLocaleString('zh-CN'),
                          ip: session.previousLoginIp ?? t('shell.noRecord'),
                      })
                    : t('noPreviousLogin')}
            </p>
            <LogoutButton compact />
        </>
    )
}
