import { useState } from 'react'
import { createStore } from 'zustand/vanilla'
import { useStore } from 'zustand'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import {
    fieldTypes,
    mappingRows,
    mappingConfig,
    newMappingRow,
    type MappingRow,
} from './mapping-model'

type Props = {
    configText: string
    disabled: boolean
    dirty: (value: boolean) => void
    accept: (text: string) => boolean
}
export function MappingEditor(props: Props) {
    const { t } = useTranslation()
    let initial: MappingRow[]
    try {
        initial = mappingRows(props.configText)
    } catch {
        return <p role="alert">{t('routes.mapping.unsupported')}</p>
    }
    return <MappingEditorForm {...props} initial={initial} />
}
function MappingEditorForm({
    initial,
    ...props
}: Props & { initial: MappingRow[] }) {
    const { t } = useTranslation()
    const [store] = useState(() =>
        createStore<{ rows: MappingRow[] }>(() => ({ rows: initial })),
    )
    const { rows } = useStore(store)
    const [page, setPage] = useState(0)
    const [error, setError] = useState(false)
    const current = Math.min(page, Math.max(0, Math.ceil(rows.length / 50) - 1))
    function change(rows: MappingRow[]) {
        store.setState({ rows })
        props.dirty(true)
        setError(false)
    }
    function edit(index: number, patch: Partial<MappingRow>) {
        change(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)))
    }
    const columns = [
        'target',
        'source',
        'value',
        'type',
        'unit',
        'required',
        'scale',
        'offset',
        'decimalPlaces',
    ] as const
    const inputClass =
        'w-full min-w-28 rounded border bg-background p-1 text-sm'
    return (
        <form
            aria-label={t('routes.mapping.title')}
            className="space-y-2"
            onSubmit={(event) => {
                event.preventDefault()
                if (props.disabled) return
                try {
                    if (props.accept(mappingConfig(rows))) props.dirty(false)
                    else setError(true)
                } catch {
                    setError(true)
                }
            }}
        >
            <fieldset disabled={props.disabled} className="space-y-2">
                <h3 className="font-medium">{t('routes.mapping.title')}</h3>
                <p className="text-xs">{t('routes.mapping.notice')}</p>
                <p className="text-xs">{t('routes.mapping.scalarHelp')}</p>
                <div className="max-h-96 overflow-auto">
                    <table className="min-w-[1200px] text-sm">
                        <thead>
                            <tr>
                                {columns.map((key) => (
                                    <th key={key} scope="col">
                                        {t(`routes.mapping.${key}`)}
                                    </th>
                                ))}
                                <th scope="col">
                                    {t('routes.mapping.remove')}
                                </th>
                            </tr>
                        </thead>
                        <tbody>
                            {rows
                                .slice(current * 50, current * 50 + 50)
                                .map((row, offset) => {
                                    const index = current * 50 + offset
                                    return (
                                        <tr key={index}>
                                            {columns.map((key) => (
                                                <td key={key} className="p-1">
                                                    {key === 'source' ? (
                                                        <select
                                                            className={
                                                                inputClass
                                                            }
                                                            aria-label={`${t('routes.mapping.source')} ${index + 1}`}
                                                            value={row.source}
                                                            onChange={(event) =>
                                                                edit(index, {
                                                                    source: event
                                                                        .target
                                                                        .value as MappingRow['source'],
                                                                    value:
                                                                        event
                                                                            .target
                                                                            .value ===
                                                                        'constant'
                                                                            ? 'null'
                                                                            : '',
                                                                })
                                                            }
                                                        >
                                                            {(
                                                                [
                                                                    'field',
                                                                    'path',
                                                                    'constant',
                                                                ] as const
                                                            ).map((source) => (
                                                                <option
                                                                    key={source}
                                                                    value={
                                                                        source
                                                                    }
                                                                >
                                                                    {t(
                                                                        `routes.mapping.${source}`,
                                                                    )}
                                                                </option>
                                                            ))}
                                                        </select>
                                                    ) : key === 'type' ? (
                                                        <select
                                                            className={
                                                                inputClass
                                                            }
                                                            aria-label={`${t('routes.mapping.type')} ${index + 1}`}
                                                            value={row.type}
                                                            onChange={(event) =>
                                                                edit(index, {
                                                                    type: event
                                                                        .target
                                                                        .value as MappingRow['type'],
                                                                })
                                                            }
                                                        >
                                                            {fieldTypes.map(
                                                                (type) => (
                                                                    <option
                                                                        key={
                                                                            type
                                                                        }
                                                                        value={
                                                                            type
                                                                        }
                                                                    >
                                                                        {type ||
                                                                            t(
                                                                                'routes.mapping.chooseType',
                                                                            )}
                                                                    </option>
                                                                ),
                                                            )}
                                                        </select>
                                                    ) : key === 'required' ? (
                                                        <input
                                                            type="checkbox"
                                                            aria-label={`${t('routes.mapping.required')} ${index + 1}`}
                                                            checked={
                                                                row.required
                                                            }
                                                            onChange={(event) =>
                                                                edit(index, {
                                                                    required:
                                                                        event
                                                                            .target
                                                                            .checked,
                                                                })
                                                            }
                                                        />
                                                    ) : (
                                                        <input
                                                            className={
                                                                inputClass
                                                            }
                                                            aria-label={`${t(`routes.mapping.${key}`)} ${index + 1}`}
                                                            value={row[key]}
                                                            maxLength={
                                                                key === 'value'
                                                                    ? 65535
                                                                    : key ===
                                                                            'target' ||
                                                                        key ===
                                                                            'unit'
                                                                      ? 64
                                                                      : 128
                                                            }
                                                            onChange={(event) =>
                                                                edit(index, {
                                                                    [key]: event
                                                                        .target
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
                                                        change(
                                                            rows.filter(
                                                                (_, i) =>
                                                                    i !== index,
                                                            ),
                                                        )
                                                    }
                                                >
                                                    {t('routes.mapping.remove')}
                                                </Button>
                                            </td>
                                        </tr>
                                    )
                                })}
                        </tbody>
                    </table>
                </div>
                <p>
                    {t('routes.mapping.page', {
                        page: current + 1,
                        count: rows.length,
                    })}
                </p>
                <Button
                    type="button"
                    variant="outline"
                    disabled={current === 0}
                    onClick={() => setPage(current - 1)}
                >
                    {t('routes.catalogPrevious')}
                </Button>
                <Button
                    type="button"
                    variant="outline"
                    disabled={(current + 1) * 50 >= rows.length}
                    onClick={() => setPage(current + 1)}
                >
                    {t('routes.catalogNext')}
                </Button>
                <Button
                    type="button"
                    variant="outline"
                    disabled={rows.length >= 1000}
                    onClick={() => {
                        change([...rows, newMappingRow()])
                        setPage(Math.floor(rows.length / 50))
                    }}
                >
                    {t('routes.mapping.add')}
                </Button>
                {error && <p role="alert">{t('routes.configError')}</p>}
                <Button type="submit">{t('routes.mapping.accept')}</Button>
                <Button
                    type="button"
                    variant="outline"
                    onClick={() => {
                        store.setState({ rows: initial })
                        setPage(0)
                        setError(false)
                        props.dirty(false)
                    }}
                >
                    {t('routes.mapping.discard')}
                </Button>
            </fieldset>
        </form>
    )
}
