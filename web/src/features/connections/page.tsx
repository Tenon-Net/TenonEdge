import { PageEmpty, PageHeading } from '../../components/layout/page'
import { ModuleCredentialPanel } from './module-credential-panel'
import { ModuleConnectionEditor } from './module-editor'
import { ConnectionDeletionReview } from './deletion-review'
import { moduleConnectionForm, readConnectionList } from './module-model'
import { ConnectionTable } from './list'
import { connectionRows, connectionSnapshotCurrent } from './list-model'
import { ParameterFields } from '../parameters/fields'
import { parameterIssues } from '../parameters/model'
import {
    changeConnectionParameter,
    connectionParameterValues,
    initializeConnectionParameters,
} from './parameters'
import { businessKeyPattern } from '../../lib/business-key'
import { CredentialPanel } from './credential-panel'
import { MqttTestPanel } from './mqtt-test-panel'
import { ModbusTestPanel } from './modbus-test-panel'
import { HttpTestPanel } from './http-test-panel'
import { ConnectionOutboundPanel } from './outbound-panel'
import { outboundTarget, type OutboundTarget } from './outbound-api'
import { useStore } from 'zustand'
import { createConnectionEditor } from './store'
import { useBlocker } from '@tanstack/react-router'
import { useState } from 'react'
import { useIsMutating, useMutation, useQuery } from '@tanstack/react-query'
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

function useParameterSchema(kind: 'modbus' | 'mqtt') {
    return useQuery({
        queryKey: ['parameter-schema', kind],
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/parameter-schemas/{id}',
                {
                    signal,
                    params: { path: { id: `connection.${kind}` } },
                },
            )
            if (
                !data ||
                data.id !== `connection.${kind}` ||
                data.scope !== 'connection' ||
                !data.parameters?.length
            )
                throw new Error('missing-parameter-schema')
            return data
        },
    })
}
// This cache includes lexical module parameters; do not share the plain DTO query shape.
const queryKey = ['connection-drafts', 'exact']
const fieldClass =
    'mt-1 w-full rounded-md border bg-background p-2 disabled:opacity-60'
export function ConnectionsPage({ onReturn }: { onReturn?: () => void } = {}) {
    const { t } = useTranslation()
    const activeMutations = useIsMutating()
    const session = useSession((state) => state.value)
    const canEdit =
        session?.role === 'administrator' || session?.role === 'configurator'
    const query = useQuery({
        queryKey,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/connections', {
                signal,
                parseAs: 'text',
            })
            if (!data) throw new Error('missing-connections')
            return readConnectionList(data)
        },
    })
    const modules = useQuery({
        queryKey: ['connection-modules'],
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/connection-modules',
                { signal },
            )
            if (!data) throw new Error('missing-connection-modules')
            return data
        },
    })
    const runtime = useQuery({
        queryKey: ['runtime-connections', query.data?.version],
        enabled: !!query.data,
        refetchInterval: 5000,
        refetchIntervalInBackground: false,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/connections',
                {
                    signal,
                    params: {
                        query: { expectedDraftVersion: query.data!.version },
                    },
                },
            )
            if (!data) throw new Error('missing-runtime-connections')
            return data
        },
    })
    const current = connectionSnapshotCurrent(
        query.data?.version,
        runtime.data,
        !!runtime.error || !!query.error,
    )
    const [editor] = useState(createConnectionEditor)
    const { form, setForm, moduleForm, setModuleForm, original, setOriginal } =
        useStore(editor)
    const modbusSchema = useParameterSchema('modbus')
    const mqttSchema = useParameterSchema('mqtt')
    const schemaQuery = form?.kind === 'mqtt' ? mqttSchema : modbusSchema
    const schemaReady =
        !!schemaQuery.data && !schemaQuery.error && !schemaQuery.isFetching
    const [showParameterIssues, setShowParameterIssues] = useState(false)
    const scalarIssues =
        form && form.kind !== 'http' && schemaQuery.data
            ? parameterIssues(
                  schemaQuery.data,
                  connectionParameterValues(form, schemaQuery.data),
              )
            : []
    const [search, setSearch] = useState('')
    const [kind, setKind] = useState('')
    const [notice, setNotice] = useState('')
    const [testGeneration, setTestGeneration] = useState(0)
    const [localError, setLocalError] = useState('')
    const [savedTarget, setSavedTarget] = useState<OutboundTarget | null>(null)
    const [deleting, setDeleting] = useState<Connection | null>(null)
    const [outboundBusy, setOutboundBusy] = useState(false)
    const [moduleBusy, setModuleBusy] = useState(false)
    const [credentialBusy, setCredentialBusy] = useState(false)
    const [credentialDirty, setCredentialDirty] = useState(false)
    // Keep credential material bound to the explicitly opened draft, not to a
    // replacement row from a background/explicit list refresh.
    const [credentialConnection, setCredentialConnection] =
        useState<Connection | null>(null)
    const moduleDirty =
        moduleForm !== null && JSON.stringify(moduleForm) !== original
    const draftDirty =
        (form !== null && JSON.stringify(form) !== original) || moduleDirty
    const dirty = draftDirty || credentialDirty
    const moduleDefinition = modules.data?.find(
        (item) =>
            item.kind === moduleForm?.kind &&
            Number(item.version) === moduleForm?.moduleVersion,
    )
    const moduleReady = !!modules.data && !modules.error && !modules.isFetching
    const editorBusy = moduleBusy || credentialBusy
    useBlocker({
        disabled: !dirty,
        enableBeforeUnload: dirty,
        shouldBlockFn: () => !window.confirm(t('connections.discard')),
    })
    const mutation = useMutation({
        mutationFn: async (form: ConnectionForm) => {
            const body = connectionRequest(form)
            if (form.id !== undefined)
                await api.PUT('/api/edge-admin/v1/connections/{connectionId}', {
                    params: {
                        path: { connectionId: Number(form.id) },
                    },
                    body,
                })
            else await api.POST('/api/edge-admin/v1/connections', { body })
        },
        onSuccess: async (_, form) => {
            void queryClient.invalidateQueries({
                queryKey: ['configuration-summary'],
            })
            setNotice(t('connections.saved'))
            setForm(null)
            setModuleForm(null)
            setOriginal('')
            // Inspect only after the draft was saved; no automatic target connection or policy write.
            setSavedTarget(outboundTarget(form))
            await queryClient.invalidateQueries({
                queryKey: ['connection-drafts'],
            })
        },
    })
    function open(connection?: Connection, extended = false) {
        if (outboundBusy || editorBusy) return
        if (!query.data || (dirty && !window.confirm(t('connections.discard'))))
            return
        setTestGeneration((value) => value + 1)
        setSavedTarget(null)
        if (connection?.kind === 'module' || extended) {
            const parameters = connection
                ? query.data.exactParameters[connection.id]
                : '{}'
            if (parameters === undefined) {
                setLocalError(t('moduleConnections.exactMissing'))
                return
            }
            const next = moduleConnectionForm(
                query.data.version,
                connection,
                parameters,
            )
            setForm(null)
            setModuleForm(next)
            setOriginal(JSON.stringify(next))
            setNotice('')
            setLocalError('')
            mutation.reset()
            return
        }
        setModuleForm(null)
        if (
            !connection &&
            (!modbusSchema.data ||
                modbusSchema.error ||
                modbusSchema.isFetching)
        )
            return
        const base = connectionForm(query.data.version, connection)
        const next =
            !connection && modbusSchema.data
                ? initializeConnectionParameters(base, modbusSchema.data)
                : base
        setShowParameterIssues(false)
        setCredentialConnection(connection ?? null)
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
                    pattern={key === 'key' ? businessKeyPattern : undefined}
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
    const entries = connectionRows(
        query.data?.connections ?? [],
        runtime.data?.connections ?? [],
        search,
        kind,
    )
    return (
        <section className="product-page">
            {onReturn && (
                <Button
                    type="button"
                    variant="outline"
                    disabled={
                        activeMutations > 0 ||
                        outboundBusy ||
                        editorBusy ||
                        deleting !== null
                    }
                    onClick={() => {
                        if (!dirty || window.confirm(t('connections.discard')))
                            onReturn()
                    }}
                >
                    {t('routes.inline.return')}
                </Button>
            )}
            <PageHeading
                level={onReturn ? 2 : 1}
                description={
                    <>
                        <p>{t('connections.notice')}</p>
                        <p>{t('connections.security')}</p>
                        <p>{t('connections.runtime.notice')}</p>
                    </>
                }
                title={t('connections.title')}
            />

            <div className="product-toolbar">
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
                        aria-label={t('connections.filter')}
                        className={fieldClass}
                        value={kind}
                        onChange={(event) => setKind(event.target.value)}
                    >
                        <option value="">{t('connections.all')}</option>
                        {(['modbus', 'mqtt', 'http', 'module'] as const).map(
                            (value) => (
                                <option key={value} value={value}>
                                    {t(`connections.kinds.${value}`)}
                                </option>
                            ),
                        )}
                    </select>
                </label>
                <Button
                    variant="outline"
                    disabled={
                        query.isFetching || mutation.isPending || editorBusy
                    }
                    onClick={() => {
                        void query.refetch()
                        void runtime.refetch()
                    }}
                >
                    {t('connections.refresh')}
                </Button>
                {canEdit && (
                    <Button
                        disabled={
                            !query.data ||
                            mutation.isPending ||
                            outboundBusy ||
                            editorBusy ||
                            !modbusSchema.data ||
                            !!modbusSchema.error ||
                            modbusSchema.isFetching
                        }
                        onClick={() => open()}
                    >
                        {t('connections.add')}
                    </Button>
                )}
                {canEdit && (
                    <Button
                        variant="outline"
                        disabled={
                            !query.data ||
                            !!query.error ||
                            mutation.isPending ||
                            outboundBusy ||
                            editorBusy ||
                            !moduleReady
                        }
                        onClick={() => open(undefined, true)}
                    >
                        {t('moduleConnections.add')}
                    </Button>
                )}
            </div>
            <ErrorMessage error={modules.error} />
            {modules.error && (
                <Button
                    variant="outline"
                    disabled={modules.isFetching}
                    onClick={() => void modules.refetch()}
                >
                    {t('moduleConnections.definitions')}
                </Button>
            )}
            <ErrorMessage error={query.error ?? mutation.error} />
            <ErrorMessage error={runtime.error} />
            {!current && (
                <p role="alert">{t('connections.runtime.unavailable')}</p>
            )}
            {current && runtime.data && (
                <p className="text-xs text-muted-foreground">
                    {t('connections.runtime.snapshot', {
                        revision: runtime.data.revision ?? '—',
                        time: new Date(
                            runtime.data.capturedAt,
                        ).toLocaleString(),
                    })}
                </p>
            )}
            {current && runtime.data && !runtime.data.queueAvailable && (
                <p role="alert">{t('connections.runtime.queueUnavailable')}</p>
            )}

            {[modbusSchema, mqttSchema].map(
                (metadata, index) =>
                    metadata.error && (
                        <div key={index} role="alert">
                            <p>
                                {index === 0 ? 'Modbus' : 'MQTT'}:{' '}
                                {t('parameters.unavailable')}
                            </p>
                            <ErrorMessage error={metadata.error} />
                            <Button
                                variant="outline"
                                disabled={metadata.isFetching}
                                onClick={() => void metadata.refetch()}
                            >
                                {t('parameters.refresh')}
                            </Button>
                        </div>
                    ),
            )}
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
            {query.data &&
                current &&
                !entries.length &&
                !form &&
                !moduleForm && <PageEmpty>{t('connections.empty')}</PageEmpty>}
            <div
                className={`product-split ${entries.length && (form || moduleForm || deleting || savedTarget) ? 'has-detail' : ''}`}
            >
                <div className="product-list">
                    {!!entries.length && (
                        <ConnectionTable
                            rows={entries}
                            current={current}
                            canEdit={canEdit}
                            busy={
                                mutation.isPending || outboundBusy || editorBusy
                            }
                            onOpen={open}
                            onDelete={(connection) => {
                                if (
                                    !dirty ||
                                    window.confirm(t('connections.discard'))
                                )
                                    setDeleting(connection)
                            }}
                        />
                    )}
                </div>
                <div className="product-detail">
                    {savedTarget && (
                        <ConnectionOutboundPanel
                            target={savedTarget}
                            onBusy={setOutboundBusy}
                        />
                    )}
                    {deleting && query.data && (
                        <ConnectionDeletionReview
                            connection={deleting}
                            version={query.data.version}
                            onDeleted={async () => {
                                // The review owns the single DELETE. This callback only
                                // refreshes local/query state after its confirmed success.
                                setDeleting(null)
                                mutation.reset()
                                setNotice(t('connections.deleted'))
                                setForm(null)
                                setModuleForm(null)
                                setOriginal('')
                                setSavedTarget(null)
                                void queryClient.invalidateQueries({
                                    queryKey: ['configuration-summary'],
                                })
                                await queryClient.invalidateQueries({
                                    queryKey: ['connection-drafts'],
                                })
                            }}
                            onClose={() => setDeleting(null)}
                        />
                    )}
                    {moduleForm && (
                        <>
                            <ModuleConnectionEditor
                                key={testGeneration}
                                form={moduleForm}
                                definitions={modules.data ?? []}
                                ready={moduleReady && !query.error}
                                connectionsReady={
                                    query.isSuccess && !query.isFetching
                                }
                                connections={(
                                    query.data?.connections ?? []
                                ).map((item) => ({
                                    id: String(item.id),
                                    kind: item.module?.kind ?? item.kind,
                                    name: `${item.name} · ${item.key}`,
                                }))}
                                canEdit={canEdit}
                                blocked={
                                    credentialBusy ||
                                    credentialDirty ||
                                    mutation.isPending
                                }
                                onChange={setModuleForm}
                                onBusy={setModuleBusy}
                                onSaved={async () => {
                                    setModuleForm(null)
                                    setOriginal('')
                                    setNotice(t('connections.saved'))
                                    await queryClient.invalidateQueries({
                                        queryKey: ['connection-drafts'],
                                    })
                                }}
                                onClose={() => {
                                    if (
                                        !dirty ||
                                        window.confirm(t('connections.discard'))
                                    ) {
                                        setModuleForm(null)
                                        setOriginal('')
                                    }
                                }}
                                onReload={async () => {
                                    if (
                                        dirty &&
                                        !window.confirm(
                                            t('connections.discard'),
                                        )
                                    )
                                        return
                                    const result = await query.refetch()
                                    if (!result.data || result.error) return
                                    const connection =
                                        result.data.connections.find(
                                            (item) => item.id === moduleForm.id,
                                        )
                                    const parameters = connection
                                        ? result.data.exactParameters[
                                              connection.id
                                          ]
                                        : '{}'
                                    if (
                                        (moduleForm.id && !connection) ||
                                        parameters === undefined
                                    ) {
                                        setLocalError(
                                            t('moduleConnections.exactMissing'),
                                        )
                                        return
                                    }
                                    const next = moduleConnectionForm(
                                        result.data.version,
                                        connection,
                                        parameters,
                                    )
                                    setModuleForm(next)
                                    setOriginal(JSON.stringify(next))
                                    setTestGeneration((value) => value + 1)
                                }}
                            />
                            {moduleForm.id &&
                                moduleDefinition?.parameters.parameters?.some(
                                    (field) => field.type === 'credential',
                                ) &&
                                (!moduleDirty &&
                                moduleForm.version === query.data?.version ? (
                                    <ModuleCredentialPanel
                                        key={`${testGeneration}:${moduleForm.id}:${moduleForm.version}`}
                                        id={Number(moduleForm.id)}
                                        draftVersion={moduleForm.version}
                                        blocked={!moduleReady || !!query.error}
                                        definition={moduleDefinition}
                                        onBusy={setCredentialBusy}
                                        onDirty={setCredentialDirty}
                                    />
                                ) : (
                                    <p>{t('moduleConnections.saveFirst')}</p>
                                ))}
                        </>
                    )}
                    {form && (
                        <form
                            className="product-panel space-y-4"
                            onSubmit={(event) => {
                                event.preventDefault()
                                if (!canEdit || credentialDirty || editorBusy)
                                    return
                                if (form.kind !== 'http') {
                                    if (!schemaReady) return
                                    setShowParameterIssues(true)
                                    if (scalarIssues.length) return
                                }
                                try {
                                    connectionRequest(form)
                                    setLocalError('')
                                    mutation.mutate(form)
                                } catch {
                                    setLocalError(
                                        t('connections.duplicateHeaders'),
                                    )
                                }
                            }}
                        >
                            <p>{t('connections.immutable')}</p>
                            <fieldset
                                disabled={
                                    !canEdit ||
                                    mutation.isPending ||
                                    credentialDirty ||
                                    editorBusy
                                }
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
                                            const metadata =
                                                value === 'modbus'
                                                    ? modbusSchema
                                                    : mqttSchema
                                            if (
                                                value !== 'http' &&
                                                (!metadata.data ||
                                                    metadata.error ||
                                                    metadata.isFetching)
                                            )
                                                return
                                            const next = {
                                                ...form,
                                                kind: value,
                                            }
                                            setForm(
                                                value === 'http'
                                                    ? next
                                                    : initializeConnectionParameters(
                                                          next,
                                                          metadata.data!,
                                                      ),
                                            )
                                            setShowParameterIssues(false)
                                        }}
                                    >
                                        {(
                                            ['modbus', 'mqtt', 'http'] as const
                                        ).map((value) => (
                                            <option
                                                key={value}
                                                value={value}
                                                disabled={
                                                    value !== 'http' &&
                                                    !(
                                                        value === 'modbus'
                                                            ? modbusSchema
                                                            : mqttSchema
                                                    ).isSuccess
                                                }
                                            >
                                                {t(
                                                    `connections.kinds.${value}`,
                                                )}
                                            </option>
                                        ))}
                                    </select>
                                </label>
                                <label className="flex items-center gap-2">
                                    <input
                                        type="checkbox"
                                        checked={form.enabled}
                                        onChange={(event) =>
                                            edit(
                                                'enabled',
                                                event.target.checked,
                                            )
                                        }
                                    />
                                    {t('connections.enabled')}
                                </label>
                                {form.kind !== 'http' && schemaQuery.data && (
                                    <ParameterFields
                                        schema={schemaQuery.data}
                                        values={connectionParameterValues(
                                            form,
                                            schemaQuery.data,
                                        )}
                                        onChange={(name, value) =>
                                            setForm(
                                                (current) =>
                                                    current &&
                                                    changeConnectionParameter(
                                                        current,
                                                        name,
                                                        value,
                                                    ),
                                            )
                                        }
                                        hidden={
                                            form.kind === 'modbus' &&
                                            form.format === 'tcp'
                                                ? ['rtuSilenceMs']
                                                : []
                                        }
                                        issues={
                                            showParameterIssues
                                                ? scalarIssues
                                                : []
                                        }
                                    />
                                )}
                                {form.kind === 'http' &&
                                    numberField('timeout', 100, 60000)}
                                {form.kind === 'http' &&
                                    textField('baseAddress', true, 2048)}
                            </fieldset>
                            {form.kind !== 'http' && (
                                <div>
                                    {schemaQuery.isFetching && (
                                        <p role="status">
                                            {t('parameters.loading')}
                                        </p>
                                    )}
                                    <Button
                                        type="button"
                                        variant="outline"
                                        disabled={
                                            schemaQuery.isFetching ||
                                            mutation.isPending
                                        }
                                        onClick={() =>
                                            void schemaQuery.refetch()
                                        }
                                    >
                                        {t('parameters.refresh')}
                                    </Button>
                                </div>
                            )}
                            {form.kind === 'http' && (
                                <fieldset
                                    disabled={
                                        !canEdit ||
                                        mutation.isPending ||
                                        credentialDirty ||
                                        editorBusy
                                    }
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
                                                        form.headers.map(
                                                            (item, i) =>
                                                                i === index
                                                                    ? {
                                                                          ...item,
                                                                          name: event
                                                                              .target
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
                                                        form.headers.map(
                                                            (item, i) =>
                                                                i === index
                                                                    ? {
                                                                          ...item,
                                                                          value: event
                                                                              .target
                                                                              .value,
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
                                                            (_, i) =>
                                                                i !== index,
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
                            <div className="flex flex-wrap gap-2">
                                {canEdit && (
                                    <Button
                                        type="submit"
                                        disabled={
                                            mutation.isPending ||
                                            credentialDirty ||
                                            editorBusy ||
                                            outboundBusy ||
                                            (form.kind !== 'http' &&
                                                !schemaReady)
                                        }
                                    >
                                        {t('connections.save')}
                                    </Button>
                                )}
                                <Button
                                    type="button"
                                    variant="outline"
                                    disabled={
                                        mutation.isPending ||
                                        outboundBusy ||
                                        editorBusy
                                    }
                                    onClick={() => {
                                        if (
                                            !dirty ||
                                            window.confirm(
                                                t('connections.discard'),
                                            )
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
                                            mutation.isPending ||
                                            query.isFetching
                                        }
                                        onClick={() => {
                                            const entry =
                                                query.data?.connections.find(
                                                    (item) =>
                                                        item.id === form.id,
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
                    {form?.kind === 'modbus' && canEdit && (
                        <ModbusTestPanel key={testGeneration} form={form} />
                    )}
                    {form?.kind === 'http' && canEdit && (
                        <HttpTestPanel
                            key={testGeneration}
                            form={form}
                            savedConnection={
                                !dirty && form.version === query.data?.version
                                    ? query.data.connections.find(
                                          (item) => item.id === form.id,
                                      )
                                    : undefined
                            }
                        />
                    )}
                    {form?.kind === 'mqtt' && canEdit && (
                        <MqttTestPanel
                            key={testGeneration}
                            form={form}
                            savedConnection={
                                !dirty && form.version === query.data?.version
                                    ? query.data.connections.find(
                                          (item) => item.id === form.id,
                                      )
                                    : undefined
                            }
                        />
                    )}
                    {form?.id !== undefined &&
                        form.kind !== 'modbus' &&
                        (!draftDirty &&
                        credentialConnection &&
                        credentialConnection.id === form.id ? (
                            <CredentialPanel
                                key={`${form.id}:${form.version}:${testGeneration}`}
                                connection={credentialConnection}
                                draftVersion={form.version}
                                draftCurrent={
                                    form.version === query.data?.version &&
                                    !query.error &&
                                    !query.isFetching
                                }
                                onDirty={setCredentialDirty}
                                onBusy={setCredentialBusy}
                            />
                        ) : (
                            <p>{t('credentials.saveDraftFirst')}</p>
                        ))}
                </div>
            </div>
        </section>
    )
}
