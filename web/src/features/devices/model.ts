import type { components } from '../../api/schema'
import { createStore } from 'zustand/vanilla'

export type Device = components['schemas']['DeviceDraft']
export type DeviceRequest = components['schemas']['DeviceDraftRequest']
export type DeviceForm = DeviceRequest & { id?: number }
export const deviceKinds = [
    'modbus',
    'mqtt',
    'http',
    'simulator',
    'module',
] as const
export function deviceForm(version: string, device?: Device): DeviceForm {
    return {
        id: device ? Number(device.id) : undefined,
        expectedVersion: version,
        key: device?.key ?? '',
        name: device?.name ?? '',
        kind: device?.kind ?? 'modbus',
        group: device?.group ?? '',
        connectionId: device?.connectionId ?? null,
        unitId: device?.unitId ?? (device ? null : 1),
        addressNotation: device?.addressNotation ?? 'offset',
        enabled: device?.enabled ?? true,
        moduleKind: device?.moduleKind ?? null,
    }
}
export function changeDeviceKind(
    form: DeviceForm,
    kind: DeviceForm['kind'],
): DeviceForm {
    return {
        ...form,
        kind,
        connectionId: null,
        unitId: kind === 'modbus' ? 1 : null,
        addressNotation: 'offset',
        moduleKind: null,
    }
}

/** A local suggestion, not an ID reservation. The original server transaction
 * remains authoritative for uniqueness and keys retained by runtime versions. */
export function suggestDeviceKey(
    name: string,
    existingKeys: Iterable<string>,
): string {
    if (!name.trim()) return ''
    const normalized = name
        .normalize('NFKD')
        .replace(/[\u0300-\u036f]/g, '')
        .toLowerCase()
    const base = (
        Array.from(normalized, (character) => {
            if (/[a-z0-9_.-]/.test(character)) return character
            const code = character.codePointAt(0)!
            return code >= 128 ? `-u${code.toString(16)}-` : '-'
        })
            .join('')
            .replace(/-+/g, '-')
            .replace(/^[^a-z0-9]+|[-_.]+$/g, '') || 'device'
    ).slice(0, 64)
    const used = new Set(existingKeys)
    let key = base
    for (let suffix = 2; used.has(key); suffix++) {
        const end = `-${suffix}`
        key = base.slice(0, 64 - end.length) + end
    }
    return key
}
/** Query data never owns or overwrites the local form and its captured version. */
export function createDeviceEditor() {
    return createStore<{
        form: DeviceForm | null
        original: string
        keyEdited: boolean
        open: (form: DeviceForm | null) => void
        edit: (form: DeviceForm) => void
        rename: (name: string, keys: Iterable<string>) => void
        setKey: (key: string) => void
        suggestKey: (keys: Iterable<string>) => void
    }>((set) => ({
        form: null,
        original: '',
        keyEdited: false,
        open: (form) =>
            set({
                form,
                original: JSON.stringify(form),
                keyEdited: form?.id !== undefined,
            }),
        edit: (form) => set({ form }),
        rename: (name, keys) =>
            set((state) =>
                state.form
                    ? {
                          form: {
                              ...state.form,
                              name,
                              key:
                                  state.form.id === undefined &&
                                  !state.keyEdited
                                      ? suggestDeviceKey(name, keys)
                                      : state.form.key,
                          },
                      }
                    : {},
            ),
        setKey: (key) =>
            set((state) =>
                state.form?.id === undefined && state.form
                    ? { form: { ...state.form, key }, keyEdited: true }
                    : {},
            ),
        suggestKey: (keys) =>
            set((state) =>
                state.form?.id === undefined && state.form
                    ? {
                          form: {
                              ...state.form,
                              key: suggestDeviceKey(state.form.name, keys),
                          },
                          keyEdited: false,
                      }
                    : {},
            ),
    }))
}
