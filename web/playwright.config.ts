import { defineConfig } from '@playwright/test'
const baseURL = process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1:18082'
export default defineConfig({
    testDir: './tests',
    fullyParallel: false,
    workers: 1,
    retries: 0,
    use: { baseURL, browserName: 'chromium', trace: 'retain-on-failure' },
    webServer: {
        command: 'node scripts/test-host.mjs',
        url: `${baseURL}/healthz`,
        reuseExistingServer: false,
        timeout: 30_000,
    },
})
