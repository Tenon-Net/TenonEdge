import { describe, expect, it } from 'vitest'
import { inputForm, inputRequest, inputRows, type Input } from './model'
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
