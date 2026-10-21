import { ConfigurationHeader } from '../../features/configuration/header'
import { useEffect, useState } from 'react'
import { useSystemInfo } from '../../features/diagnostics/queries'
import { Link, Outlet } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Main } from './main'
import { Button } from '../ui/button'
import { useSession } from '../../lib/session'
import { LogoutButton } from '../../features/auth/logout-button'

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
        <p className="break-words text-sm font-medium">
            {t('shell.gateway')}：{name || t('shell.unnamed')}
        </p>
    )
}

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
                        to="/diagnostics"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.diagnostics')}
                    </Link>
                    <Link
                        to="/deliveries"
                        search={{}}
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.deliveries')}
                    </Link>
                    <Link
                        to="/system"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.system')}
                    </Link>
                    {session?.role === 'administrator' && (
                        <Link
                            to="/audit"
                            className="rounded-md p-2 hover:bg-accent"
                            activeProps={{ className: 'bg-accent font-medium' }}
                        >
                            {t('shell.audit')}
                        </Link>
                    )}
                    {session?.role === 'administrator' && (
                        <Link
                            to="/tokens"
                            className="rounded-md p-2 hover:bg-accent"
                            activeProps={{ className: 'bg-accent font-medium' }}
                        >
                            {t('shell.tokens')}
                        </Link>
                    )}
                    {session?.role === 'administrator' && (
                        <Link
                            to="/users"
                            className="rounded-md p-2 hover:bg-accent"
                            activeProps={{ className: 'bg-accent font-medium' }}
                        >
                            {t('shell.users')}
                        </Link>
                    )}
                    <Link
                        to="/api-docs"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.documentation')}
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
                        {t('shell.points')}
                    </Link>
                    <Link
                        to="/prototypes/canvas"
                        className="rounded-md px-3 py-2 text-sm hover:bg-accent"
                    >
                        {t('shell.canvas')}
                    </Link>
                    <Link
                        to="/routes"
                        className="rounded-md px-3 py-2 text-sm hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.routes')}
                    </Link>
                    <Link
                        to="/connections"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.connections')}
                    </Link>
                    <Link
                        to="/devices"
                        className="rounded-md p-2 hover:bg-accent"
                        activeProps={{ className: 'bg-accent font-medium' }}
                    >
                        {t('shell.devices')}
                    </Link>
                </nav>
            </aside>
            <div className="min-w-0">
                {location.protocol === 'http:' && (
                    <div
                        role="note"
                        className="border-b border-amber-300 bg-amber-50 px-6 py-3 text-sm text-amber-950"
                    >
                        <p>{t('httpWarning')}</p>
                        <p>{t('httpsInstructions')}</p>
                    </div>
                )}
                <header className="flex flex-wrap items-center justify-between gap-4 border-b px-6 py-4">
                    <GatewayName />
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
                    <p className="text-xs text-muted-foreground">
                        {session?.previousLoginAt
                            ? t('previousLogin', {
                                  time: new Date(
                                      session.previousLoginAt,
                                  ).toLocaleString(),
                                  ip:
                                      session.previousLoginIp ??
                                      t('shell.noRecord'),
                              })
                            : t('noPreviousLogin')}
                    </p>
                    <ConfigurationHeader />
                    <LogoutButton />
                </header>
                <Main>
                    <Outlet />
                </Main>
            </div>
        </div>
    )
}
