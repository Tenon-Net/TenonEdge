import { describe, expect, it } from 'vitest'
import { decodeGraph, encodeGraph, defaultConfig } from './codec'
import { isOutputModuleKind, isNodeKind } from './model'
import { createGraphEditor } from './store'
import { parameterIssues, type ParameterSchema } from '../parameters/model'
import {
    outputModuleConfig,
    outputModuleDefaults,
    outputModuleValues,
    validateOutputModules,
    type OutputModule,
} from './output-module-model'

const schema: ParameterSchema = {
    id: 'output.example',
    scope: 'output',
    parameters: [
        {
            name: 'connection',
            displayName: 'Connection',
            type: 'connectionReference',
            required: true,
            connectionKinds: ['http'],
        },
        {
            name: 'path',
            displayName: 'Path',
            type: 'text',
            required: true,
            maximumLength: 8,
            defaultJson: '"events"',
        },
        {
            name: 'precision',
            displayName: 'Exact',
            type: 'number',
            wholeNumber: true,
            defaultJson: '9007199254740993',
        },
        { name: 'optional', displayName: 'Optional', type: 'number' },
    ],
}
const module: OutputModule = {
    kind: 'output.example',
    version: 2,
    displayName: 'Example',
    parameters: schema,
    targetConnectionKinds: ['http'],
}
const original =
    '{"connectionId":9007199254740991,"parameters":{"path":"events","precision":9007199254740993,"optional":null},"connections":{"connection":9007199254740991}}'
const connections = [{ id: '9007199254740991', kind: 'http', name: 'Target' }]

describe('output module lossless draft form', () => {
    it.each(['null', '"wire"', '[1]', '[null]'])(
        'rejects malformed target capabilities %s without a built-in fallback',
        (json) => {
            expect(() =>
                validateOutputModules([
                    { ...module, targetConnectionKinds: JSON.parse(json) },
                ]),
            ).toThrow()
        },
    )
    it('accepts declared extension targets without hardcoding the built-in protocols', () => {
        const wire = {
            ...module,
            targetConnectionKinds: ['wire'],
            parameters: {
                ...schema,
                parameters: [
                    {
                        name: 'sink',
                        displayName: 'Sink',
                        type: 'connectionReference' as const,
                        connectionKinds: ['wire'],
                    },
                ],
            },
        }
        expect(validateOutputModules([wire])).toEqual([wire])
        const missing = JSON.parse(JSON.stringify(wire))
        delete missing.targetConnectionKinds
        expect(() => validateOutputModules([missing])).toThrow()
    })

    it('keeps exact default numbers and explicit reference/target selection separate', () => {
        const text = outputModuleDefaults(schema)
        expect(text).toBe(
            '{"connectionId":null,"parameters":{"path":"events","precision":9007199254740993},"connections":{}}',
        )
        expect(outputModuleValues(text).target).toBe('')
        expect(outputModuleValues(text).values.precision).toBe(
            '9007199254740993',
        )
    })
    it('roundtrips exact tokens and nulls without inferring connection IDs from scalar values', () => {
        const form = outputModuleValues(original)
        expect(form.target).toBe('9007199254740991')
        expect(form.connections).toEqual({ connection: form.target })
        expect(form.values).toEqual({
            path: 'events',
            precision: '9007199254740993',
        })
        expect(outputModuleConfig(original, schema, {}, form.target)).toBe(
            original,
        )
        expect(
            parameterIssues(
                schema,
                { ...form.values, ...form.connections },
                connections,
            ),
        ).toEqual([])
        const edited = outputModuleConfig(
            original,
            schema,
            { path: 'next' },
            form.target,
        )
        expect(edited).toContain('"precision":9007199254740993,"optional":null')
        expect(edited).toContain('"connection":9007199254740991')
    })
    it('allows incomplete scalars but rejects unknown and undisclosed reference fields', () => {
        expect(
            outputModuleConfig(
                original,
                schema,
                { precision: 'oops', path: '' },
                '',
            ),
        ).toContain('"precision":"oops"')
        expect(() =>
            outputModuleConfig(original, schema, { password: 'fixture' }, ''),
        ).toThrow('output-module-field')
        expect(() =>
            outputModuleConfig(original, { ...schema, parameters: [] }, {}, ''),
        ).toThrow('output-module-reference')
        expect(() =>
            outputModuleConfig(
                '{"parameters":{"connection":null}}',
                schema,
                {},
                '',
            ),
        ).toThrow('output-module-reference')
    })
    it('removes references without silently changing the target or untouched values', () => {
        const changed = outputModuleConfig(
            original,
            schema,
            { connection: '' },
            '9007199254740991',
        )
        expect(outputModuleValues(changed).connections).toEqual({})
        expect(outputModuleValues(changed).target).toBe('9007199254740991')
        expect(changed).toContain('"precision":9007199254740993')
    })
    it.each(['0', '-1', '01', '1.5', '1e1', '9007199254740992'])(
        'rejects unsafe or ambiguous reference %s',
        (id) => {
            expect(() =>
                outputModuleConfig(original, schema, { connection: id }, ''),
            ).toThrow()
            expect(() => outputModuleConfig(original, schema, {}, id)).toThrow()
        },
    )
    it.each([
        '{"parameters":{},"connections":{"connection":1,"connection":2}}',
        '{"parameters":{"connection":null},"connections":{"connection":1}}',
        '{"parameters":{"nested":{}}}',
        '{"parameters":{},"unexpected":1}',
        '{"parameters":{},"connections":{"connection":"1"}}',
    ])('does not silently normalize invalid structure %s', (text) =>
        expect(() => outputModuleValues(text)).toThrow(),
    )
    it('uses original declarations for required, range and protocol checks', () => {
        const form = outputModuleValues(original)
        expect(
            parameterIssues(
                schema,
                { precision: '1.5', path: 'waytoolong' },
                connections,
            ).map((item) => item.code),
        ).toEqual(['required', 'length', 'integer'])
        expect(
            parameterIssues(schema, { ...form.values, ...form.connections }, [
                { ...connections[0], kind: 'mqtt' },
            ]),
        ).toContainEqual({ name: 'connection', code: 'connection' })
    })
})

describe('versioned output module canvas discovery and history', () => {
    it.each([['mqtt'], ['wire'], ['http', 'http'], ['']])(
        'rejects undeclared or duplicate transport metadata %j',
        (...kinds) => {
            expect(() =>
                validateOutputModules([
                    { ...module, targetConnectionKinds: kinds },
                ]),
            ).toThrow()
        },
    )
    it('keeps an empty installed transport set empty rather than guessing built-ins', () => {
        expect(
            validateOutputModules([{ ...module, targetConnectionKinds: [] }])[0]
                .targetConnectionKinds,
        ).toEqual([])
    })
    it('accepts declared versions, never built-in overrides or credential controls', () => {
        expect(
            validateOutputModules([module, { ...module, version: 3 }]),
        ).toHaveLength(2)
        expect(() => validateOutputModules([module, module])).toThrow()
        expect(() =>
            validateOutputModules([{ ...module, version: 0 }]),
        ).toThrow()
        expect(() =>
            validateOutputModules([{ ...module, kind: 'output.http' }]),
        ).toThrow()
        expect(() =>
            validateOutputModules([
                { ...module, parameters: { ...schema, scope: 'input' } },
            ]),
        ).toThrow()
        expect(() =>
            validateOutputModules([
                {
                    ...module,
                    parameters: {
                        ...schema,
                        parameters: [
                            {
                                name: 'secret',
                                displayName: 'Secret',
                                type: 'credential',
                            },
                        ],
                    },
                },
            ]),
        ).toThrow()
    })
    it.each([
        'output.',
        'output.UPPER',
        'output.bad-name',
        'output..example',
        'output.' + 'a'.repeat(64),
    ])('rejects invalid extension kind %s', (kind) => {
        expect(isOutputModuleKind(kind)).toBe(false)
        expect(isNodeKind(kind)).toBe(false)
    })
    it('copies and undo/redoes versioned nodes with exact config and terminal direction', () => {
        const store = createGraphEditor(),
            state = () => store.getState()
        const input = state().add('input.simulator', 'Source', { x: 0, y: 0 })
        const id = state().add(
            'output.example',
            'Example',
            { x: 300, y: 0 },
            { version: 2, configText: original },
        )
        state().connect(input, id)
        expect(() => state().connect(id, input)).toThrow('direction')
        const encoded = encodeGraph(state().document)
        const read = decodeGraph(encoded)
        expect(read.definition.nodes[1]).toMatchObject({
            kind: 'output.example',
            version: 2,
            configText: original,
        })
        state().select([id], [])
        state().copy()
        state().paste()
        expect(state().document.definition.nodes[2].configText).toBe(original)
        const pasted = encodeGraph(state().document)
        state().undo()
        expect(encodeGraph(state().document)).toBe(encoded)
        state().redo()
        expect(encodeGraph(state().document)).toBe(pasted)
        expect(defaultConfig('output.example')).toBe(
            '{"connectionId":null,"parameters":{},"connections":{}}',
        )
    })
})
