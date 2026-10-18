import { describe, expect, it } from 'vitest'
import { changeDeviceKind, createDeviceEditor, deviceForm } from './model'
describe('device drafts', () => {
    it('captures the version without pretending a device already exists', () => {
        const form = deviceForm('v1')
        expect(form.id).toBeUndefined()
        expect(form.expectedVersion).toBe('v1')
        expect(form.kind).toBe('modbus')
        expect(form.unitId).toBe(1)
        expect(form.connectionId).toBeNull()
    })
    it('normalizes a persisted identifier and retains simulator null settings', () => {
        const form = deviceForm('v2', {
            id: '123',
            key: 'sim',
            name: 'Simulator',
            kind: 'simulator',
            group: null,
            connectionId: null,
            unitId: null,
            addressNotation: 'offset',
            enabled: false,
        })
        expect(form.id).toBe(123)
        expect(form.unitId).toBeNull()
        expect(form.enabled).toBe(false)
    })
    it('changing protocol discards incompatible physical settings', () => {
        const form = {
            ...deviceForm('v1'),
            connectionId: 123,
            unitId: 255,
            addressNotation: 'manual' as const,
        }
        const http = changeDeviceKind(form, 'http')
        expect(http.connectionId).toBeNull()
        expect(http.unitId).toBeNull()
        expect(http.addressNotation).toBe('offset')
        expect(changeDeviceKind(http, 'modbus').unitId).toBe(1)
    })
    it('local editing retains original content and version until explicitly reopened', () => {
        const store = createDeviceEditor()
        const form = deviceForm('v1')
        store.getState().open(form)
        store.getState().edit({ ...form, name: 'unsaved' })
        expect(store.getState().original).toBe(JSON.stringify(form))
        expect(store.getState().form?.expectedVersion).toBe('v1')
        expect(store.getState().form?.name).toBe('unsaved')
        store.getState().open(deviceForm('v2'))
        expect(store.getState().form?.expectedVersion).toBe('v2')
        store.getState().open(null)
        expect(store.getState().form).toBeNull()
    })
})

it('module device edits retain its kind; switching protocols clears incompatible references', () => {
    const form = deviceForm('v1', {
        id: 1,
        key: 'sensor',
        name: 'Sensor',
        kind: 'module',
        enabled: true,
        moduleKind: 'input.testscalar',
        group: null,
        connectionId: null,
        unitId: null,
        addressNotation: 'offset',
    })
    expect(form.moduleKind).toBe('input.testscalar')
    expect(changeDeviceKind(form, 'http')).toMatchObject({
        moduleKind: null,
        connectionId: null,
        unitId: null,
    })
    expect(
        changeDeviceKind({ ...deviceForm('v2'), connectionId: 3 }, 'module'),
    ).toMatchObject({
        kind: 'module',
        moduleKind: null,
        connectionId: null,
        unitId: null,
    })
})
