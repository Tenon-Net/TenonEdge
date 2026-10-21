import { useMemo, useRef, useState, type ClipboardEvent } from 'react'
import { useStore } from 'zustand'
import {
    getCoreRowModel,
    useReactTable,
    type ColumnDef,
} from '@tanstack/react-table'
import { useVirtualizer } from '@tanstack/react-virtual'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { createPointEditor } from './store'
import {
    byteOrders,
    manualReference,
    pointFields,
    pointTypes,
    PointEditError,
    validateRows,
    type AddressMode,
    type PointField,
    type PointRow,
} from './model'

const columns: ColumnDef<PointRow>[] = pointFields.map((field) => ({
    accessorKey: field,
    id: field,
}))
const gridColumns =
    '42px 52px 155px 135px 80px 120px 135px 65px 120px 95px 85px 185px 270px'
const selectStyle =
    'h-9 w-full min-w-0 rounded-md border bg-background px-2 text-sm'
const orders64: Record<string, string> = {
    ABCD: 'ABCDEFGH',
    CDAB: 'GHEFCDAB',
    BADC: 'BADCFEHG',
    DCBA: 'HGFEDCBA',
}

export function PointEditor({
    store: suppliedStore,
    managed = false,
    readOnly = false,
}: {
    store?: ReturnType<typeof createPointEditor>
    managed?: boolean
    readOnly?: boolean
} = {}) {
    const { t } = useTranslation()
    const [store] = useState(() => suppliedStore ?? createPointEditor())
    const {
        rows,
        selected,
        mode,
        edit,
        select,
        add,
        remove,
        paste,
        bulk,
        changeMode,
    } = useStore(store)
    const [pasteText, setPasteText] = useState('')
    const [pasteMode, setPasteMode] = useState<'append' | 'replace'>('append')
    const [bulkField, setBulkField] = useState<
        'type' | 'byteOrder' | 'unit' | 'enabled'
    >('unit')
    const [bulkValue, setBulkValue] = useState('')
    const [error, setError] = useState<string>()
    const [pasteMs, setPasteMs] = useState(0)
    const viewport = useRef<HTMLDivElement>(null)
    const validation = useMemo(() => {
        const started = performance.now()
        const results = validateRows(rows, mode)
        return { results, milliseconds: performance.now() - started }
    }, [rows, mode])
    const errorRows = validation.results.flatMap((result, index) =>
        result.errors.length ? [index] : [],
    )
    const selectedCount = Object.values(selected).filter(Boolean).length
    const table = useReactTable({
        data: rows,
        columns,
        getRowId: (row) => row.id,
        getCoreRowModel: getCoreRowModel(),
        state: { rowSelection: selected },
        onRowSelectionChange: (update) =>
            select(typeof update === 'function' ? update(selected) : update),
    })
    const tableRows = table.getRowModel().rows
    const virtualizer = useVirtualizer({
        count: tableRows.length,
        getScrollElement: () => viewport.current,
        estimateSize: () => 72,
        overscan: 5,
        scrollMargin: 40,
    })
    const attempt = (action: () => void) => {
        try {
            action()
            setError(undefined)
        } catch (failure) {
            if (!(failure instanceof PointEditError)) throw failure
            setError(t(`points.errors.${failure.code}`))
        }
    }
    const pasteRows = (text: string) =>
        attempt(() => {
            const started = performance.now()
            paste(text, pasteMode)
            setPasteMs(performance.now() - started)
        })
    const onPaste = (event: ClipboardEvent) => {
        if (readOnly) return
        const text = event.clipboardData.getData('text/plain')
        if (!/[\t\r\n]/.test(text) && event.target !== viewport.current) return
        event.preventDefault()
        pasteRows(text)
    }
    const locate = (index: number) => {
        virtualizer.scrollToIndex(index, { align: 'center' })
        requestAnimationFrame(() =>
            requestAnimationFrame(() =>
                viewport.current
                    ?.querySelector<HTMLInputElement>(
                        `[data-point-index="${index}"] input[data-point-key]`,
                    )
                    ?.focus(),
            ),
        )
    }
    const issueText = (index: number) =>
        [
            ...validation.results[index].errors,
            ...validation.results[index].warnings,
        ]
            .map((code) => t(`points.errors.${code}`))
            .join('；')
    const choices = (field: PointField): readonly string[] | undefined =>
        ({
            type: pointTypes,
            functionCode: ['1', '2', '3', '4'],
            byteOrder: ['', ...byteOrders],
            enabled: ['true', 'false'],
        })[field as 'type' | 'functionCode' | 'byteOrder' | 'enabled']
    const optionLabel = (field: PointField, value: string, type: string) =>
        value === ''
            ? t('points.notSet')
            : field === 'enabled'
              ? t(
                    value === 'true'
                        ? 'points.enabledTrue'
                        : 'points.enabledFalse',
                )
              : field === 'type' && value === 'bit'
                ? t('points.bitType')
                : field === 'byteOrder' && type.endsWith('64')
                  ? `${value} / ${orders64[value] ?? value}`
                  : value
    return (
        <fieldset disabled={readOnly} className="min-w-0 space-y-4">
            {managed ? (
                <h2 className="text-base font-semibold">
                    {t('points.managedTitle')}
                </h2>
            ) : (
                <h1 className="text-lg font-semibold">{t('points.title')}</h1>
            )}
            <p className="rounded-md border bg-muted/40 p-3 text-sm text-muted-foreground">
                {t(managed ? 'points.managedNotice' : 'points.notice')}
            </p>
            <div className="flex flex-wrap items-center gap-3">
                <label>
                    {t('points.mode')}{' '}
                    <select
                        className={selectStyle}
                        value={mode}
                        onChange={(event) =>
                            attempt(() =>
                                changeMode(event.target.value as AddressMode),
                            )
                        }
                    >
                        {(['offset', 'manual', 'register'] as const).map(
                            (value) => (
                                <option key={value} value={value}>
                                    {t(`points.modes.${value}`)}
                                </option>
                            ),
                        )}
                    </select>
                </label>
                <Button variant="outline" onClick={() => attempt(add)}>
                    {t('points.add')}
                </Button>
                <Button
                    variant="outline"
                    disabled={!selectedCount}
                    onClick={() => {
                        if (window.confirm(t('points.removeConfirm'))) remove()
                    }}
                >
                    {t('points.remove')}
                </Button>
            </div>
            <p className="text-sm text-muted-foreground">
                {t('points.modeHelp')}
            </p>
            <details className="rounded-md border p-3" open={!managed}>
                <summary className="cursor-pointer font-medium">
                    {t('points.pasteTitle')}
                </summary>
                <p className="my-2 text-sm text-muted-foreground">
                    {t('points.pasteHelp')}
                </p>
                <textarea
                    aria-label={t('points.pasteText')}
                    className="h-24 w-full rounded border p-2 font-mono text-sm"
                    value={pasteText}
                    onChange={(event) => setPasteText(event.target.value)}
                />
                <div className="mt-2 flex flex-wrap items-center gap-3">
                    <label>
                        {t('points.pasteMode')}{' '}
                        <select
                            className={selectStyle}
                            value={pasteMode}
                            onChange={(event) =>
                                setPasteMode(
                                    event.target.value as 'append' | 'replace',
                                )
                            }
                        >
                            <option value="append">{t('points.append')}</option>
                            <option value="replace">
                                {t('points.replace')}
                            </option>
                        </select>
                    </label>
                    <Button
                        disabled={!pasteText}
                        onClick={() => pasteRows(pasteText)}
                    >
                        {t('points.applyPaste')}
                    </Button>
                </div>
            </details>
            <fieldset className="flex flex-wrap items-end gap-3 rounded-md border p-3">
                <legend>{t('points.bulkTitle')}</legend>
                <label>
                    {t('points.bulkField')}{' '}
                    <select
                        className={selectStyle}
                        value={bulkField}
                        onChange={(event) => {
                            setBulkField(event.target.value as typeof bulkField)
                            setBulkValue('')
                        }}
                    >
                        {(
                            ['type', 'byteOrder', 'unit', 'enabled'] as const
                        ).map((field) => (
                            <option key={field} value={field}>
                                {t(`points.fields.${field}`)}
                            </option>
                        ))}
                    </select>
                </label>
                <label>
                    {t('points.bulkValue')}{' '}
                    {choices(bulkField) ? (
                        <select
                            className={selectStyle}
                            value={bulkValue}
                            onChange={(event) =>
                                setBulkValue(event.target.value)
                            }
                        >
                            <option value="">{t('points.notSet')}</option>
                            {choices(bulkField)!
                                .filter(Boolean)
                                .map((value) => (
                                    <option key={value} value={value}>
                                        {optionLabel(bulkField, value, '')}
                                    </option>
                                ))}
                        </select>
                    ) : (
                        <input
                            className={selectStyle}
                            value={bulkValue}
                            onChange={(event) =>
                                setBulkValue(event.target.value)
                            }
                        />
                    )}
                </label>
                <Button
                    disabled={!selectedCount}
                    onClick={() => bulk(bulkField, bulkValue)}
                >
                    {t('points.applyBulk')}
                </Button>
            </fieldset>
            {error && (
                <p role="alert" className="text-destructive">
                    {error}
                </p>
            )}
            <p role="status" data-testid="point-count">
                {t('points.count', {
                    count: rows.length,
                    selected: selectedCount,
                    errors: errorRows.length,
                    warnings: validation.results.filter(
                        (result) => result.warnings.length,
                    ).length,
                })}
            </p>
            <p
                className="text-xs text-muted-foreground"
                data-testid="point-timing"
                data-paste-ms={pasteMs}
                data-validation-ms={validation.milliseconds}
            >
                {t('points.timing', {
                    paste: pasteMs.toFixed(1),
                    validation: validation.milliseconds.toFixed(1),
                })}
            </p>
            {errorRows.length > 0 && (
                <details className="rounded border border-destructive/40 p-2">
                    <summary>{t('points.firstErrors')}</summary>
                    <ul>
                        {errorRows.slice(0, 20).map((index) => (
                            <li key={rows[index].id}>
                                <button
                                    type="button"
                                    className="text-left text-sm text-destructive underline"
                                    onClick={() => locate(index)}
                                >
                                    {t('points.goTo', {
                                        row: index + 1,
                                        errors: issueText(index),
                                    })}
                                </button>
                            </li>
                        ))}
                    </ul>
                </details>
            )}
            <div
                ref={viewport}
                role="table"
                tabIndex={0}
                aria-label={t('points.grid')}
                aria-rowcount={rows.length + 1}
                aria-colcount={13}
                data-testid="point-grid"
                onPaste={onPaste}
                className="h-[520px] overflow-auto rounded-md border"
            >
                <div
                    className="relative"
                    style={{
                        height: Math.max(100, virtualizer.getTotalSize() + 40),
                        width: 1695,
                    }}
                >
                    <div
                        role="row"
                        aria-rowindex={1}
                        className="sticky top-0 z-10 grid h-10 items-center gap-1 border-b bg-muted px-1 text-sm font-medium"
                        style={{ gridTemplateColumns: gridColumns }}
                    >
                        <div role="columnheader">
                            <input
                                type="checkbox"
                                aria-label={t('points.selectAll')}
                                checked={
                                    rows.length > 0 &&
                                    table.getIsAllRowsSelected()
                                }
                                onChange={table.getToggleAllRowsSelectedHandler()}
                            />
                        </div>
                        {[
                            t('points.row'),
                            ...pointFields.map((field) =>
                                t(`points.fields.${field}`),
                            ),
                            t('points.normalized'),
                            t('points.issues'),
                        ].map((label) => (
                            <div key={label} role="columnheader">
                                {label}
                            </div>
                        ))}
                    </div>
                    {!rows.length && <p className="p-3">{t('points.empty')}</p>}
                    {virtualizer.getVirtualItems().map((virtual) => {
                        const row = tableRows[virtual.index],
                            point = row.original,
                            valid = validation.results[virtual.index]
                        const bad = valid.errors.length > 0
                        return (
                            <div
                                key={row.id}
                                role="row"
                                aria-rowindex={virtual.index + 2}
                                data-point-index={virtual.index}
                                data-invalid={bad}
                                className={`absolute left-0 grid h-[72px] items-center gap-1 border-b px-1 text-sm ${bad ? 'bg-red-50' : 'bg-background'}`}
                                style={{
                                    top: virtual.start,
                                    gridTemplateColumns: gridColumns,
                                }}
                            >
                                <div role="cell">
                                    <input
                                        type="checkbox"
                                        aria-label={t('points.selectRow', {
                                            row: virtual.index + 1,
                                        })}
                                        checked={row.getIsSelected()}
                                        onChange={row.getToggleSelectedHandler()}
                                    />
                                </div>
                                <div role="cell">{virtual.index + 1}</div>
                                {pointFields.map((field) => {
                                    const options = choices(field)
                                    const value =
                                        field === 'functionCode' &&
                                        mode === 'manual' &&
                                        valid.address
                                            ? String(valid.address.functionCode)
                                            : point[field]
                                    const label = t('points.cell', {
                                        row: virtual.index + 1,
                                        field: t(`points.fields.${field}`),
                                    })
                                    return (
                                        <div role="cell" key={field}>
                                            {options ? (
                                                <select
                                                    className={selectStyle}
                                                    aria-label={label}
                                                    value={value}
                                                    disabled={
                                                        field ===
                                                            'functionCode' &&
                                                        mode === 'manual'
                                                    }
                                                    onChange={(event) =>
                                                        edit(
                                                            point.id,
                                                            field,
                                                            event.target.value,
                                                        )
                                                    }
                                                >
                                                    {!options.includes(
                                                        value,
                                                    ) && (
                                                        <option value={value}>
                                                            {value ||
                                                                t(
                                                                    'points.notSet',
                                                                )}
                                                        </option>
                                                    )}
                                                    {options.map((item) => (
                                                        <option
                                                            key={item}
                                                            value={item}
                                                        >
                                                            {optionLabel(
                                                                field,
                                                                item,
                                                                point.type,
                                                            )}
                                                        </option>
                                                    ))}
                                                </select>
                                            ) : (
                                                <input
                                                    data-point-key={
                                                        field === 'key'
                                                            ? ''
                                                            : undefined
                                                    }
                                                    className={selectStyle}
                                                    aria-label={label}
                                                    aria-invalid={bad}
                                                    value={point[field]}
                                                    onChange={(event) =>
                                                        edit(
                                                            point.id,
                                                            field,
                                                            event.target.value,
                                                        )
                                                    }
                                                />
                                            )}
                                        </div>
                                    )
                                })}
                                <div
                                    role="cell"
                                    className="font-mono text-xs"
                                    data-testid={`address-${virtual.index}`}
                                >
                                    {valid.address
                                        ? `${valid.address.offset} / ${manualReference(valid.address)}`
                                        : '—'}
                                </div>
                                <div
                                    role="cell"
                                    title={issueText(virtual.index)}
                                    className={`line-clamp-3 text-xs ${bad ? 'text-destructive' : valid.warnings.length ? 'text-amber-800' : 'text-muted-foreground'}`}
                                >
                                    {issueText(virtual.index) ||
                                        t('points.valid')}
                                </div>
                            </div>
                        )
                    })}
                </div>
            </div>
        </fieldset>
    )
}
