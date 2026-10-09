import { describe, it, expect } from 'vitest'
import { verify } from '../../scripts/verify-results.mjs'

const unit = {
    success: true,
    numTotalTests: 2,
    numPassedTests: 2,
    numFailedTests: 0,
    numPendingTests: 0,
    numTodoTests: 0,
}
const browser = {
    stats: { expected: 2, unexpected: 0, skipped: 0, flaky: 0 },
    errors: [],
}
describe('CI report gate', () => {
    it('accepts real nonzero counts', () =>
        expect(() => verify(unit, browser)).not.toThrow())
    it.each([
        { success: false },
        { numTotalTests: 0, numPassedTests: 0 },
        { numPassedTests: 1 },
        { numFailedTests: 1 },
        { numPendingTests: 1 },
        { numTodoTests: 1 },
    ])('rejects incomplete or failing unit reports %j', (override) => {
        expect(() => verify({ ...unit, ...override }, browser)).toThrow()
    })
    it.each([{ expected: 0 }, { unexpected: 1 }, { skipped: 1 }, { flaky: 1 }])(
        'rejects browser reports %j',
        (override) => {
            expect(() =>
                verify(unit, { stats: { ...browser.stats, ...override } }),
            ).toThrow()
        },
    )
    it('rejects global browser errors', () =>
        expect(() => verify(unit, { ...browser, errors: ['crash'] })).toThrow())
})
