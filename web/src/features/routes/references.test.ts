import { expect, it } from 'vitest'
import { selectedReferences, withReferences } from './references'

it('changes source references without losing existing selections outside the current catalog page', () => {
    const text = '{"sourceIds":[1,200,300]}'
    expect(selectedReferences('input.modbus', text)).toEqual([1, 200, 300])
    expect(withReferences('input.modbus', text, [1, 300, 401])).toBe(
        '{"sourceIds":[1,300,401]}',
    )
})
it('changes only the output connection and preserves exact payload constants', () => {
    const text =
        '{"connectionId":1,"payload":{"constant":9223372036854775807,"decimal":0.1234567890123456789012345678}}'
    expect(withReferences('output.http', text, [2])).toBe(
        text.replace('"connectionId":1', '"connectionId":2'),
    )
    expect(
        selectedReferences(
            'output.http',
            withReferences('output.http', text, []),
        ),
    ).toEqual([])
})
it('rejects unsafe IDs, duplicates, too many sources and unsupported kinds', () => {
    expect(() =>
        withReferences(
            'input.http',
            '{}',
            Array.from({ length: 501 }, (_, index) => index + 1),
        ),
    ).toThrow('limit')
    expect(() => withReferences('input.http', '{}', [1, 1])).toThrow()
    expect(() => withReferences('output.mqtt', '{}', [1, 2])).toThrow()
    expect(() => withReferences('transform.map', '{}', [])).toThrow()
    expect(() =>
        withReferences('input.mqtt', '{}', [9007199254740992]),
    ).toThrow()
})
it.each([
    '{"sourceIds":[9007199254740993]}',
    '{"sourceIds":[1.00000000000000001]}',
    '{"sourceIds":["1"]}',
    '{"sourceIds":null}',
])('does not silently accept malformed reference configuration %s', (text) => {
    expect(() => selectedReferences('input.simulator', text)).toThrow()
})
