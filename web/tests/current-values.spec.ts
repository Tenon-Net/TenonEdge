import { test, expect, type BrowserContext } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('real HTTP point values retain exact last success on failure, recover, paginate, enforce privacy and clear on restart', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-values-'))
    let failed = false
    let secret = false
    let reading = 42
    const server = createServer((_request, response) => {
        response.writeHead(failed ? 503 : 200, {
            'Content-Type': 'application/json',
        })
        response.end(
            failed
                ? '{}'
                : '{' +
                      Array.from(
                          { length: 104 },
                          (_, i) =>
                              `"p${String(i).padStart(3, '0')}":${reading}`,
                      ).join(',') +
                      ',"z64":9223372036854775807' +
                      (secret ? ',"password":"fixture-private"' : '') +
                      '}',
        )
    })
    server.listen(0, '127.0.0.1')
    await once(server, 'listening')
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('http-peer')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    try {
        const seed = join(data, 'seed.json')
        // Software HTTP peer and seed, not a no-JSON route-configuration acceptance.
        await writeFile(
            seed,
            JSON.stringify({
                schemaVersion: 1,
                units: [
                    {
                        kind: 'input.http',
                        key: 'meter/main',
                        configuration: {
                            connectionId: 11,
                            baseAddress: `http://127.0.0.1:${address.port}/`,
                            path: 'values',
                            periodMs: 1000,
                            timeoutMs: 1000,
                            fields: [
                                ...Array.from({ length: 104 }, (_, i) => {
                                    const key = `p${String(i).padStart(3, '0')}`
                                    return {
                                        key,
                                        path: '/' + key,
                                        type: 'integer',
                                    }
                                }),
                                { key: 'z64', path: '/z64', type: 'integer' },
                            ],
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
        console.log(
            'CURRENT VALUES OWNED HOST',
            host.url,
            data,
            'HTTP PEER',
            address.port,
        )
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('values-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Current-values-7419')
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await page
            .getByRole('link', { name: '设备运行与当前值', exact: true })
            .click()
        await page
            .getByRole('button', { name: '查看 meter', exact: true })
            .click()
        const points = page.getByRole('region', {
            name: '点位当前值',
            exact: true,
        })
        const first = points.getByRole('row').filter({
            has: page.getByRole('cell', { name: 'p000', exact: true }),
        })
        await expect(first).toContainText('42')
        await expect(first).toHaveAttribute('data-stale', 'false')
        failed = true
        await expect(first).toHaveAttribute('data-stale', 'true', {
            timeout: 15_000,
        })
        await expect(first).toContainText('过期 · 最后成功值')
        await expect(first).toContainText('42')
        await expect(
            page.getByRole('region', { name: '设备状态', exact: true }),
        ).toContainText('离线', { timeout: 15_000 })
        reading = 84
        failed = false
        await expect(first).toHaveAttribute('data-stale', 'false', {
            timeout: 15_000,
        })
        await expect(first).toContainText('84')
        await points
            .getByRole('button', { name: '下一页', exact: true })
            .click()
        await expect(points).toContainText('9223372036854775807')
        await expect(points.getByRole('row')).toHaveCount(6)
        await expect(
            points.getByRole('button', { name: '下一页', exact: true }),
        ).toBeDisabled()
        await page.route(
            '**/runtime/devices/meter/inputs/main/values?*',
            (route) =>
                route.fulfill({
                    status: 500,
                    contentType: 'application/problem+json',
                    body: '{"status":500}',
                }),
        )
        await points
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        await expect(points.getByRole('alert')).toBeVisible()
        await expect(points).toContainText('9223372036854775807')
        await expect(points).toContainText('第 2 页')
        await page.unroute('**/runtime/devices/meter/inputs/main/values?*')
        secret = true
        await expect(points).toContainText('隐私检查未通过', {
            timeout: 15_000,
        })
        await expect(points).not.toContainText('9223372036854775807')
        expect(await page.content()).not.toContain('fixture-private')
        let requests = 0
        page.on('request', (request) => {
            if (request.url().includes('/runtime/devices')) requests++
        })
        await page
            .getByRole('navigation')
            .getByRole('link', { name: '账号安全', exact: true })
            .click()
        await page.waitForTimeout(100)
        const count = requests
        await page.waitForTimeout(2300)
        expect(requests).toBe(count)
        await context.close()
        context = undefined
        await host.stop()
        host = undefined
        // Offline role fixture only; not user-management UI acceptance.
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                "UPDATE user_account SET role='Viewer', security_stamp=? WHERE account_name=?",
            ).run('current-values-viewer', 'values-admin')
        } finally {
            db.close()
        }
        failed = true
        secret = false
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const viewer = await context.newPage()
        await viewer.goto('/runtime/devices')
        await viewer.getByLabel('账号', { exact: true }).fill('values-admin')
        await viewer
            .getByLabel('密码', { exact: true })
            .fill('Current-values-7419')
        await viewer.getByRole('button', { name: '登录', exact: true }).click()
        await viewer
            .getByRole('button', { name: '查看 meter', exact: true })
            .click()
        const restarted = viewer.getByRole('region', {
            name: '点位当前值',
            exact: true,
        })
        await expect(restarted).toContainText('无可用样本')
        await expect(restarted).not.toContainText('9223372036854775807')
        reading = 126
        failed = false
        await expect(
            restarted.getByRole('row').filter({
                has: viewer.getByRole('cell', {
                    name: 'p000',
                    exact: true,
                }),
            }),
        ).toContainText('126', { timeout: 15_000 })
        await viewer.screenshot({
            path: '/tmp/tenonedge-current-values-final.png',
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
