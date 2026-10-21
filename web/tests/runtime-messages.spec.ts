import { test, expect, type BrowserContext } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('runtime messages show real HTTP input and queued output, preserve raw values, pause, protect bodies and clear on restart', async ({
    browser,
}, testInfo) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-messages-'))
    let secret = false
    let large = false
    let failed = false
    let reads = 0
    let largeReads = 0
    let posts = 0
    const peer = createServer((request, response) => {
        if (request.method === 'POST') {
            posts++
            request.resume()
            response.writeHead(204).end()
            return
        }
        reads++
        response.writeHead(failed ? 503 : 200, {
            'Content-Type': 'application/json',
        })
        const prefix =
            '{"v":9223372036854775807' +
            (secret ? ',"password":"fixture-private"' : '')
        const text = large
            ? ',"text":"' +
              'x'.repeat(
                  1024 * 1024 - Buffer.byteLength(prefix + ',"text":""}'),
              ) +
              '"'
            : ''
        const payload = failed ? '{}' : prefix + text + '}'
        if (large && !failed) {
            expect(Buffer.byteLength(payload)).toBe(1024 * 1024)
            largeReads++
        }
        response.end(payload)
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('message-peer')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    try {
        const seed = join(data, 'seed.json')
        const baseAddress = `http://127.0.0.1:${address.port}/`
        // Seed + local HTTP peer: software diagnostics acceptance, not the no-JSON flow.
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
                            baseAddress,
                            path: 'source',
                            periodMs: 1000,
                            timeoutMs: 1000,
                            fields: [{ key: 'v', path: '/v', type: 'integer' }],
                        },
                    },
                    {
                        kind: 'output.http',
                        key: 'route/out',
                        configuration: {
                            connectionId: 12,
                            baseAddress,
                            path: 'output',
                            timeoutMs: 1000,
                            inputs: ['meter/main'],
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
        await page.getByLabel('账号', { exact: true }).fill('messages-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Messages-browser-7419')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Messages-browser-7419')
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByRole('button', { name: '退出登录', exact: true }),
        ).toBeVisible()
        await page.goto('/diagnostics')
        await page
            .getByRole('link', { name: '运行消息调试', exact: true })
            .click()
        const choice = page.getByLabel('选择输入或输出', { exact: true })
        await choice.selectOption('input.http/meter/main')
        const records = page.getByRole('region', {
            name: '最近消息',
            exact: true,
        })
        await expect(records).toContainText('已接收')
        let bodyReads = 0
        let metadataReads = 0
        page.on('request', (request) => {
            if (
                request.url().includes('/runtime/messages/') &&
                request.url().includes('/body?')
            )
                bodyReads++
            else if (request.url().includes('/runtime/messages'))
                metadataReads++
        })
        await expect.poll(() => reads).toBeGreaterThan(1)
        expect(bodyReads).toBe(0)
        await records
            .getByRole('button', { name: /^查看正文 / })
            .first()
            .click()
        const body = page.getByRole('region', {
            name: '所选记录正文',
            exact: true,
        })
        await expect(body.locator('pre')).toContainText('9223372036854775807')
        await page
            .getByRole('button', { name: '关闭正文', exact: true })
            .click()
        await choice.selectOption('output.http/route/out')
        await expect(records).toContainText('已入队（不代表发送成功）')
        await expect.poll(() => posts).toBeGreaterThan(0)
        await records
            .getByRole('button', { name: /^查看正文 / })
            .first()
            .click()
        await expect(body.locator('pre')).toContainText('9223372036854775807')
        await page
            .getByRole('button', { name: '关闭正文', exact: true })
            .click()
        let release!: () => void
        let intercepted = false
        const held = new Promise<void>((resolve) => {
            release = resolve
        })
        await page.route('**/runtime/messages?*', async (route) => {
            intercepted = true
            await held
            await route.continue().catch(() => {}) // already aborted by pause
        })
        try {
            await expect.poll(() => intercepted).toBe(true)
            const cancelled = page.waitForEvent('requestfailed', (request) =>
                request.url().includes('/runtime/messages?'),
            )
            await page
                .getByRole('button', { name: '暂停刷新', exact: true })
                .click()
            await cancelled
        } finally {
            release()
            await page.unroute('**/runtime/messages?*')
        }
        const frozen = await records.innerText()
        const requests = metadataReads
        const beforeReads = reads
        await expect.poll(() => reads).toBeGreaterThan(beforeReads + 1)
        expect(metadataReads).toBe(requests)
        expect(await records.innerText()).toBe(frozen)
        await page
            .getByRole('button', { name: '继续刷新', exact: true })
            .click()
        await choice.selectOption('input.http/meter/main')
        await page.route('**/runtime/messages?*', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await expect(records.getByRole('alert')).toBeVisible()
        await expect(choice).toHaveValue('input.http/meter/main')
        await expect(records).toContainText('已接收')
        await page.unroute('**/runtime/messages?*')
        await expect(records.getByRole('alert')).toHaveCount(0)
        large = true
        await expect(records.getByRole('row').nth(1)).toContainText('已截断', {
            timeout: 15_000,
        })
        await expect.poll(() => largeReads).toBeGreaterThanOrEqual(3)
        // Polling can replace the first row between reading its size and clicking.
        // Capture identity and metadata from one DOM snapshot, then open that
        // exact record even if a newer sample arrives in the meantime.
        const selected = await records
            .getByRole('row')
            .nth(1)
            .evaluate((row) => ({
                size: row.querySelectorAll('td')[4]?.textContent ?? '',
                identity:
                    row.querySelectorAll('td')[1]?.textContent?.trim() ?? '',
                button: row.querySelector('button')?.textContent?.trim() ?? '',
            }))
        expect(selected.button).toMatch(/^查看正文 \d+$/)
        const sizes = /^(\d+) \/ (\d+) B/.exec(selected.size)
        expect(sizes).not.toBeNull()
        expect(Number(sizes![1])).toBe(64 * 1024)
        // OriginalBytes is the full diagnostic snapshot (including raw JSON), not only the wire body.
        expect(Number(sizes![2])).toBeGreaterThan(1024 * 1024)
        // Exercise the real refresh race deliberately: the live head advances,
        // but the body must still belong to the row whose bytes were captured.
        await expect(
            records.getByRole('row').nth(1).getByRole('button'),
        ).not.toHaveText(selected.button)
        await records
            .getByRole('button', { name: selected.button, exact: true })
            .click()
        await expect(body.getByRole('heading')).toHaveText(
            `所选记录正文 · ${selected.identity}`,
        )
        await expect(body).toContainText('已截断；以下可能不是完整 JSON。')
        await expect(body.locator('pre')).toContainText('9223372036854775807')
        expect(Buffer.byteLength(await body.locator('pre').innerText())).toBe(
            64 * 1024,
        )
        await expect(body).toContainText(`${sizes![1]} / ${sizes![2]} B`)
        await testInfo.attach('one-mib-diagnostic-evidence', {
            body: Buffer.from(
                JSON.stringify({
                    wireBytes: 1024 * 1024,
                    largeReads,
                    storedBytes: Number(sizes![1]),
                    originalBytes: Number(sizes![2]),
                    browser: await page.evaluate(() => navigator.userAgent),
                }),
            ),
            contentType: 'application/json',
        })
        await page
            .getByRole('button', { name: '关闭正文', exact: true })
            .click()
        secret = true
        await expect(records.getByRole('row').nth(1)).toContainText(
            '隐私限制',
            { timeout: 15_000 },
        )
        await expect(
            records.getByRole('row').nth(1).getByRole('button'),
        ).toBeDisabled()
        await expect(page.locator('body')).not.toContainText('fixture-private')
        await page.screenshot({
            path: '/tmp/tenonedge-runtime-messages-admin.png',
            fullPage: true,
        })
        await page.goto('/account')
        const leftRequests = metadataReads
        await expect.poll(() => reads).toBeGreaterThan(reads + 1)
        expect(metadataReads).toBe(leftRequests)
        await context.close()
        context = undefined
        await host.stop()
        host = undefined
        // Offline role fixture, not user-management UI acceptance.
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                "UPDATE user_account SET role='Viewer', security_stamp=? WHERE account_name=?",
            ).run('message-viewer', 'messages-admin')
        } finally {
            db.close()
        }
        failed = true
        secret = false
        large = false
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const viewer = await context.newPage()
        await viewer.goto('/runtime/messages')
        await viewer.getByLabel('账号', { exact: true }).fill('messages-admin')
        await viewer
            .getByLabel('密码', { exact: true })
            .fill('Messages-browser-7419')
        await viewer.getByRole('button', { name: '登录', exact: true }).click()
        await viewer
            .getByLabel('选择输入或输出', { exact: true })
            .selectOption('input.http/meter/main')
        const viewerRecords = viewer.getByRole('region', {
            name: '最近消息',
            exact: true,
        })
        await expect(viewerRecords).toContainText('尚无消息')
        await expect(viewerRecords).toContainText('只读角色')
        failed = false
        await expect(viewerRecords).toContainText('已接收', { timeout: 15_000 })
        await expect(
            viewerRecords.getByRole('button', { name: /^查看正文 / }),
        ).toHaveCount(0)
        await expect(viewer.locator('body')).not.toContainText(
            '9223372036854775807',
        )
        await viewer.screenshot({
            path: '/tmp/tenonedge-runtime-messages-viewer.png',
            fullPage: true,
        })
    } finally {
        try {
            await context?.close()
        } finally {
            try {
                await host?.stop()
            } finally {
                peer.closeAllConnections()
                await new Promise<void>((resolve, reject) =>
                    peer.close((error) => (error ? reject(error) : resolve())),
                )
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
