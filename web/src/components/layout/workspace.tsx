import { useState } from 'react'
import { Link, Outlet } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Main } from './main'
import { Button } from '../ui/button'
import { useSession } from '../../lib/session'
import { LogoutButton } from '../../features/auth/logout-button'

export function Workspace() {
    const { t } = useTranslation()
    const session = useSession((state) => state.value)
    const [navigation, setNavigation] = useState(false)
    return (
        <div className="min-h-svh md:grid md:grid-cols-[15rem_1fr]">
            <aside className="border-r bg-sidebar p-5">
                <div className="flex items-center justify-between gap-3">
                    <Link
                        to="/"
                        className="flex items-center gap-3 font-semibold"
                    >
                        <img src="/icon-128.png" className="size-9" alt="" />
                        {t('product')}
                    </Link>
                    <Button
                        variant="outline"
                        className="md:hidden"
                        aria-expanded={navigation}
                        onClick={() => setNavigation(!navigation)}
                    >
                        {t(navigation ? 'hideNavigation' : 'showNavigation')}
                    </Button>
                </div>
                <nav
                    aria-label={t('navigation')}
                    className={`${navigation ? 'flex' : 'hidden'} mt-8 flex-col gap-2 md:flex`}
                >
                    <Link
                        to="/"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                        activeOptions={{ exact: true }}
                    >
                        {t('workspace')}
                    </Link>
                    <Link
                        to="/account"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('security')}
                    </Link>
                    <Link
                        to="/prototypes/points"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('points.title')}
                    </Link>
                    <Link
                        to="/prototypes/canvas"
                        className="rounded-md px-3 py-2 text-sm hover:bg-accent"
                    >
                        {t('canvas.title')}
                    </Link>
                    <Link
                        to="/connections"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('connections.title')}
                    </Link>
                    <Link
                        to="/devices"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('devices.title')}
                    </Link>
                </nav>
            </aside>
            <div className="min-w-0">
                <header className="flex flex-wrap items-center justify-between gap-4 border-b px-6 py-4">
                    <div>
                        <p className="text-sm">
                            {t('accountDetail', {
                                name:
                                    session?.displayName ||
                                    session?.accountName,
                            })}
                        </p>
                        <p className="text-xs text-muted-foreground">
                            {session && t(`roles.${session.role}`)}
                        </p>
                    </div>
                    <LogoutButton />
                </header>
                <Main>
                    <Outlet />
                </Main>
            </div>
        </div>
    )
}
