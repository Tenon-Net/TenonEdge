import { PageHeading } from '../../components/layout/page'
import { DeviceDeletionPanel } from './deletion-review'
import { businessKeyPattern } from '../../lib/business-key'
import { useState } from 'react'
import { useStore } from 'zustand'
import { Link, useBlocker } from '@tanstack/react-router'
import { useIsMutating, useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import { Button } from '../../components/ui/button'
import { useInputModules } from '../inputs/module-query'
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
export function DevicesPage({
    onReturn,
    onOpenInputs,
}: { onReturn?: () => void; onOpenInputs?: (id: number) => void } = {}) {
    const { t } = useTranslation()
    const activeMutations = useIsMutating()
    const role = useSession((s) => s.value?.role)
    const canEdit = role === 'administrator' || role === 'configurator'
    const modules = useInputModules()
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
    const { form, original, open, edit, rename, setKey, suggestKey } =
        useStore(editor)
    const [search, setSearch] = useState('')
    const [group, setGroup] = useState('')
    const [enabled, setEnabled] = useState('')
    const [notice, setNotice] = useState('')
    const [deletionReview, setDeletionReview] = useState(false)
    const [copyCount, setCopyCount] = useState(1)
    const dirty = form !== null && JSON.stringify(form) !== original
    useBlocker({
        disabled: !dirty && !deletionReview,
        enableBeforeUnload: dirty || deletionReview,
        shouldBlockFn: () =>
            !window.confirm(
                t(
                    deletionReview
                        ? 'devices.deletion.leaving'
                        : 'devices.discard',
                ),
            ),
    })
    const mutation = useMutation({
        retry: false,
        mutationFn: async (
            action:
                | { type: 'save'; form: DeviceForm }
                | { type: 'copy'; id: number; version: string; count: number },
        ) => {
            if (action.type === 'copy') {
                await api.POST('/api/edge-admin/v1/devices/{deviceId}/copy', {
                    params: { path: { deviceId: action.id } },
                    body: {
                        expectedVersion: action.version,
                        count: action.count,
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
        onSuccess: (type) => changed(type),
    })
    async function changed(type: 'save' | 'copy' | 'delete') {
        setDeletionReview(false)
        void queryClient.invalidateQueries({
            queryKey: ['configuration-summary'],
        })
        open(null)
        setNotice(
            t(
                type === 'save'
                    ? 'devices.saved'
                    : type === 'copy'
                      ? 'devices.copied'
                      : 'devices.deleted',
            ),
        )
        await Promise.all([
            queryClient.invalidateQueries({ queryKey }),
            queryClient.invalidateQueries({
                queryKey: ['connection-drafts'],
            }),
        ])
    }
    const busy = mutation.isPending || deletionReview
    const moduleKinds = [
        ...new Map(
            (modules.data ?? []).map((module) => [module.kind, module]),
        ).values(),
    ]
    const moduleBlocked =
        form?.kind === 'module' &&
        (!modules.isSuccess ||
            modules.isFetching ||
            !moduleKinds.some((module) => module.kind === form.moduleKind))
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
        setDeletionReview(false)
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
        <section className="product-page">
            {onReturn && (
                <Button
                    type="button"
                    variant="outline"
                    disabled={busy || activeMutations > 0}
                    onClick={() => {
                        if (!dirty || window.confirm(t('devices.discard')))
                            onReturn()
                    }}
                >
                    {t('routes.inline.return')}
                </Button>
            )}
            <PageHeading
                level={onReturn ? 2 : 1}
                description={t('devices.notice')}
                title={t('devices.title')}
            />
            {!onReturn && (
                <Link to="/runtime/devices" className="underline">
                    {t('currentValues.title')}
                </Link>
            )}

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
            <div className="flex flex-wrap gap-2">
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
            <div
                className={
                    form && query.data?.devices.length === 0
                        ? ''
                        : 'product-workbench'
                }
            >
                <aside
                    className={
                        form && query.data?.devices.length === 0
                            ? 'hidden'
                            : 'product-rail space-y-3'
                    }
                >
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
                        className="product-panel w-full max-w-5xl space-y-4"
                        onSubmit={(e) => {
                            e.preventDefault()
                            if (canEdit && !busy && !moduleBlocked)
                                mutation.mutate({
                                    type: 'save',
                                    form: { ...form },
                                })
                        }}
                    >
                        <p className="text-sm">{t('devices.immutable')}</p>
                        {form.id !== undefined &&
                            (onOpenInputs ? (
                                <Button
                                    type="button"
                                    variant="outline"
                                    disabled={busy || activeMutations > 0}
                                    onClick={() => {
                                        if (
                                            !dirty ||
                                            window.confirm(t('devices.discard'))
                                        )
                                            onOpenInputs(form.id!)
                                    }}
                                >
                                    {t('inputs.title')}
                                </Button>
                            ) : (
                                <Link
                                    to="/devices/$deviceId/inputs"
                                    params={{ deviceId: String(form.id) }}
                                    className="inline-block underline"
                                >
                                    {t('inputs.title')}
                                </Link>
                            ))}
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
                                    pattern={businessKeyPattern}
                                    onChange={(e) => setKey(e.target.value)}
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
                                        rename(
                                            e.target.value,
                                            query.data?.devices.map(
                                                (device) => device.key,
                                            ) ?? [],
                                        )
                                    }
                                />
                            </label>
                            {form.id === undefined && (
                                <div className="space-y-2 md:col-span-2">
                                    <p className="text-sm text-muted-foreground">
                                        {t('devices.keySuggestion')}
                                    </p>
                                    <Button
                                        type="button"
                                        variant="outline"
                                        disabled={!form.name.trim()}
                                        onClick={() =>
                                            suggestKey(
                                                query.data?.devices.map(
                                                    (device) => device.key,
                                                ) ?? [],
                                            )
                                        }
                                    >
                                        {t('devices.suggestKey')}
                                    </Button>
                                </div>
                            )}
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
                                    {deviceKinds
                                        .filter(
                                            (kind) =>
                                                kind !== 'module' ||
                                                moduleKinds.length > 0 ||
                                                form.kind === 'module',
                                        )
                                        .map((kind) => (
                                            <option key={kind} value={kind}>
                                                {t('devices.kinds.' + kind)}
                                            </option>
                                        ))}
                                </select>
                            </label>
                            {form.kind === 'module' && (
                                <div className="space-y-2">
                                    <label className="block">
                                        {t('devices.moduleKind')}
                                        <select
                                            className={fieldClass}
                                            value={form.moduleKind ?? ''}
                                            disabled={
                                                !modules.isSuccess ||
                                                modules.isFetching
                                            }
                                            onChange={(event) =>
                                                patch({
                                                    moduleKind:
                                                        event.target.value ||
                                                        null,
                                                })
                                            }
                                        >
                                            <option value="">
                                                {t('parameters.select')}
                                            </option>
                                            {form.moduleKind &&
                                                !moduleKinds.some(
                                                    (module) =>
                                                        module.kind ===
                                                        form.moduleKind,
                                                ) && (
                                                    <option
                                                        value={form.moduleKind}
                                                    >
                                                        {form.moduleKind} —{' '}
                                                        {t(
                                                            'parameters.unknown',
                                                        )}
                                                    </option>
                                                )}
                                            {moduleKinds.map((module) => (
                                                <option
                                                    key={module.kind}
                                                    value={module.kind}
                                                >
                                                    {module.displayName} (
                                                    {module.kind})
                                                </option>
                                            ))}
                                        </select>
                                    </label>
                                    {moduleBlocked && (
                                        <p role="alert">
                                            {t('inputs.moduleUnavailable')}
                                        </p>
                                    )}
                                    <Button
                                        type="button"
                                        variant="outline"
                                        disabled={modules.isFetching}
                                        onClick={() => void modules.refetch()}
                                    >
                                        {t('inputs.moduleRefresh')}
                                    </Button>
                                </div>
                            )}
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
                            {form.kind !== 'simulator' &&
                                form.kind !== 'module' && (
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
                                            {(
                                                connections.data?.connections ??
                                                []
                                            )
                                                .filter(
                                                    (c) => c.kind === form.kind,
                                                )
                                                .map((c) => (
                                                    <option
                                                        key={c.id}
                                                        value={c.id}
                                                    >
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
                        {canEdit && form.id !== undefined && (
                            <fieldset
                                disabled={busy || dirty || !coherent}
                                className="space-y-2 rounded border p-3"
                            >
                                <p className="text-sm text-muted-foreground">
                                    {t('devices.copyHelp')}
                                </p>
                                <div className="flex items-end gap-3">
                                    <label>
                                        {t('devices.copyCount')}
                                        <select
                                            className={fieldClass}
                                            value={copyCount}
                                            onChange={(e) =>
                                                setCopyCount(
                                                    Number(e.target.value),
                                                )
                                            }
                                        >
                                            {Array.from(
                                                { length: 20 },
                                                (_, index) => index + 1,
                                            ).map((count) => (
                                                <option
                                                    key={count}
                                                    value={count}
                                                >
                                                    {count}
                                                </option>
                                            ))}
                                        </select>
                                    </label>
                                    <Button
                                        type="button"
                                        variant="outline"
                                        onClick={() =>
                                            mutation.mutate({
                                                type: 'copy',
                                                id: form.id!,
                                                version: form.expectedVersion,
                                                count: copyCount,
                                            })
                                        }
                                    >
                                        {t('devices.copy')}
                                    </Button>
                                </div>
                            </fieldset>
                        )}
                        <div className="flex flex-wrap gap-2">
                            {canEdit && (
                                <Button
                                    type="submit"
                                    disabled={busy || moduleBlocked}
                                >
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
                                            disabled={
                                                busy || dirty || !coherent
                                            }
                                            onClick={() =>
                                                setDeletionReview(true)
                                            }
                                        >
                                            {t('devices.delete')}
                                        </Button>
                                    )}
                                </>
                            )}
                        </div>
                        {deletionReview && form.id !== undefined && (
                            <DeviceDeletionPanel
                                id={form.id}
                                version={form.expectedVersion}
                                onDeleted={() => changed('delete')}
                                onClose={() => setDeletionReview(false)}
                            />
                        )}
                    </form>
                )}
            </div>
        </section>
    )
}
