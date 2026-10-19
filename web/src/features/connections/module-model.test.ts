import { describe, expect, it } from 'vitest'
import {
    moduleConnectionForm,
    moduleConnectionRequest,
    readConnectionList,
    selectConnectionModule,
    type ConnectionModule,
} from './module-model'
import { transformConfig, transformValues } from '../canvas/transform-model'
import { parameterIssues } from '../parameters/model'

const definition: ConnectionModule = {
    kind: 'wire',
    version: 2,
    displayName: 'Wire',
    recipientParameters: ['host'],
    parameters: {
        id: 'connection.wire',
        scope: 'connection',
        parameters: [
            {
                name: 'host',
                displayName: 'Host',
                type: 'text',
                required: true,
                maximumLength: 255,
            },
            {
                name: 'large',
                displayName: 'Large',
                type: 'number',
                defaultJson: '9007199254740993',
                wholeNumber: true,
            },
            {
                name: 'flag',
                displayName: 'Flag',
                type: 'switch',
                defaultJson: 'true',
            },
            {
                name: 'secret',
                displayName: 'Secret',
                type: 'credential',
                required: true,
            },
        ],
    },
}
const list =
    '{"version":"v1","connections":[{"id":42,"key":"wire","name":"Wire","enabled":true,"kind":"module","module":{"kind":"wire","version":2,"parameters":{"host":"peer","large":9007199254740993,"flag":false}}}]}'
describe('module connection form', () => {
    it('starts without an implicit type and initializes only the explicitly selected version', () => {
        const blank = moduleConnectionForm('v')
        expect(blank.kind).toBe('')
        const selected = selectConnectionModule(blank, definition)
        expect(selected.moduleVersion).toBe(2)
        expect(selected.parameters).toBe(
            '{"large":9007199254740993,"flag":true}',
        )
        expect(selected.parameters).not.toContain('secret')
    })
    it('preserves original numeric tokens through list, edit and serialized request', () => {
        const result = readConnectionList(list)
        const form = moduleConnectionForm(
            result.version,
            result.connections[0],
            result.exactParameters['42'],
        )
        expect(form.id).toBe(42)
        expect(transformValues(form.parameters).large).toBe('9007199254740993')
        form.parameters = transformConfig(
            form.parameters,
            definition.parameters,
            { host: 'next' },
        )
        const { serialized, body } = moduleConnectionRequest(form)
        expect(serialized).toContain('"large":9007199254740993')
        expect(serialized).toContain('"host":"next"')
        expect(serialized).toContain('"expectedVersion":"v1"')
        expect(serialized).not.toContain('secret')
        expect(body.kind).toBe('module')
    })
    it('does not silently replace saved parameters when the original text is missing', () => {
        const result = readConnectionList(list)
        expect(() => moduleConnectionForm('v', result.connections[0])).toThrow(
            'module-parameters-missing',
        )
    })
    it('rejects duplicate properties and non-object parameter text', () => {
        expect(() =>
            readConnectionList(
                list.replace('"large":9007199254740993', '"large":1,"large":2'),
            ),
        ).toThrow('duplicate')
        expect(() =>
            moduleConnectionRequest({
                ...moduleConnectionForm('v'),
                parameters: '[]',
            }),
        ).toThrow()
    })
    it('never changes a saved real kind or interprets a built-in as a module', () => {
        const result = readConnectionList(list)
        const form = moduleConnectionForm(
            'v',
            result.connections[0],
            result.exactParameters['42'],
        )
        expect(() =>
            selectConnectionModule(form, { ...definition, kind: 'other' }),
        ).toThrow('module-kind-immutable')
        expect(() =>
            moduleConnectionForm('v', {
                ...result.connections[0],
                kind: 'mqtt',
            }),
        ).toThrow('module-connection')
        expect(
            selectConnectionModule(form, { ...definition, version: 3 })
                .moduleVersion,
        ).toBe(3)
    })
    it('excludes credential inputs from draft editing and rejects secret edits', () => {
        expect(() =>
            transformConfig('{}', definition.parameters, { secret: 'private' }),
        ).toThrow('transform-field')
        const schema = {
            ...definition.parameters,
            parameters: definition.parameters.parameters?.filter(
                (field) => field.type !== 'credential',
            ),
        }
        expect(
            parameterIssues(schema, {
                host: 'peer',
                large: '9007199254740993',
            }),
        ).toEqual([])
        expect(
            parameterIssues(schema, { host: 'peer', secret: 'private' }),
        ).toEqual([{ name: '', code: 'unknown' }])
    })
})
