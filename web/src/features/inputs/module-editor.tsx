import { useRef } from 'react'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { ParameterFields } from '../parameters/fields'
import { parameterIssues, type ParameterConnection } from '../parameters/model'
import {
    transformConfig,
    transformDefaults,
    transformValues,
} from '../canvas/transform-model'
import { moduleConnectionEdit, type InputForm } from './model'
import { routeObject } from '../canvas/codec'
import { readRouteJson } from '../canvas/json'
import type { useInputModules } from './module-query'

type Catalog = ReturnType<typeof useInputModules>
type Module = NonNullable<Catalog['data']>[number]
export function validModuleForm(
    form: InputForm,
    modules: readonly Module[],
    connections: readonly ParameterConnection[] = [],
): boolean {
    const module = modules.find(
        (item) =>
            item.kind === form.moduleKind &&
            Number(item.version) === form.moduleVersion,
    )
    if (!module) return false
    try {
        const parameters = routeObject(readRouteJson(form.moduleConfigText))
        const scalars = transformValues(form.moduleConfigText)
        if (
            Object.keys(form.moduleConnections).some(
                (name) =>
                    Object.hasOwn(parameters, name) ||
                    !module.parameters.parameters?.some(
                        (field) =>
                            field.name === name &&
                            field.type === 'connectionReference',
                    ),
            ) ||
            module.parameters.parameters?.some(
                (field) =>
                    field.type === 'connectionReference' &&
                    parameters[field.name] != null,
            )
        )
            return false
        return (
            parameterIssues(
                module.parameters,
                { ...scalars, ...form.moduleConnections },
                connections,
            ).length === 0
        )
    } catch {
        return false
    }
}

export function ModuleSourceEditor({
    form,
    patch,
    catalog,
    readOnly,
    connections,
    connectionsReady,
    refreshConnections,
}: {
    form: InputForm
    patch: (value: Partial<InputForm>) => void
    catalog: Catalog
    readOnly: boolean
    connections: readonly ParameterConnection[]
    connectionsReady: boolean
    refreshConnections: () => void
}) {
    const { t } = useTranslation()
    const selected = catalog.data?.find(
        (item) =>
            item.kind === form.moduleKind &&
            Number(item.version) === form.moduleVersion,
    )
    // Keep the last schema visible during metadata failure/removal; edits live in
    // the page's local store and are never replaced by a successful background read.
    const retained = useRef<Module | undefined>(undefined)
    if (selected) retained.current = selected
    const module =
        selected ??
        (retained.current?.kind === form.moduleKind &&
        Number(retained.current.version) === form.moduleVersion
            ? retained.current
            : undefined)
    const available = catalog.isSuccess && !catalog.isFetching
    let values
    try {
        values = transformValues(form.moduleConfigText)
    } catch {
        values = undefined
    }
    return (
        <section className="space-y-3" aria-label={t('inputs.moduleTitle')}>
            <h3 className="font-semibold">{t('inputs.moduleTitle')}</h3>
            <p className="text-sm">{t('inputs.moduleNotice')}</p>
            <p>{form.moduleKind}</p>
            {(!available || !selected) && (
                <p role="alert">{t('inputs.moduleUnavailable')}</p>
            )}
            <Button
                type="button"
                variant="outline"
                disabled={catalog.isFetching}
                onClick={() => void catalog.refetch()}
            >
                {t('inputs.moduleRefresh')}
            </Button>
            {module?.parameters.parameters?.some(
                (field) => field.type === 'connectionReference',
            ) && (
                <div>
                    {!connectionsReady && (
                        <p role="alert">
                            {t('inputs.moduleConnectionsUnavailable')}
                        </p>
                    )}
                    <Button
                        type="button"
                        variant="outline"
                        onClick={refreshConnections}
                    >
                        {t('inputs.moduleConnectionsRefresh')}
                    </Button>
                </div>
            )}
            <label className="block">
                {t('inputs.moduleVersion')}
                <select
                    className="ml-2 rounded border p-2"
                    value={form.moduleVersion}
                    disabled={readOnly || !available}
                    onChange={(event) => {
                        const next = catalog.data?.find(
                            (item) =>
                                item.kind === form.moduleKind &&
                                Number(item.version) ===
                                    Number(event.target.value),
                        )
                        if (
                            !next ||
                            (form.moduleVersion !== 0 &&
                                !window.confirm(t('inputs.moduleChange')))
                        )
                            return
                        patch({
                            moduleVersion: Number(next.version),
                            moduleConnections: {},
                            moduleConfigText: transformDefaults(
                                next.parameters,
                            ),
                        })
                    }}
                >
                    <option value={0}>{t('parameters.select')}</option>
                    {!selected && form.moduleVersion !== 0 && (
                        <option value={form.moduleVersion}>
                            v{form.moduleVersion} — {t('parameters.unknown')}
                        </option>
                    )}
                    {(catalog.data ?? [])
                        .filter((item) => item.kind === form.moduleKind)
                        .map((item) => (
                            <option key={item.version} value={item.version}>
                                {item.displayName} (v{item.version})
                            </option>
                        ))}
                </select>
            </label>
            {module && values && (
                <fieldset
                    disabled={
                        readOnly ||
                        !available ||
                        !selected ||
                        (module.parameters.parameters?.some(
                            (field) => field.type === 'connectionReference',
                        ) &&
                            !connectionsReady)
                    }
                >
                    <ParameterFields
                        schema={module.parameters}
                        values={{ ...values, ...form.moduleConnections }}
                        connections={connections}
                        issues={parameterIssues(
                            module.parameters,
                            { ...values, ...form.moduleConnections },
                            connections,
                        )}
                        onChange={(name, value) => {
                            if (
                                module.parameters.parameters?.some(
                                    (field) =>
                                        field.name === name &&
                                        field.type === 'connectionReference',
                                )
                            ) {
                                if (
                                    !connectionsReady ||
                                    typeof value !== 'string'
                                )
                                    return
                                patch(moduleConnectionEdit(form, name, value))
                            } else
                                patch({
                                    moduleConfigText: transformConfig(
                                        form.moduleConfigText,
                                        module.parameters,
                                        { [name]: value },
                                    ),
                                })
                        }}
                    />
                </fieldset>
            )}
            {!values && (
                <p role="alert">{t('parameters.transformUnsupported')}</p>
            )}
        </section>
    )
}
