import { LogoutButton } from './logout-button'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, restoreSession, useSession } from '../../lib/session'
import { AuthForm } from './auth-form'
import { ErrorMessage } from './error-message'
import { Button } from '../../components/ui/button'
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
} from '../../components/ui/card'
import { Workspace } from '../../components/layout/workspace'

export function SessionGate() {
    const { t } = useTranslation()
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
                <ErrorMessage error={sessionQuery.error} />
                <Workspace />
            </>
        )
    const setup = setupQuery.data
    const mode = session?.mustChangePassword
        ? 'passwordChange'
        : setup?.state === 'resetPending'
          ? 'reset'
          : setup?.state === 'uninitialized'
            ? 'setup'
            : 'login'
    const closed =
        !session &&
        setup?.state !== 'initialized' &&
        setup?.windowOpen === false
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
                            {session?.mustChangePassword
                                ? t('temporaryPassword')
                                : location.protocol === 'http:'
                                  ? t('httpWarning')
                                  : ''}
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
                                <AuthForm
                                    key={mode}
                                    mode={mode}
                                    resetAccount={setup?.resetAccountName}
                                />
                            )
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
