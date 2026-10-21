import { PageHeading } from '../../components/layout/page'
import { businessKeyPattern } from '../../lib/business-key'
import { HttpPreviewPanel } from './http-preview-panel'
import { ModbusPreviewPanel } from './modbus-preview-panel'
import { PointCsvPanel } from './csv-panel'
import { JsonSourceEditor } from './json-editor'
import { useInputModules, useModuleConnections } from './module-query'
import { ModuleSourceEditor, validModuleForm } from './module-editor'
import { useState } from 'react'
import { useStore } from 'zustand'
import { createStore } from 'zustand/vanilla'
import { useBlocker, useParams } from '@tanstack/react-router'
import { useIsMutating, useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import { Button } from '../../components/ui/button'
import { PointEditor } from '../points/editor'
import { createPointEditor } from '../points/store'
import {
    inputForm,
    inputRows,
    inputRequest,
    inputRequestJson,
    decodeInputList,
    type Input,
    type InputForm,
    type InputRequest,
} from './model'
import type { AddressMode } from '../points/model'

const fieldClass =
    'mt-1 w-full rounded border bg-background p-2 disabled:opacity-60'
export function InputsPage() {
    const { deviceId } = useParams({ from: '/devices/$deviceId/inputs' })
    return <DeviceInputsPage key={deviceId} routeId={deviceId} />
}
export function DeviceInputsPage({
    routeId,
    onReturn,
}: {
    routeId: string
    onReturn?: () => void
}) {
    const deviceId = Number(routeId)
    const { t } = useTranslation()
    const activeMutations = useIsMutating()
    const role = useSession((s) => s.value?.role)
    const canEdit = role === 'administrator' || role === 'configurator'
    const modules = useInputModules()
    const queryKey = ['input-drafts', deviceId]
    const query = useQuery({
        queryKey,
        retry: false,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/devices/{deviceId}/inputs',
                { params: { path: { deviceId } }, signal, parseAs: 'text' },
            )
            if (!data) throw new Error('missing-inputs')
            return decodeInputList(data)
        },
    })
    const devices = useQuery({
        queryKey: ['device-drafts'],
        queryFn: async ({ signal }) => {
            const { data } = await api.GET('/api/edge-admin/v1/devices', {
                signal,
            })
            if (!data) throw new Error('missing-devices')
            return data
        },
    })
    const device = devices.data?.devices.find((d) => Number(d.id) === deviceId)
    const connections = useModuleConnections(device?.kind === 'module')
    const connectionChoices = (connections.data?.connections ?? []).map(
        (connection) => ({
            id: String(connection.id),
            kind:
                connection.kind === 'module'
                    ? (connection.module?.kind ?? '')
                    : connection.kind,
            name: `${connection.name} (${connection.key})`,
        }),
    )
    const coherent =
        device && query.data && query.data.version === devices.data?.version
    const [editor] = useState(() =>
        createStore<{
            form: InputForm | null
            original: string
            generation: number
        }>(() => ({ form: null, original: '', generation: 0 })),
    )
    const [points] = useState(createPointEditor)
    const { form, original, generation } = useStore(editor)
    const { rows, mode } = useStore(points)
    const [csvDirty, setCsvDirty] = useState(false)
    const dirty = form !== null && JSON.stringify({ form, rows }) !== original
    const hasUnsavedWork = dirty || csvDirty
    const [notice, setNotice] = useState('')
    const [invalid, setInvalid] = useState(false)
    useBlocker({
        disabled: !hasUnsavedWork,
        enableBeforeUnload: hasUnsavedWork,
        shouldBlockFn: () => !window.confirm(t('inputs.discard')),
    })
    const mutation = useMutation({
        retry: false,
        mutationFn: async (
            action:
                | {
                      type: 'save'
                      id?: number
                      body: InputRequest
                      serialized?: string
                  }
                | { type: 'delete'; id: number; version: string },
        ) => {
            if (action.type === 'delete')
                await api.DELETE(
                    '/api/edge-admin/v1/devices/{deviceId}/inputs/{inputId}',
                    {
                        params: {
                            path: { deviceId, inputId: action.id },
                            query: { expectedVersion: action.version },
                        },
                    },
                )
            else if (action.id === undefined)
                await api.POST('/api/edge-admin/v1/devices/{deviceId}/inputs', {
                    params: { path: { deviceId } },
                    body: action.body,
                    bodySerializer:
                        action.serialized === undefined
                            ? undefined
                            : () => action.serialized!,
                })
            else
                await api.PUT(
                    '/api/edge-admin/v1/devices/{deviceId}/inputs/{inputId}',
                    {
                        params: { path: { deviceId, inputId: action.id } },
                        body: action.body,
                        bodySerializer:
                            action.serialized === undefined
                                ? undefined
                                : () => action.serialized!,
                    },
                )
            return action.type
        },
        onSuccess: async (type) => {
            void queryClient.invalidateQueries({
                queryKey: ['configuration-summary'],
            })
            editor.setState({ form: null })
            setNotice(t(type === 'save' ? 'inputs.saved' : 'inputs.deleted'))
            await Promise.all([
                queryClient.invalidateQueries({ queryKey }),
                queryClient.invalidateQueries({ queryKey: ['device-drafts'] }),
                queryClient.invalidateQueries({
                    queryKey: ['connection-drafts'],
                }),
            ])
        },
    })
    function open(input?: Input) {
        if (
            !coherent ||
            mutation.isPending ||
            (dirty && !window.confirm(t('inputs.discard')))
        )
            return
        const nextForm = inputForm(
            query.data!.version,
            input,
            device!.kind,
            device!.moduleKind,
        )
        const nextMode: AddressMode =
            device?.addressNotation === 'manual' ? 'manual' : 'offset'
        const nextRows = inputRows(input, nextMode)
        points.getState().load(nextRows, nextMode)
        editor.setState({
            form: nextForm,
            original: JSON.stringify({ form: nextForm, rows: nextRows }),
            generation: generation + 1,
        })
        mutation.reset()
        setNotice('')
        setInvalid(false)
    }
    function save() {
        if (!canEdit || !form || mutation.isPending || moduleBlocked) return
        try {
            const body = inputRequest(form, rows, mode)
            setInvalid(false)
            mutation.mutate({
                type: 'save',
                id: form.id,
                body,
                serialized: inputRequestJson(form, body),
            })
        } catch {
            setInvalid(true)
        }
    }
    const patch = (value: Partial<InputForm>) => {
        if (form) editor.setState({ form: { ...form, ...value } })
    }
    const selectedModule = modules.data?.find(
        (item) =>
            item.kind === form?.moduleKind &&
            Number(item.version) === form?.moduleVersion,
    )
    const needsConnections =
        selectedModule?.parameters.parameters?.some(
            (field) => field.type === 'connectionReference',
        ) === true
    const connectionsReady =
        connections.isSuccess &&
        !connections.isFetching &&
        connections.data.version === form?.version
    const moduleBlocked =
        form?.kind === 'module' &&
        (!modules.isSuccess ||
            modules.isFetching ||
            (needsConnections && !connectionsReady) ||
            !validModuleForm(form, modules.data ?? [], connectionChoices))
    return (
        <section className="product-page">
            {onReturn && (
                <Button
                    type="button"
                    variant="outline"
                    disabled={activeMutations > 0}
                    onClick={() => {
                        if (
                            !hasUnsavedWork ||
                            window.confirm(t('inputs.discard'))
                        )
                            onReturn()
                    }}
                >
                    {t('routes.inline.backDevices')}
                </Button>
            )}
            <PageHeading
                level={onReturn ? 2 : 1}
                description={t('inputs.notice')}
                title={t('inputs.title')}
            />
            <p>
                {t('inputs.device', {
                    name: device?.name ?? '—',
                    key: device?.key ?? routeId,
                })}
            </p>

            <ErrorMessage
                error={query.error ?? devices.error ?? mutation.error}
            />
            {invalid && <p role="alert">{t('inputs.invalid')}</p>}
            {mutation.error instanceof ApiError &&
                mutation.error.status === 409 && (
                    <p role="alert">{t('inputs.conflict')}</p>
                )}
            {!coherent && !query.isPending && !devices.isPending && (
                <p role="alert">{t('inputs.missing')}</p>
            )}
            {notice && <p role="status">{notice}</p>}
            <div className="flex flex-wrap gap-2">
                <Button
                    variant="outline"
                    disabled={
                        mutation.isPending ||
                        query.isFetching ||
                        devices.isFetching
                    }
                    onClick={() => {
                        void query.refetch()
                        void devices.refetch()
                        if (device?.kind === 'module')
                            void connections.refetch()
                    }}
                >
                    {t('inputs.refresh')}
                </Button>
                {canEdit && device && (
                    <Button
                        disabled={!coherent || mutation.isPending}
                        onClick={() => open()}
                    >
                        {t(
                            device?.kind === 'modbus'
                                ? 'inputs.new'
                                : 'inputs.newGeneric',
                        )}
                    </Button>
                )}
            </div>
            <p className="text-xs text-muted-foreground">
                {t('inputs.version', { version: query.data?.version ?? '—' })}
            </p>
            {!query.data?.inputs.length && !form && <p>{t('inputs.empty')}</p>}
            <div className="product-toolbar">
                {query.data?.inputs.map((input) => (
                    <Button
                        key={input.id}
                        variant="outline"
                        disabled={!coherent || mutation.isPending}
                        onClick={() => open(input)}
                    >
                        {input.name} ({input.key}) ·{' '}
                        {input.kind === 'modbus'
                            ? t('inputs.points', {
                                  count: input.modbus?.points.length ?? 0,
                                  period: input.modbus?.periodMs,
                              })
                            : t('devices.kinds.' + input.kind)}
                    </Button>
                ))}
            </div>
            {form && (
                <section className="product-panel space-y-4">
                    <form
                        onSubmit={(e) => {
                            e.preventDefault()
                            save()
                        }}
                        className="space-y-3"
                    >
                        <fieldset
                            disabled={!canEdit || mutation.isPending}
                            className="grid gap-3 md:grid-cols-4"
                        >
                            <label>
                                {t('inputs.key')}
                                <input
                                    className={fieldClass}
                                    required
                                    maxLength={64}
                                    pattern={businessKeyPattern}
                                    disabled={form.id !== undefined}
                                    value={form.key}
                                    onChange={(e) =>
                                        patch({ key: e.target.value })
                                    }
                                />
                            </label>
                            <label>
                                {t('inputs.name')}
                                <input
                                    className={fieldClass}
                                    required
                                    maxLength={64}
                                    value={form.name}
                                    onChange={(e) =>
                                        patch({ name: e.target.value })
                                    }
                                />
                            </label>
                            {form.kind !== 'mqtt' && form.kind !== 'module' && (
                                <>
                                    <label>
                                        {t('inputs.period')}
                                        <input
                                            className={fieldClass}
                                            required
                                            type="number"
                                            min={1000}
                                            max={86400000}
                                            step={1}
                                            value={form.period}
                                            onChange={(e) =>
                                                patch({
                                                    period: Number(
                                                        e.target.value,
                                                    ),
                                                })
                                            }
                                        />
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
                                {t('inputs.enabled')}
                            </label>
                        </fieldset>
                        <div className="flex flex-wrap gap-2">
                            {canEdit && (
                                <Button
                                    type="submit"
                                    disabled={
                                        mutation.isPending || moduleBlocked
                                    }
                                >
                                    {t('inputs.save')}
                                </Button>
                            )}
                            <Button
                                type="button"
                                variant="outline"
                                disabled={mutation.isPending}
                                onClick={() => {
                                    if (
                                        !dirty ||
                                        window.confirm(t('inputs.discard'))
                                    )
                                        editor.setState({ form: null })
                                }}
                            >
                                {t('inputs.close')}
                            </Button>
                            {form.id !== undefined && (
                                <>
                                    <Button
                                        type="button"
                                        variant="outline"
                                        disabled={
                                            !coherent || mutation.isPending
                                        }
                                        onClick={() => {
                                            const input =
                                                query.data?.inputs.find(
                                                    (i) =>
                                                        Number(i.id) ===
                                                        form.id,
                                                )
                                            if (input) open(input)
                                        }}
                                    >
                                        {t('inputs.reload')}
                                    </Button>
                                    {canEdit && (
                                        <Button
                                            type="button"
                                            variant="outline"
                                            disabled={mutation.isPending}
                                            onClick={() => {
                                                if (
                                                    window.confirm(
                                                        t(
                                                            'inputs.confirmDelete',
                                                        ),
                                                    )
                                                )
                                                    mutation.mutate({
                                                        type: 'delete',
                                                        id: form.id!,
                                                        version: form.version,
                                                    })
                                            }}
                                        >
                                            {t('inputs.delete')}
                                        </Button>
                                    )}
                                </>
                            )}
                        </div>
                    </form>
                    {form.kind === 'modbus' ? (
                        <PointEditor
                            key={generation}
                            store={points}
                            managed
                            readOnly={!canEdit || mutation.isPending}
                        />
                    ) : form.kind === 'module' ? (
                        <ModuleSourceEditor
                            key={generation}
                            form={form}
                            patch={patch}
                            catalog={modules}
                            connections={connectionChoices}
                            connectionsReady={connectionsReady}
                            refreshConnections={() =>
                                void connections.refetch()
                            }
                            readOnly={!canEdit || mutation.isPending}
                        />
                    ) : (
                        <JsonSourceEditor
                            deviceId={deviceId}
                            key={generation}
                            form={form}
                            patch={patch}
                            readOnly={!canEdit || mutation.isPending}
                        />
                    )}
                    {form.kind === 'modbus' && canEdit && (
                        <ModbusPreviewPanel
                            key={`preview:${generation}`}
                            deviceId={deviceId}
                            version={form.version}
                            inputId={form.id}
                            source={() =>
                                inputRequest(form, rows, mode).modbus!
                            }
                            blocked={mutation.isPending || !coherent}
                            onOrder={
                                form.id === undefined
                                    ? undefined
                                    : (key, order) => {
                                          const row = rows.find(
                                              (point) => point.key === key,
                                          )
                                          if (!row) return
                                          const next = rows.map((point) =>
                                              point.id === row.id
                                                  ? {
                                                        ...point,
                                                        byteOrder: order,
                                                    }
                                                  : point,
                                          )
                                          try {
                                              const body = inputRequest(
                                                  form,
                                                  next,
                                                  mode,
                                              )
                                              points
                                                  .getState()
                                                  .edit(
                                                      row.id,
                                                      'byteOrder',
                                                      order,
                                                  )
                                              mutation.mutate({
                                                  type: 'save',
                                                  id: form.id,
                                                  body,
                                              })
                                          } catch {
                                              setInvalid(true)
                                          }
                                      }
                            }
                        />
                    )}
                </section>
            )}
            <div className="product-diagnostic-grid">
                {canEdit && coherent && device?.kind === 'modbus' && (
                    <ModbusPreviewPanel
                        key={`device:${query.data!.version}`}
                        deviceId={deviceId}
                        version={query.data!.version}
                        blocked={mutation.isPending}
                    />
                )}
                {canEdit && coherent && device?.kind === 'http' && (
                    <HttpPreviewPanel
                        key={`http:${query.data!.version}`}
                        deviceId={deviceId}
                        version={query.data!.version}
                        blocked={mutation.isPending}
                    />
                )}
                {canEdit && device?.kind === 'modbus' && query.data && (
                    <PointCsvPanel
                        deviceId={deviceId}
                        version={query.data.version}
                        blocked={
                            !coherent || form !== null || mutation.isPending
                        }
                        canEdit={canEdit}
                        onDirtyChange={setCsvDirty}
                    />
                )}
            </div>
        </section>
    )
}
