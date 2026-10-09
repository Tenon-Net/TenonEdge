import { readFile } from 'node:fs/promises'
import { pathToFileURL } from 'node:url'

export function verify(unit, browser) {
    if (
        unit.success !== true ||
        !(unit.numTotalTests > 0) ||
        unit.numPassedTests !== unit.numTotalTests ||
        unit.numFailedTests !== 0 ||
        unit.numPendingTests !== 0 ||
        unit.numTodoTests !== 0
    ) {
        throw new Error(
            'Vitest report must contain nonzero tests with no failures or skips',
        )
    }
    const stats = browser.stats
    if (
        !stats ||
        !(stats.expected > 0) ||
        stats.unexpected !== 0 ||
        stats.skipped !== 0 ||
        stats.flaky !== 0 ||
        browser.errors?.length
    ) {
        throw new Error(
            'Playwright report must contain nonzero tests with no failures, flakes, errors or skips',
        )
    }
}

if (
    process.argv[1] &&
    import.meta.url === pathToFileURL(process.argv[1]).href
) {
    const [unitPath, browserPath] = process.argv.slice(2)
    verify(
        JSON.parse(await readFile(unitPath, 'utf8')),
        JSON.parse(await readFile(browserPath, 'utf8')),
    )
    console.log('Frontend reports contain nonzero, passing, non-skipped tests')
}
