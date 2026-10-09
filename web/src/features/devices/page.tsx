import { useState } from 'react'
import { useStore } from 'zustand'
import { useBlocker } from '@tanstack/react-router'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import { Button } from '../../components/ui/button'
import {
    changeDeviceKind,
    createDeviceEditor,
    deviceForm,
    deviceKinds,
    type Device,
    type DeviceForm,
} from './model'

const queryKey = ['device-drafts']
const fieldClass =
    'mt-1 w-full rounded border bg-background p-2 disabled:opacity-60'
export function DevicesPage() {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    const canEdit = role === 'administrator' || role === 'configurator'
    const query = useQuery({
        queryKey,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/devices', {
                signal,
            })
            if (!data) throw new Error('missing-devices')
            return data
        },
    })
    const connections = useQuery({
        queryKey: ['connection-drafts'],
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/connections', {
                signal,
            })
            if (!data) throw new Error('missing-connections')
            return data
        },
    })
    const [editor] = useState(createDeviceEditor)
    const { form, original, open, edit } = useStore(editor)
    const [search, setSearch] = useState('')
    const [group, setGroup] = useState('')
    const [enabled, setEnabled] = useState('')
    const [notice, setNotice] = useState('')
    const dirty = form !== null && JSON.stringify(form) !== original
    useBlocker({
        disabled: !dirty,
        enableBeforeUnload: dirty,
        shouldBlockFn: () => !window.confirm(t('devices.discard')),
    })
    const mutation = useMutation({
        retry: false,
        mutationFn: async (
            action:
                | { type: 'save'; form: DeviceForm }
                | { type: 'delete'; device: Device; version: string },
        ) => {
            if (action.type === 'delete') {
                await api.DELETE('/api/edge-admin/v1/devices/{deviceId}', {
                    params: {
                        path: { deviceId: Number(action.device.id) },
                        query: { expectedVersion: action.version },
                    },
                })
            } else {
                const { id, ...body } = action.form
                if (id === undefined)
                    await api.POST('/api/edge-admin/v1/devices', { body })
                else
                    await api.PUT('/api/edge-admin/v1/devices/{deviceId}', {
                        params: { path: { deviceId: id } },
                        body,
                    })
            }
            return action.type
        },
        onSuccess: async (type) => {
            open(null)
            setNotice(t(type === 'save' ? 'devices.saved' : 'devices.deleted'))
            await Promise.all([
                queryClient.invalidateQueries({ queryKey }),
                queryClient.invalidateQueries({
                    queryKey: ['connection-drafts'],
                }),
            ])
        },
    })
    const busy = mutation.isPending
    const coherent =
        !!query.data &&
        !!connections.data &&
        query.data.version === connections.data.version
    function select(device?: Device) {
        if (
            !coherent ||
            busy ||
            (dirty && !window.confirm(t('devices.discard')))
        )
            return
        open(deviceForm(query.data!.version, device))
        setNotice('')
        mutation.reset()
    }
    const groups = [
        ...new Set(
            (query.data?.devices ?? [])
                .map((d) => d.group)
                .filter((g): g is string => !!g),
        ),
    ]
    const list = (query.data?.devices ?? []).filter(
        (d) =>
            (d.name + ' ' + d.key)
                .toLowerCase()
                .includes(search.toLowerCase()) &&
            (!group || d.group === group) &&
            (!enabled || d.enabled === (enabled === 'yes')),
    )
    const patch = (value: Partial<DeviceForm>) => {
        if (form) edit({ ...form, ...value })
    }
    return (
        <section className="space-y-4">
            <h1 className="text-2xl font-semibold">{t('devices.title')}</h1>
            <p className="text-sm text-muted-foreground">
                {t('devices.notice')}
            </p>
            <ErrorMessage
                error={query.error ?? connections.error ?? mutation.error}
            />
            {mutation.error instanceof ApiError &&
                mutation.error.status === 409 && (
                    <p role="alert">{t('devices.conflict')}</p>
                )}
            {query.data && connections.data && !coherent && (
                <p role="alert">{t('devices.versionsDiffer')}</p>
            )}
            {notice && <p role="status">{notice}</p>}
            <div className="flex gap-2">
                <Button
                    variant="outline"
                    disabled={
                        busy || query.isFetching || connections.isFetching
                    }
                    onClick={() => {
                        void query.refetch()
                        void connections.refetch()
                    }}
                >
                    {t('devices.refresh')}
                </Button>
                {canEdit && (
                    <Button
                        disabled={!coherent || busy}
                        onClick={() => select()}
                    >
                        {t('devices.new')}
                    </Button>
                )}
            </div>
            <p className="text-xs text-muted-foreground">
                {t('devices.version', { version: query.data?.version ?? '—' })}
            </p>
            <div className="grid gap-5 lg:grid-cols-[20rem_1fr]">
                <aside className="space-y-3">
                    <label className="block">
                        {t('devices.search')}
                        <input
                            className={fieldClass}
                            value={search}
                            onChange={(e) => setSearch(e.target.value)}
                        />
                    </label>
                    <label className="block">
                        {t('devices.groupFilter')}
                        <select
                            className={fieldClass}
                            value={group}
                            onChange={(e) => setGroup(e.target.value)}
                        >
                            <option value="">{t('devices.all')}</option>
                            {groups.map((g) => (
                                <option key={g}>{g}</option>
                            ))}
                        </select>
                    </label>
                    <label className="block">
                        {t('devices.stateFilter')}
                        <select
                            className={fieldClass}
                            value={enabled}
                            onChange={(e) => setEnabled(e.target.value)}
                        >
                            <option value="">{t('devices.all')}</option>
                            <option value="yes">{t('devices.enabled')}</option>
                            <option value="no">{t('devices.disabled')}</option>
                        </select>
                    </label>
                    {!list.length && <p>{t('devices.empty')}</p>}
                    <ul className="space-y-2">
                        {list.map((d) => (
                            <li key={d.id}>
                                <button
                                    className="w-full rounded border p-3 text-left hover:bg-accent"
                                    disabled={busy || !coherent}
                                    onClick={() => select(d)}
                                    aria-pressed={form?.id === Number(d.id)}
                                >
                                    <strong className="block">{d.name}</strong>
                                    <span className="block text-sm">
                                        {d.key} · {t('devices.kinds.' + d.kind)}
                                    </span>
                                    <span className="text-xs">
                                        {t(
                                            d.enabled
                                                ? 'devices.enabled'
                                                : 'devices.disabled',
                                        )}
                                    </span>
                                </button>
                            </li>
                        ))}
                    </ul>
                </aside>
                {form && (
                    <form
                        className="space-y-4 rounded border p-4"
                        onSubmit={(e) => {
                            e.preventDefault()
                            if (canEdit && !busy)
                                mutation.mutate({
                                    type: 'save',
                                    form: { ...form },
                                })
                        }}
                    >
                        <p className="text-sm">{t('devices.immutable')}</p>
                        {!canEdit && <p>{t('devices.readOnly')}</p>}
                        <fieldset
                            disabled={!canEdit || busy}
                            className="grid gap-3 md:grid-cols-2"
                        >
                            <label>
                                {t('devices.key')}
                                <input
                                    className={fieldClass}
                                    value={form.key}
                                    required
                                    maxLength={64}
                                    disabled={form.id !== undefined}
                                    pattern="[A-Za-z0-9][A-Za-z0-9_.-]{0,63}"
                                    onChange={(e) =>
                                        patch({ key: e.target.value })
                                    }
                                />
                            </label>
                            <label>
                                {t('devices.name')}
                                <input
                                    className={fieldClass}
                                    value={form.name}
                                    required
                                    maxLength={64}
                                    onChange={(e) =>
                                        patch({ name: e.target.value })
                                    }
                                />
                            </label>
                            <label>
                                {t('devices.kind')}
                                <select
                                    className={fieldClass}
                                    value={form.kind}
                                    onChange={(e) =>
                                        edit(
                                            changeDeviceKind(
                                                form,
                                                e.target
                                                    .value as DeviceForm['kind'],
                                            ),
                                        )
                                    }
                                >
                                    {deviceKinds.map((kind) => (
                                        <option key={kind} value={kind}>
                                            {t('devices.kinds.' + kind)}
                                        </option>
                                    ))}
                                </select>
                            </label>
                            <label>
                                {t('devices.group')}
                                <input
                                    className={fieldClass}
                                    value={form.group ?? ''}
                                    maxLength={64}
                                    onChange={(e) =>
                                        patch({ group: e.target.value })
                                    }
                                />
                            </label>
                            {form.kind !== 'simulator' && (
                                <label>
                                    {t('devices.connection')}
                                    <select
                                        className={fieldClass}
                                        required
                                        value={form.connectionId ?? ''}
                                        onChange={(e) =>
                                            patch({
                                                connectionId: e.target.value
                                                    ? Number(e.target.value)
                                                    : null,
                                            })
                                        }
                                    >
                                        <option value="">
                                            {t('devices.chooseConnection')}
                                        </option>
                                        {(connections.data?.connections ?? [])
                                            .filter((c) => c.kind === form.kind)
                                            .map((c) => (
                                                <option key={c.id} value={c.id}>
                                                    {c.name} ({c.key})
                                                </option>
                                            ))}
                                    </select>
                                </label>
                            )}
                            {form.kind === 'modbus' && (
                                <>
                                    <label>
                                        {t('devices.unitId')}
                                        <input
                                            className={fieldClass}
                                            type="number"
                                            min={0}
                                            max={255}
                                            step={1}
                                            required
                                            value={form.unitId ?? ''}
                                            onChange={(e) =>
                                                patch({
                                                    unitId: e.target.value
                                                        ? Number(e.target.value)
                                                        : null,
                                                })
                                            }
                                        />
                                    </label>
                                    <label>
                                        {t('devices.notation')}
                                        <select
                                            className={fieldClass}
                                            value={form.addressNotation}
                                            onChange={(e) =>
                                                patch({
                                                    addressNotation: e.target
                                                        .value as DeviceForm['addressNotation'],
                                                })
                                            }
                                        >
                                            <option value="offset">
                                                {t('devices.offset')}
                                            </option>
                                            <option value="manual">
                                                {t('devices.manual')}
                                            </option>
                                        </select>
                                    </label>
                                </>
                            )}
                            <label className="flex items-center gap-2">
                                <input
                                    type="checkbox"
                                    checked={form.enabled}
                                    onChange={(e) =>
                                        patch({ enabled: e.target.checked })
                                    }
                                />
                                {t('devices.enabled')}
                            </label>
                        </fieldset>
                        <div className="flex flex-wrap gap-2">
                            {canEdit && (
                                <Button type="submit" disabled={busy}>
                                    {t('devices.save')}
                                </Button>
                            )}
                            <Button
                                type="button"
                                variant="outline"
                                disabled={busy}
                                onClick={() => {
                                    if (
                                        !dirty ||
                                        window.confirm(t('devices.discard'))
                                    )
                                        open(null)
                                }}
                            >
                                {t('devices.close')}
                            </Button>
                            {form.id !== undefined && (
                                <>
                                    <Button
                                        type="button"
                                        variant="outline"
                                        disabled={busy || !coherent}
                                        onClick={() => {
                                            const device =
                                                query.data?.devices.find(
                                                    (d) =>
                                                        Number(d.id) ===
                                                        form.id,
                                                )
                                            if (device) select(device)
                                        }}
                                    >
                                        {t('devices.reload')}
                                    </Button>
                                    {canEdit && (
                                        <Button
                                            type="button"
                                            variant="outline"
                                            disabled={busy}
                                            onClick={() => {
                                                const device =
                                                    query.data?.devices.find(
                                                        (d) =>
                                                            Number(d.id) ===
                                                            form.id,
                                                    )
                                                if (
                                                    device &&
                                                    window.confirm(
                                                        t(
                                                            'devices.confirmDelete',
                                                            {
                                                                name: device.name,
                                                            },
                                                        ),
                                                    )
                                                )
                                                    mutation.mutate({
                                                        type: 'delete',
                                                        device,
                                                        version:
                                                            form.expectedVersion,
                                                    })
                                            }}
                                        >
                                            {t('devices.delete')}
                                        </Button>
                                    )}
                                </>
                            )}
                        </div>
                    </form>
                )}
            </div>
        </section>
    )
}
