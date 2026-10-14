import { useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { useStore } from 'zustand'
import { createStore } from 'zustand/vanilla'
import { api, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { createTokensApi, type ApiToken, type TokenForm } from './api'
import { TokenTable } from './table'
const tokensApi = createTokensApi(api)
const inputClass = 'mt-1 w-full rounded-md border bg-background p-2'
export function TokensPage() {
    const { t } = useTranslation()
    const canManage =
        useSession((state) => state.value?.role) === 'administrator'
    const https = window.location.protocol === 'https:'
    const [after, setAfter] = useState(0)
    const [editor] = useState(() =>
        createStore<{ form: TokenForm | null }>(() => ({ form: null })),
    )
    const form = useStore(editor, (state) => state.form)
    // The one-time plaintext is only component-local. Never a query/mutation result or persisted store.
    const [secret, setSecret] = useState<string | null>(null)
    const [target, setTarget] = useState<ApiToken | null>(null)
    const [confirmed, setConfirmed] = useState(false)
    const [busy, setBusy] = useState(false)
    const [needsRead, setNeedsRead] = useState(false)
    const [error, setError] = useState<unknown>(null)
    const [notice, setNotice] = useState('')
    const sending = useRef(false)
    const query = useQuery({
        queryKey: ['tokens', after],
        enabled: canManage,
        queryFn: ({ signal }) => tokensApi.list(after, signal),
    })
    const editing = !!form || !!target || !!secret
    useBlocker({
        disabled: !editing && !busy,
        enableBeforeUnload: editing || busy,
        shouldBlockFn: () => !window.confirm(t('tokens.discard')),
    })
    const blocked =
        busy || query.isFetching || needsRead || !!query.error || editing
    function edit<K extends keyof TokenForm>(key: K, value: TokenForm[K]) {
        if (form) editor.setState({ form: { ...form, [key]: value } })
    }
    async function execute() {
        if (sending.current || !canManage || (!form && (!target || !confirmed)))
            return
        if (form && !https) return
        sending.current = true
        setBusy(true)
        setError(null)
        setNotice('')
        const current = editor.getState().form
        editor.setState({ form: null })
        const revoked = target
        setTarget(null)
        setConfirmed(false)
        try {
            if (current) {
                const created = await tokensApi.create(current)
                setSecret(created.secret)
                setNotice(t('tokens.saved'))
            } else if (revoked) {
                await tokensApi.revoke(Number(revoked.id), true)
                setNotice(t('tokens.revoked'))
            }
            await query.refetch()
        } catch (failure) {
            setError(failure)
            setNeedsRead(true)
        } finally {
            sending.current = false
            setBusy(false)
        }
    }
    async function refresh() {
        const result = await query.refetch()
        if (result.isSuccess) {
            setNeedsRead(false)
            setError(null)
        }
    }
    const code =
        error instanceof ApiError &&
        error.problem &&
        typeof error.problem === 'object' &&
        'code' in error.problem &&
        typeof error.problem.code === 'string'
            ? error.problem.code
            : ''
    if (!canManage) return <p role="alert">{t('tokens.administratorOnly')}</p>
    return (
        <section className="space-y-5">
            <div>
                <h1 className="text-2xl font-semibold">{t('tokens.title')}</h1>
                <p className="mt-2 text-sm text-muted-foreground">
                    {t('tokens.description')}
                </p>
            </div>
            <p role={!https ? 'alert' : undefined}>{t('tokens.https')}</p>
            <ErrorMessage error={query.error} />
            <ErrorMessage error={error} />
            {code && t(`tokens.errors.${code}`, { defaultValue: '' }) && (
                <p role="alert">{t(`tokens.errors.${code}`)}</p>
            )}
            {needsRead && <p role="alert">{t('tokens.afterFailure')}</p>}
            {notice && <p role="status">{notice}</p>}
            <div className="flex gap-2">
                <Button
                    disabled={blocked || !https || !query.data}
                    onClick={() => {
                        editor.setState({
                            form: { name: '', role: 'viewer', expiresAt: '' },
                        })
                        setNotice('')
                    }}
                >
                    {t('tokens.create')}
                </Button>
                <Button
                    variant="outline"
                    disabled={busy || editing || query.isFetching}
                    onClick={() => void refresh()}
                >
                    {t('tokens.refresh')}
                </Button>
            </div>
            {query.isPending && <p>{t('loading')}</p>}
            {query.data && (
                <TokenTable
                    items={query.data.items}
                    disabled={blocked}
                    revoke={(token) => {
                        setTarget(token)
                        setConfirmed(false)
                        setNotice('')
                    }}
                />
            )}
            <div className="flex gap-2">
                <Button
                    variant="outline"
                    disabled={
                        busy || editing || query.isFetching || after === 0
                    }
                    onClick={() => setAfter(0)}
                >
                    {t('tokens.first')}
                </Button>
                <Button
                    variant="outline"
                    disabled={
                        busy ||
                        editing ||
                        query.isFetching ||
                        !query.data?.nextCursor
                    }
                    onClick={() => setAfter(Number(query.data!.nextCursor))}
                >
                    {t('tokens.next')}
                </Button>
            </div>
            {form && (
                <form
                    className="max-w-xl space-y-4 rounded-md border p-5"
                    onSubmit={(event) => {
                        event.preventDefault()
                        void execute()
                    }}
                >
                    <h2 className="text-lg font-semibold">
                        {t('tokens.create')}
                    </h2>
                    <p>{t('tokens.warning')}</p>
                    <p>{t('tokens.oneTime')}</p>
                    <label className="block">
                        {t('tokens.name')}
                        <input
                            className={inputClass}
                            value={form.name}
                            required
                            maxLength={64}
                            autoComplete="off"
                            onChange={(event) =>
                                edit('name', event.target.value)
                            }
                        />
                    </label>
                    <label className="block">
                        {t('tokens.role')}
                        <select
                            aria-label={t('tokens.role')}
                            className={inputClass}
                            value={form.role}
                            onChange={(event) => {
                                const value = event.target.value
                                if (
                                    value === 'administrator' ||
                                    value === 'configurator' ||
                                    value === 'viewer'
                                )
                                    edit('role', value)
                            }}
                        >
                            {(
                                [
                                    'viewer',
                                    'configurator',
                                    'administrator',
                                ] as const
                            ).map((role) => (
                                <option key={role} value={role}>
                                    {t(`roles.${role}`)}
                                </option>
                            ))}
                        </select>
                    </label>
                    <label className="block">
                        {t('tokens.expiry')}
                        <input
                            className={inputClass}
                            type="datetime-local"
                            value={form.expiresAt}
                            onChange={(event) =>
                                edit('expiresAt', event.target.value)
                            }
                        />
                    </label>
                    <div className="flex gap-2">
                        <Button type="submit" disabled={busy}>
                            {t('tokens.create')}
                        </Button>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={() => editor.setState({ form: null })}
                        >
                            {t('tokens.cancel')}
                        </Button>
                    </div>
                </form>
            )}
            {secret && (
                <section
                    className="max-w-2xl space-y-3 rounded-md border p-5"
                    aria-label={t('tokens.secret')}
                >
                    <p>{t('tokens.oneTime')}</p>
                    <label className="block">
                        {t('tokens.secret')}
                        <textarea
                            className={inputClass}
                            readOnly
                            value={secret}
                            autoComplete="off"
                            spellCheck={false}
                        />
                    </label>
                    <Button onClick={() => setSecret(null)}>
                        {t('tokens.closeSecret')}
                    </Button>
                </section>
            )}
            {target && (
                <section
                    className="max-w-xl space-y-4 rounded-md border p-5"
                    aria-label={t('tokens.execute')}
                >
                    <h2 className="text-lg font-semibold">
                        {t('tokens.execute')}：{target.name}
                    </h2>
                    <p>{t('tokens.revokeWarning')}</p>
                    <p>
                        {t(`roles.${target.role}`)} ·{' '}
                        {target.creatorAccountName}
                    </p>
                    <label className="flex items-center gap-2">
                        <input
                            type="checkbox"
                            checked={confirmed}
                            onChange={(event) =>
                                setConfirmed(event.target.checked)
                            }
                        />
                        {t('tokens.confirm')}
                    </label>
                    <div className="flex gap-2">
                        <Button
                            disabled={!confirmed || busy}
                            onClick={() => void execute()}
                        >
                            {t('tokens.execute')}
                        </Button>
                        <Button
                            variant="outline"
                            onClick={() => setTarget(null)}
                        >
                            {t('tokens.cancel')}
                        </Button>
                    </div>
                </section>
            )}
        </section>
    )
}
