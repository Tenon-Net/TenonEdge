import { CredentialPanel } from './credential-panel'
import { MqttTestPanel } from './mqtt-test-panel'
import { HttpTestPanel } from './http-test-panel'
import { useStore } from 'zustand'
import { createConnectionEditor } from './store'
import { useBlocker } from '@tanstack/react-router'
import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import { Button } from '../../components/ui/button'
import {
    connectionForm,
    connectionRequest,
    type Connection,
    type ConnectionForm,
} from './model'

const queryKey = ['connection-drafts']
const fieldClass =
    'mt-1 w-full rounded-md border bg-background p-2 disabled:opacity-60'
export function ConnectionsPage() {
    const { t } = useTranslation()
    const session = useSession((state) => state.value)
    const canEdit =
        session?.role === 'administrator' || session?.role === 'configurator'
    const query = useQuery({
        queryKey,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/connections', {
                signal,
            })
            if (!data) throw new Error('missing-connections')
            return data
        },
    })
    const [editor] = useState(createConnectionEditor)
    const { form, setForm, original, setOriginal } = useStore(editor)
    const [search, setSearch] = useState('')
    const [kind, setKind] = useState('')
    const [notice, setNotice] = useState('')
    const [testGeneration, setTestGeneration] = useState(0)
    const [localError, setLocalError] = useState('')
    const dirty = form !== null && JSON.stringify(form) !== original
    useBlocker({
        disabled: !dirty,
        enableBeforeUnload: dirty,
        shouldBlockFn: () => !window.confirm(t('connections.discard')),
    })
    const mutation = useMutation({
        mutationFn: async (
            action:
                | { type: 'save'; form: ConnectionForm }
                | { type: 'delete'; connection: Connection; version: string },
        ) => {
            if (action.type === 'delete') {
                await api.DELETE(
                    '/api/edge-admin/v1/connections/{connectionId}',
                    {
                        params: {
                            path: {
                                connectionId: Number(action.connection.id),
                            },
                            query: { expectedVersion: action.version },
                        },
                    },
                )
            } else {
                const body = connectionRequest(action.form)
                if (action.form.id !== undefined)
                    await api.PUT(
                        '/api/edge-admin/v1/connections/{connectionId}',
                        {
                            params: {
                                path: { connectionId: Number(action.form.id) },
                            },
                            body,
                        },
                    )
                else await api.POST('/api/edge-admin/v1/connections', { body })
            }
            return action.type
        },
        onSuccess: async (type) => {
            setNotice(
                t(
                    type === 'save'
                        ? 'connections.saved'
                        : 'connections.deleted',
                ),
            )
            setForm(null)
            setOriginal('')
            await queryClient.invalidateQueries({ queryKey })
        },
    })
    function open(connection?: Connection) {
        if (!query.data || (dirty && !window.confirm(t('connections.discard'))))
            return
        setTestGeneration((value) => value + 1)
        const next = connectionForm(query.data.version, connection)
        setForm(next)
        setOriginal(JSON.stringify(next))
        setNotice('')
        setLocalError('')
        mutation.reset()
    }
    function edit<K extends keyof ConnectionForm>(
        key: K,
        value: ConnectionForm[K],
    ) {
        setForm((current) => current && { ...current, [key]: value })
    }
    function textField(
        key: 'key' | 'name' | 'host' | 'clientId' | 'baseAddress',
        required = true,
        maxLength = 253,
    ) {
        return (
            <label className="block">
                {t(`connections.${key}`)}
                <input
                    className={fieldClass}
                    value={String(form![key])}
                    onChange={(event) => edit(key, event.target.value)}
                    required={required}
                    maxLength={maxLength}
                    disabled={key === 'key' && form!.id !== undefined}
                    pattern={
                        key === 'key'
                            ? '[A-Za-z0-9][A-Za-z0-9_.-]{0,63}'
                            : undefined
                    }
                />
            </label>
        )
    }
    function numberField(
        key:
            | 'port'
            | 'connectTimeout'
            | 'readTimeout'
            | 'interval'
            | 'rtuSilence'
            | 'timeout'
            | 'ping',
        min: number,
        max: number,
    ) {
        return (
            <label className="block">
                {t(`connections.${key}`)}
                <input
                    className={fieldClass}
                    type="number"
                    value={form![key]}
                    onChange={(event) => edit(key, event.target.value)}
                    required
                    min={min}
                    max={max}
                    step={1}
                />
            </label>
        )
    }
    const entries =
        query.data?.connections.filter(
            (connection) =>
                (!kind || connection.kind === kind) &&
                `${connection.name} ${connection.key}`
                    .toLowerCase()
                    .includes(search.toLowerCase()),
        ) ?? []
    return (
        <section className="space-y-4">
            <h1 className="text-2xl font-semibold">{t('connections.title')}</h1>
            <p className="text-sm text-muted-foreground">
                {t('connections.notice')}
            </p>
            <p className="text-sm text-muted-foreground">
                {t('connections.security')}
            </p>
            <div className="flex flex-wrap items-end gap-3">
                <label>
                    {t('connections.search')}
                    <input
                        className={fieldClass}
                        value={search}
                        onChange={(event) => setSearch(event.target.value)}
                    />
                </label>
                <label>
                    {t('connections.filter')}
                    <select
                        className={fieldClass}
                        value={kind}
                        onChange={(event) => setKind(event.target.value)}
                    >
                        <option value="">{t('connections.all')}</option>
                        {(['modbus', 'mqtt', 'http'] as const).map((value) => (
                            <option key={value} value={value}>
                                {t(`connections.kinds.${value}`)}
                            </option>
                        ))}
                    </select>
                </label>
                <Button
                    variant="outline"
                    disabled={query.isFetching || mutation.isPending}
                    onClick={() => void query.refetch()}
                >
                    {t('connections.refresh')}
                </Button>
                {canEdit && (
                    <Button
                        disabled={!query.data || mutation.isPending}
                        onClick={() => open()}
                    >
                        {t('connections.add')}
                    </Button>
                )}
            </div>
            <ErrorMessage error={query.error ?? mutation.error} />
            {mutation.error instanceof ApiError &&
                mutation.error.status === 409 && (
                    <p role="alert">{t('connections.conflict')}</p>
                )}
            {localError && <p role="alert">{localError}</p>}
            {notice && <p role="status">{notice}</p>}
            {query.data && (
                <p className="text-xs text-muted-foreground">
                    {t('connections.version', { version: query.data.version })}
                </p>
            )}
            {query.isPending && <p>{t('loading')}</p>}
            {query.data && !query.data.connections.length && (
                <p>{t('connections.empty')}</p>
            )}
            {!!entries.length && (
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead>
                            <tr>
                                {[
                                    'key',
                                    'name',
                                    'kind',
                                    'target',
                                    'enabled',
                                    'actions',
                                ].map((field) => (
                                    <th className="border-b p-2" key={field}>
                                        {t(`connections.${field}`)}
                                    </th>
                                ))}
                            </tr>
                        </thead>
                        <tbody>
                            {entries.map((connection) => (
                                <tr key={connection.id}>
                                    <td className="border-b p-2">
                                        {connection.key}
                                    </td>
                                    <td className="border-b p-2">
                                        {connection.name}
                                    </td>
                                    <td className="border-b p-2">
                                        {t(
                                            `connections.kinds.${connection.kind}`,
                                        )}
                                    </td>
                                    <td className="border-b p-2">
                                        {connection.http?.baseAddress ??
                                            `${connection.modbus?.host ?? connection.mqtt?.host}:${connection.modbus?.port ?? connection.mqtt?.port}`}
                                    </td>
                                    <td className="border-b p-2">
                                        {t(
                                            connection.enabled
                                                ? 'connections.enabled'
                                                : 'connections.disabled',
                                        )}
                                    </td>
                                    <td className="space-x-2 border-b p-2">
                                        <Button
                                            variant="outline"
                                            disabled={mutation.isPending}
                                            onClick={() => open(connection)}
                                        >
                                            {t(
                                                canEdit
                                                    ? 'connections.edit'
                                                    : 'connections.view',
                                            )}
                                        </Button>
                                        {canEdit && (
                                            <Button
                                                variant="outline"
                                                disabled={mutation.isPending}
                                                onClick={() => {
                                                    if (
                                                        (!dirty ||
                                                            window.confirm(
                                                                t(
                                                                    'connections.discard',
                                                                ),
                                                            )) &&
                                                        window.confirm(
                                                            t(
                                                                'connections.deleteConfirm',
                                                            ),
                                                        )
                                                    )
                                                        mutation.mutate({
                                                            type: 'delete',
                                                            connection,
                                                            version:
                                                                query.data!
                                                                    .version,
                                                        })
                                                }}
                                            >
                                                {t('connections.remove')}
                                            </Button>
                                        )}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
            {form && (
                <form
                    className="space-y-4 rounded-md border p-4"
                    onSubmit={(event) => {
                        event.preventDefault()
                        if (!canEdit) return
                        try {
                            connectionRequest(form)
                            setLocalError('')
                            mutation.mutate({ type: 'save', form })
                        } catch {
                            setLocalError(t('connections.duplicateHeaders'))
                        }
                    }}
                >
                    <p>{t('connections.immutable')}</p>
                    <fieldset
                        disabled={!canEdit || mutation.isPending}
                        className="grid gap-4 md:grid-cols-2"
                    >
                        {textField('key', true, 64)}
                        {textField('name', true, 64)}
                        <label>
                            {t('connections.kind')}
                            <select
                                className={fieldClass}
                                value={form.kind}
                                disabled={form.id !== undefined}
                                onChange={(event) => {
                                    const value = event.target
                                        .value as ConnectionForm['kind']
                                    edit('kind', value)
                                    edit(
                                        'port',
                                        value === 'modbus'
                                            ? '502'
                                            : form.tls
                                              ? '8883'
                                              : '1883',
                                    )
                                }}
                            >
                                {(['modbus', 'mqtt', 'http'] as const).map(
                                    (value) => (
                                        <option key={value} value={value}>
                                            {t(`connections.kinds.${value}`)}
                                        </option>
                                    ),
                                )}
                            </select>
                        </label>
                        <label className="flex items-center gap-2">
                            <input
                                type="checkbox"
                                checked={form.enabled}
                                onChange={(event) =>
                                    edit('enabled', event.target.checked)
                                }
                            />
                            {t('connections.enabled')}
                        </label>
                        {form.kind !== 'http' && (
                            <>
                                {textField('host')}
                                {numberField('port', 1, 65535)}
                            </>
                        )}
                        {form.kind === 'modbus' && (
                            <>
                                <label>
                                    {t('connections.format')}
                                    <select
                                        className={fieldClass}
                                        value={form.format}
                                        onChange={(event) =>
                                            edit(
                                                'format',
                                                event.target
                                                    .value as ConnectionForm['format'],
                                            )
                                        }
                                    >
                                        {(['tcp', 'rtuOverTcp'] as const).map(
                                            (value) => (
                                                <option
                                                    key={value}
                                                    value={value}
                                                >
                                                    {t(
                                                        `connections.formats.${value}`,
                                                    )}
                                                </option>
                                            ),
                                        )}
                                    </select>
                                </label>
                                {numberField('connectTimeout', 100, 60000)}
                                {numberField('readTimeout', 100, 60000)}
                                {numberField('interval', 0, 60000)}
                                {form.format === 'rtuOverTcp' &&
                                    numberField('rtuSilence', 1, 60000)}
                            </>
                        )}
                        {form.kind === 'mqtt' && (
                            <>
                                <label className="flex items-center gap-2">
                                    <input
                                        type="checkbox"
                                        checked={form.tls}
                                        onChange={(event) => {
                                            edit('tls', event.target.checked)
                                            if (
                                                ['1883', '8883'].includes(
                                                    form.port,
                                                )
                                            )
                                                edit(
                                                    'port',
                                                    event.target.checked
                                                        ? '8883'
                                                        : '1883',
                                                )
                                        }}
                                    />
                                    {t('connections.tls')}
                                </label>
                                {textField('clientId', false, 128)}
                                {numberField('ping', 1000, 60000)}
                            </>
                        )}
                        {form.kind !== 'modbus' &&
                            numberField('timeout', 100, 60000)}
                        {form.kind === 'http' &&
                            textField('baseAddress', true, 2048)}
                    </fieldset>
                    {form.kind === 'http' && (
                        <fieldset
                            disabled={!canEdit || mutation.isPending}
                            className="space-y-2"
                        >
                            <legend>{t('connections.headers')}</legend>
                            {form.headers.map((header, index) => (
                                <div key={index} className="flex gap-2">
                                    <input
                                        className={fieldClass}
                                        aria-label={t(
                                            'connections.headerName',
                                            { index: index + 1 },
                                        )}
                                        value={header.name}
                                        required
                                        onChange={(event) =>
                                            edit(
                                                'headers',
                                                form.headers.map((item, i) =>
                                                    i === index
                                                        ? {
                                                              ...item,
                                                              name: event.target
                                                                  .value,
                                                          }
                                                        : item,
                                                ),
                                            )
                                        }
                                    />
                                    <input
                                        className={fieldClass}
                                        aria-label={t(
                                            'connections.headerValue',
                                            { index: index + 1 },
                                        )}
                                        value={header.value}
                                        onChange={(event) =>
                                            edit(
                                                'headers',
                                                form.headers.map((item, i) =>
                                                    i === index
                                                        ? {
                                                              ...item,
                                                              value: event
                                                                  .target.value,
                                                          }
                                                        : item,
                                                ),
                                            )
                                        }
                                    />
                                    <Button
                                        type="button"
                                        variant="outline"
                                        onClick={() =>
                                            edit(
                                                'headers',
                                                form.headers.filter(
                                                    (_, i) => i !== index,
                                                ),
                                            )
                                        }
                                    >
                                        {t('connections.removeHeader', {
                                            index: index + 1,
                                        })}
                                    </Button>
                                </div>
                            ))}
                            <Button
                                type="button"
                                variant="outline"
                                disabled={form.headers.length >= 64}
                                onClick={() =>
                                    edit('headers', [
                                        ...form.headers,
                                        { name: '', value: '' },
                                    ])
                                }
                            >
                                {t('connections.addHeader')}
                            </Button>
                        </fieldset>
                    )}
                    <p className="text-sm text-muted-foreground">
                        {t('connections.addressNotice')}
                    </p>
                    <div className="flex gap-2">
                        {canEdit && (
                            <Button type="submit" disabled={mutation.isPending}>
                                {t('connections.save')}
                            </Button>
                        )}
                        <Button
                            type="button"
                            variant="outline"
                            disabled={mutation.isPending}
                            onClick={() => {
                                if (
                                    !dirty ||
                                    window.confirm(t('connections.discard'))
                                )
                                    setForm(null)
                            }}
                        >
                            {t('connections.cancel')}
                        </Button>
                        {form.id !== undefined && (
                            <Button
                                type="button"
                                variant="outline"
                                disabled={
                                    mutation.isPending || query.isFetching
                                }
                                onClick={() => {
                                    const entry = query.data?.connections.find(
                                        (item) => item.id === form.id,
                                    )
                                    if (entry) open(entry)
                                }}
                            >
                                {t('connections.reload')}
                            </Button>
                        )}
                    </div>
                </form>
            )}
            {form?.kind === 'http' && canEdit && (
                <HttpTestPanel key={testGeneration} form={form} />
            )}
            {form?.kind === 'mqtt' && canEdit && (
                <MqttTestPanel key={testGeneration} form={form} />
            )}
            {form?.id !== undefined &&
                form.kind !== 'modbus' &&
                (!dirty && form.version === query.data?.version ? (
                    <CredentialPanel
                        key={`${form.id}:${form.version}`}
                        connection={query.data.connections.find(
                            (item) => item.id === form.id,
                        )!}
                        draftVersion={form.version}
                    />
                ) : (
                    <p>{t('credentials.saveDraftFirst')}</p>
                ))}
        </section>
    )
}
