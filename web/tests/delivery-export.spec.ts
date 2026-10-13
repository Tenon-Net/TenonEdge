import {
    test,
    expect,
    type Page,
    type BrowserContext,
    type Download,
} from '@playwright/test'
import { mkdtemp, rm, writeFile, chmod, stat } from 'node:fs/promises'
import { createReadStream } from 'node:fs'
import { createInterface } from 'node:readline'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const exportRoot = root + '/deliveries/export'
const password = 'Export-test-9472'
const panel = (page: Page) =>
    page.getByRole('region', { name: '导出故障记录', exact: true })
const button = (page: Page, name: string) =>
    panel(page).getByRole('button', { name, exact: true })
async function csrf(page: Page) {
    return {
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    }
}
async function status(page: Page) {
    return (await (await page.request.get(exportRoot)).json()).export
}
async function confirm(page: Page) {
    await panel(page)
        .getByLabel(
            '我已核对完整范围、条数和时间，确认导出并妥善保护完整业务正文',
        )
        .check()
    await button(page, '确认生成完整导出文件').click()
}
async function authenticate(page: Page, setup = false) {
    await page.addInitScript(() => {
        const original = window.fetch
        window.fetch = (...args) => {
            const input = args[0]
            const url = input instanceof Request ? input.url : String(input)
            if (
                url.includes('/deliveries/export/') &&
                url.endsWith('/download')
            )
                throw new Error('file must not use window.fetch')
            return original(...args)
        }
    })
    await page.goto(setup ? '/' : '/deliveries')
    await page.getByLabel('账号', { exact: true }).fill('export-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page
        .getByRole('button', {
            name: setup ? '创建管理员并登录' : '登录',
            exact: true,
        })
        .click()
    if (setup) {
        await expect(
            page.getByRole('link', { name: '输出目标诊断', exact: true }),
        ).toBeVisible()
        await page.goto('/deliveries')
    }
    await expect(
        page.getByRole('heading', { name: '失败区与隔离区', exact: true }),
    ).toBeVisible()
}
async function nativeFile(page: Page) {
    const event = page.waitForEvent('download')
    await panel(page)
        .getByRole('link', { name: '下载完整 JSON Lines 文件', exact: true })
        .click()
    const download = await event
    expect(await download.failure()).toBeNull()
    expect(download.suggestedFilename()).toMatch(
        /^tenonedge-deliveries-[0-9a-f-]+\.jsonl$/,
    )
    return download
}
async function inspect(
    download: Download,
    each: (record: Record<string, unknown>) => void,
) {
    const path = await download.path()
    if (!path) throw new Error('download-path')
    let count = 0
    const lines = createInterface({
        input: createReadStream(path),
        crlfDelay: Infinity,
    })
    for await (const line of lines) {
        each(JSON.parse(line))
        count++
    }
    return { count, bytes: (await stat(path)).size }
}

test('native export of real failures: applied frozen scope, cancel, new arrivals, lost reply, stale review, cleanup failure and roles', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-export-browser-'))
    const payloads: string[] = []
    const peer = createServer(async (request, response) => {
        const chunks: Buffer[] = []
        for await (const chunk of request) chunks.push(Buffer.from(chunk))
        payloads.push(Buffer.concat(chunks).toString('utf8'))
        response.writeHead(422)
        response.end()
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('peer-address')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const seed = join(data, 'seed.json')
    const args = [
        `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
        `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
    ]
    async function stop() {
        await context?.close()
        context = undefined
        await host?.stop()
        host = undefined
    }
    async function rows(page: Page) {
        return (
            await (
                await page.request.get(root + '/deliveries?state=failed')
            ).json()
        ).items as {
            sequence: number
            deliveryId: string
            connectionId: number
            migrationId: null
        }[]
    }
    async function pause(page: Page, paused: boolean) {
        const control = await (
            await page.request.get(root + '/connections/11/delivery-control')
        ).json()
        expect(
            (
                await page.request.put(
                    root + '/connections/11/delivery-control',
                    {
                        headers: await csrf(page),
                        data: {
                            paused,
                            confirmed: true,
                            expectedVersion: control.resumeVersion,
                        },
                    },
                )
            ).status(),
        ).toBe(200)
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(root + '/runtime/outputs/11')
                        ).json()
                    ).items[0].sender.state,
            )
            .toBe(paused ? 'manuallyPaused' : 'normal')
    }
    try {
        await writeFile(
            seed,
            JSON.stringify({
                schemaVersion: 1,
                units: [
                    {
                        kind: 'input.simulator',
                        key: 'meter/sample',
                        configuration: {
                            periodMs: 1000,
                            sample: { text: 'x'.repeat(70 * 1024) },
                        },
                    },
                    {
                        kind: 'output.http',
                        key: 'out',
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
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        let page = await context.newPage()
        await authenticate(page, true)
        await expect
            .poll(async () => (await rows(page)).length)
            .toBeGreaterThanOrEqual(3)
        await pause(page, true)
        const selected = await rows(page)
        await page.getByLabel('目标连接编号', { exact: true }).fill('11')
        await page
            .getByLabel('原因包含（区分大小写）', { exact: true })
            .fill('422')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await page
            .getByLabel('原因包含（区分大小写）', { exact: true })
            .fill('UNAPPLIED')
        await button(page, '核对整个已应用筛选的导出').click()
        await expect(button(page, '确认生成完整导出文件')).toBeDisabled()
        await expect(panel(page)).toContainText(
            `完整范围 ${selected.length} 条`,
        )
        await expect(panel(page)).toContainText('原因 422')
        await expect(panel(page)).not.toContainText('UNAPPLIED')
        await expect(panel(page)).not.toContainText('x'.repeat(100))
        await button(page, '取消导出并清理临时文件').click()
        await expect(panel(page)).toContainText('导出已取消')
        expect(await rows(page)).toHaveLength(selected.length)
        await button(page, '核对整个已应用筛选的导出').click()
        await expect(panel(page)).toContainText(
            `完整范围 ${selected.length} 条`,
        )
        const selectedId = (await status(page)).id
        // Same account in a second real browser session cannot take the first session's resource.
        const otherContext = await browser.newContext({ baseURL: host.url })
        try {
            const other = await otherContext.newPage()
            await authenticate(other)
            expect(await status(other)).toBeNull()
            expect(
                (
                    await other.request.get(
                        `${exportRoot}/${selectedId}/download`,
                    )
                ).status(),
            ).toBe(404)
        } finally {
            await otherContext.close()
        }
        let confirmations = 0
        await page.route('**/deliveries/export/*/confirm', async (route) => {
            const response = await route.fetch()
            expect(response.status()).toBe(202)
            confirmations++
            await route.abort('connectionreset')
        })
        await confirm(page)
        await expect(panel(page)).toContainText('请求失败或响应丢失')
        await expect.poll(async () => (await status(page)).state).toBe('ready')
        await pause(page, false)
        await expect
            .poll(async () => (await rows(page)).length)
            .toBeGreaterThan(selected.length)
        await pause(page, true)
        expect(confirmations).toBe(1)
        await page.unroute('**/deliveries/export/*/confirm')
        await button(page, '重新读取导出状态').click()
        const downloadRequests: string[] = []
        page.on('request', (request) => {
            if (request.url().endsWith('/download'))
                downloadRequests.push(request.resourceType())
        })
        const download = await nativeFile(page)
        const ids: unknown[] = []
        const file = await inspect(download, (record) => {
            ids.push(record.deliveryId)
            expect(record.encoding).toBe('utf-8')
            expect(typeof record.content).toBe('string')
            const content = String(record.content)
            expect(Buffer.byteLength(content)).toBeGreaterThan(65536)
            expect(record.payloadBytes).toBe(Buffer.byteLength(content))
            expect(payloads).toContain(content)
            expect(record).not.toHaveProperty('headers')
            expect(record).not.toHaveProperty('fingerprint')
        })
        expect(file.count).toBe(selected.length)
        expect(ids).toEqual(selected.map((row) => row.deliveryId))
        expect(download.url()).toBe(
            `${host.url}${exportRoot}/${selectedId}/download`,
        )
        expect(downloadRequests).not.toContain('fetch')
        expect(downloadRequests).not.toContain('xhr')
        await expect(panel(page)).toContainText('服务器不能证明保存成功')
        await page.screenshot({
            path: '/tmp/tenonedge-export-ui-downloaded.png',
            fullPage: true,
        })
        // A genuine concurrent cleanup invalidates the new reviewed scope, never a partial successful file.
        await button(page, '核对整个已应用筛选的导出').click()
        await expect(button(page, '确认生成完整导出文件')).toBeVisible()
        const currentRows = await rows(page)
        expect(
            (
                await page.request.post(root + '/deliveries/cleanup', {
                    headers: await csrf(page),
                    data: {
                        state: 'failed',
                        confirmed: true,
                        items: [currentRows[0]],
                    },
                })
            ).status(),
        ).toBe(200)
        await confirm(page)
        await expect(panel(page)).toContainText('核对范围内的记录已变化')
        await expect(
            panel(page).getByRole('link', { name: '下载完整 JSON Lines 文件' }),
        ).toHaveCount(0)
        await button(page, '核对整个已应用筛选的导出').click()
        await expect(button(page, '确认生成完整导出文件')).toBeVisible()
        await confirm(page)
        await expect(
            panel(page).getByRole('link', { name: '下载完整 JSON Lines 文件' }),
        ).toBeVisible()
        const directory = join(data, 'delivery-export')
        try {
            await chmod(directory, 0o500)
            await button(page, '取消导出并清理临时文件').click()
            await expect(panel(page)).toContainText('临时文件清理失败')
            await expect(
                button(page, '核对整个已应用筛选的导出'),
            ).toBeDisabled()
            expect(
                (await stat(join(directory, 'snapshot.jsonl.tmp'))).size,
            ).toBeGreaterThan(0)
            await page.screenshot({
                path: '/tmp/tenonedge-export-ui-cleanup-failed.png',
                fullPage: true,
            })
        } finally {
            await chmod(directory, 0o700)
        }
        await stop()
        for (const role of ['Configurator', 'Viewer']) {
            const db = new DatabaseSync(join(data, 'config.db'))
            try {
                db.prepare(
                    'UPDATE user_account SET role=?,security_stamp=? WHERE account_name=?',
                ).run(role, randomUUID(), 'export-admin')
            } finally {
                db.close()
            }
            host = await startOwnedHost(data, 0, args)
            context = await browser.newContext({ baseURL: host.url })
            page = await context.newPage()
            await authenticate(page)
            await expect(panel(page)).toHaveCount(0)
            expect((await page.request.get(exportRoot)).status()).toBe(403)
            expect(
                (
                    await page.request.post(exportRoot, {
                        headers: await csrf(page),
                        data: { state: 'failed' },
                    })
                ).status(),
            ).toBe(403)
            expect(
                (
                    await page.request.get(
                        `${exportRoot}/${selectedId}/download`,
                    )
                ).status(),
            ).toBe(403)
            await stop()
        }
    } finally {
        await stop()
        peer.closeAllConnections()
        await new Promise<void>((resolve) => peer.close(() => resolve()))
        await rm(data, { recursive: true, force: true })
    }
})

test('native browser exports all 1025 quarantined fixture records and more than 64 MiB without page batching or JavaScript file buffering', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-export-bulk-browser-'))
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    async function stop() {
        await context?.close()
        context = undefined
        await host?.stop()
        host = undefined
    }
    try {
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        await authenticate(await context.newPage(), true)
        await stop()
        // Offline storage-size fixture, not a protocol, migration, or no-code configuration acceptance claim.
        const db = new DatabaseSync(join(data, 'queue.db'))
        const ids: string[] = []
        let totalBytes = 0
        try {
            db.exec('BEGIN')
            const insert = db.prepare(`INSERT INTO delivery
                (delivery_id, connection_id, source_message_id, device_key, revision, payload, fingerprint, fingerprint_summary, created_at, state, quarantine_reason, byte_size)
                VALUES (?, 99, ?, 'bulk-fixture', 1, ?, 'fixture', 'fixture', ?, 'quarantined', 'targetMissing', ?)`)
            for (let i = 0; i < 1025; i++) {
                const id = randomUUID()
                ids.push(id)
                const body = Buffer.from(
                    JSON.stringify({
                        messageId: id,
                        index: i,
                        text: 'z'.repeat(70 * 1024),
                    }),
                )
                totalBytes += body.length
                insert.run(
                    id,
                    randomUUID(),
                    body,
                    Date.now(),
                    body.length + 256 + 'bulk-fixturefixturefixture'.length,
                )
            }
            db.exec('COMMIT')
        } finally {
            db.close()
        }
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        await page.addInitScript(() => {
            const original = window.fetch
            window.fetch = (...args) => {
                const input = args[0]
                const url = input instanceof Request ? input.url : String(input)
                if (
                    url.includes('/deliveries/export/') &&
                    url.endsWith('/download')
                )
                    throw new Error('file must not use window.fetch')
                return original(...args)
            }
        })
        await authenticate(page)
        await page
            .getByLabel('区域', { exact: true })
            .selectOption('quarantined')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await button(page, '核对整个已应用筛选的导出').click()
        await expect(panel(page)).toContainText(
            `完整范围 1025 条 · 原始正文 ${totalBytes} 字节`,
        )
        await expect(panel(page)).toContainText('15 分钟')
        await confirm(page)
        await expect(
            panel(page).getByRole('link', { name: '下载完整 JSON Lines 文件' }),
        ).toBeVisible()
        const download = await nativeFile(page)
        let index = 0
        const file = await inspect(download, (row) => {
            expect(row.deliveryId).toBe(ids[index])
            const body = JSON.parse(String(row.content))
            expect(body.messageId).toBe(ids[index])
            expect(body.index).toBe(index)
            expect(body.text).toBe('z'.repeat(70 * 1024))
            index++
        })
        expect(file.count).toBe(1025)
        expect(file.bytes).toBeGreaterThan(64 * 1024 * 1024)
        await expect(panel(page)).toContainText('HTTP 下载响应已写出')
        await expect(panel(page)).toContainText('完整范围 1025 条')
        expect(
            (await (await page.request.get(root + '/runtime/outputs')).json())
                .usage.quarantinedCount,
        ).toBe(1025)
    } finally {
        await stop()
        await rm(data, { recursive: true, force: true })
    }
})
