import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import { Button } from '../../components/ui/button'
import type { components } from '../../api/schema'
import { credentialRecipient, type Connection } from './model'
import { summaryKey } from '../configuration/queries'

type Kind = NonNullable<components['schemas']['HttpCredentialKind']>
export function CredentialPanel({
    connection,
    draftVersion,
}: {
    connection: Connection
    draftVersion: string
}) {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const canEdit = role === 'administrator' || role === 'configurator'
    const mqtt = connection.kind === 'mqtt'
    const id = Number(connection.id)
    const recipient = credentialRecipient(connection)
    const key = ['connection-credential', id, connection.kind]
    const query = useQuery({
        queryKey: key,
        retry: false,
        queryFn: async ({ signal }) => {
            try {
                const response = mqtt
                    ? await api.GET(
                          '/api/edge-admin/v1/connections/{connectionId}/mqtt-credential',
                          { params: { path: { connectionId: id } }, signal },
                      )
                    : await api.GET(
                          '/api/edge-admin/v1/connections/{connectionId}/http-credential',
                          { params: { path: { connectionId: id } }, signal },
                      )
                if (!response.data) throw new Error('missing-credential-status')
                return response.data
            } catch (error) {
                if (error instanceof ApiError && error.status === 404)
                    return null
                throw error
            }
        },
    })
    const [kind, setKind] = useState<Kind>('bearer')
    const [username, setUsername] = useState('')
    const [secret, setSecret] = useState('')
    const [name, setName] = useState('')
    const [account, setAccount] = useState('')
    const [notice, setNotice] = useState('')
    const mutation = useMutation({
        gcTime: 0,
        retry: false,
        // Only the non-secret action enters mutation variables; secrets stay in this form's memory.
        mutationFn: async (action: 'save' | 'clear' | 'retain') => {
            if (action === 'clear') {
                const options = {
                    params: {
                        path: { connectionId: id },
                        query: {
                            expectedDraftVersion: draftVersion,
                            expectedVersion: query.data!.version,
                        },
                    },
                }
                if (mqtt)
                    await api.DELETE(
                        '/api/edge-admin/v1/connections/{connectionId}/mqtt-credential',
                        options,
                    )
                else
                    await api.DELETE(
                        '/api/edge-admin/v1/connections/{connectionId}/http-credential',
                        options,
                    )
            } else {
                const common = {
                    recipient,
                    expectedDraftVersion: draftVersion,
                    expectedVersion: query.data?.version ?? null,
                    retainExisting: action === 'retain',
                }
                const accountChanged =
                    account === '' ? null : account === 'changed'
                if (mqtt)
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}/mqtt-credential',
                        {
                            params: { path: { connectionId: id } },
                            body: {
                                ...common,
                                ...(action === 'save'
                                    ? {
                                          username,
                                          password: secret,
                                          accountChanged,
                                      }
                                    : {}),
                            },
                        },
                    )
                else
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}/http-credential',
                        {
                            params: { path: { connectionId: id } },
                            body: {
                                ...common,
                                ...(action === 'save'
                                    ? {
                                          kind,
                                          secret,
                                          username:
                                              kind === 'basic'
                                                  ? username
                                                  : null,
                                          name:
                                              kind === 'apiKeyHeader' ||
                                              kind === 'apiKeyQuery'
                                                  ? name
                                                  : null,
                                          accountChanged,
                                      }
                                    : {}),
                            },
                        },
                    )
            }
            return action
        },
        onSuccess: async (action) => {
            setSecret('')
            setUsername('')
            setName('')
            setAccount('')
            setNotice(
                t(
                    action === 'clear'
                        ? 'credentials.cleared'
                        : 'credentials.saved',
                ),
            )
            mutation.reset()
            // Completing an imported credential requirement also advances the
            // draft version. Re-read it rather than reusing the old editor token.
            await Promise.all([
                queryClient.invalidateQueries({ queryKey: key }),
                queryClient.invalidateQueries({
                    queryKey: ['connection-drafts'],
                }),
                queryClient.invalidateQueries({ queryKey: summaryKey }),
            ])
        },
    })
    const blocked =
        query.isPending ||
        query.isError ||
        query.isFetching ||
        mutation.isPending
    const mismatch = query.data && query.data.recipient !== recipient
    function act(action: 'save' | 'clear' | 'retain') {
        if (blocked) return
        if (
            window.confirm(
                t(
                    action === 'save'
                        ? 'credentials.confirmSave'
                        : action === 'clear'
                          ? 'credentials.confirmClear'
                          : 'credentials.confirmRetain',
                ),
            )
        ) {
            setNotice('')
            mutation.mutate(action)
        }
    }
    return (
        <section
            className="space-y-3 rounded-md border p-4"
            aria-label={t('credentials.title')}
        >
            <h2 className="font-semibold">{t('credentials.title')}</h2>
            <p className="text-sm text-muted-foreground">
                {t('credentials.notice')}
            </p>
            <p>
                {query.isPending
                    ? t('credentials.loading')
                    : query.data?.configured
                      ? t('credentials.configured')
                      : query.isError
                        ? '—'
                        : t('credentials.empty')}
            </p>
            {mismatch && <p>{t('credentials.mismatch')}</p>}
            <ErrorMessage error={query.error ?? mutation.error} />
            {mutation.error instanceof ApiError &&
                mutation.error.status === 409 && (
                    <p role="alert">{t('credentials.conflict')}</p>
                )}
            {notice && <p role="status">{notice}</p>}
            <Button
                variant="outline"
                disabled={query.isFetching || mutation.isPending}
                onClick={() => void query.refetch()}
            >
                {t('credentials.refresh')}
            </Button>
            {canEdit && (
                <form
                    className="space-y-3"
                    onSubmit={(event) => {
                        event.preventDefault()
                        act('save')
                    }}
                >
                    <fieldset
                        disabled={blocked}
                        className="grid gap-3 md:grid-cols-2"
                    >
                        {!mqtt && (
                            <label>
                                {t('credentials.kind')}
                                <select
                                    className="mt-1 w-full rounded border p-2"
                                    value={kind}
                                    onChange={(event) =>
                                        setKind(event.target.value as Kind)
                                    }
                                >
                                    {(
                                        [
                                            'basic',
                                            'bearer',
                                            'apiKeyHeader',
                                            'apiKeyQuery',
                                        ] as const
                                    ).map((value) => (
                                        <option key={value} value={value}>
                                            {t(`credentials.kinds.${value}`)}
                                        </option>
                                    ))}
                                </select>
                            </label>
                        )}
                        {(mqtt || kind === 'basic') && (
                            <label>
                                {t('credentials.username')}
                                <input
                                    className="mt-1 w-full rounded border p-2"
                                    value={username}
                                    onChange={(event) =>
                                        setUsername(event.target.value)
                                    }
                                    autoComplete="off"
                                    required
                                    maxLength={256}
                                />
                            </label>
                        )}
                        {!mqtt &&
                            (kind === 'apiKeyHeader' ||
                                kind === 'apiKeyQuery') && (
                                <label>
                                    {t('credentials.name')}
                                    <input
                                        className="mt-1 w-full rounded border p-2"
                                        value={name}
                                        onChange={(event) =>
                                            setName(event.target.value)
                                        }
                                        autoComplete="off"
                                        required
                                        maxLength={256}
                                    />
                                </label>
                            )}
                        <label>
                            {t('credentials.secret')}
                            <input
                                className="mt-1 w-full rounded border p-2"
                                type="password"
                                value={secret}
                                onChange={(event) =>
                                    setSecret(event.target.value)
                                }
                                autoComplete="new-password"
                                required
                                maxLength={4096}
                            />
                        </label>
                        <label>
                            {t('credentials.account')}
                            <select
                                className="mt-1 w-full rounded border p-2"
                                value={account}
                                onChange={(event) =>
                                    setAccount(event.target.value)
                                }
                            >
                                <option value="">
                                    {t('credentials.choose')}
                                </option>
                                <option value="same">
                                    {t('credentials.same')}
                                </option>
                                <option value="changed">
                                    {t('credentials.changed')}
                                </option>
                            </select>
                        </label>
                    </fieldset>
                    <p className="text-sm text-muted-foreground">
                        {t('credentials.accountHelp')}
                    </p>
                    <div className="flex flex-wrap gap-2">
                        <Button type="submit" disabled={blocked}>
                            {t('credentials.save')}
                        </Button>
                        <Button
                            variant="outline"
                            type="button"
                            disabled={blocked || !query.data}
                            onClick={() => act('clear')}
                        >
                            {t('credentials.clear')}
                        </Button>
                        {role === 'administrator' &&
                            mismatch &&
                            query.data?.configured && (
                                <Button
                                    variant="outline"
                                    type="button"
                                    disabled={blocked}
                                    onClick={() => act('retain')}
                                >
                                    {t('credentials.retain')}
                                </Button>
                            )}
                    </div>
                </form>
            )}
        </section>
    )
}
