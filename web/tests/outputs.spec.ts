import { test, expect, type BrowserContext } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('real HTTP output diagnostics show backlog, rates, retry, suspend, durable manual pause, recovery and viewer metadata', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-outputs-'))
    let code = 503
    let attempts = 0
    const bodies: string[] = []
    const server = createServer(async (request, response) => {
        let body = ''
        for await (const chunk of request) body += chunk.toString()
        bodies.push(body)
        attempts++
        response.writeHead(code, { 'Content-Type': 'application/json' })
        response.end('{"private":"output-response-private-marker"}')
    })
    server.listen(0, '127.0.0.1')
    await once(server, 'listening')
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('http-peer')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const root = '/api/edge-admin/v1/runtime/outputs'
    try {
        const seed = join(data, 'seed.json')
        // Seed/software peer only; not full no-JSON configuration or physical hardware acceptance.
        await writeFile(
            seed,
            JSON.stringify({
                schemaVersion: 1,
                units: [
                    {
                        kind: 'input.simulator',
                        key: 'meter/sample',
                        configuration: { periodMs: 1000, sample: { v: 42 } },
                    },
                    {
                        kind: 'output.http',
                        key: 'http',
                        configuration: {
                            connectionId: 11,
                            baseAddress: `http://127.0.0.1:${address.port}/`,
                            path: 'send',
                            inputs: ['meter/sample'],
                        },
                    },
                ],
            }),
        )
        const args = [
            `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
            `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
        ]
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('outputs-admin')
        await page.getByLabel('密码', { exact: true }).fill('Outputs-test-7419')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Outputs-test-7419')
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await page
            .getByRole('link', { name: '输出目标诊断', exact: true })
            .click()
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        const detail = page.getByRole('region', {
            name: '目标详情',
            exact: true,
        })
        await expect(
            detail.getByRole('heading', { name: / · 退避中$/ }),
        ).toBeVisible({ timeout: 15_000 })
        await expect
            .poll(
                async () =>
                    (await (await page.request.get(root + '/11')).json())
                        .items[0].traffic.recordsPerSecond,
                { timeout: 15_000 },
            )
            .toBeGreaterThan(0)
        const report = await (await page.request.get(root + '/11')).json()
        expect(report.items[0].queue.usage.pendingCount).toBeGreaterThan(0)
        expect(report.items[0].headBlockedSeconds).toBeGreaterThanOrEqual(0)
        expect(report.items[0].traffic.points.length).toBeGreaterThan(0)
        expect(report.items[0].capacity.seconds).toBeGreaterThan(0)
        expect(
            report.items[0].capacity.constraints.find(
                (constraint: { kind: string }) => constraint.kind === 'disk',
            ).status,
        ).toBe('estimated')
        expect(report.items[0].capacity.quotaUpperBoundSeconds).toBeGreaterThan(
            0,
        )
        await expect(detail).toContainText('k=2.3')
        await expect(
            detail
                .getByRole('img', { name: '目标最近一小时趋势' })
                .locator('circle'),
        ).toHaveCount(3)
        expect(JSON.stringify(report)).not.toContain(
            'output-response-private-marker',
        )
        expect(JSON.stringify(report)).not.toContain('"payload"')
        expect(bodies.some((body) => body.includes('42'))).toBe(true)
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        const control = '/api/edge-admin/v1/connections/11/delivery-control'
        let version: string | null = null
        async function pause(paused: boolean) {
            const response = await page.request.put(control, {
                headers: { 'X-CSRF-Token': session.csrfToken },
                data: { paused, confirmed: true, expectedVersion: version },
            })
            expect(response.ok()).toBe(true)
            version = (await response.json()).resumeVersion
        }
        code = 401
        await pause(false) // This diagnostic scenario uses the API; output-controls.spec.ts exercises the UI.
        await expect(
            detail.getByRole('heading', { name: / · 自动暂停$/ }),
        ).toBeVisible({ timeout: 15_000 })
        await pause(true)
        await expect(
            detail.getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible()
        await expect
            .poll(
                async () =>
                    (await (await page.request.get(root + '/11')).json())
                        .items[0].sender.state,
            )
            .toBe('manuallyPaused')
        const stoppedAttempts = attempts
        const beforePause = (
            await (await page.request.get(root + '/11')).json()
        ).usage.pendingCount
        await expect
            .poll(
                async () =>
                    (await (await page.request.get(root + '/11')).json()).usage
                        .pendingCount,
            )
            .toBeGreaterThan(beforePause)
        expect(attempts).toBe(stoppedAttempts)
        await page.route('**/runtime/outputs/11?*', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503}',
            }),
        )
        await detail.getByRole('button', { name: '刷新本区域' }).click()
        await expect(detail.getByRole('alert')).toBeVisible()
        await expect(
            detail.getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible() // Failed refresh retains last metadata, visibly stale.
        await page.unroute('**/runtime/outputs/11?*')
        await page.screenshot({
            path: '/tmp/tenonedge-output-diagnostics-final.png',
            fullPage: true,
        })
        let requests = 0
        page.on('request', (request) => {
            if (request.url().includes('/runtime/outputs')) requests++
        })
        await page
            .getByRole('navigation')
            .getByRole('link', { name: '账号安全', exact: true })
            .click()
        await page.waitForTimeout(100)
        const leaving = requests
        await page.waitForTimeout(2300)
        expect(requests).toBe(leaving)
        await context.close()
        context = undefined
        await host.stop()
        host = undefined
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                "UPDATE user_account SET role='Viewer', security_stamp=? WHERE account_name=?",
            ).run('outputs-viewer-fixture', 'outputs-admin')
        } finally {
            db.close()
        }
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const viewer = await context.newPage()
        await viewer.goto('/runtime/outputs')
        await viewer.getByLabel('账号', { exact: true }).fill('outputs-admin')
        await viewer
            .getByLabel('密码', { exact: true })
            .fill('Outputs-test-7419')
        await viewer.getByRole('button', { name: '登录', exact: true }).click()
        await viewer
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        await expect(
            viewer
                .getByRole('region', { name: '目标详情', exact: true })
                .getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible()
        const restarted = await (await viewer.request.get(root + '/11')).json()
        expect(restarted.usage.pendingCount).toBeGreaterThanOrEqual(beforePause)
        expect(restarted.items[0].sender.lastResult).toBeNull()
        expect(restarted.items[0].traffic.points).toHaveLength(0)
        const viewerSession = await (
            await viewer.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        expect(
            (
                await viewer.request.put(control, {
                    headers: { 'X-CSRF-Token': viewerSession.csrfToken },
                    data: {
                        paused: false,
                        confirmed: true,
                        expectedVersion: version,
                    },
                })
            ).status(),
        ).toBe(403)
        expect(attempts).toBe(stoppedAttempts)
        await context.close()
        context = undefined
        await host.stop()
        host = undefined
        const resumeDb = new DatabaseSync(join(data, 'config.db'))
        try {
            resumeDb
                .prepare(
                    "UPDATE user_account SET role='Administrator', security_stamp=? WHERE account_name=?",
                )
                .run('outputs-admin-return-fixture', 'outputs-admin')
        } finally {
            resumeDb.close()
        }
        code = 200
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const recovered = await context.newPage()
        await recovered.goto('/runtime/outputs')
        await recovered
            .getByLabel('账号', { exact: true })
            .fill('outputs-admin')
        await recovered
            .getByLabel('密码', { exact: true })
            .fill('Outputs-test-7419')
        await recovered
            .getByRole('button', { name: '登录', exact: true })
            .click()
        await expect(
            recovered.getByRole('button', { name: '查看目标 11', exact: true }),
        ).toBeVisible()
        const adminSession = await (
            await recovered.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        expect(
            (
                await recovered.request.put(control, {
                    headers: { 'X-CSRF-Token': adminSession.csrfToken },
                    data: {
                        paused: false,
                        confirmed: true,
                        expectedVersion: version,
                    },
                })
            ).ok(),
        ).toBe(true)
        await recovered
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        await expect(
            recovered.getByRole('region', { name: '目标详情', exact: true }),
        ).toContainText('本次发送成功', { timeout: 15_000 })
        await expect
            .poll(
                async () =>
                    (await (await recovered.request.get(root + '/11')).json())
                        .usage.pendingCount,
            )
            .toBeLessThan(2)
        expect(attempts).toBeGreaterThan(stoppedAttempts)
        await recovered.screenshot({
            path: '/tmp/tenonedge-output-diagnostics-recovered.png',
            fullPage: true,
        })
    } finally {
        try {
            await context?.close()
        } finally {
            try {
                await host?.stop()
            } finally {
                server.closeAllConnections()
                await new Promise<void>((resolve, reject) =>
                    server.close((error) =>
                        error ? reject(error) : resolve(),
                    ),
                )
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
