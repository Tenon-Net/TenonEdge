import { describe, expect, it, vi } from 'vitest'
import { inferSample, maxSampleBytes, selectedFields } from './sample'

describe('transient JSON sample inference', () => {
    it('lists nested leaves with fixed array indexes and escaped pointers', () => {
        expect(
            inferSample('{"a/b":{"~key":[1,true,"text",null]},"empty":{}}'),
        ).toEqual([
            { path: '/a~1b/~0key/0', type: 'number', preview: '1' },
            { path: '/a~1b/~0key/1', type: 'boolean', preview: 'true' },
            { path: '/a~1b/~0key/2', type: 'string', preview: '"text"' },
            { path: '/a~1b/~0key/3', type: null, preview: 'null' },
        ])
    })
    it('keeps numeric lexemes without integer or decimal rounding', () => {
        expect(
            inferSample('[9007199254740993,1.234567890123456789,-0,1e400]').map(
                (f) => f.preview,
            ),
        ).toEqual(['9007199254740993', '1.234567890123456789', '-0', '1e400'])
        expect(inferSample('9223372036854775807')).toEqual([
            { path: '', type: 'number', preview: '9223372036854775807' },
        ])
    })
    it('rejects invalid JSON and bounded resource overflow without partial results', () => {
        expect(() => inferSample('{bad')).toThrow('json')
        expect(() => inferSample(' '.repeat(maxSampleBytes + 1))).toThrow(
            'limit',
        )
        expect(() =>
            inferSample(JSON.stringify('温'.repeat(maxSampleBytes / 2))),
        ).toThrow('limit')
        expect(() => inferSample(JSON.stringify(Array(1001).fill(1)))).toThrow(
            'limit',
        )
        expect(() =>
            inferSample('['.repeat(65) + '0' + ']'.repeat(65)),
        ).toThrow('limit')
        expect(() =>
            inferSample(JSON.stringify({ ['a'.repeat(4096)]: 1 })),
        ).toThrow('limit')
        expect(inferSample(JSON.stringify(Array(1000).fill(1)))).toHaveLength(
            1000,
        )
    })
    it('generates only selected descriptors with unique valid keys and no values', () => {
        const fields = inferSample(
            '{"a":{"value":1},"b":{"value":2},"中文":"secret-sample", "nil":null}',
        )
        const selected = selectedFields(
            fields,
            new Set(fields.map((f) => f.path)),
            [{ key: 'value', path: '/old', type: 'integer' }],
        )
        expect(selected.map((f) => f.key)).toEqual([
            'value_2',
            'value_3',
            'field',
        ])
        expect(selected[0].type).toBe('number')
        expect(JSON.stringify(selected)).not.toContain('secret-sample')
        expect(selectedFields(fields, new Set(['/b/value']), [])).toHaveLength(
            1,
        )
        expect(() =>
            selectedFields(
                fields,
                new Set(['/b/value']),
                Array(1000).fill({ key: 'x' }),
            ),
        ).toThrow('limit')
    })
    it('rejects ambiguous object members, including escaped duplicate names', () => {
        expect(() => inferSample('{"a":1,"a":2}')).toThrow('duplicate')
        expect(() => inferSample('{"nested":{"a":1,"\\u0061":2}}')).toThrow(
            'duplicate',
        )
        expect(inferSample('[{"a":1},{"a":2}]')).toHaveLength(2)
        expect(
            inferSample('{"braces":"[{}]","quote":"\\\"a\\\": 1"}'),
        ).toHaveLength(2)
    })
    it('fails closed when the browser cannot expose original numeric tokens', () => {
        const nativeParse = JSON.parse
        const spy = vi
            .spyOn(JSON, 'parse')
            .mockImplementation((text, reviver) =>
                nativeParse(
                    text,
                    reviver
                        ? function (key, value) {
                              return reviver.call(this, key, value)
                          }
                        : undefined,
                ),
            )
        try {
            expect(() => inferSample('9007199254740993')).toThrow('browser')
        } finally {
            spy.mockRestore()
        }
    })
    it('treats prototype-looking keys and star as literal object keys', () => {
        expect(
            inferSample('{"__proto__":{"polluted":true},"*":2}').map(
                (f) => f.path,
            ),
        ).toEqual(['/__proto__/polluted', '/*'])
        expect(Object.prototype).not.toHaveProperty('polluted')
    })
})
