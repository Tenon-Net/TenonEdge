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
    createParameterApi,
    editParameters,
    parameterRequest,
    type ParameterForm,
    type ParameterValues,
} from './parameters-api'

const parameters = createParameterApi(api)
const queryKey = ['runtime-parameters']
const fields: (keyof ParameterValues)[] = [
    'auditRetentionDays',
    'previewGlobalLimit',
    'previewSessionLimit',
    'debugObjectBytes',
    'debugGlobalBytes',
    'debugObjectRecords',
    'inFlightBytes',
    'inputChannelCapacity',
    'planSources',
    'planConnections',
    'planModbusConnections',
    'planPathsPerSource',
    'planTotalPaths',
]
export function RuntimeParameterPanel() {
    const { t } = useTranslation()
    const canEdit = useSession((s) => s.value?.role) === 'administrator'
    const query = useQuery({
        queryKey,
        queryFn: ({ signal }) => parameters.read(signal),
    })
    const [store] = useState(() =>
        createStore<{ form: ParameterForm | null }>(() => ({ form: null })),
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
            !window.confirm(t('systemSettings.parameters.discard')),
    })
    const view = query.error ? undefined : query.data
    const blocked = busy || query.isFetching || !view || needsRead
    function begin() {
        if (blocked || !view) return
        store.setState({ form: editParameters(view) })
        confirm(false)
        setError(null)
        setInvalid(false)
        setSaved(false)
    }
    async function refresh() {
        if (form && !window.confirm(t('systemSettings.parameters.discard')))
            return
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
            parameterRequest(form, confirmed)
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
            aria-label={t('systemSettings.parameters.title')}
            className="space-y-3 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('systemSettings.parameters.title')}
            </h2>
            <p className="text-sm">{t('systemSettings.parameters.notice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('systemSettings.parameters.boundary')}
            </p>
            <p className="text-sm text-muted-foreground">
                {t('systemSettings.parameters.planScope')}
            </p>
            <div className="flex flex-wrap gap-3">
                <Button
                    variant="outline"
                    disabled={busy || query.isFetching}
                    onClick={() => void refresh()}
                >
                    {t('systemSettings.parameters.refresh')}
                </Button>
                {canEdit && !form && (
                    <Button disabled={blocked} onClick={begin}>
                        {t('systemSettings.parameters.edit')}
                    </Button>
                )}
            </div>
            <ErrorMessage error={error || query.error} />
            {query.error && (
                <p role="alert">{t('systemSettings.parameters.readFailed')}</p>
            )}
            {needsRead && (
                <p role="alert">{t('systemSettings.parameters.unknown')}</p>
            )}
            {saved && (
                <p role="status">{t('systemSettings.parameters.saved')}</p>
            )}
            {view && (
                <>
                    <dl className="grid gap-3 sm:grid-cols-3">
                        {fields.map((key) => (
                            <div key={key}>
                                <dt className="text-sm text-muted-foreground">
                                    {t(`systemSettings.parameters.${key}`)}
                                </dt>
                                <dd data-parameter={key}>
                                    {String(view.values[key])}
                                </dd>
                            </div>
                        ))}
                    </dl>
                    <p className="text-sm">
                        {t('systemSettings.parameters.minimum', {
                            days: view.deploymentAuditMinimumDays,
                        })}
                    </p>
                </>
            )}
            {canEdit && form && (
                <form
                    aria-label={t('systemSettings.parameters.editing')}
                    className="space-y-3"
                    onSubmit={(e) => {
                        e.preventDefault()
                        void save()
                    }}
                >
                    {view && view.version !== form.expectedVersion && (
                        <p role="alert">
                            {t('systemSettings.parameters.stale')}
                        </p>
                    )}
                    <fieldset disabled={busy} className="space-y-3">
                        {fields.map((key) => (
                            <label key={key} className="block text-sm">
                                {t(`systemSettings.parameters.${key}`)}
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
                            {t('systemSettings.parameters.risk')}
                        </p>
                        <label className="flex items-center gap-2 text-sm">
                            <input
                                type="checkbox"
                                checked={confirmed}
                                onChange={(e) => confirm(e.target.checked)}
                            />
                            {t('systemSettings.parameters.confirm')}
                        </label>
                    </fieldset>
                    {invalid && (
                        <p role="alert">
                            {t('systemSettings.parameters.invalid')}
                        </p>
                    )}
                    <div className="flex flex-wrap gap-3">
                        <Button type="submit" disabled={blocked || !confirmed}>
                            {t('systemSettings.parameters.save')}
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
                            {t('systemSettings.parameters.cancel')}
                        </Button>
                    </div>
                </form>
            )}
        </section>
    )
}
