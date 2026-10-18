import { useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { ParameterFields } from '../parameters/fields'
import { parameterIssues } from '../parameters/model'
import {
    transformConfig,
    transformDefaults,
    transformValues,
} from '../canvas/transform-model'
import type { InputForm } from './model'
import type { useInputModules } from './module-query'

type Catalog = ReturnType<typeof useInputModules>
type Module = NonNullable<Catalog['data']>[number]
export function validModuleForm(
    form: InputForm,
    modules: readonly Module[],
): boolean {
    const module = modules.find(
        (item) =>
            item.kind === form.moduleKind &&
            Number(item.version) === form.moduleVersion,
    )
    if (!module) return false
    try {
        return (
            parameterIssues(
                module.parameters,
                transformValues(form.moduleConfigText),
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
}: {
    form: InputForm
    patch: (value: Partial<InputForm>) => void
    catalog: Catalog
    readOnly: boolean
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
                <fieldset disabled={readOnly || !available || !selected}>
                    <ParameterFields
                        schema={module.parameters}
                        values={values}
                        issues={parameterIssues(module.parameters, values)}
                        onChange={(name, value) =>
                            patch({
                                moduleConfigText: transformConfig(
                                    form.moduleConfigText,
                                    module.parameters,
                                    { [name]: value },
                                ),
                            })
                        }
                    />
                </fieldset>
            )}
            {!values && (
                <p role="alert">{t('parameters.transformUnsupported')}</p>
            )}
        </section>
    )
}
