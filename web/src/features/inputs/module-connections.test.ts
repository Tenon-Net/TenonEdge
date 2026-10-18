import { describe, expect, it } from 'vitest'
import {
    decodeInputList,
    inputForm,
    inputRequest,
    inputRequestJson,
    moduleConnectionEdit,
} from './model'
import { validModuleForm } from './module-editor'
import type { components } from '../../api/schema'

const modules: components['schemas']['InputModuleDefinition'][] = [
    {
        kind: 'input.reference',
        version: 1,
        displayName: 'HTTP reference',
        parameters: {
            id: 'input.reference',
            scope: 'input',
            parameters: [
                {
                    name: 'connection',
                    displayName: 'Connection',
                    type: 'connectionReference',
                    required: true,
                    connectionKinds: ['http'],
                },
                {
                    name: 'value',
                    displayName: 'Value',
                    type: 'number',
                    wholeNumber: true,
                },
            ],
        },
    },
]
const connections = [{ id: '9007199254740991', kind: 'http', name: 'Source' }]

describe('input module explicit connection bindings', () => {
    it('round-trips safe IDs separately from exact scalar parameters', () => {
        const list = decodeInputList(
            '{"version":"7","inputs":[{"id":1,"deviceId":2,"key":"source","name":"Source","kind":"module","enabled":true,"module":{"kind":"input.reference","version":1,"parameters":{"value":9007199254740993},"connections":{"connection":9007199254740991}}}]}',
        )
        const form = inputForm(list.version, list.inputs[0])
        expect(form.moduleConnections).toEqual({
            connection: '9007199254740991',
        })
        expect(form.moduleConfigText).toBe('{"value":9007199254740993}')
        expect(validModuleForm(form, modules, connections)).toBe(true)
        const body = inputRequest(form, [], 'offset')
        const text = inputRequestJson(form, body)
        expect(text).toContain('"parameters":{"value":9007199254740993}')
        expect(text).toContain('"connections":{"connection":9007199254740991}')
        expect(JSON.parse(text).module.parameters.connection).toBeUndefined()
    })
    it.each(['0', '-1', '01', '1.5', '9007199254740992', '1e1'])(
        'rejects a noncanonical or unsafe reference %s',
        (id) => {
            const form = {
                ...inputForm('v1', undefined, 'module', 'input.reference'),
                moduleVersion: 1,
                moduleConnections: { connection: id },
            }
            expect(validModuleForm(form, modules, connections)).toBe(false)
            expect(() => inputRequest(form, [], 'offset')).toThrow(
                'module-connection',
            )
        },
    )
    it('requires a current matching protocol and never converts scalar IDs implicitly', () => {
        const form = {
            ...inputForm('v1', undefined, 'module', 'input.reference'),
            moduleVersion: 1,
            moduleConnections: { connection: '9007199254740991' },
        }
        expect(validModuleForm(form, modules)).toBe(false)
        expect(
            validModuleForm(form, modules, [
                { ...connections[0], kind: 'mqtt' },
            ]),
        ).toBe(false)
        expect(() =>
            inputRequest(
                { ...form, moduleConfigText: '{"connection":null}' },
                [],
                'offset',
            ),
        ).toThrow('module-connection')
        expect(
            validModuleForm(
                { ...form, moduleConfigText: '{"connection":null}' },
                modules,
                connections,
            ),
        ).toBe(false)
        expect(
            validModuleForm(
                {
                    ...form,
                    moduleConnections: {},
                    moduleConfigText: '{"connection":"9007199254740991"}',
                },
                modules,
                connections,
            ),
        ).toBe(false)
        expect(
            validModuleForm(
                {
                    ...form,
                    moduleConnections: {
                        ...form.moduleConnections,
                        value: '9007199254740991',
                    },
                },
                modules,
                connections,
            ),
        ).toBe(false)
    })
    it('explicit selection replaces legacy null and clearing keeps every other numeric lexeme', () => {
        const before = {
            ...inputForm('v1', undefined, 'module', 'input.reference'),
            moduleVersion: 1,
            moduleConfigText: '{"connection":null,"value":9007199254740993}',
        }
        const selected = {
            ...before,
            ...moduleConnectionEdit(before, 'connection', connections[0].id),
        }
        expect(selected.moduleConfigText).toBe('{"value":9007199254740993}')
        expect(validModuleForm(selected, modules, connections)).toBe(true)
        const cleared = {
            ...selected,
            ...moduleConnectionEdit(selected, 'connection', ''),
        }
        expect(cleared.moduleConnections).toEqual({})
        expect(cleared.moduleConfigText).toBe(selected.moduleConfigText)
        expect(validModuleForm(cleared, modules, connections)).toBe(false)
        expect(before.moduleConfigText).toContain('"connection":null')
    })
})
