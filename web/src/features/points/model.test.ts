import { describe, expect, it } from 'vitest'
import {
    applyPaste,
    blankRow,
    changeAddressMode,
    manualReference,
    maxPasteCharacters,
    normalizeAddress,
    parsePaste,
    validateRows,
    type PointRow,
} from './model'
import { createPointEditor } from './store'
function point(overrides: Partial<PointRow> = {}): PointRow {
    return { ...blankRow('1'), key: 'temperature', address: '0', ...overrides }
}

describe('explicit Modbus addresses', () => {
    it.each([
        ['40001', '3', 'offset', 40001, 3],
        ['40001', '1', 'manual', 0, 3],
        ['00001', '3', 'manual', 0, 1],
        ['100001', '3', 'manual', 0, 2],
        ['30001', '3', 'manual', 0, 4],
        ['465536', '3', 'manual', 65535, 3],
        ['65536', '3', 'register', 65535, 3],
    ] as const)(
        '%s in %s mode is normalized explicitly',
        (raw, fc, mode, offset, functionCode) => {
            expect(normalizeAddress(raw, fc, mode)).toEqual({
                offset,
                functionCode,
            })
        },
    )
    it.each([
        '40000',
        '265536',
        '465537',
        '4000010',
        '4.001',
        ' 40001',
        '٤٠٠٠١',
    ])('rejects invalid full reference %s', (raw) => {
        expect(normalizeAddress(raw, '3', 'manual')).toBeUndefined()
    })
    it('checks the final occupied register, not only the start', () => {
        expect(normalizeAddress('65532', '3', 'offset', 4)).toBeDefined()
        expect(normalizeAddress('65533', '3', 'offset', 4)).toBeUndefined()
        expect(normalizeAddress('-1', '3', 'offset')).toBeUndefined()
        expect(normalizeAddress('1e2', '3', 'offset')).toBeUndefined()
    })
    it('round trips every function at the five/six digit boundary without losing leading zeros', () => {
        for (const functionCode of [1, 2, 3, 4])
            for (const offset of [0, 9998, 9999, 65535]) {
                const address = { functionCode, offset }
                expect(
                    normalizeAddress(manualReference(address), '3', 'manual'),
                ).toEqual(address)
            }
    })
    it('converts existing offsets instead of reinterpreting them and rejects invalid edits atomically', () => {
        const rows = [point({ address: '40001' })]
        expect(changeAddressMode(rows, 'offset', 'manual')[0].address).toBe(
            '440002',
        )
        expect(rows[0].address).toBe('40001')
        expect(() =>
            changeAddressMode([point({ address: 'bad' })], 'offset', 'manual'),
        ).toThrow('invalidModeChange')
    })
})

describe('point validation', () => {
    it.each([
        [{ key: '' }, 'key'],
        [{ key: 'with space' }, 'key'],
        [{ type: 'constructor' }, 'type'],
        [{ functionCode: '1' }, 'type'],
        [{ type: 'bool' }, 'type'],
        [{ type: 'bit', bit: '16' }, 'bit'],
        [{ type: 'uint32' }, 'byteOrder'],
        [{ byteOrder: 'ABCD' }, 'byteOrder'],
        [{ bit: '0' }, 'bit'],
        [{ enabled: 'yes' }, 'enabled'],
        [{ name: 'a'.repeat(65) }, 'length'],
        [{ extraColumns: true }, 'columns'],
        [{ type: 'uint64', byteOrder: 'ABCD', address: '65533' }, 'address'],
    ] as const)('validates bad row %j', (overrides, code) => {
        expect(validateRows([point(overrides)], 'offset')[0].errors).toContain(
            code,
        )
    })
    it.each(Array.from({ length: 16 }, (_, index) => index))(
        'accepts register bit %i',
        (bit) => {
            expect(
                validateRows(
                    [point({ type: 'bit', bit: String(bit) })],
                    'offset',
                )[0].errors,
            ).toEqual([])
        },
    )
    it('flags both duplicate rows, including disabled points and keys from other groups', () => {
        const results = validateRows(
            [point(), point({ enabled: 'false' }), point({ key: 'elsewhere' })],
            'offset',
            new Set(['elsewhere']),
        )
        expect(
            results.every((result) => result.errors.includes('duplicate')),
        ).toBe(true)
    })
    it('overlap is only a warning and separate functions do not overlap', () => {
        const result = validateRows(
            [
                point({ type: 'uint64', byteOrder: 'ABCD' }),
                point({ key: 'b', address: '3' }),
                point({ key: 'c', functionCode: '4' }),
            ],
            'offset',
        )
        expect(result.map((row) => row.warnings)).toEqual([
            ['overlap'],
            ['overlap'],
            [],
        ])
        expect(result.every((row) => row.errors.length === 0)).toBe(true)
    })
    it('validates 10000 rows and identifies one bad row without poisoning neighbors', () => {
        const rows = Array.from({ length: 10000 }, (_, index) =>
            point({
                id: String(index),
                key: `p${index}`,
                address: String(index),
            }),
        )
        rows[5000].bit = 'bad'
        const results = validateRows(rows, 'offset')
        expect(results.filter((row) => row.errors.length)).toHaveLength(1)
        expect(results[5000].errors).toEqual(['bit'])
    })
})

it.each(['00', '01', '015'])(
    'accepts zero-padded bit numbers %s as decimal values',
    (bit) => {
        expect(
            validateRows([point({ type: 'bit', bit })], 'offset')[0].errors,
        ).toEqual([])
    },
)

describe('paste and editor ownership', () => {
    it('preserves strings, empty cells, CRLF, embedded tabs/newlines and escaped quotes', () => {
        expect(
            parsePaste('key\t"name\twith\nline and ""quote"""\t03\t00001\r\n'),
        ).toEqual([['key', 'name\twith\nline and "quote"', '03', '00001']])
        expect(parsePaste('a\t\tb\n\n')).toEqual([['a', '', 'b'], ['']])
    })
    it.each(['"unclosed', '"closed"suffix'])(
        'rejects structurally broken TSV %s',
        (text) => expect(() => parsePaste(text)).toThrow('quotedPaste'),
    )
    it('bounds the entire paste before allocating cells and rows', () => {
        expect(() => parsePaste('x'.repeat(maxPasteCharacters + 1))).toThrow(
            'pasteSize',
        )
        expect(() => parsePaste('key\n'.repeat(10001))).toThrow('limit')
    })
    it('replaces exactly selected rows in table order, retains identities, and keeps invalid row text', () => {
        const rows = [
            point({ id: 'a' }),
            point({ id: 'b' }),
            point({ id: 'c' }),
        ]
        const next = applyPaste(
            rows,
            'new\t\t3\tbad',
            new Set(['b']),
            'replace',
            () => 'unused',
        )
        expect(next[0]).toBe(rows[0])
        expect(next[2]).toBe(rows[2])
        expect(next[1].id).toBe('b')
        expect(next[1].address).toBe('bad')
        expect(() =>
            applyPaste(rows, 'x\ny', new Set(['b']), 'replace', () => 'unused'),
        ).toThrow('selectionCount')
    })
    it('failed edits do not modify the store and bulk operations only affect selected rows', () => {
        const store = createPointEditor()
        store.getState().paste('a\t\t3\t0\n' + 'b\t\t3\t1', 'append')
        const rows = store.getState().rows
        store.getState().select({ [rows[0].id]: true })
        store.getState().bulk('unit', '℃')
        expect(store.getState().rows.map((row) => row.unit)).toEqual(['℃', ''])
        expect(() => store.getState().paste('x\ny', 'replace')).toThrow(
            'selectionCount',
        )
        expect(store.getState().rows.map((row) => row.key)).toEqual(['a', 'b'])
        store.getState().remove()
        expect(store.getState().rows[0].key).toBe('b')
        expect(store.getState().selected).toEqual({})
    })
    it('append at 10000 is rejected without overwriting earlier data', () => {
        const store = createPointEditor()
        store
            .getState()
            .paste(
                Array.from(
                    { length: 10000 },
                    (_, i) => `p${i}\t\t3\t${i}`,
                ).join('\n'),
                'append',
            )
        expect(() => store.getState().add()).toThrow('limit')
        expect(() => store.getState().paste('extra', 'append')).toThrow('limit')
        expect(store.getState().rows).toHaveLength(10000)
    })
})
