import { defineConfig } from '@playwright/test'
const baseURL = process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1:18082'
const browserName = process.env.TENONEDGE_TEST_BROWSER ?? 'chromium'
if (
    browserName !== 'chromium' &&
    browserName !== 'firefox' &&
    browserName !== 'webkit'
)
    throw new Error(
        'TENONEDGE_TEST_BROWSER must be chromium, firefox or webkit',
    )
export default defineConfig({
    testDir: './tests',
    fullyParallel: false,
    workers: 1,
    retries: 0,
    use: {
        baseURL,
        browserName,
        // Archive binaries are supplied explicitly for minimum-version checks.
        // The normal regression still uses the version pinned by Playwright.
        launchOptions: {
            executablePath: process.env.TENONEDGE_TEST_EXECUTABLE,
        },
        trace: 'retain-on-failure',
    },
    webServer: {
        command: 'node scripts/test-host.mjs',
        url: `${baseURL}/healthz`,
        reuseExistingServer: false,
        timeout: 30_000,
    },
})
