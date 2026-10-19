import { useState } from 'react'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { useModuleConnections } from '../inputs/module-query'
import { ParameterFields } from '../parameters/fields'
import { parameterIssues, type ParameterValues } from '../parameters/model'
import {
    outputModuleConfig,
    outputModuleValues,
    type OutputModule,
} from './output-module-model'

type Props = {
    module: OutputModule
    configText: string
    version?: string
    disabled: boolean
    dirty: (value: boolean) => void
    accept: (text: string) => boolean
}
export function OutputModuleEditor(props: Props) {
    const { t } = useTranslation()
    let initial: ReturnType<typeof outputModuleValues>
    try {
        initial = outputModuleValues(props.configText)
    } catch {
        return <p role="alert">{t('routes.outputModule.unsupported')}</p>
    }
    return <OutputModuleForm {...props} initial={initial} />
}

function OutputModuleForm({
    initial,
    ...props
}: Props & { initial: ReturnType<typeof outputModuleValues> }) {
    const { t } = useTranslation()
    const catalog = useModuleConnections(true)
    const [store] = useState(() =>
        createStore<{ edits: ParameterValues; target: string }>(() => ({
            edits: {},
            target: initial.target,
        })),
    )
    const { edits, target } = useStore(store)
    const [error, setError] = useState(false)
    const ready =
        catalog.isSuccess &&
        !catalog.isFetching &&
        catalog.data.version === props.version
    const connections = (catalog.data?.connections ?? []).map((item) => ({
        id: String(item.id),
        kind: item.kind === 'module' ? (item.module?.kind ?? '') : item.kind,
        name: `${item.name} (${item.key})${item.enabled ? '' : ` — ${t('routes.catalogDisabled')}`}`,
    }))
    const values = { ...initial.values, ...initial.connections, ...edits }
    const fields = props.module.parameters.parameters ?? []
    const referenceIds = new Set(
        fields
            .filter((field) => field.type === 'connectionReference')
            .flatMap((field) => {
                const value = values[field.name]
                return typeof value === 'string' &&
                    connections.some(
                        (connection) =>
                            connection.id === value &&
                            field.connectionKinds?.includes(connection.kind),
                    )
                    ? [value]
                    : []
            }),
    )
    const targets = connections.filter(
        (connection) =>
            referenceIds.has(connection.id) &&
            props.module.targetConnectionKinds.includes(connection.kind),
    )
    const issues = parameterIssues(props.module.parameters, values, connections)
    const targetValid = targets.some((connection) => connection.id === target)
    const blocked = props.disabled || !ready
    return (
        <form
            noValidate
            aria-label={t('routes.outputModule.title')}
            className="space-y-2"
            onSubmit={(event) => {
                event.preventDefault()
                if (blocked) return
                try {
                    if (
                        props.accept(
                            outputModuleConfig(
                                props.configText,
                                props.module.parameters,
                                edits,
                                target,
                            ),
                        )
                    )
                        props.dirty(false)
                    else setError(true)
                } catch {
                    setError(true)
                }
            }}
        >
            <h3 className="font-medium">{t('routes.outputModule.title')}</h3>
            <p className="text-xs">{t('routes.outputModule.notice')}</p>
            {!ready && (
                <p role="alert">{t('inputs.moduleConnectionsUnavailable')}</p>
            )}
            <Button
                type="button"
                variant="outline"
                disabled={catalog.isFetching}
                onClick={() => void catalog.refetch()}
            >
                {t('inputs.moduleConnectionsRefresh')}
            </Button>
            <fieldset disabled={blocked} className="space-y-2">
                <ParameterFields
                    schema={props.module.parameters}
                    values={values}
                    connections={connections}
                    issues={issues}
                    onChange={(name, value) => {
                        store.setState({ edits: { ...edits, [name]: value } })
                        props.dirty(true)
                        setError(false)
                    }}
                />
                <label className="block">
                    {t('routes.outputModule.target')}
                    <select
                        className="w-full rounded border bg-background p-2"
                        value={target}
                        aria-invalid={!targetValid}
                        onChange={(event) => {
                            store.setState({ target: event.target.value })
                            props.dirty(true)
                            setError(false)
                        }}
                    >
                        <option value="">{t('parameters.select')}</option>
                        {target && !targetValid && (
                            <option value={target} disabled>
                                {t('parameters.unknown')}
                            </option>
                        )}
                        {targets.map((connection) => (
                            <option key={connection.id} value={connection.id}>
                                {connection.name}
                            </option>
                        ))}
                    </select>
                </label>
                {!targetValid && (
                    <p role="alert">
                        {t('routes.outputModule.targetRequired')}
                    </p>
                )}
                {issues.some((issue) => !issue.name) && (
                    <p role="alert">{t('parameters.issues.unknown')}</p>
                )}
                {error && <p role="alert">{t('routes.configError')}</p>}
                <Button type="submit">{t('routes.configAccept')}</Button>
            </fieldset>
            <Button
                type="button"
                variant="outline"
                onClick={() => {
                    store.setState({ edits: {}, target: initial.target })
                    props.dirty(false)
                    setError(false)
                }}
            >
                {t('routes.configDiscard')}
            </Button>
        </form>
    )
}
