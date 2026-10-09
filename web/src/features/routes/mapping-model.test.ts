import { expect, it } from 'vitest'
import { mappingRows, mappingConfig, newMappingRow } from './mapping-model'
it('round trips constants and decimal parameters without floating-point conversion', () => {
    const rows = mappingRows(
        '{"rules":[{"target":"value","type":"integer","constant":9223372036854775807,"scale":0.1234567890123456789012345678,"offset":-0,"decimalPlaces":2,"unit":"kW","required":true}]}',
    )
    expect(rows[0]).toMatchObject({
        value: '9223372036854775807',
        scale: '0.1234567890123456789012345678',
        offset: '-0',
    })
    const text = mappingConfig(rows)
    expect(text).toContain('"constant":9223372036854775807')
    expect(text).toContain('"scale":0.1234567890123456789012345678')
    expect(text).toContain('"offset":-0')
    expect(mappingRows(text)).toEqual(rows)
})
it('keeps field, JSON Pointer, numeric string and null constant distinct', () => {
    const rows = mappingRows(
        '{"rules":[{"sourceField":"v"},{"sourcePath":"/v"},{"constant":"9223372036854775807"},{"constant":null}]}',
    )
    expect(rows.map((row) => [row.source, row.value])).toEqual([
        ['field', 'v'],
        ['path', '/v'],
        ['constant', '"9223372036854775807"'],
        ['constant', 'null'],
    ])
    expect(mappingRows(mappingConfig(rows))).toEqual(rows)
})
it('allows empty and incomplete drafts without claiming executable validation', () => {
    expect(mappingConfig([])).toBe('{"rules":[]}')
    expect(mappingRows(mappingConfig([newMappingRow()]))).toEqual([
        newMappingRow(),
    ])
})
it.each([
    '{"rules":[{"sourcePath":"/a","constant":1}]}',
    '{"rules":[{"unknown":1}]}',
    '{"rules":[{"type":"script"}]}',
    '{"rules":[{"scale":"0.1"}]}',
])('rejects unsupported saved shape without dropping data %s', (text) =>
    expect(() => mappingRows(text)).toThrow(),
)
it('enforces rule bounds, numeric syntax and integer decimal places without mutating drafts', () => {
    expect(() =>
        mappingConfig(Array.from({ length: 1001 }, newMappingRow)),
    ).toThrow('limit')
    for (const patch of [
        { scale: 'NaN' },
        { offset: '1,2' },
        { decimalPlaces: '1.5' },
    ]) {
        const row = { ...newMappingRow(), ...patch }
        const original = JSON.stringify(row)
        expect(() => mappingConfig([row])).toThrow()
        expect(JSON.stringify(row)).toBe(original)
    }
})
