import { defineConfig } from '@playwright/test'

// In pinned Playwright 1.64 the failure-copy prompt otherwise takes an automatic
// ARIA snapshot even with trace/screenshot off. Excel's header contains identities.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'

// Explicit external-source acceptance, not part of the offline/default suite.
// Never trace, record video or auto-screenshot the shared workbook/its URL.
export default defineConfig({
    testDir: './acceptance',
    testMatch: 'excel-web.spec.ts',
    workers: 1,
    retries: 0,
    timeout: 180_000,
    use: {
        trace: 'off',
        screenshot: 'off',
        video: 'off',
        actionTimeout: 15_000,
        navigationTimeout: 45_000,
    },
})
