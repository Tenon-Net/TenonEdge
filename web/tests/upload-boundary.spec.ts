import { test, expect } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

// Keep strict status assertions without persisting repeated 1/8/16 MiB payloads in traces.
test.use({ trace: 'off' })
test('native browser receives complete source, CSV and ordinary body-limit responses without retries or draft changes', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-upload-boundary-'))
    const host = await startOwnedHost(
        data,
        0,
        [],
        new URL(process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1').hostname,
    )
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
                ['/points/csv', '/inputs', '/connections'].some((path) =>
                    request.url().endsWith(path),
                )
            )
                requests++
        })
        const results = await page.evaluate(async (csrf) => {
            const results: { path: string; bytes: number; status: number }[] =
                []
            for (let round = 0; round < 5; round++) {
                for (const [path, limit] of [
                    ['/api/edge-admin/v1/connections', 1024 * 1024],
                    [
                        '/api/edge-admin/v1/devices/1/points/csv',
                        8 * 1024 * 1024,
                    ],
                    ['/api/edge-admin/v1/devices/1/inputs', 16 * 1024 * 1024],
                ] as const) {
                    for (const bytes of [limit, limit + 1, limit + 14]) {
                        // {"content":""} is 14 ASCII bytes. The payload is deliberately not a valid request.
                        const body = JSON.stringify({
                            content: 'x'.repeat(bytes - 14),
                        })
                        const controller = new AbortController()
                        const timer = setTimeout(
                            () => controller.abort(),
                            10_000,
                        )
                        try {
                            const response = await fetch(path, {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-CSRF-Token': csrf,
                                },
                                body,
                                signal: controller.signal,
                            })
                            // Require a complete response, not only headers before a transport failure.
                            await response.text()
                            results.push({
                                path,
                                bytes: new TextEncoder().encode(body).length,
                                status: response.status,
                            })
                        } finally {
                            clearTimeout(timer)
                        }
                    }
                }
            }
            return results
        }, session.csrfToken)
        expect(results).toEqual(
            Array.from({ length: 5 }, () =>
                [
                    ['/api/edge-admin/v1/connections', 1024 * 1024],
                    [
                        '/api/edge-admin/v1/devices/1/points/csv',
                        8 * 1024 * 1024,
                    ],
                    ['/api/edge-admin/v1/devices/1/inputs', 16 * 1024 * 1024],
                ].flatMap(([path, limit]) => [
                    { path, bytes: Number(limit), status: 400 },
                    { path, bytes: Number(limit) + 1, status: 413 },
                    { path, bytes: Number(limit) + 14, status: 413 },
                ]),
            ).flat(),
        )
        expect(requests).toBe(45)
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
