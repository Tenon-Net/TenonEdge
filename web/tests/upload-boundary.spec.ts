import { test, expect } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

// Keep strict status assertions without persisting repeated 8 MiB payloads in traces.
test.use({ trace: 'off' })
test('native browser receives complete CSV body-limit responses without retries or draft changes', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-upload-boundary-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const setup = await context.request.post('/api/edge-admin/v1/setup', {
            data: {
                accountName: 'boundary-admin',
                password: 'Boundary-browser-7284!',
                gatewayName: null,
            },
        })
        expect(setup.status()).toBe(200)
        const session = await setup.json()
        const before = await (
            await context.request.get('/api/edge-admin/v1/devices')
        ).json()
        const page = await context.newPage()
        // No application code needed to test the actual browser HTTP transport and real Kestrel limit.
        await page.goto('/healthz')
        let requests = 0
        page.on('request', (request) => {
            if (
                request.method() === 'POST' &&
                request.url().endsWith('/points/csv')
            )
                requests++
        })
        const results = await page.evaluate(async (csrf) => {
            const results: { bytes: number; status: number }[] = []
            const limit = 8 * 1024 * 1024
            for (let round = 0; round < 5; round++) {
                for (const bytes of [limit, limit + 1, limit + 14]) {
                    // {"content":""} is 14 ASCII bytes. The payload is deliberately not a valid import.
                    const body = JSON.stringify({
                        content: 'x'.repeat(bytes - 14),
                    })
                    const controller = new AbortController()
                    const timer = setTimeout(() => controller.abort(), 10_000)
                    try {
                        const response = await fetch(
                            '/api/edge-admin/v1/devices/1/points/csv',
                            {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-CSRF-Token': csrf,
                                },
                                body,
                                signal: controller.signal,
                            },
                        )
                        // Require a complete response, not only headers before a transport failure.
                        await response.text()
                        results.push({
                            bytes: new TextEncoder().encode(body).length,
                            status: response.status,
                        })
                    } finally {
                        clearTimeout(timer)
                    }
                }
            }
            return results
        }, session.csrfToken)
        expect(results).toEqual(
            Array.from({ length: 5 }, () => [
                { bytes: 8 * 1024 * 1024, status: 400 },
                { bytes: 8 * 1024 * 1024 + 1, status: 413 },
                { bytes: 8 * 1024 * 1024 + 14, status: 413 },
            ]).flat(),
        )
        expect(requests).toBe(15)
        expect(
            await (
                await context.request.get('/api/edge-admin/v1/devices')
            ).json(),
        ).toEqual(before)
        expect((await context.request.get('/healthz')).status()).toBe(200)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
