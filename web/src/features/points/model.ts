export const maxPoints = 10000
export const maxPasteCharacters = 2 * 1024 * 1024
export const pointFields = [
    'key',
    'name',
    'functionCode',
    'address',
    'type',
    'bit',
    'byteOrder',
    'unit',
    'enabled',
] as const
export type PointField = (typeof pointFields)[number]
export type AddressMode = 'offset' | 'manual' | 'register'
export type PointRow = Record<PointField, string> & {
    id: string
    extraColumns?: boolean
}
export const pointTypes = [
    'bool',
    'bit',
    'int16',
    'uint16',
    'int32',
    'uint32',
    'float32',
    'int64',
    'uint64',
    'float64',
] as const
export const byteOrders = ['ABCD', 'CDAB', 'BADC', 'DCBA'] as const
export type Address = { functionCode: number; offset: number }
export type IssueCode =
    | 'key'
    | 'duplicate'
    | 'functionCode'
    | 'address'
    | 'type'
    | 'bit'
    | 'byteOrder'
    | 'length'
    | 'enabled'
    | 'columns'
    | 'overlap'
export type RowValidation = {
    errors: IssueCode[]
    warnings: IssueCode[]
    address?: Address
}

export function blankRow(id: string): PointRow {
    return {
        id,
        key: '',
        name: '',
        functionCode: '3',
        address: '',
        type: 'uint16',
        bit: '',
        byteOrder: '',
        unit: '',
        enabled: 'true',
    }
}
const widths: Record<string, number> = {
    bool: 1,
    bit: 1,
    int16: 1,
    uint16: 1,
    int32: 2,
    uint32: 2,
    float32: 2,
    int64: 4,
    uint64: 4,
    float64: 4,
}
function integer(text: string) {
    return /^\d+$/.test(text) ? Number(text) : NaN
}

export function normalizeAddress(
    raw: string,
    functionCode: string,
    mode: AddressMode,
    width = 1,
): Address | undefined {
    let fc = integer(functionCode)
    let offset = integer(raw)
    if (mode === 'manual') {
        if (!/^[0134]\d{4,5}$/.test(raw)) return undefined
        fc = ({ '0': 1, '1': 2, '3': 4, '4': 3 } as Record<string, number>)[
            raw[0]
        ]
        offset = integer(raw.slice(1)) - 1
    } else if (mode === 'register') offset--
    if (
        ![1, 2, 3, 4].includes(fc) ||
        !Number.isInteger(offset) ||
        offset < 0 ||
        offset + width > 65536
    )
        return undefined
    return { functionCode: fc, offset }
}
export function manualReference(address: Address): string {
    const prefix = (
        { 1: '0', 2: '1', 3: '4', 4: '3' } as Record<number, string>
    )[address.functionCode]
    return prefix + String(address.offset + 1).padStart(4, '0')
}

// All rows, including disabled rows, participate in identity and bounds validation.
// Sort intervals, not every pair: 10,000 overlapping rows must not cause quadratic work.
export function validateRows(
    rows: readonly PointRow[],
    mode: AddressMode,
    otherGroupKeys: ReadonlySet<string> = new Set(),
): RowValidation[] {
    const seen = new Map<string, number>()
    const ranges: { index: number; fc: number; start: number; end: number }[] =
        []
    const result = rows.map((row, index): RowValidation => {
        const errors: IssueCode[] = []
        if (!/^[A-Za-z0-9_.-]{1,64}$/.test(row.key)) errors.push('key')
        if (otherGroupKeys.has(row.key)) errors.push('duplicate')
        if (row.name.length > 64 || row.unit.length > 64) errors.push('length')
        if (!['true', 'false'].includes(row.enabled)) errors.push('enabled')
        if (row.extraColumns) errors.push('columns')
        if (!Object.hasOwn(widths, row.type)) errors.push('type')
        const width = Object.hasOwn(widths, row.type) ? widths[row.type] : 1
        const address = normalizeAddress(
            row.address,
            row.functionCode,
            mode,
            width,
        )
        if (!address) errors.push('address')
        const fc = address?.functionCode ?? integer(row.functionCode)
        if (![1, 2, 3, 4].includes(fc)) errors.push('functionCode')
        if (
            (fc <= 2 && row.type !== 'bool') ||
            (fc >= 3 && row.type === 'bool')
        )
            errors.push('type')
        if (
            row.type === 'bit'
                ? !Number.isInteger(integer(row.bit)) || integer(row.bit) > 15
                : row.bit !== ''
        )
            errors.push('bit')
        if (
            width > 1
                ? !byteOrders.some((value) => value === row.byteOrder)
                : row.byteOrder !== ''
        )
            errors.push('byteOrder')
        if (address)
            ranges.push({
                index,
                fc: address.functionCode,
                start: address.offset,
                end: address.offset + width,
            })
        return { errors: [...new Set(errors)], warnings: [], address }
    })
    rows.forEach((row, index) => {
        const previous = seen.get(row.key)
        if (previous !== undefined) {
            if (!result[previous].errors.includes('duplicate'))
                result[previous].errors.push('duplicate')
            if (!result[index].errors.includes('duplicate'))
                result[index].errors.push('duplicate')
        } else seen.set(row.key, index)
    })
    ranges.sort((a, b) => a.fc - b.fc || a.start - b.start || b.end - a.end)
    let farthest: (typeof ranges)[number] | undefined
    for (const range of ranges) {
        if (
            farthest &&
            range.fc === farthest.fc &&
            range.start < farthest.end
        ) {
            result[range.index].warnings = ['overlap']
            result[farthest.index].warnings = ['overlap']
        }
        if (!farthest || range.fc !== farthest.fc || range.end > farthest.end)
            farthest = range
    }
    return result
}

export class PointEditError extends Error {
    constructor(
        public readonly code:
            | 'limit'
            | 'pasteSize'
            | 'quotedPaste'
            | 'selectionCount'
            | 'invalidModeChange',
    ) {
        super(code)
    }
}

// Excel TSV, including quoted tabs/newlines and doubled quotes. No Clipboard API permission is needed.
export function parsePaste(text: string): string[][] {
    if (text.length > maxPasteCharacters) throw new PointEditError('pasteSize')
    if (text === '') return []
    const rows: string[][] = []
    let row: string[] = [],
        cell = '',
        quoted = false,
        closed = false
    const finishCell = () => {
        row.push(cell)
        cell = ''
        closed = false
    }
    const finishRow = () => {
        finishCell()
        rows.push(row)
        row = []
        if (rows.length > maxPoints) throw new PointEditError('limit')
    }
    for (let i = 0; i < text.length; i++) {
        const c = text[i]
        if (quoted) {
            if (c === '"' && text[i + 1] === '"') {
                cell += '"'
                i++
            } else if (c === '"') {
                quoted = false
                closed = true
            } else cell += c
        } else if (c === '\t') finishCell()
        else if (c === '\r' || c === '\n') {
            if (c === '\r' && text[i + 1] === '\n') i++
            finishRow()
        } else if (c === '"' && cell === '' && !closed) quoted = true
        else {
            if (closed) throw new PointEditError('quotedPaste')
            cell += c
        }
    }
    if (quoted) throw new PointEditError('quotedPaste')
    if (row.length || cell !== '' || closed || !/[\r\n]$/.test(text))
        finishRow()
    return rows
}

export function applyPaste(
    rows: readonly PointRow[],
    text: string,
    selected: ReadonlySet<string>,
    mode: 'append' | 'replace',
    nextId: () => string,
): PointRow[] {
    const matrix = parsePaste(text)
    if (!matrix.length) return [...rows]
    const targets = rows.filter((row) => selected.has(row.id))
    if (mode === 'replace' && targets.length !== matrix.length)
        throw new PointEditError('selectionCount')
    if (mode === 'append' && rows.length + matrix.length > maxPoints)
        throw new PointEditError('limit')
    const pasted = matrix.map((cells, index) => {
        const row = blankRow(mode === 'replace' ? targets[index].id : nextId())
        pointFields.forEach((field, i) => {
            if (i < cells.length) row[field] = cells[i]
        })
        row.extraColumns = cells.length > pointFields.length
        return row
    })
    if (mode === 'append') return [...rows, ...pasted]
    let index = 0
    return rows.map((row) => (selected.has(row.id) ? pasted[index++] : row))
}

export function changeAddressMode(
    rows: readonly PointRow[],
    from: AddressMode,
    to: AddressMode,
): PointRow[] {
    // Do not silently reinterpret valid 40001 offsets as manual numbers, or drop invalid edits.
    return rows.map((row) => {
        const address = normalizeAddress(row.address, row.functionCode, from)
        if (!address) throw new PointEditError('invalidModeChange')
        return {
            ...row,
            functionCode: String(address.functionCode),
            address:
                to === 'manual'
                    ? manualReference(address)
                    : String(address.offset + (to === 'register' ? 1 : 0)),
        }
    })
}
