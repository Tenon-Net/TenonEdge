import { expect, it } from 'vitest'
import { outputForm, outputConfig, scalar } from './output-model'
import { writeRouteJson } from '../canvas/json'

it('defaults MQTT to the product topic, QoS 1 and envelope without changing its reference', () => {
    const form = outputForm('{"connectionId":42}')
    expect(form).toMatchObject({
        topic: 'tenonedge/{deviceKey}/data',
        qos: '1',
        mode: 'envelope',
    })
    expect(
        JSON.parse(outputConfig('{"connectionId":42}', 'output.mqtt', form)),
    ).toEqual({
        connectionId: 42,
        topic: form.topic,
        qos: 1,
        payload: { mode: 'envelope' },
    })
})
it('edits HTTP parameters without rounding payload or success constants', () => {
    const text =
        '{"connectionId":42,"path":"/old","successPath":"/ok","successValue":9223372036854775807,"payload":{"mode":"custom","fields":[{"target":"large","constant":9223372036854775807},{"target":"decimal","constant":0.1234567890123456789012345678},{"target":"text","constant":"9223372036854775807"}]}}'
    const form = outputForm(text)
    form.path = '/new'
    form.headers.push({ name: 'X-Unit', value: 'fixture' })
    const encoded = outputConfig(text, 'output.http', form)
    expect(encoded).toContain('"successValue":9223372036854775807')
    expect(encoded).toContain('"constant":9223372036854775807')
    expect(encoded).toContain('"constant":0.1234567890123456789012345678')
    expect(encoded).toContain('"constant":"9223372036854775807"')
    expect(encoded).toContain('"path":"/new"')
    expect(encoded).toContain('"connectionId":42')
})
it('supports fields, whitelisted placeholder choices, boolean and null constants distinctly', () => {
    const form = outputForm('{}')
    form.mode = 'custom'
    form.fields = [
        { target: 'v', source: 'field', value: 'voltage', required: true },
        {
            target: 'id',
            source: 'placeholder',
            value: '{sourceMessageId}',
            required: false,
        },
        { target: 'flag', source: 'constant', value: 'true', required: false },
        { target: 'empty', source: 'constant', value: 'null', required: false },
    ]
    const result = JSON.parse(outputConfig('{}', 'output.mqtt', form))
    expect(result.payload.fields[0]).toEqual({
        target: 'v',
        sourceField: 'voltage',
        required: true,
    })
    expect(result.payload.fields[1].placeholder).toBe('{sourceMessageId}')
    expect(result.payload.fields[2].constant).toBe(true)
    expect(result.payload.fields[3].constant).toBeNull()
})
it('clears custom fields when explicitly confirming another payload mode and optional HTTP success check', () => {
    const text =
        '{"successPath":"/ok","successValue":true,"payload":{"mode":"custom","fields":[]}}'
    const form = outputForm(text)
    form.successPath = ''
    form.mode = 'values'
    const config = JSON.parse(outputConfig(text, 'output.http', form))
    expect(config.payload).toEqual({ mode: 'values' })
    expect(config.successPath).toBeNull()
    expect(config).not.toHaveProperty('successValue')
})
it.each(['{}', '[]', '1e9999', 'NaN', 'unquoted'])(
    'rejects non-scalar or invalid constant %s without modifying the form',
    (value) => {
        const form = outputForm('{}')
        form.mode = 'custom'
        form.fields = [
            { target: 'x', source: 'constant', value, required: false },
        ]
        const original = JSON.stringify(form)
        expect(() => outputConfig('{}', 'output.http', form)).toThrow()
        expect(JSON.stringify(form)).toBe(original)
    },
)
it('keeps negative zero and decimal lexemes intact', () => {
    for (const text of ['-0', '1.2300e+20', '0.1234567890123456789012345678'])
        expect(writeRouteJson(scalar(text))).toBe(text)
})
it('rejects ambiguous or unsupported stored forms instead of silently dropping content', () => {
    for (const text of [
        '{"qos":2}',
        '{"payload":{"mode":"unknown"}}',
        '{"payload":{"mode":"values","fields":[]}}',
        '{"payload":{"mode":"custom","fields":[{"sourceField":"x","constant":1}]}}',
        '{"payload":{"mode":"custom","fields":[{"unknown":1}]}}',
    ])
        expect(() => outputForm(text)).toThrow()
})
it('rejects duplicate case-insensitive headers and enforces bounded rows', () => {
    const form = outputForm('{}')
    form.headers = [
        { name: 'X-A', value: 'a' },
        { name: 'x-a', value: 'b' },
    ]
    expect(() => outputConfig('{}', 'output.http', form)).toThrow('headers')
    form.headers = Array.from({ length: 101 }, (_, i) => ({
        name: `X-${i}`,
        value: 'a',
    }))
    expect(() => outputConfig('{}', 'output.http', form)).toThrow('limit')
    form.headers = []
    form.fields = Array.from({ length: 1001 }, () => ({
        target: 'x',
        source: 'field',
        value: 'v',
        required: false,
    }))
    expect(() => outputConfig('{}', 'output.http', form)).toThrow('limit')
})
