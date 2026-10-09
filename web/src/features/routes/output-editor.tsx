import { useState } from 'react'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import {
    outputForm,
    outputConfig,
    type OutputForm,
    type PayloadFieldForm,
} from './output-model'

type Props = {
    kind: 'output.http' | 'output.mqtt'
    configText: string
    disabled: boolean
    dirty: (value: boolean) => void
    accept: (text: string) => boolean
}
export function OutputEditor(props: Props) {
    const { t } = useTranslation()
    let initial: OutputForm
    try {
        initial = outputForm(props.configText)
    } catch {
        return <p role="alert">{t('routes.outputUnsupported')}</p>
    }
    return <OutputEditorForm {...props} initial={initial} />
}
function OutputEditorForm({
    initial,
    ...props
}: Props & { initial: OutputForm }) {
    const { t } = useTranslation()
    const [store] = useState(() =>
        createStore<{ form: OutputForm }>(() => ({ form: initial })),
    )
    const { form } = useStore(store)
    const [error, setError] = useState(false)
    function edit(patch: Partial<OutputForm>) {
        store.setState({ form: { ...form, ...patch } })
        props.dirty(true)
        setError(false)
    }
    function field(index: number, patch: Partial<PayloadFieldForm>) {
        edit({
            fields: form.fields.map((item, i) =>
                i === index ? { ...item, ...patch } : item,
            ),
        })
    }
    const inputClass = 'w-full rounded border bg-background p-1 text-sm'
    const text = (
        key: 'topic' | 'path' | 'successPath' | 'successValue',
        maxLength: number,
    ) => (
        <label className="block">
            {t(`routes.output.${key}`)}
            <input
                className={inputClass}
                value={form[key]}
                maxLength={maxLength}
                onChange={(event) => edit({ [key]: event.target.value })}
            />
        </label>
    )
    return (
        <form
            aria-label={t('routes.output.title')}
            className="space-y-2"
            onSubmit={(event) => {
                event.preventDefault()
                if (props.disabled) return
                try {
                    if (
                        props.accept(
                            outputConfig(props.configText, props.kind, form),
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
                <h3 className="font-medium">{t('routes.output.title')}</h3>
                {props.kind === 'output.mqtt' ? (
                    <>
                        {text('topic', 65535)}
                        <label>
                            {t('routes.output.qos')}
                            <select
                                className={inputClass}
                                value={form.qos}
                                onChange={(event) =>
                                    edit({ qos: event.target.value })
                                }
                            >
                                <option value="0">0</option>
                                <option value="1">1</option>
                            </select>
                        </label>
                    </>
                ) : (
                    <>
                        {text('path', 2048)}
                        <p className="text-xs">
                            {t('routes.output.headersNotice')}
                        </p>
                        {form.headers.map((header, index) => (
                            <div
                                key={index}
                                className="space-y-1 rounded border p-1"
                            >
                                <input
                                    aria-label={`${t('routes.output.headerName')} ${index + 1}`}
                                    className={inputClass}
                                    value={header.name}
                                    onChange={(event) =>
                                        edit({
                                            headers: form.headers.map(
                                                (row, i) =>
                                                    i === index
                                                        ? {
                                                              ...row,
                                                              name: event.target
                                                                  .value,
                                                          }
                                                        : row,
                                            ),
                                        })
                                    }
                                />
                                <input
                                    aria-label={`${t('routes.output.headerValue')} ${index + 1}`}
                                    className={inputClass}
                                    value={header.value}
                                    onChange={(event) =>
                                        edit({
                                            headers: form.headers.map(
                                                (row, i) =>
                                                    i === index
                                                        ? {
                                                              ...row,
                                                              value: event
                                                                  .target.value,
                                                          }
                                                        : row,
                                            ),
                                        })
                                    }
                                />
                                <Button
                                    type="button"
                                    variant="outline"
                                    onClick={() =>
                                        edit({
                                            headers: form.headers.filter(
                                                (_, i) => i !== index,
                                            ),
                                        })
                                    }
                                >
                                    {t('routes.output.removeHeader')}
                                </Button>
                            </div>
                        ))}
                        <Button
                            type="button"
                            variant="outline"
                            disabled={form.headers.length >= 100}
                            onClick={() =>
                                edit({
                                    headers: [
                                        ...form.headers,
                                        { name: '', value: '' },
                                    ],
                                })
                            }
                        >
                            {t('routes.output.addHeader')}
                        </Button>
                        {text('successPath', 1024)}
                        {form.successPath && text('successValue', 65535)}
                        <p className="text-xs">
                            {t('routes.output.successNotice')}
                        </p>
                    </>
                )}
                <label className="block">
                    {t('routes.output.mode')}
                    <select
                        className={inputClass}
                        value={form.mode}
                        onChange={(event) =>
                            edit({
                                mode: event.target.value as OutputForm['mode'],
                            })
                        }
                    >
                        {(['envelope', 'values', 'custom'] as const).map(
                            (mode) => (
                                <option key={mode} value={mode}>
                                    {t(`routes.output.${mode}`)}
                                </option>
                            ),
                        )}
                    </select>
                </label>
                {form.mode === 'values' && (
                    <p role="status" className="text-sm">
                        {t('routes.output.valuesWarning')}
                    </p>
                )}
                {form.mode === 'custom' && (
                    <>
                        {form.fields.map((row, index) => (
                            <fieldset
                                key={index}
                                className="space-y-1 rounded border p-2"
                            >
                                <legend>
                                    {t('routes.output.fieldNumber', {
                                        index: index + 1,
                                    })}
                                </legend>
                                <label>
                                    {t('routes.output.target')}
                                    <input
                                        className={inputClass}
                                        value={row.target}
                                        maxLength={64}
                                        onChange={(event) =>
                                            field(index, {
                                                target: event.target.value,
                                            })
                                        }
                                    />
                                </label>
                                <label>
                                    {t('routes.output.source')}
                                    <select
                                        className={inputClass}
                                        value={row.source}
                                        onChange={(event) =>
                                            field(index, {
                                                source: event.target
                                                    .value as PayloadFieldForm['source'],
                                                value:
                                                    event.target.value ===
                                                    'constant'
                                                        ? 'null'
                                                        : event.target.value ===
                                                            'placeholder'
                                                          ? '{messageId}'
                                                          : '',
                                            })
                                        }
                                    >
                                        {(
                                            [
                                                'field',
                                                'placeholder',
                                                'constant',
                                            ] as const
                                        ).map((source) => (
                                            <option key={source} value={source}>
                                                {t(`routes.output.${source}`)}
                                            </option>
                                        ))}
                                    </select>
                                </label>
                                <label>
                                    {t('routes.output.value')}
                                    {row.source === 'placeholder' ? (
                                        <select
                                            className={inputClass}
                                            value={row.value}
                                            onChange={(event) =>
                                                field(index, {
                                                    value: event.target.value,
                                                })
                                            }
                                        >
                                            {[
                                                '{messageId}',
                                                '{sourceMessageId}',
                                                '{collectedAt}',
                                            ].map((value) => (
                                                <option key={value}>
                                                    {value}
                                                </option>
                                            ))}
                                        </select>
                                    ) : (
                                        <input
                                            className={inputClass}
                                            value={row.value}
                                            maxLength={65535}
                                            onChange={(event) =>
                                                field(index, {
                                                    value: event.target.value,
                                                })
                                            }
                                        />
                                    )}
                                </label>
                                {row.source === 'constant' && (
                                    <p className="text-xs">
                                        {t('routes.output.scalarHelp')}
                                    </p>
                                )}
                                <label>
                                    <input
                                        type="checkbox"
                                        checked={row.required}
                                        onChange={(event) =>
                                            field(index, {
                                                required: event.target.checked,
                                            })
                                        }
                                    />{' '}
                                    {t('routes.output.required')}
                                </label>
                                <Button
                                    type="button"
                                    variant="outline"
                                    onClick={() =>
                                        edit({
                                            fields: form.fields.filter(
                                                (_, i) => i !== index,
                                            ),
                                        })
                                    }
                                >
                                    {t('routes.output.removeField')}
                                </Button>
                            </fieldset>
                        ))}
                        <Button
                            type="button"
                            variant="outline"
                            disabled={form.fields.length >= 1000}
                            onClick={() =>
                                edit({
                                    fields: [
                                        ...form.fields,
                                        {
                                            target: '',
                                            source: 'field',
                                            value: '',
                                            required: false,
                                        },
                                    ],
                                })
                            }
                        >
                            {t('routes.output.addField')}
                        </Button>
                    </>
                )}
                {error && <p role="alert">{t('routes.configError')}</p>}
                <Button type="submit">{t('routes.output.accept')}</Button>
                <Button
                    type="button"
                    variant="outline"
                    onClick={() => {
                        store.setState({ form: initial })
                        props.dirty(false)
                        setError(false)
                    }}
                >
                    {t('routes.output.discard')}
                </Button>
            </fieldset>
        </form>
    )
}
