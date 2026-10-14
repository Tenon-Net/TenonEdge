import { describe, expect, it } from 'vitest'
import { businessKeyPattern } from './business-key'

describe.each(['u', 'v'])('business key pattern (%s)', (flags) => {
    it.each(['a', '0', 'meter-001.A_b', 'a'.repeat(64)])(
        'accepts %s',
        (value) => {
            expect(
                new RegExp(`^(?:${businessKeyPattern})$`, flags).test(value),
            ).toBe(true)
        },
    )
    it.each([
        '',
        '.bad',
        '_bad',
        '-bad',
        'bad key',
        '设备',
        'a/b',
        'a:b',
        'a'.repeat(65),
    ])('rejects %s', (value) => {
        expect(
            new RegExp(`^(?:${businessKeyPattern})$`, flags).test(value),
        ).toBe(false)
    })
})
