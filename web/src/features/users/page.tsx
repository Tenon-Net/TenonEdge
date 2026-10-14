import { useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useStore } from 'zustand'
import { createStore } from 'zustand/vanilla'
import { api, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import {
    createUsersApi,
    userForm,
    type User,
    type UserAction,
    type UserForm,
} from './api'

const usersApi = createUsersApi(api)
const fieldClass = 'mt-1 w-full rounded-md border bg-background p-2'
export function UsersPage() {
    const { t } = useTranslation()
    const session = useSession((state) => state.value)
    const canManage = session?.role === 'administrator'
    const [after, setAfter] = useState(0)
    const [editor] = useState(() =>
        createStore<{ form: UserForm | null }>(() => ({ form: null })),
    )
    const form = useStore(editor, (state) => state.form)
    const [review, setReview] = useState(false)
    const [confirmed, setConfirmed] = useState(false)
    const [needsRead, setNeedsRead] = useState(false)
    const [notice, setNotice] = useState('')
    const sending = useRef(false)
    const query = useQuery({
        queryKey: ['users', after],
        enabled: canManage,
        queryFn: ({ signal }) => usersApi.list(after, signal),
    })
    useBlocker({
        disabled: form === null,
        enableBeforeUnload: form !== null,
        shouldBlockFn: () => !window.confirm(t('connections.discard')),
    })
    const mutation = useMutation({
        // Never put a password in mutation variables/cache. Only this awaited call owns the request copy.
        gcTime: 0,
        mutationFn: async () => {
            const current = editor.getState().form
            editor.setState({ form: null })
            setReview(false)
            if (!current) throw new Error('missing-user-command')
            await usersApi.write(current, confirmed)
        },
        onSuccess: async () => {
            setNotice(t('users.saved'))
            await query.refetch()
        },
        onError: () => setNeedsRead(true),
        onSettled: () => {
            sending.current = false
        },
    })
    function open(action: UserAction, user: User | null = null) {
        editor.setState({ form: userForm(action, user) })
        setReview(false)
        setConfirmed(false)
        setNotice('')
        mutation.reset()
    }
    function edit<K extends keyof UserForm>(key: K, value: UserForm[K]) {
        if (form) editor.setState({ form: { ...form, [key]: value } })
    }
    async function refresh() {
        const result = await query.refetch()
        if (result.isSuccess) {
            setNeedsRead(false)
            mutation.reset()
        }
    }
    const formatTime = (time: string | null | undefined) =>
        time ? new Date(time).toLocaleString() : t('users.unknown')
    const errorCode =
        mutation.error instanceof ApiError &&
        mutation.error.problem &&
        typeof mutation.error.problem === 'object' &&
        'code' in mutation.error.problem &&
        typeof mutation.error.problem.code === 'string'
            ? mutation.error.problem.code
            : ''
    const busy = mutation.isPending || query.isFetching
    const blocked = busy || needsRead || !!query.error || !!form
    if (!canManage) return <p role="alert">{t('users.administratorOnly')}</p>
    return (
        <section className="space-y-5">
            <div>
                <h1 className="text-2xl font-semibold">{t('users.title')}</h1>
                <p className="mt-2 text-sm text-muted-foreground">
                    {t('users.description')}
                </p>
            </div>
            <ErrorMessage error={query.error} />
            <ErrorMessage error={mutation.error} />
            {errorCode &&
                t(`users.errors.${errorCode}`, { defaultValue: '' }) && (
                    <p role="alert">{t(`users.errors.${errorCode}`)}</p>
                )}
            {notice && <p role="status">{notice}</p>}
            {needsRead && <p role="alert">{t('users.checkAfterFailure')}</p>}
            <div className="flex flex-wrap gap-2">
                <Button
                    disabled={blocked || !query.data}
                    onClick={() => open('create')}
                >
                    {t('users.create')}
                </Button>
                <Button
                    variant="outline"
                    disabled={busy || !!form}
                    onClick={() => void refresh()}
                >
                    {t('users.refresh')}
                </Button>
            </div>
            {query.isPending && <p>{t('loading')}</p>}
            {query.data && (
                <div className="overflow-x-auto rounded-md border">
                    <table className="w-full text-left text-sm">
                        <thead className="bg-muted">
                            <tr>
                                {[
                                    'account',
                                    'displayName',
                                    'role',
                                    'state',
                                    'lastLogin',
                                    'ip',
                                ].map((key) => (
                                    <th key={key} className="p-3">
                                        {t(`users.${key}`)}
                                    </th>
                                ))}
                                <th className="p-3">
                                    {t('users.operation', { action: '' })}
                                </th>
                            </tr>
                        </thead>
                        <tbody>
                            {query.data.items.map((user) => (
                                <tr
                                    key={user.id}
                                    className="border-t align-top"
                                >
                                    <td className="p-3 font-medium">
                                        {user.accountName}
                                    </td>
                                    <td className="p-3">
                                        {user.displayName ?? '—'}
                                    </td>
                                    <td className="p-3">
                                        {t(`roles.${user.role}`)}
                                    </td>
                                    <td className="p-3">
                                        <p>
                                            {t(
                                                user.isEnabled
                                                    ? 'users.enabledState'
                                                    : 'users.disabledState',
                                            )}
                                        </p>
                                        {user.lockedUntil && (
                                            <p>
                                                {t('users.locked', {
                                                    time: formatTime(
                                                        user.lockedUntil,
                                                    ),
                                                })}
                                            </p>
                                        )}
                                        {user.mustChangePassword && (
                                            <p>{t('users.mustChange')}</p>
                                        )}
                                    </td>
                                    <td className="p-3">
                                        {formatTime(user.lastLoginAt)}
                                    </td>
                                    <td className="p-3">
                                        {user.lastLoginIp ?? t('users.unknown')}
                                    </td>
                                    <td className="p-3">
                                        <div className="flex max-w-xs flex-wrap gap-2">
                                            {(
                                                [
                                                    'update',
                                                    'password',
                                                    'unlock',
                                                    'end-sessions',
                                                    'delete',
                                                ] satisfies UserAction[]
                                            ).map((action) => (
                                                <Button
                                                    key={action}
                                                    size="sm"
                                                    variant="outline"
                                                    disabled={
                                                        blocked ||
                                                        (action === 'delete' &&
                                                            user.accountName ===
                                                                session?.accountName) ||
                                                        (action === 'unlock' &&
                                                            !user.lockedUntil)
                                                    }
                                                    onClick={() =>
                                                        open(action, user)
                                                    }
                                                >
                                                    {t(`users.${action}`)}
                                                </Button>
                                            ))}
                                        </div>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
            <div className="flex gap-2">
                <Button
                    variant="outline"
                    disabled={busy || !!form || after === 0}
                    onClick={() => setAfter(0)}
                >
                    {t('users.first')}
                </Button>
                <Button
                    variant="outline"
                    disabled={busy || !!form || !query.data?.nextCursor}
                    onClick={() => setAfter(Number(query.data!.nextCursor))}
                >
                    {t('users.next')}
                </Button>
            </div>
            {form && (
                <section
                    className="max-w-xl space-y-4 rounded-md border p-5"
                    aria-label={t(
                        review ? 'users.confirmTitle' : `users.${form.action}`,
                    )}
                >
                    <h2 className="text-lg font-semibold">
                        {t(
                            review
                                ? 'users.confirmTitle'
                                : `users.${form.action}`,
                        )}
                    </h2>
                    <p>{t('users.securityWarning')}</p>
                    {(form.action === 'create' ||
                        form.action === 'password') && (
                        <p>{t('users.passwordWarning')}</p>
                    )}
                    {form.action === 'delete' && (
                        <p>{t('users.deleteWarning')}</p>
                    )}
                    {review ? (
                        <>
                            <p>
                                {t('users.target', {
                                    account: form.accountName,
                                })}
                            </p>
                            <p>
                                {t('users.operation', {
                                    action: t(`users.${form.action}`),
                                })}
                            </p>
                            {(form.action === 'create' ||
                                form.action === 'update') && (
                                <p>
                                    {t('users.settings', {
                                        name: form.displayName || '—',
                                        role: t(`roles.${form.role}`),
                                        state: t(
                                            form.isEnabled
                                                ? 'users.enabledState'
                                                : 'users.disabledState',
                                        ),
                                    })}
                                </p>
                            )}
                            <label className="flex items-start gap-2">
                                <input
                                    type="checkbox"
                                    checked={confirmed}
                                    onChange={(event) =>
                                        setConfirmed(event.target.checked)
                                    }
                                />
                                {t('users.confirmation')}
                            </label>
                            <Button
                                disabled={!confirmed || mutation.isPending}
                                onClick={() => {
                                    if (sending.current) return
                                    sending.current = true
                                    mutation.mutate()
                                }}
                            >
                                {t('users.confirm')}
                            </Button>
                        </>
                    ) : (
                        <form
                            className="space-y-4"
                            onSubmit={(event) => {
                                event.preventDefault()
                                setReview(true)
                                setConfirmed(false)
                            }}
                        >
                            <label className="block">
                                {t('users.account')}
                                <input
                                    className={fieldClass}
                                    value={form.accountName}
                                    readOnly={form.action !== 'create'}
                                    onChange={(event) =>
                                        edit('accountName', event.target.value)
                                    }
                                    required
                                    minLength={3}
                                    maxLength={32}
                                    pattern="[A-Za-z0-9._\-]{3,32}"
                                    autoComplete="off"
                                />
                            </label>
                            {(form.action === 'create' ||
                                form.action === 'update') && (
                                <>
                                    <label className="block">
                                        {t('users.displayName')}
                                        <input
                                            className={fieldClass}
                                            value={form.displayName}
                                            onChange={(event) =>
                                                edit(
                                                    'displayName',
                                                    event.target.value,
                                                )
                                            }
                                            maxLength={128}
                                        />
                                    </label>
                                    <label className="block">
                                        {t('users.role')}
                                        <select
                                            aria-label={t('users.role')}
                                            className={fieldClass}
                                            value={form.role}
                                            onChange={(event) => {
                                                const role = event.target.value
                                                if (
                                                    role === 'administrator' ||
                                                    role === 'configurator' ||
                                                    role === 'viewer'
                                                )
                                                    edit('role', role)
                                            }}
                                        >
                                            {(
                                                [
                                                    'administrator',
                                                    'configurator',
                                                    'viewer',
                                                ] as const
                                            ).map((role) => (
                                                <option key={role} value={role}>
                                                    {t(`roles.${role}`)}
                                                </option>
                                            ))}
                                        </select>
                                    </label>
                                    <label className="flex items-center gap-2">
                                        <input
                                            type="checkbox"
                                            checked={form.isEnabled}
                                            onChange={(event) =>
                                                edit(
                                                    'isEnabled',
                                                    event.target.checked,
                                                )
                                            }
                                        />
                                        {t('users.enabled')}
                                    </label>
                                </>
                            )}
                            {(form.action === 'create' ||
                                form.action === 'password') && (
                                <label className="block">
                                    {t('users.temporaryPassword')}
                                    <input
                                        className={fieldClass}
                                        type="password"
                                        aria-label={t(
                                            'users.temporaryPassword',
                                        )}
                                        aria-describedby="users-password-help"
                                        value={form.temporaryPassword}
                                        onChange={(event) =>
                                            edit(
                                                'temporaryPassword',
                                                event.target.value,
                                            )
                                        }
                                        autoComplete="new-password"
                                        required
                                        minLength={8}
                                        maxLength={128}
                                    />
                                    <span
                                        id="users-password-help"
                                        className="text-sm text-muted-foreground"
                                    >
                                        {t('users.passwordHelp')}
                                    </span>
                                </label>
                            )}
                            <Button type="submit">{t('users.review')}</Button>
                        </form>
                    )}
                    <Button
                        variant="outline"
                        disabled={mutation.isPending}
                        onClick={() => {
                            editor.setState({ form: null })
                            setReview(false)
                        }}
                    >
                        {t('users.cancel')}
                    </Button>
                </section>
            )}
        </section>
    )
}
