import { useId, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import {
    createTrustedProxyApi,
    editTrustedProxies,
    trustedProxyRequest,
    type TrustedProxyForm,
} from './trusted-proxies-api'
const proxies = createTrustedProxyApi(api)
const queryKey = ['trusted-proxies']
export function TrustedProxyPanel() {
    const { t } = useTranslation()
    const inputId = useId()
    const canEdit = useSession((s) => s.value?.role) === 'administrator'
    const query = useQuery({
        queryKey,
        queryFn: ({ signal }) => proxies.read(signal),
    })
    const [store] = useState(() =>
        createStore<{ form: TrustedProxyForm | null }>(() => ({ form: null })),
    )
    const form = useStore(store, (s) => s.form)
    const [confirmed, confirm] = useState(false)
    const [busy, setBusy] = useState(false)
    const [needsRead, requireRead] = useState(false)
    const [error, setError] = useState<unknown>(null)
    const [invalid, setInvalid] = useState(false)
    const [saved, setSaved] = useState(false)
    const sending = useRef(false)
    useBlocker({
        disabled: !form && !busy,
        enableBeforeUnload: !!form || busy,
        shouldBlockFn: () =>
            !window.confirm(t('systemSettings.proxies.discard')),
    })
    const view = query.error ? undefined : query.data
    const blocked = busy || query.isFetching || !view || needsRead
    function begin() {
        if (blocked || !view) return
        store.setState({ form: editTrustedProxies(view) })
        confirm(false)
        setError(null)
        setInvalid(false)
        setSaved(false)
    }
    async function refresh() {
        if (form && !window.confirm(t('systemSettings.proxies.discard'))) return
        store.setState({ form: null })
        confirm(false)
        setSaved(false)
        const result = await query.refetch()
        requireRead(!!result.error)
        if (!result.error) {
            setError(null)
            setInvalid(false)
        }
    }
    async function save() {
        if (!canEdit || !form || !confirmed || blocked || sending.current)
            return
        try {
            trustedProxyRequest(form, confirmed)
        } catch {
            setInvalid(true)
            confirm(false)
            return
        }
        sending.current = true
        setBusy(true)
        confirm(false)
        setError(null)
        setSaved(false)
        try {
            const value = await proxies.write(form, true)
            queryClient.setQueryData(queryKey, value)
            store.setState({ form: null })
            setSaved(true)
        } catch (failure) {
            setError(failure)
            if (!(failure instanceof ApiError && failure.status === 400)) {
                store.setState({ form: null })
                requireRead(true)
            }
        } finally {
            sending.current = false
            setBusy(false)
        }
    }
    return (
        <section
            aria-label={t('systemSettings.proxies.title')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('systemSettings.proxies.title')}
            </h2>
            <p className="text-sm">{t('systemSettings.proxies.notice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('systemSettings.proxies.risk')}
            </p>
            <div className="flex gap-3">
                <Button
                    variant="outline"
                    disabled={busy || query.isFetching}
                    onClick={() => void refresh()}
                >
                    {t('systemSettings.proxies.refresh')}
                </Button>
                {canEdit && !form && (
                    <Button disabled={blocked} onClick={begin}>
                        {t('systemSettings.proxies.edit')}
                    </Button>
                )}
            </div>
            <ErrorMessage error={error || query.error} />
            {query.error && (
                <p role="alert">{t('systemSettings.proxies.readFailed')}</p>
            )}
            {needsRead && (
                <p role="alert">{t('systemSettings.proxies.unknown')}</p>
            )}
            {saved && <p role="status">{t('systemSettings.proxies.saved')}</p>}
            {view && (
                <dl className="grid gap-3 sm:grid-cols-2">
                    {(['networks', 'deployment'] as const).map((key) => (
                        <div key={key}>
                            <dt className="text-sm text-muted-foreground">
                                {t(`systemSettings.proxies.${key}`)}
                            </dt>
                            <dd data-proxy={key}>
                                {view[key].length ? (
                                    <ul>
                                        {view[key].map((value) => (
                                            <li key={value}>
                                                <code>{value}</code>
                                            </li>
                                        ))}
                                    </ul>
                                ) : (
                                    t('systemSettings.proxies.empty')
                                )}
                            </dd>
                        </div>
                    ))}
                </dl>
            )}
            {canEdit && form && (
                <form
                    aria-label={t('systemSettings.proxies.editing')}
                    className="space-y-3"
                    onSubmit={(e) => {
                        e.preventDefault()
                        void save()
                    }}
                >
                    {view && view.version !== form.expectedVersion && (
                        <p role="alert">{t('systemSettings.proxies.stale')}</p>
                    )}
                    <fieldset disabled={busy} className="space-y-3">
                        <label htmlFor={inputId} className="block text-sm">
                            {t('systemSettings.proxies.input')}
                        </label>
                        <textarea
                            id={inputId}
                            rows={6}
                            spellCheck={false}
                            className="mt-1 block w-full rounded border bg-background p-2 font-mono"
                            value={form.networks}
                            onChange={(e) => {
                                store.setState({
                                    form: {
                                        ...form,
                                        networks: e.target.value,
                                    },
                                })
                                confirm(false)
                                setInvalid(false)
                            }}
                        />
                        <p className="text-sm">
                            {t('systemSettings.proxies.help')}
                        </p>
                        <label className="flex items-center gap-2 text-sm">
                            <input
                                type="checkbox"
                                checked={confirmed}
                                onChange={(e) => confirm(e.target.checked)}
                            />
                            {t('systemSettings.proxies.confirm')}
                        </label>
                    </fieldset>
                    {invalid && (
                        <p role="alert">
                            {t('systemSettings.proxies.invalid')}
                        </p>
                    )}
                    <div className="flex gap-3">
                        <Button type="submit" disabled={blocked || !confirmed}>
                            {t('systemSettings.proxies.save')}
                        </Button>
                        <Button
                            type="button"
                            variant="outline"
                            disabled={busy}
                            onClick={() => {
                                store.setState({ form: null })
                                confirm(false)
                                setError(null)
                                setInvalid(false)
                            }}
                        >
                            {t('systemSettings.proxies.cancel')}
                        </Button>
                    </div>
                </form>
            )}
        </section>
    )
}
