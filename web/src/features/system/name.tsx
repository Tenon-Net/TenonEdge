import { useRef, useState } from 'react'
import { useBlocker } from '@tanstack/react-router'
import { useTranslation } from './i18n'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { api, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { useSystemInfo } from '../diagnostics/queries'
import { createSystemApi, gatewayNameRequest } from './api'

const settings = createSystemApi(api)
type NameForm = { name: string; expectedName: string | null }
export function GatewayNameEditor() {
    const { t } = useTranslation()
    const canEdit = useSession((s) => s.value?.role) === 'administrator'
    const query = useSystemInfo()
    const [store] = useState(() =>
        createStore<{ form: NameForm | null }>(() => ({ form: null })),
    )
    const form = useStore(store, (s) => s.form)
    const [confirmed, confirm] = useState(false)
    const [busy, setBusy] = useState(false)
    const [needsRead, requireRead] = useState(false)
    const [error, setError] = useState<unknown>(null)
    const [invalid, setInvalid] = useState(false)
    const [result, setResult] = useState<'saved' | 'unchanged' | null>(null)
    const sending = useRef(false)
    useBlocker({
        disabled: !form && !busy,
        enableBeforeUnload: !!form || busy,
        shouldBlockFn: () => !window.confirm(t('systemSettings.name.discard')),
    })
    if (!canEdit) return null
    const blocked = busy || query.isFetching || !!query.error || needsRead
    function begin() {
        if (blocked || !query.data) return
        store.setState({
            form: {
                name: query.data.gatewayName ?? '',
                expectedName: query.data.gatewayName,
            },
        })
        confirm(false)
        setError(null)
        setInvalid(false)
        setResult(null)
    }
    async function refresh() {
        if (form && !window.confirm(t('systemSettings.name.discard'))) return
        store.setState({ form: null })
        confirm(false)
        const read = await query.refetch()
        if (!read.error) {
            requireRead(false)
            setError(null)
            setInvalid(false)
        }
    }
    async function save() {
        if (!form || !confirmed || blocked || sending.current) return
        let request
        try {
            request = gatewayNameRequest(form.name, form.expectedName)
        } catch {
            setInvalid(true)
            confirm(false)
            return
        }
        sending.current = true
        setBusy(true)
        confirm(false)
        setError(null)
        setResult(null)
        try {
            const response = await settings.writeName(request, true)
            store.setState({ form: null })
            setResult(response.changed ? 'saved' : 'unchanged')
            const read = await query.refetch() // Shared info query updates both the page header and document title.
            requireRead(!!read.error)
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
            aria-label={t('systemSettings.name.title')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('systemSettings.name.title')}
            </h2>
            <p className="text-sm">{t('systemSettings.name.notice')}</p>
            <div className="flex gap-3">
                <Button
                    variant="outline"
                    disabled={busy || query.isFetching}
                    onClick={() => void refresh()}
                >
                    {t('systemSettings.name.refresh')}
                </Button>
                {!form && (
                    <Button disabled={blocked || !query.data} onClick={begin}>
                        {t('systemSettings.name.edit')}
                    </Button>
                )}
            </div>
            <ErrorMessage error={error} />
            {query.error && (
                <p role="alert">{t('systemSettings.name.readFailed')}</p>
            )}
            {needsRead && (
                <p role="alert">{t('systemSettings.name.unknown')}</p>
            )}
            {result && (
                <p role="status">{t(`systemSettings.name.${result}`)}</p>
            )}
            {form && (
                <form
                    aria-label={t('systemSettings.name.editing')}
                    className="space-y-3"
                    onSubmit={(e) => {
                        e.preventDefault()
                        void save()
                    }}
                >
                    {query.data &&
                        form.expectedName !== query.data.gatewayName && (
                            <p role="alert">{t('systemSettings.name.stale')}</p>
                        )}
                    <fieldset disabled={busy} className="space-y-3">
                        <label className="block text-sm">
                            {t('systemSettings.name.input')}
                            <input
                                className="mt-1 block w-full max-w-lg rounded border bg-background p-2"
                                maxLength={64}
                                value={form.name}
                                onChange={(e) => {
                                    store.setState({
                                        form: { ...form, name: e.target.value },
                                    })
                                    confirm(false)
                                    setInvalid(false)
                                    setResult(null)
                                }}
                            />
                        </label>
                        <p className="text-sm text-muted-foreground">
                            {t('systemSettings.name.help')}
                        </p>
                        <label className="flex items-center gap-2 text-sm">
                            <input
                                type="checkbox"
                                checked={confirmed}
                                onChange={(e) => confirm(e.target.checked)}
                            />
                            {t('systemSettings.name.confirm')}
                        </label>
                    </fieldset>
                    {invalid && (
                        <p role="alert">{t('systemSettings.name.invalid')}</p>
                    )}
                    <div className="flex gap-3">
                        <Button type="submit" disabled={blocked || !confirmed}>
                            {t('systemSettings.name.save')}
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
                            {t('systemSettings.name.cancel')}
                        </Button>
                    </div>
                </form>
            )}
        </section>
    )
}
