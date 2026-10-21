import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api, queryClient } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { ParameterFields } from '../parameters/fields'
import { parameterIssues } from '../parameters/model'
import {
    transformConfig,
    transformValues,
    moduleKey,
} from '../canvas/transform-model'
import { businessKeyPattern } from '../../lib/business-key'
import {
    moduleConnectionRequest,
    selectConnectionModule,
    type ConnectionModule,
    type ModuleConnectionForm,
} from './module-model'

const fieldClass =
    'mt-1 w-full rounded-md border bg-background p-2 disabled:opacity-60'
export function ModuleConnectionEditor({
    form,
    definitions,
    ready,
    canEdit,
    blocked,
    onChange,
    onSaved,
    onClose,
    onReload,
    onBusy,
}: {
    form: ModuleConnectionForm
    definitions: ConnectionModule[]
    ready: boolean
    canEdit: boolean
    blocked: boolean
    onChange: (value: ModuleConnectionForm) => void
    onSaved: () => Promise<void>
    onClose: () => void
    onReload: () => void
    onBusy: (value: boolean) => void
}) {
    const { t } = useTranslation()
    const definition = definitions.find(
        (item) =>
            item.kind === form.kind &&
            Number(item.version) === form.moduleVersion,
    )
    const nested = definition?.parameters.parameters?.some(
        (field) => field.type === 'connectionReference',
    )
    const [showIssues, setShowIssues] = useState(false)
    const [needsReload, setNeedsReload] = useState(false)
    const [notice, setNotice] = useState('')
    const values = transformValues(form.parameters)
    // Draft structural validation is intentionally separate from credential readiness.
    const schema = definition && {
        ...definition.parameters,
        parameters: definition.parameters.parameters?.filter(
            (field) => field.type !== 'credential',
        ),
    }
    const issues = schema ? parameterIssues(schema, values) : []
    const operation = useMutation({
        retry: false,
        mutationFn: async () => {
            const { body, serialized } = moduleConnectionRequest(form)
            const options = { body, bodySerializer: () => serialized }
            if (form.id)
                await api.PUT('/api/edge-admin/v1/connections/{connectionId}', {
                    ...options,
                    params: { path: { connectionId: Number(form.id) } },
                })
            else await api.POST('/api/edge-admin/v1/connections', options)
            await queryClient.invalidateQueries({
                queryKey: ['configuration-summary'],
            })
            setNotice('connections.saved')
            await onSaved()
        },
        onError: () => setNeedsReload(true),
    })
    useEffect(() => {
        onBusy(operation.isPending)
        return () => onBusy(false)
    }, [onBusy, operation.isPending])
    const disabled =
        !canEdit ||
        blocked ||
        operation.isPending ||
        needsReload ||
        !ready ||
        !definition ||
        !!nested
    return (
        <form
            className="space-y-4 rounded-md border p-4"
            aria-label={t('moduleConnections.editor')}
            onSubmit={(event) => {
                event.preventDefault()
                setShowIssues(true)
                if (!disabled && !issues.length) operation.mutate()
            }}
        >
            <h2 className="font-semibold">{t('moduleConnections.editor')}</h2>
            <p>{t('moduleConnections.notice')}</p>
            {!ready || !definition ? (
                <p role="alert">{t('moduleConnections.unavailable')}</p>
            ) : null}
            {nested && <p role="alert">{t('moduleConnections.nested')}</p>}
            <fieldset
                disabled={
                    !canEdit || blocked || operation.isPending || needsReload
                }
                className="grid gap-4 md:grid-cols-2"
            >
                <label>
                    {t('moduleConnections.type')}
                    <select
                        aria-label={t('moduleConnections.type')}
                        className={fieldClass}
                        value={moduleKey(form.kind, form.moduleVersion)}
                        disabled={!ready}
                        required
                        onChange={(event) => {
                            const next = definitions.find(
                                (item) =>
                                    moduleKey(
                                        item.kind,
                                        Number(item.version),
                                    ) === event.target.value,
                            )
                            if (
                                next &&
                                window.confirm(
                                    t('moduleConnections.changeType'),
                                )
                            ) {
                                onChange(selectConnectionModule(form, next))
                                setShowIssues(false)
                            }
                        }}
                    >
                        <option value="@0">{t('parameters.select')}</option>
                        {!definition && form.kind && (
                            <option
                                value={moduleKey(form.kind, form.moduleVersion)}
                                disabled
                            >
                                {form.kind} v{form.moduleVersion}
                            </option>
                        )}
                        {definitions
                            .filter(
                                (item) => !form.id || item.kind === form.kind,
                            )
                            .map((item) => (
                                <option
                                    key={moduleKey(
                                        item.kind,
                                        Number(item.version),
                                    )}
                                    value={moduleKey(
                                        item.kind,
                                        Number(item.version),
                                    )}
                                >
                                    {item.displayName} · {item.kind} v
                                    {item.version}
                                </option>
                            ))}
                    </select>
                </label>
                <label>
                    {t('connections.key')}
                    <input
                        className={fieldClass}
                        value={form.key}
                        disabled={!!form.id}
                        pattern={businessKeyPattern}
                        maxLength={64}
                        required
                        onChange={(event) =>
                            onChange({ ...form, key: event.target.value })
                        }
                    />
                </label>
                <label>
                    {t('connections.name')}
                    <input
                        className={fieldClass}
                        value={form.name}
                        maxLength={64}
                        required
                        onChange={(event) =>
                            onChange({ ...form, name: event.target.value })
                        }
                    />
                </label>
                <label>
                    <input
                        type="checkbox"
                        checked={form.enabled}
                        onChange={(event) =>
                            onChange({ ...form, enabled: event.target.checked })
                        }
                    />{' '}
                    {t('connections.enabled')}
                </label>
                <fieldset className="contents" disabled={!ready || !!nested}>
                    {schema && (
                        <ParameterFields
                            schema={schema}
                            values={values}
                            issues={showIssues ? issues : []}
                            onChange={(name, value) =>
                                onChange({
                                    ...form,
                                    parameters: transformConfig(
                                        form.parameters,
                                        schema,
                                        { [name]: value },
                                    ),
                                })
                            }
                        />
                    )}
                </fieldset>
            </fieldset>
            {(!canEdit || !definition || nested) && (
                <pre className="overflow-auto whitespace-pre-wrap">
                    {form.parameters}
                </pre>
            )}
            <ErrorMessage error={operation.error} />
            {needsReload && (
                <p role="alert">{t('moduleConnections.unknown')}</p>
            )}
            {notice && <p role="status">{t(notice)}</p>}
            <div className="flex gap-2">
                {canEdit && (
                    <Button disabled={disabled} type="submit">
                        {t('connections.save')}
                    </Button>
                )}
                <Button
                    disabled={blocked || operation.isPending}
                    type="button"
                    variant="outline"
                    onClick={onReload}
                >
                    {t('connections.reload')}
                </Button>
                <Button
                    disabled={blocked || operation.isPending}
                    type="button"
                    variant="outline"
                    onClick={onClose}
                >
                    {t('connections.cancel')}
                </Button>
            </div>
        </form>
    )
}
