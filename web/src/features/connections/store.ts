import { createStore } from 'zustand/vanilla'
import type { ConnectionForm } from './model'
/** A query refresh never replaces the local editor or its captured draft version. */
export function createConnectionEditor() {
    return createStore<{
        form: ConnectionForm | null
        original: string
        setForm: (
            value:
                | ConnectionForm
                | null
                | ((current: ConnectionForm | null) => ConnectionForm | null),
        ) => void
        setOriginal: (value: string) => void
    }>((set) => ({
        form: null,
        original: '',
        setForm: (value) =>
            set((state) => ({
                form: typeof value === 'function' ? value(state.form) : value,
            })),
        setOriginal: (original) => set({ original }),
    }))
}
