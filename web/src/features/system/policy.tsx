import { useId, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { useTranslation } from './i18n'
import { useStore } from 'zustand'
import { createStore } from 'zustand/vanilla'
import { api, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import {
    createSystemApi,
    editPolicy,
    policyRequest,
    policyRow,
    type OutboundView,
    type PolicyForm,
} from './api'
const settings = createSystemApi(api)
const inputClass = 'rounded border bg-background p-2'
function RuleList({
    value,
}: {
    value: Pick<OutboundView, 'networks' | 'localPorts'>
}) {
    const { t } = useTranslation()
    return (
        <div className="space-y-3">
            {value.networks.length ? (
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead>
                            <tr>
                                <th className="p-2">
                                    {t('systemSettings.policy.cidr')}
                                </th>
                                <th className="p-2">
                                    {t('systemSettings.policy.port')}
                                </th>
                            </tr>
                        </thead>
                        <tbody>
                            {value.networks.map((row, i) => (
                                <tr key={i} className="border-t">
                                    <td className="break-all p-2 font-mono">
                                        {row.cidr}
                                    </td>
                                    <td className="p-2">{row.port}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            ) : (
                <p className="text-sm">{t('systemSettings.policy.none')}</p>
            )}
            <p className="break-words text-sm">
                {t('systemSettings.policy.local')}：
                {value.localPorts.length
                    ? value.localPorts.join('、')
                    : t('systemSettings.policy.none')}
            </p>
        </div>
    )
}
export function OutboundPolicyPanel() {
    const { t } = useTranslation()
    const localPortsId = useId()
    const epoch = useSession((s) => s.epoch)
    const canEdit = useSession((s) => s.value?.role) === 'administrator'
    const [store] = useState(() =>
        createStore<{ form: PolicyForm | null }>(() => ({ form: null })),
    )
    const form = useStore(store, (s) => s.form)
    const [confirmed, confirm] = useState(false)
    const [busy, setBusy] = useState(false)
    const [needsRead, requireRead] = useState(false)
    const [error, setError] = useState<unknown>(null)
    const [invalid, setInvalid] = useState(false)
    const [saved, setSaved] = useState(false)
    const sending = useRef(false)
    const query = useQuery({
        queryKey: ['outbound-policy', epoch],
        queryFn: ({ signal }) => settings.policy(signal),
        retry: false,
        gcTime: 0,
    })
    useBlocker({
        disabled: !form && !busy,
        enableBeforeUnload: !!form || busy,
        shouldBlockFn: () =>
            !window.confirm(t('systemSettings.policy.discard')),
    })
    const blocked = busy || query.isFetching || !!query.error || needsRead
    function edit(next: PolicyForm) {
        store.setState({ form: next })
        confirm(false)
        setInvalid(false)
        setSaved(false)
    }
    async function refresh() {
        if (form && !window.confirm(t('systemSettings.policy.reloadDiscard')))
            return
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
        if (!form || !confirmed || !canEdit || blocked || sending.current)
            return
        let request
        try {
            request = policyRequest(form)
        } catch {
            setInvalid(true)
            confirm(false)
            return
        }
        sending.current = true
        setBusy(true)
        setError(null)
        confirm(false)
        setSaved(false)
        try {
            await settings.writePolicy(request, true)
            store.setState({ form: null })
            setSaved(true)
            const read = await query.refetch()
            requireRead(!!read.error)
        } catch (failure) {
            setError(failure)
            // Validation proves no write occurred; keep these fields for correction. Conflict/unknown results require a fresh read.
            if (!(failure instanceof ApiError && failure.status === 400)) {
                store.setState({ form: null })
                requireRead(true)
            }
        } finally {
            sending.current = false
            setBusy(false)
        }
    }
    const current = query.error || needsRead ? undefined : query.data
    return (
        <section
            aria-label={t('systemSettings.policy.title')}
            className="space-y-4 rounded-lg border bg-card p-5"
        >
            <h2 className="text-lg font-semibold">
                {t('systemSettings.policy.title')}
            </h2>
            <p className="text-sm">{t('systemSettings.policy.defaults')}</p>
            <p className="text-sm">{t('systemSettings.policy.protected')}</p>
            <p className="text-sm text-muted-foreground">
                {t('systemSettings.policy.effective')}
            </p>
            {!canEdit && (
                <p className="text-sm">{t('systemSettings.policy.allRoles')}</p>
            )}
            <div className="flex flex-wrap gap-3">
                <Button
                    variant="outline"
                    disabled={busy || query.isFetching}
                    onClick={() => void refresh()}
                >
                    {t('systemSettings.policy.refresh')}
                </Button>
                {canEdit && !form && (
                    <Button
                        disabled={blocked || !current}
                        onClick={() => current && edit(editPolicy(current))}
                    >
                        {t('systemSettings.policy.edit')}
                    </Button>
                )}
            </div>
            {query.isPending && <p role="status">{t('loading')}</p>}
            {query.error && (
                <p role="alert">{t('systemSettings.policy.readFailed')}</p>
            )}
            <ErrorMessage error={error} />
            {needsRead && (
                <p role="alert">{t('systemSettings.policy.resultUnknown')}</p>
            )}
            {saved && <p role="status">{t('systemSettings.policy.saved')}</p>}
            {current && (
                <div className="grid gap-5 lg:grid-cols-2">
                    <section
                        aria-label={t('systemSettings.policy.deployment')}
                        className="space-y-3 rounded border p-3"
                    >
                        <h3 className="font-semibold">
                            {t('systemSettings.policy.deployment')}
                        </h3>
                        {current.deployment ? (
                            <RuleList value={current.deployment} />
                        ) : (
                            <p role="alert">
                                {t('systemSettings.policy.deploymentInvalid')}
                            </p>
                        )}
                    </section>
                    <section
                        aria-label={t('systemSettings.policy.stored')}
                        className="space-y-3 rounded border p-3"
                    >
                        <h3 className="font-semibold">
                            {t('systemSettings.policy.stored')}
                        </h3>
                        <RuleList value={current} />
                    </section>
                </div>
            )}
            {form && canEdit && (
                <form
                    aria-label={t('systemSettings.policy.editing')}
                    className="space-y-4 rounded border bg-background p-4"
                    onSubmit={(e) => {
                        e.preventDefault()
                        void save()
                    }}
                >
                    <h3 className="font-semibold">
                        {t('systemSettings.policy.editing')}
                    </h3>
                    <p className="text-sm">
                        {t('systemSettings.policy.grantRisk')}
                    </p>
                    {current && current.version !== form.expectedVersion && (
                        <p role="alert">{t('systemSettings.policy.stale')}</p>
                    )}
                    <fieldset disabled={busy} className="space-y-3">
                        {form.networks.map((row, index) => (
                            <div
                                key={row.key}
                                className="flex flex-wrap items-end gap-3"
                            >
                                <label className="text-sm">
                                    {t('systemSettings.policy.cidr')}{' '}
                                    {index + 1}
                                    <input
                                        className={inputClass + ' mt-1 block'}
                                        value={row.cidr}
                                        maxLength={64}
                                        onChange={(e) =>
                                            edit({
                                                ...form,
                                                networks: form.networks.map(
                                                    (item) =>
                                                        item.key === row.key
                                                            ? {
                                                                  ...item,
                                                                  cidr: e.target
                                                                      .value,
                                                              }
                                                            : item,
                                                ),
                                            })
                                        }
                                    />
                                </label>
                                <label className="text-sm">
                                    {t('systemSettings.policy.port')}{' '}
                                    {index + 1}
                                    <input
                                        className={
                                            inputClass + ' mt-1 block w-28'
                                        }
                                        inputMode="numeric"
                                        value={row.port}
                                        maxLength={5}
                                        onChange={(e) =>
                                            edit({
                                                ...form,
                                                networks: form.networks.map(
                                                    (item) =>
                                                        item.key === row.key
                                                            ? {
                                                                  ...item,
                                                                  port: e.target
                                                                      .value,
                                                              }
                                                            : item,
                                                ),
                                            })
                                        }
                                    />
                                </label>
                                <Button
                                    type="button"
                                    variant="outline"
                                    onClick={() =>
                                        edit({
                                            ...form,
                                            networks: form.networks.filter(
                                                (item) => item.key !== row.key,
                                            ),
                                        })
                                    }
                                >
                                    {t('systemSettings.policy.remove')}{' '}
                                    {index + 1}
                                </Button>
                            </div>
                        ))}
                        <Button
                            type="button"
                            variant="outline"
                            disabled={form.networks.length >= 1000}
                            onClick={() =>
                                edit({
                                    ...form,
                                    networks: [...form.networks, policyRow()],
                                })
                            }
                        >
                            {t('systemSettings.policy.add')}
                        </Button>
                        <label htmlFor={localPortsId} className="block text-sm">
                            {t('systemSettings.policy.local')}
                        </label>
                        <textarea
                            id={localPortsId}
                            className={
                                inputClass + ' mt-1 block w-full font-mono'
                            }
                            rows={4}
                            maxLength={6000}
                            value={form.localPorts}
                            onChange={(e) =>
                                edit({
                                    ...form,
                                    localPorts: e.target.value,
                                })
                            }
                        />
                        <p className="text-sm text-muted-foreground">
                            {t('systemSettings.policy.localHelp')}
                        </p>
                        <label className="flex items-center gap-2 text-sm">
                            <input
                                type="checkbox"
                                checked={confirmed}
                                onChange={(e) => confirm(e.target.checked)}
                            />
                            {t('systemSettings.policy.confirm')}
                        </label>
                    </fieldset>
                    {invalid && (
                        <p role="alert">{t('systemSettings.policy.invalid')}</p>
                    )}
                    <div className="flex flex-wrap gap-3">
                        <Button type="submit" disabled={blocked || !confirmed}>
                            {t('systemSettings.policy.save')}
                        </Button>
                        <Button
                            type="button"
                            variant="outline"
                            disabled={busy}
                            onClick={() => {
                                store.setState({ form: null })
                                confirm(false)
                                setInvalid(false)
                                setError(null)
                            }}
                        >
                            {t('systemSettings.policy.cancel')}
                        </Button>
                    </div>
                </form>
            )}
        </section>
    )
}
