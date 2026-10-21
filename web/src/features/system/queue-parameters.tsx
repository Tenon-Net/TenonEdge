import { useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { useTranslation } from './i18n'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import {
    createQueueParameterApi,
    editQueueParameters,
    queueParameterRequest,
    type QueueParameterForm,
    type QueueParameterValues,
    queueParameterFields,
} from './queue-parameters-api'

const parameters = createQueueParameterApi(api)
const queryKey = ['queue-parameters']
const fields: (keyof QueueParameterValues)[] = queueParameterFields
export function QueueParameterPanel() {
    const { t } = useTranslation()
    const canEdit = useSession((s) => s.value?.role) === 'administrator'
    const query = useQuery({
        queryKey,
        queryFn: ({ signal }) => parameters.read(signal),
    })
    const [store] = useState(() =>
        createStore<{ form: QueueParameterForm | null }>(() => ({
            form: null,
        })),
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
        shouldBlockFn: () => !window.confirm(t('systemSettings.queue.discard')),
    })
    const view = query.error ? undefined : query.data
    const blocked = busy || query.isFetching || !view || needsRead
    function begin() {
        if (blocked || !view) return
        store.setState({ form: editQueueParameters(view) })
        confirm(false)
        setError(null)
        setInvalid(false)
        setSaved(false)
    }
    async function refresh() {
        if (form && !window.confirm(t('systemSettings.queue.discard'))) return
        store.setState({ form: null })
        confirm(false)
        setSaved(false)
        const read = await query.refetch()
        requireRead(!!read.error)
        if (!read.error) {
            setError(null)
            setInvalid(false)
        }
    }
    async function save() {
        if (!canEdit || !form || !confirmed || blocked || sending.current)
            return
        try {
            queueParameterRequest(form, confirmed)
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
            const value = await parameters.write(form, true)
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
            aria-label={t('systemSettings.queue.title')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('systemSettings.queue.title')}
            </h2>
            <p className="text-sm">{t('systemSettings.queue.notice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('systemSettings.queue.boundary')}
            </p>
            <div className="flex gap-3">
                <Button
                    variant="outline"
                    disabled={busy || query.isFetching}
                    onClick={() => void refresh()}
                >
                    {t('systemSettings.queue.refresh')}
                </Button>
                {canEdit && !form && (
                    <Button disabled={blocked} onClick={begin}>
                        {t('systemSettings.queue.edit')}
                    </Button>
                )}
            </div>
            <ErrorMessage error={error || query.error} />
            {query.error && (
                <p role="alert">{t('systemSettings.queue.readFailed')}</p>
            )}
            {needsRead && (
                <p role="alert">{t('systemSettings.queue.unknown')}</p>
            )}
            {saved && <p role="status">{t('systemSettings.queue.saved')}</p>}
            {view && (
                <>
                    <dl className="grid gap-3 sm:grid-cols-3">
                        {fields.map((key) => (
                            <div key={key}>
                                <dt className="text-sm text-muted-foreground">
                                    {t(`systemSettings.queue.${key}`)}
                                </dt>
                                <dd data-queue-parameter={key}>
                                    {String(view.values[key])}
                                </dd>
                            </div>
                        ))}
                    </dl>
                </>
            )}
            {canEdit && form && (
                <form
                    aria-label={t('systemSettings.queue.editing')}
                    className="space-y-3"
                    onSubmit={(e) => {
                        e.preventDefault()
                        void save()
                    }}
                >
                    {view && view.version !== form.expectedVersion && (
                        <p role="alert">{t('systemSettings.queue.stale')}</p>
                    )}
                    <fieldset disabled={busy} className="space-y-3">
                        {fields.map((key) => (
                            <label key={key} className="block text-sm">
                                {t(`systemSettings.queue.${key}`)}
                                <input
                                    className="mt-1 block rounded border bg-background p-2"
                                    inputMode="numeric"
                                    value={form.values[key]}
                                    onChange={(e) => {
                                        store.setState({
                                            form: {
                                                ...form,
                                                values: {
                                                    ...form.values,
                                                    [key]: e.target.value,
                                                },
                                            },
                                        })
                                        confirm(false)
                                        setInvalid(false)
                                    }}
                                />
                            </label>
                        ))}
                        <p className="text-sm">
                            {t('systemSettings.queue.risk')}
                        </p>
                        <label className="flex items-center gap-2 text-sm">
                            <input
                                type="checkbox"
                                checked={confirmed}
                                onChange={(e) => confirm(e.target.checked)}
                            />
                            {t('systemSettings.queue.confirm')}
                        </label>
                    </fieldset>
                    {invalid && (
                        <p role="alert">{t('systemSettings.queue.invalid')}</p>
                    )}
                    <div className="flex gap-3">
                        <Button type="submit" disabled={blocked || !confirmed}>
                            {t('systemSettings.queue.save')}
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
                            {t('systemSettings.queue.cancel')}
                        </Button>
                    </div>
                </form>
            )}
        </section>
    )
}
