import { describe, expect, it } from 'vitest'
import {
    moduleConnectionForm,
    moduleConnectionRequest,
    readConnectionList,
    selectConnectionModule,
    changeModuleConnectionReference,
    moduleConnectionIssues,
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

describe('explicit nested connection references', () => {
    const nested: ConnectionModule = {
        ...definition,
        parameters: {
            ...definition.parameters,
            parameters: [
                ...(definition.parameters.parameters ?? []),
                {
                    name: 'upstream',
                    displayName: 'Upstream',
                    type: 'connectionReference',
                    required: true,
                    connectionKinds: ['wire', 'http'],
                },
            ],
        },
    }
    const choices = [
        { id: '42', kind: 'wire', name: 'Self' },
        { id: '43', kind: 'http', name: 'HTTP' },
        { id: '44', kind: 'mqtt', name: 'MQTT' },
        { id: '45', kind: 'wire', name: 'Other module' },
    ]
    const saved = () => {
        const parsed = readConnectionList(
            list.replace(
                '"parameters":{',
                '"connections":{"upstream":43},"parameters":{',
            ),
        )
        return moduleConnectionForm(
            parsed.version,
            parsed.connections[0],
            parsed.exactParameters['42'],
        )
    }
    it('round-trips references outside scalar parameters and preserves exact numbers', () => {
        const form = saved()
        expect(form.connections).toEqual({ upstream: '43' })
        expect(moduleConnectionIssues(form, nested, choices)).toEqual([])
        const { body, serialized } = moduleConnectionRequest(form)
        expect(body.module?.connections).toEqual({ upstream: 43 })
        expect(serialized).toContain('"large":9007199254740993')
        expect(JSON.parse(serialized).module.parameters).not.toHaveProperty(
            'upstream',
        )
        expect(JSON.parse(serialized).module.connections).toEqual({
            upstream: 43,
        })
    })
    it('explicitly selects another module by its actual kind, then clears and reinitializes references', () => {
        const form = saved()
        const edited = changeModuleConnectionReference(form, 'upstream', '45')
        expect(moduleConnectionIssues(edited, nested, choices)).toEqual([])
        expect(form.connections).toEqual({ upstream: '43' })
        expect(
            changeModuleConnectionReference(edited, 'upstream', '').connections,
        ).toEqual({})
        expect(
            selectConnectionModule(edited, { ...nested, version: 3 })
                .connections,
        ).toEqual({})
    })
    it('rejects missing, wrong-kind, self and undeclared references without erasing saved values', () => {
        const form = saved()
        for (const id of ['42', '44', '99']) {
            const edited = changeModuleConnectionReference(form, 'upstream', id)
            expect(
                moduleConnectionIssues(edited, nested, choices),
            ).toContainEqual({ name: 'upstream', code: 'connection' })
            expect(edited.connections.upstream).toBe(id)
        }
        expect(
            moduleConnectionIssues(form, definition, choices),
        ).toContainEqual({ name: 'upstream', code: 'connection' })
        expect(moduleConnectionIssues(form, nested, [])).toContainEqual({
            name: 'upstream',
            code: 'connection',
        })
    })
    it('never treats a numeric scalar as a connection selection; explicit edit can repair it', () => {
        const form = {
            ...saved(),
            parameters:
                '{"host":"peer","large":9007199254740993,"upstream":43}',
            connections: {},
        }
        expect(moduleConnectionIssues(form, nested, choices)).toContainEqual({
            name: 'upstream',
            code: 'connection',
        })
        const edited = changeModuleConnectionReference(form, 'upstream', '43')
        expect(moduleConnectionIssues(edited, nested, choices)).toEqual([])
        expect(edited.parameters).toContain('9007199254740993')
        expect(edited.parameters).not.toContain('upstream')
    })
    it('rejects unsafe ID encodings and duplicate scalar/reference names at serialization', () => {
        for (const id of [
            '0',
            '-1',
            '01',
            '1e2',
            '1.5',
            '9007199254740992',
            '42',
        ])
            expect(() =>
                moduleConnectionRequest({
                    ...saved(),
                    connections: { upstream: id },
                }),
            ).toThrow('module-connection')
        expect(() =>
            moduleConnectionRequest({
                ...saved(),
                parameters: '{"upstream":null}',
            }),
        ).toThrow('module-connection')
    })
    it('keeps the no-reference serialized shape compatible', () => {
        const form = selectConnectionModule(
            moduleConnectionForm('v'),
            definition,
        )
        expect(moduleConnectionRequest(form).serialized).not.toContain(
            'connections',
        )
    })
})
