import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import type { InputForm, JsonField, TimeFormat } from './model'

const fieldClass =
    'mt-1 w-full rounded border bg-background p-2 disabled:opacity-60'
export function JsonSourceEditor({
    form,
    patch,
    readOnly,
}: {
    form: InputForm
    patch: (value: Partial<InputForm>) => void
    readOnly: boolean
}) {
    const { t } = useTranslation()
    const editField = (index: number, value: Partial<JsonField>) =>
        patch({
            fields: form.fields.map((field, i) =>
                i === index ? { ...field, ...value } : field,
            ),
        })
    return (
        <fieldset disabled={readOnly} className="min-w-0 space-y-4">
            {form.kind === 'mqtt' && (
                <div className="grid gap-3 md:grid-cols-3">
                    <label>
                        {t('inputs.topic')}
                        <input
                            className={fieldClass}
                            value={form.topic}
                            maxLength={65535}
                            onChange={(e) => patch({ topic: e.target.value })}
                        />
                    </label>
                    <label>
                        {t('inputs.qos')}
                        <select
                            className={fieldClass}
                            value={form.qos}
                            onChange={(e) =>
                                patch({ qos: Number(e.target.value) })
                            }
                        >
                            <option value={0}>0</option>
                            <option value={1}>1</option>
                        </select>
                    </label>
                    <label className="flex items-center gap-2">
                        <input
                            type="checkbox"
                            checked={form.retained}
                            onChange={(e) =>
                                patch({ retained: e.target.checked })
                            }
                        />
                        {t('inputs.retained')}
                    </label>
                </div>
            )}
            {form.kind === 'http' && (
                <div className="space-y-3">
                    <label className="block">
                        {t('inputs.httpPath')}
                        <input
                            className={fieldClass}
                            value={form.httpPath}
                            maxLength={2048}
                            onChange={(e) =>
                                patch({ httpPath: e.target.value })
                            }
                        />
                    </label>
                    <p className="text-sm text-muted-foreground">
                        {t('inputs.httpHelp')}
                    </p>
                    <Button
                        type="button"
                        variant="outline"
                        disabled={form.headers.length >= 64}
                        onClick={() =>
                            patch({
                                headers: [
                                    ...form.headers,
                                    { name: '', value: '' },
                                ],
                            })
                        }
                    >
                        {t('inputs.addHeader')}
                    </Button>
                    {form.headers.map((header, index) => (
                        <div className="flex gap-2" key={index}>
                            <label className="flex-1">
                                {t('inputs.headerName', { row: index + 1 })}
                                <input
                                    className={fieldClass}
                                    value={header.name}
                                    onChange={(e) =>
                                        patch({
                                            headers: form.headers.map((h, i) =>
                                                i === index
                                                    ? {
                                                          ...h,
                                                          name: e.target.value,
                                                      }
                                                    : h,
                                            ),
                                        })
                                    }
                                />
                            </label>
                            <label className="flex-1">
                                {t('inputs.headerValue', { row: index + 1 })}
                                <input
                                    className={fieldClass}
                                    value={header.value}
                                    onChange={(e) =>
                                        patch({
                                            headers: form.headers.map((h, i) =>
                                                i === index
                                                    ? {
                                                          ...h,
                                                          value: e.target.value,
                                                      }
                                                    : h,
                                            ),
                                        })
                                    }
                                />
                            </label>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() =>
                                    patch({
                                        headers: form.headers.filter(
                                            (_, i) => i !== index,
                                        ),
                                    })
                                }
                            >
                                {t('inputs.removeHeader')}
                            </Button>
                        </div>
                    ))}
                </div>
            )}
            {(form.kind === 'mqtt' || form.kind === 'http') && (
                <>
                    <label className="flex items-center gap-2">
                        <input
                            type="checkbox"
                            checked={form.timeEnabled}
                            onChange={(e) =>
                                patch({ timeEnabled: e.target.checked })
                            }
                        />
                        {t('inputs.useTime')}
                    </label>
                    <div className="grid gap-3 md:grid-cols-2">
                        <label>
                            {t('inputs.timePath')}
                            <input
                                className={fieldClass}
                                value={form.timePath}
                                onChange={(e) =>
                                    patch({
                                        timePath: e.target.value,
                                        timeEnabled: true,
                                    })
                                }
                            />
                        </label>
                        <label>
                            {t('inputs.timeFormat')}
                            <select
                                className={fieldClass}
                                value={form.timeFormat}
                                onChange={(e) =>
                                    patch({
                                        timeFormat: e.target
                                            .value as TimeFormat,
                                    })
                                }
                            >
                                {(
                                    [
                                        'rfc3339',
                                        'unixSeconds',
                                        'unixMilliseconds',
                                    ] as const
                                ).map((format) => (
                                    <option key={format} value={format}>
                                        {t('inputs.timeFormats.' + format)}
                                    </option>
                                ))}
                            </select>
                        </label>
                    </div>
                    <p className="text-sm text-muted-foreground">
                        {t('inputs.timeHelp')}
                    </p>
                    <Button
                        type="button"
                        variant="outline"
                        disabled={form.fields.length >= 1000}
                        onClick={() =>
                            patch({
                                fields: [
                                    ...form.fields,
                                    {
                                        key: '',
                                        path: '',
                                        type: 'number',
                                        name: '',
                                        unit: '',
                                    },
                                ],
                            })
                        }
                    >
                        {t('inputs.addField')}
                    </Button>
                    <p className="text-sm text-muted-foreground">
                        {t('inputs.fieldHelp')}
                    </p>
                    <div className="overflow-x-auto">
                        <table className="w-full min-w-[800px] border-collapse text-sm">
                            <thead>
                                <tr>
                                    {[
                                        'key',
                                        'path',
                                        'type',
                                        'name',
                                        'unit',
                                        'actions',
                                    ].map((key) => (
                                        <th key={key} className="p-2 text-left">
                                            {t('inputs.fieldColumns.' + key)}
                                        </th>
                                    ))}
                                </tr>
                            </thead>
                            <tbody>
                                {form.fields.map((field, index) => (
                                    <tr key={index}>
                                        {(
                                            [
                                                'key',
                                                'path',
                                                'type',
                                                'name',
                                                'unit',
                                            ] as const
                                        ).map((column) => (
                                            <td key={column} className="p-1">
                                                {column === 'type' ? (
                                                    <select
                                                        className={
                                                            fieldClass +
                                                            ' min-w-[7rem]'
                                                        }
                                                        aria-label={t(
                                                            'inputs.fieldCell',
                                                            {
                                                                row: index + 1,
                                                                field: column,
                                                            },
                                                        )}
                                                        value={
                                                            field.type ??
                                                            'number'
                                                        }
                                                        onChange={(e) =>
                                                            editField(index, {
                                                                type: e.target
                                                                    .value as JsonField['type'],
                                                            })
                                                        }
                                                    >
                                                        {(
                                                            [
                                                                'number',
                                                                'integer',
                                                                'boolean',
                                                                'string',
                                                            ] as const
                                                        ).map((type) => (
                                                            <option key={type}>
                                                                {type}
                                                            </option>
                                                        ))}
                                                    </select>
                                                ) : (
                                                    <input
                                                        className={fieldClass}
                                                        aria-label={t(
                                                            'inputs.fieldCell',
                                                            {
                                                                row: index + 1,
                                                                field: column,
                                                            },
                                                        )}
                                                        value={
                                                            field[column] ?? ''
                                                        }
                                                        maxLength={
                                                            column === 'path'
                                                                ? 4096
                                                                : 64
                                                        }
                                                        onChange={(e) =>
                                                            editField(index, {
                                                                [column]:
                                                                    e.target
                                                                        .value,
                                                            })
                                                        }
                                                    />
                                                )}
                                            </td>
                                        ))}
                                        <td>
                                            <Button
                                                type="button"
                                                variant="outline"
                                                onClick={() =>
                                                    patch({
                                                        fields: form.fields.filter(
                                                            (_, i) =>
                                                                i !== index,
                                                        ),
                                                    })
                                                }
                                            >
                                                {t('inputs.removeField')}
                                            </Button>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                </>
            )}
            {form.kind === 'simulator' && (
                <>
                    <label className="block">
                        {t('inputs.simulatorMode')}
                        <select
                            className={fieldClass}
                            value={form.simulatorMode}
                            onChange={(e) =>
                                patch({
                                    simulatorMode: e.target
                                        .value as InputForm['simulatorMode'],
                                })
                            }
                        >
                            <option value="meter">{t('inputs.meter')}</option>
                            <option value="custom">{t('inputs.custom')}</option>
                        </select>
                    </label>
                    {form.simulatorMode === 'custom' && (
                        <label className="block">
                            {t('inputs.simulatorSample')}
                            <textarea
                                className={fieldClass + ' h-40 font-mono'}
                                value={form.sampleJson}
                                onChange={(e) =>
                                    patch({ sampleJson: e.target.value })
                                }
                            />
                        </label>
                    )}
                    <p className="text-sm text-muted-foreground">
                        {t('inputs.simulatorHelp')}
                    </p>
                    <div className="grid gap-3 md:grid-cols-3">
                        {(
                            ['missing', 'invalid', 'connectionError'] as const
                        ).map((field) => (
                            <label key={field}>
                                {t('inputs.faults.' + field)}
                                <input
                                    className={fieldClass}
                                    type="number"
                                    min={0}
                                    max={1}
                                    step={0.01}
                                    value={form[field]}
                                    onChange={(e) =>
                                        patch({
                                            [field]: Number(e.target.value),
                                        })
                                    }
                                />
                            </label>
                        ))}
                    </div>
                </>
            )}
        </fieldset>
    )
}
