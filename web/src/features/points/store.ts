import { createStore } from 'zustand/vanilla'
import {
    applyPaste,
    blankRow,
    changeAddressMode,
    maxPoints,
    PointEditError,
    type AddressMode,
    type PointField,
    type PointRow,
} from './model'

export function createPointEditor(
    initialRows: PointRow[] = [],
    initialMode: AddressMode = 'offset',
) {
    let sequence = initialRows.reduce(
        (max, row) => Math.max(max, Number(row.id) || 0),
        0,
    )
    const nextId = () => String(++sequence)
    return createStore<{
        rows: PointRow[]
        selected: Record<string, boolean>
        mode: AddressMode
        load: (rows: PointRow[], mode: AddressMode) => void
        edit: (id: string, field: PointField, value: string) => void
        select: (selected: Record<string, boolean>) => void
        add: () => void
        remove: () => void
        paste: (text: string, mode: 'append' | 'replace') => void
        bulk: (
            field: 'type' | 'byteOrder' | 'unit' | 'enabled',
            value: string,
        ) => void
        changeMode: (mode: AddressMode) => void
    }>((set, get) => ({
        rows: initialRows,
        selected: {},
        mode: initialMode,
        load: (rows, mode) => {
            sequence = rows.reduce(
                (max, row) => Math.max(max, Number(row.id) || 0),
                0,
            )
            set({ rows, mode, selected: {} })
        },
        edit: (id, field, value) =>
            set((state) => ({
                rows: state.rows.map((row) =>
                    row.id === id ? { ...row, [field]: value } : row,
                ),
            })),
        select: (selected) => set({ selected }),
        add: () => {
            if (get().rows.length >= maxPoints)
                throw new PointEditError('limit')
            set((state) => ({ rows: [...state.rows, blankRow(nextId())] }))
        },
        remove: () =>
            set((state) => ({
                rows: state.rows.filter((row) => !state.selected[row.id]),
                selected: {},
            })),
        paste: (text, mode) =>
            set((state) => ({
                rows: applyPaste(
                    state.rows,
                    text,
                    new Set(
                        Object.keys(state.selected).filter(
                            (id) => state.selected[id],
                        ),
                    ),
                    mode,
                    nextId,
                ),
            })),
        bulk: (field, value) =>
            set((state) => ({
                rows: state.rows.map((row) =>
                    state.selected[row.id] ? { ...row, [field]: value } : row,
                ),
            })),
        changeMode: (mode) =>
            set((state) => ({
                rows: changeAddressMode(state.rows, state.mode, mode),
                mode,
            })),
    }))
}
