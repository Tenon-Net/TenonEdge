import { useState } from 'react'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { ParameterFields } from '../parameters/fields'
import {
    parameterIssues,
    type ParameterSchema,
    type ParameterValues,
} from '../parameters/model'
import { transformConfig, transformValues } from './transform-model'

type Props = {
    schema: ParameterSchema
    configText: string
    disabled: boolean
    dirty: (value: boolean) => void
    accept: (text: string) => boolean
}
export function TransformEditor(props: Props) {
    const { t } = useTranslation()
    let initial: ParameterValues
    try {
        initial = transformValues(props.configText)
    } catch {
        return <p role="alert">{t('parameters.transformUnsupported')}</p>
    }
    return <TransformForm {...props} initial={initial} />
}
function TransformForm({
    initial,
    ...props
}: Props & { initial: ParameterValues }) {
    const { t } = useTranslation()
    const [store] = useState(() =>
        createStore<{ edits: ParameterValues }>(() => ({ edits: {} })),
    )
    const { edits } = useStore(store)
    const values = { ...initial, ...edits }
    const issues = parameterIssues(props.schema, values)
    const [error, setError] = useState(false)
    return (
        <form
            noValidate
            aria-label={t('parameters.transformTitle')}
            className="space-y-2"
            onSubmit={(event) => {
                event.preventDefault()
                if (props.disabled) return
                try {
                    if (
                        props.accept(
                            transformConfig(
                                props.configText,
                                props.schema,
                                edits,
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
            <fieldset disabled={props.disabled} className="space-y-2">
                <legend>{t('parameters.transformTitle')}</legend>
                <p className="text-xs">{t('parameters.transformNotice')}</p>
                <ParameterFields
                    schema={props.schema}
                    values={values}
                    issues={issues}
                    onChange={(name, value) => {
                        store.setState({ edits: { ...edits, [name]: value } })
                        props.dirty(true)
                        setError(false)
                    }}
                />
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
                    store.setState({ edits: {} })
                    props.dirty(false)
                    setError(false)
                }}
            >
                {t('routes.configDiscard')}
            </Button>
        </form>
    )
}
