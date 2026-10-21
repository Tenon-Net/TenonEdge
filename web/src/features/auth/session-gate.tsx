import { LogoutButton } from './logout-button'
import { lazy, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, restoreSession, useSession } from '../../lib/session'
import { LazyAuthForm } from './lazy-auth-form'
import { ErrorMessage } from './error-message'
import { Button } from '../../components/ui/button'
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
} from '../../components/ui/card'
import { PageBoundary } from '../../components/page-boundary'

const Workspace = lazy(() =>
    import('../../components/layout/workspace').then(({ Workspace }) => ({
        default: Workspace,
    })),
)

export function SessionGate() {
    const { t } = useTranslation()
    const [existingLogin, setExistingLogin] = useState(false)
    const session = useSession((state) => state.value)
    const sessionQuery = useQuery({
        queryKey: ['session'],
        queryFn: ({ signal }) => restoreSession(signal),
        enabled: session !== null,
        refetchInterval: session ? 60_000 : false,
    })
    const setupQuery = useQuery({
        queryKey: ['setup'],
        enabled: session === null,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/setup', {
                signal,
            })
            if (!data) throw new Error('missing-setup')
            return data
        },
    })
    if (session && !session.mustChangePassword)
        return (
            <>
                {sessionQuery.error && (
                    <div className="space-y-2 border-b p-3">
                        <ErrorMessage error={sessionQuery.error} />
                        <p className="text-xs text-muted-foreground">
                            {t('lastSessionRefresh', {
                                time: sessionQuery.dataUpdatedAt
                                    ? new Date(
                                          sessionQuery.dataUpdatedAt,
                                      ).toLocaleString('zh-CN')
                                    : t('neverRefreshed'),
                            })}
                        </p>
                        <Button
                            variant="outline"
                            disabled={sessionQuery.isFetching}
                            onClick={() => void sessionQuery.refetch()}
                        >
                            {t('retry')}
                        </Button>
                    </div>
                )}
                <PageBoundary>
                    <Workspace />
                </PageBoundary>
            </>
        )
    const setup = setupQuery.data
    const mode = session?.mustChangePassword
        ? 'passwordChange'
        : setup?.state === 'resetPending' && !existingLogin
          ? 'reset'
          : setup?.state === 'uninitialized'
            ? 'setup'
            : 'login'
    const closed =
        (mode === 'setup' || mode === 'reset') && setup?.windowOpen === false
    const error = sessionQuery.error ?? setupQuery.error
    return (
        <main className="flex min-h-svh items-center justify-center bg-muted/40 px-4 py-12">
            <div className="w-full max-w-md space-y-6">
                <div className="text-center">
                    <img
                        className="mx-auto mb-3 size-14"
                        src="/icon-128.png"
                        alt=""
                    />
                    <h1 className="text-xl font-semibold">{t('product')}</h1>
                    <p className="mt-1 text-sm text-muted-foreground">
                        {t('subtitle')}
                    </p>
                </div>
                <Card>
                    <CardHeader>
                        <CardTitle>{t(mode)}</CardTitle>
                        <CardDescription>
                            {session?.mustChangePassword &&
                                t('temporaryPassword')}
                            {location.protocol === 'http:' && (
                                <span className="block">
                                    {t('httpWarning')} {t('httpsInstructions')}
                                </span>
                            )}
                        </CardDescription>
                    </CardHeader>
                    <CardContent className="space-y-4">
                        <ErrorMessage error={error} />
                        {error && (
                            <Button
                                variant="outline"
                                onClick={() =>
                                    void (session === undefined
                                        ? sessionQuery.refetch()
                                        : setupQuery.refetch())
                                }
                            >
                                {t('retry')}
                            </Button>
                        )}
                        {(session === undefined && !error) ||
                        (session === null && setupQuery.isPending) ? (
                            <p role="status">{t('loading')}</p>
                        ) : closed ? (
                            <p role="status">{t('setupClosed')}</p>
                        ) : (
                            (session || setup) && (
                                <LazyAuthForm
                                    key={`${mode}:${setup?.resetAccountName ?? ''}`}
                                    mode={mode}
                                    resetAccount={
                                        mode === 'reset'
                                            ? setup?.resetAccountName
                                            : undefined
                                    }
                                />
                            )
                        )}
                        {!session && setup?.state === 'resetPending' && (
                            <Button
                                variant="outline"
                                onClick={() => setExistingLogin(!existingLogin)}
                            >
                                {t(
                                    existingLogin
                                        ? 'backToReset'
                                        : 'existingLogin',
                                )}
                            </Button>
                        )}
                        {!session &&
                            setup?.windowOpen &&
                            setup.windowClosesAt && (
                                <p className="text-xs text-muted-foreground">
                                    {t('windowCloses', {
                                        time: new Date(
                                            setup.windowClosesAt,
                                        ).toLocaleString('zh-CN'),
                                    })}
                                </p>
                            )}
                        {session?.mustChangePassword && <LogoutButton />}
                    </CardContent>
                </Card>
            </div>
        </main>
    )
}
