import { businessKeyPattern } from '../../lib/business-key'
import { createPreviewDraft } from './preview-model'
import { useState } from 'react'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { useBlocker } from '@tanstack/react-router'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import { Button } from '../../components/ui/button'
import { CanvasEditor } from '../canvas/editor'
import { createGraphEditor } from '../canvas/store'
import { createRouteApi, newRoute, type RouteForm } from './api'

const routes = createRouteApi(api)
const queryKey = ['route-drafts']
const inputClass = 'block w-full rounded border bg-background p-2'
export function RoutesPage() {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const canEdit = role === 'administrator' || role === 'configurator'
    const query = useQuery({
        queryKey,
        queryFn: ({ signal }) => routes.list(signal),
    })
    const [canvas] = useState(createGraphEditor)
    const [previewDraft] = useState(createPreviewDraft)
    const document = useStore(canvas, (state) => state.document)
    const [editor] = useState(() =>
        createStore<{ form: RouteForm | null; original: string }>(() => ({
            form: null,
            original: '',
        })),
    )
    const { form, original } = useStore(editor)
    const [generation, setGeneration] = useState(0)
    const [notice, setNotice] = useState('')
    const [pendingConfig, setPendingConfig] = useState(false)
    const [copyKey, setCopyKey] = useState('')
    const [copyName, setCopyName] = useState('')
    const dirty =
        form !== null &&
        (pendingConfig || JSON.stringify({ ...form, document }) !== original)
    useBlocker({
        disabled: !dirty,
        enableBeforeUnload: dirty,
        shouldBlockFn: () => !window.confirm(t('routes.discard')),
    })
    function load(next: RouteForm | null, keepSample = false) {
        if (!keepSample) previewDraft.setState({ source: '', sample: '' })
        setPendingConfig(false)
        if (next) canvas.getState().load(next.document)
        editor.setState({
            form: next,
            original: next ? JSON.stringify(next) : '',
        })
        setGeneration((value) => value + 1)
        setCopyKey('')
        setCopyName('')
    }
    const mutation = useMutation({
        mutationFn: async (
            action:
                | { type: 'read'; id: number }
                | { type: 'save'; form: RouteForm }
                | { type: 'copy'; form: RouteForm; key: string; name: string }
                | { type: 'delete'; form: RouteForm },
        ) => {
            if (action.type === 'read')
                return { type: action.type, form: await routes.read(action.id) }
            if (action.type === 'save')
                return {
                    type: action.type,
                    form: await routes.save(action.form),
                }
            if (action.type === 'copy')
                return {
                    type: action.type,
                    form: await routes.copy(
                        action.form.id!,
                        action.form.version,
                        action.key,
                        action.name,
                    ),
                }
            await routes.remove(action.form.id!, action.form.version)
            return { type: action.type, form: null }
        },
        onSuccess: async (result) => {
            void queryClient.invalidateQueries({
                queryKey: ['configuration-summary'],
            })
            load(result.form, result.type === 'save')
            setNotice(
                result.type === 'read'
                    ? ''
                    : t(
                          result.type === 'save'
                              ? 'routes.saved'
                              : result.type === 'copy'
                                ? 'routes.copied'
                                : 'routes.deleted',
                      ),
            )
            compile.reset()
            await queryClient.invalidateQueries({ queryKey })
        },
    })
    const compile = useMutation({
        mutationFn: (version: string) => routes.compile(version),
    })
    const busy = mutation.isPending || compile.isPending
    function open(id?: number) {
        if (
            !query.data ||
            busy ||
            (dirty && !window.confirm(t('routes.discard')))
        )
            return
        mutation.reset()
        compile.reset()
        setNotice('')
        if (id === undefined) load(newRoute(query.data.version))
        else mutation.mutate({ type: 'read', id })
    }
    function edit(
        key: 'key' | 'name' | 'description' | 'enabled',
        value: string | boolean,
    ) {
        if (form && canEdit && !busy)
            editor.setState({ form: { ...form, [key]: value } })
    }
    return (
        <section className="space-y-4">
            <h1 className="text-2xl font-semibold">{t('routes.title')}</h1>
            <p>{t('routes.notice')}</p>
            {!canEdit && <p>{t('routes.readOnly')}</p>}
            <ErrorMessage error={query.error} />
            <Button
                variant="outline"
                disabled={busy || query.isFetching}
                onClick={() => void query.refetch()}
            >
                {t('retry')}
            </Button>
            <Button
                disabled={!canEdit || busy || !query.data}
                onClick={() => open()}
            >
                {t('routes.new')}
            </Button>
            <ul className="space-y-2">
                {query.data?.routes.map((route) => (
                    <li
                        key={String(route.id)}
                        className="flex items-center gap-3 rounded border p-2"
                    >
                        <div className="min-w-0 flex-1 space-y-1">
                            <div className="font-medium">
                                {route.name} · <code>{route.key}</code> ·{' '}
                                {t('routes.nodes', { count: route.nodeCount })}
                            </div>
                            <p className="text-sm text-muted-foreground">
                                {route.description ||
                                    t('routes.list.descriptionEmpty')}
                            </p>
                            <div className="flex flex-wrap gap-2 text-xs">
                                <span>
                                    {t(
                                        route.enabled
                                            ? 'routes.list.enabled'
                                            : 'routes.list.disabled',
                                    )}
                                </span>
                                {route.effective && (
                                    <span role="status">
                                        {t('routes.list.effective')}
                                    </span>
                                )}
                                {route.hasErrors && (
                                    <span role="alert">
                                        {t('routes.list.errors')}
                                    </span>
                                )}
                                {route.hasUnappliedChanges && (
                                    <span role="status">
                                        {t('routes.list.unapplied')}
                                    </span>
                                )}
                                <span>
                                    {t('routes.list.updatedBy', {
                                        user: route.updatedBy || t('unknown'),
                                    })}
                                </span>
                                <time
                                    dateTime={new Date(
                                        Number(route.updatedAt),
                                    ).toISOString()}
                                >
                                    {t('routes.list.updatedAt', {
                                        time: new Date(
                                            Number(route.updatedAt),
                                        ).toLocaleString(),
                                    })}
                                </time>
                            </div>
                        </div>
                        <Button
                            variant="outline"
                            disabled={busy}
                            onClick={() => open(Number(route.id))}
                            aria-label={`${t('routes.open')} ${route.name}`}
                        >
                            {t('routes.open')}
                        </Button>
                    </li>
                ))}
            </ul>
            {query.data?.routes.length === 0 && <p>{t('routes.empty')}</p>}
            <ErrorMessage error={mutation.error} />
            {mutation.error instanceof ApiError &&
                mutation.error.status === 409 && (
                    <p role="alert">{t('routes.conflict')}</p>
                )}
            {notice && <p role="status">{notice}</p>}
            {form && (
                <>
                    {dirty && <p role="status">{t('routes.dirty')}</p>}
                    <form
                        onSubmit={(event) => {
                            event.preventDefault()
                            if (canEdit && !busy && !pendingConfig)
                                mutation.mutate({
                                    type: 'save',
                                    form: { ...form, document },
                                })
                        }}
                    >
                        <fieldset
                            disabled={!canEdit || busy}
                            className="grid gap-3 rounded border p-3 md:grid-cols-2"
                        >
                            {(['key', 'name', 'description'] as const).map(
                                (key) => (
                                    <label key={key}>
                                        {t(`routes.${key}`)}
                                        <input
                                            className={inputClass}
                                            value={form[key]}
                                            onChange={(event) =>
                                                edit(key, event.target.value)
                                            }
                                            required={key !== 'description'}
                                            maxLength={
                                                key === 'description' ? 500 : 64
                                            }
                                            disabled={
                                                key === 'key' &&
                                                form.id !== undefined
                                            }
                                            pattern={
                                                key === 'key'
                                                    ? businessKeyPattern
                                                    : undefined
                                            }
                                        />
                                    </label>
                                ),
                            )}
                            <label>
                                <input
                                    type="checkbox"
                                    checked={form.enabled}
                                    onChange={(event) =>
                                        edit('enabled', event.target.checked)
                                    }
                                />{' '}
                                {t('routes.enabled')}
                            </label>
                            <Button type="submit" disabled={pendingConfig}>
                                {t('routes.save')}
                            </Button>
                            {form.id !== undefined && (
                                <Button
                                    type="button"
                                    variant="outline"
                                    onClick={() => {
                                        if (
                                            window.confirm(
                                                t('routes.confirmDelete'),
                                            ) &&
                                            (!dirty ||
                                                window.confirm(
                                                    t('routes.discard'),
                                                ))
                                        )
                                            mutation.mutate({
                                                type: 'delete',
                                                form,
                                            })
                                    }}
                                >
                                    {t('routes.remove')}
                                </Button>
                            )}
                        </fieldset>
                    </form>
                    <CanvasEditor
                        key={generation}
                        editor={canvas}
                        persisted
                        version={form.version}
                        previewDraft={previewDraft}
                        onPendingChange={setPendingConfig}
                        readOnly={!canEdit || busy}
                    />
                    {form.id !== undefined && (
                        <form
                            onSubmit={(event) => {
                                event.preventDefault()
                                if (canEdit && !busy && !dirty)
                                    mutation.mutate({
                                        type: 'copy',
                                        form,
                                        key: copyKey,
                                        name: copyName,
                                    })
                            }}
                        >
                            <fieldset
                                disabled={!canEdit || busy || dirty}
                                className="flex flex-wrap items-end gap-3 rounded border p-3"
                            >
                                <label>
                                    {t('routes.copyKey')}
                                    <input
                                        className={inputClass}
                                        required
                                        maxLength={64}
                                        pattern={businessKeyPattern}
                                        value={copyKey}
                                        onChange={(event) =>
                                            setCopyKey(event.target.value)
                                        }
                                    />
                                </label>
                                <label>
                                    {t('routes.copyName')}
                                    <input
                                        className={inputClass}
                                        required
                                        maxLength={64}
                                        value={copyName}
                                        onChange={(event) =>
                                            setCopyName(event.target.value)
                                        }
                                    />
                                </label>
                                <Button type="submit">
                                    {t('routes.copy')}
                                </Button>
                            </fieldset>
                        </form>
                    )}
                </>
            )}
            <section className="space-y-2 rounded border p-3">
                <p>{t('routes.compileNotice')}</p>
                <Button
                    disabled={!canEdit || busy || dirty || !query.data}
                    onClick={() =>
                        query.data && compile.mutate(query.data.version)
                    }
                >
                    {t('routes.compile')}
                </Button>
                <ErrorMessage error={compile.error} />
                {compile.data && (
                    <>
                        <p role="status">
                            {compile.data.compiled
                                ? t('routes.compiled', compile.data)
                                : t('routes.invalid', compile.data)}
                        </p>
                        <ul>
                            {compile.data.issues.map((issue, index) => (
                                <li key={index}>
                                    {issue.code} · {issue.routeKey} ·{' '}
                                    {issue.nodeId} · {issue.fieldKey}
                                </li>
                            ))}
                        </ul>
                    </>
                )}
            </section>
        </section>
    )
}
