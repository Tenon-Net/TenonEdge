import { describe, expect, it } from 'vitest'
import {
    changeDeviceKind,
    createDeviceEditor,
    deviceForm,
    suggestDeviceKey,
} from './model'
import { businessKeyPattern } from '../../lib/business-key'
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

it.each([
    ['Meter One', 'meter-one'],
    [' Ｍétèr 01 ', 'meter-01'],
    ['电表一', 'u7535-u8868-u4e00'],
    ['7.A_B-C', '7.a_b-c'],
    ['....', 'device'],
    ['   ', ''],
])(
    'suggests an ASCII business key from %s without a network/transliteration service',
    (name, expected) => {
        expect(suggestDeviceKey(name, [])).toBe(expected)
    },
)
it('deduplicates suggestions including truncated long names without exceeding 64 characters', () => {
    const base = 'a'.repeat(64)
    const occupied = [base, base.slice(0, 62) + '-2']
    const result = suggestDeviceKey('a'.repeat(100), occupied)
    expect(result).toBe(base.slice(0, 62) + '-3')
    expect(result).toMatch(new RegExp(`^${businessKeyPattern}$`))
    expect(suggestDeviceKey('Meter', ['meter', 'meter-2'])).toBe('meter-3')
    expect(suggestDeviceKey('😀 / 测试', [])).toMatch(
        new RegExp(`^${businessKeyPattern}$`),
    )
})
it('manual key edits, including clearing, survive name changes until explicit regeneration', () => {
    const editor = createDeviceEditor()
    editor.getState().open(deviceForm('captured'))
    editor.getState().rename('Meter One', [])
    expect(editor.getState().form?.key).toBe('meter-one')
    editor.getState().rename('Meter Two', ['meter-two'])
    expect(editor.getState().form?.key).toBe('meter-two-2')
    editor.getState().setKey('custom')
    editor.getState().rename('Meter Three', [])
    expect(editor.getState().form?.key).toBe('custom')
    editor.getState().setKey('')
    editor.getState().rename('Meter Four', [])
    expect(editor.getState().form?.key).toBe('')
    editor.getState().suggestKey(['meter-four'])
    expect(editor.getState().form?.key).toBe('meter-four-2')
    expect(editor.getState().form?.expectedVersion).toBe('captured')
    expect(editor.getState().original).toBe(
        JSON.stringify(deviceForm('captured')),
    )
    editor.getState().open(deviceForm('next'))
    editor.getState().rename('Next', [])
    expect(editor.getState().form?.key).toBe('next')
})
it('never changes a persisted key through renaming, manual editing or regeneration', () => {
    const editor = createDeviceEditor()
    editor.getState().open({ ...deviceForm('v1'), id: 123, key: 'frozen' })
    editor.getState().rename('Changed name', [])
    editor.getState().setKey('new')
    editor.getState().suggestKey([])
    expect(editor.getState().form?.key).toBe('frozen')
})
