import type { components } from '../../api/schema'
import { createStore } from 'zustand/vanilla'

export type Device = components['schemas']['DeviceDraft']
export type DeviceRequest = components['schemas']['DeviceDraftRequest']
export type DeviceForm = DeviceRequest & { id?: number }
export const deviceKinds = ['modbus', 'mqtt', 'http', 'simulator'] as const
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
    }
}
/** Query data never owns or overwrites the local form and its captured version. */
export function createDeviceEditor() {
    return createStore<{
        form: DeviceForm | null
        original: string
        open: (form: DeviceForm | null) => void
        edit: (form: DeviceForm) => void
    }>((set) => ({
        form: null,
        original: '',
        open: (form) => set({ form, original: JSON.stringify(form) }),
        edit: (form) => set({ form }),
    }))
}
