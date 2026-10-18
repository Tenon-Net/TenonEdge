import { describe, expect, it } from 'vitest'
import {
    inputForm,
    inputRequest,
    inputRows,
    decodeInputList,
    inputRequestJson,
    type Input,
} from './model'
import { createPointEditor } from '../points/store'

const input: Input = {
    id: 1,
    deviceId: 2,
    key: 'fast',
    name: 'Fast',
    kind: 'modbus',
    enabled: true,
    modbus: {
        periodMs: 5000,
        points: [
            {
                key: 'value',
                name: 'Value',
                functionCode: 3,
                address: 0,
                type: 'uint16',
                enabled: false,
            },
        ],
    },
}
describe('persisted point table boundary', () => {
    it('roundtrips manual addresses to zero-based offsets without changing enabled flags', () => {
        const rows = inputRows(input, 'manual')
        expect(rows[0].address).toBe('40001')
        const request = inputRequest(inputForm('v1', input), rows, 'manual')
        expect(request.modbus?.points[0]).toMatchObject({
            address: 0,
            functionCode: 3,
            enabled: false,
            key: 'value',
        })
        expect(request.expectedVersion).toBe('v1')
    })
    it('allows an empty draft but rejects invalid rows before the API write', () => {
        expect(
            inputRequest(inputForm('v1'), [], 'offset').modbus?.points,
        ).toEqual([])
        expect(() =>
            inputRequest(
                inputForm('v1'),
                [{ ...inputRows(input)[0], address: 'bad' }],
                'offset',
            ),
        ).toThrow('invalid-point-table')
    })
    it('loading stored rows resets selection and prevents newly allocated row ID collisions', () => {
        const store = createPointEditor()
        store.getState().add()
        store.getState().select({ '1': true })
        store.getState().load(inputRows(input), 'offset')
        store.getState().add()
        expect(store.getState().selected).toEqual({})
        expect(store.getState().rows.map((row) => row.id)).toEqual(['1', '2'])
    })
})

describe('typed JSON and simulator form requests', () => {
    it('MQTT saves only field declarations and explicit upstream time mapping', () => {
        const form = {
            ...inputForm('v2', undefined, 'mqtt'),
            topic: 'plant/meter',
            retained: true,
            timeEnabled: true,
            timePath: '/stamp',
            timeFormat: 'unixMilliseconds' as const,
            fields: [
                {
                    key: 'count',
                    path: '/values/0',
                    type: 'integer' as const,
                    name: 'Count',
                    unit: 'n',
                },
            ],
        }
        const request = inputRequest(form, [], 'offset')
        expect(request.mqtt).toMatchObject({
            topicFilter: 'plant/meter',
            processRetained: true,
            upstreamTime: { path: '/stamp', format: 'unixMilliseconds' },
            fields: form.fields,
        })
        expect(request.modbus).toBeUndefined()
        expect(request.simulator).toBeUndefined()
    })
    it('HTTP request header names cannot collide ignoring case', () => {
        const form = {
            ...inputForm('v3', undefined, 'http'),
            headers: [
                { name: 'X-Site', value: 'a' },
                { name: 'x-site', value: 'b' },
            ],
        }
        expect(() => inputRequest(form, [], 'offset')).toThrow(
            'duplicate-headers',
        )
    })
    it('custom simulator JSON is not parsed through JavaScript numbers', () => {
        const text = '{"count":9007199254740993}'
        const request = inputRequest(
            {
                ...inputForm('v4', undefined, 'simulator'),
                simulatorMode: 'custom',
                sampleJson: text,
                missing: 0.1,
            },
            [],
            'offset',
        )
        expect(request.simulator?.sampleJson).toBe(text)
        expect(request.simulator?.template).toBeNull()
        expect(request.simulator?.missingField).toBe(0.1)
    })
})

it('an explicitly enabled root-value timestamp is not lost on save', () => {
    const form = {
        ...inputForm('v', undefined, 'mqtt'),
        timeEnabled: true,
        timePath: '',
    }
    expect(inputRequest(form, [], 'offset').mqtt?.upstreamTime).toEqual({
        path: '',
        format: 'rfc3339',
    })
})

describe('input module exact JSON boundary', () => {
    const source = (parameters: string) =>
        `{"version":"7","inputs":[{"id":1,"deviceId":2,"key":"value","name":"Value","kind":"module","enabled":true,"module":{"kind":"input.testscalar","version":3,"parameters":${parameters}}}]}`
    it.each([
        '9007199254740993',
        '9223372036854775807',
        '0.1234567890123456789012345678',
        '-0',
        '1.2300e+20',
    ])('preserves the numeric token %s across read, form and save', (value) => {
        const list = decodeInputList(
            source(
                `{"value":${value},"label":"123","enabled":true,"nullable":null}`,
            ),
        )
        const form = inputForm(list.version, list.inputs[0])
        expect(form.moduleKind).toBe('input.testscalar')
        expect(form.moduleVersion).toBe(3)
        expect(form.moduleConfigText).toBe(
            `{"value":${value},"label":"123","enabled":true,"nullable":null}`,
        )
        const body = inputRequest(form, [], 'offset')
        const text = inputRequestJson(form, body)
        expect(text).toContain(`"parameters":${form.moduleConfigText}`)
        expect(text).not.toContain('"text":')
        expect(JSON.parse(text)).toMatchObject({
            expectedVersion: '7',
            module: { kind: 'input.testscalar', version: 3 },
        })
    })
    it('does not accept already-rounded numbers or nested scalar parameters', () => {
        const rounded = JSON.parse(source('{"value":9007199254740993}'))
        expect(() => inputForm('7', rounded.inputs[0])).toThrow(
            'module-parameters',
        )
        const nested = decodeInputList(source('{"value":{"nested":1}}'))
        expect(() => inputForm('7', nested.inputs[0])).toThrow(
            'module-parameters',
        )
        expect(() => decodeInputList(source('{"value":1,"value":2}'))).toThrow(
            'duplicate',
        )
    })
    it('new sources require explicit version selection without defaulting to installed versions', () => {
        const form = inputForm('v1', undefined, 'module', 'input.testscalar')
        expect(form).toMatchObject({
            moduleKind: 'input.testscalar',
            moduleVersion: 0,
            moduleConfigText: '{}',
        })
    })
    it('keeps the original non-module wire format', () => {
        const form = inputForm('v1', undefined, 'simulator')
        const body = inputRequest(form, [], 'offset')
        expect(inputRequestJson(form, body)).toBe(JSON.stringify(body))
    })
})
