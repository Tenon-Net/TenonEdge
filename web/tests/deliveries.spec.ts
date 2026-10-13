import { test, expect, type BrowserContext, type Page } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('real failed delivery plus bounded queue fixtures: filters, paging, body isolation, truncation, errors and all roles', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-deliveries-'))
    const peer = createServer(async (request, response) => {
        for await (const _ of request) {
            /* consume genuine output */
        }
        response.writeHead(422)
        response.end(
            'private-peer-response <script>globalThis.responseInjected=true</script> ' +
                'x'.repeat(1200),
        )
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('peer')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const root = '/api/edge-admin/v1/deliveries/'
    const password = 'Queue-test-7419-Strong'
    async function login(page: Page) {
        await page.goto('/deliveries')
        await page.getByLabel('账号', { exact: true }).fill('queue-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await expect(
            page.getByRole('heading', { name: '失败区与隔离区', exact: true }),
        ).toBeVisible()
    }
    try {
        const seed = join(data, 'seed.json')
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
                            sample: {
                                private: 'queue-body-private-marker',
                                text: '<script>globalThis.queueInjected=true</script>',
                            },
                        },
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
                    {
                        kind: 'output.http',
                        key: 'http-second',
                        configuration: {
                            connectionId: 12,
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
        let page = await context.newPage()
        page.setDefaultTimeout(10_000)
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('queue-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByRole('button', { name: '退出登录', exact: true }),
        ).toBeVisible()
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                root + '?state=failed&connectionId=11',
                            )
                        ).json()
                    ).items.length,
            )
            .toBeGreaterThan(0)
        await page
            .getByRole('navigation')
            .getByRole('link', { name: '失败区与隔离区', exact: true })
            .click()
        const records = () =>
            page.getByRole('region', { name: '故障记录', exact: true })
        await expect(records()).toContainText('422')
        await expect(records()).toContainText('直接拒绝的记录可能为 0')
        await expect(records()).not.toContainText('queue-body-private-marker')
        const realPage = await (
            await page.request.get(root + '?state=failed&connectionId=11')
        ).json()
        const realId: string = realPage.items[0].deliveryId
        expect(JSON.stringify(realPage)).not.toContain('private-peer-response')
        await page
            .getByRole('button', { name: `查看正文 ${realId}`, exact: true })
            .click()
        const body = () =>
            page.getByRole('region', { name: '报文正文', exact: true })
        await expect(body()).toContainText('queue-body-private-marker')
        expect(await page.evaluate(() => 'queueInjected' in globalThis)).toBe(
            false,
        )
        const summary = () =>
            page.getByRole('region', {
                name: '上次记录的 HTTP 响应摘要',
                exact: true,
            })
        await expect(summary()).toContainText('private-peer-response')
        await expect(summary()).toContainText('HTTP 422')
        await expect(summary()).toContainText('响应摘要已截断')
        expect(
            await page.evaluate(() => 'responseInjected' in globalThis),
        ).toBe(false)
        await page.screenshot({
            path: '/tmp/tenonedge-http-error-browser.png',
            fullPage: true,
        })
        await page.route(`**/deliveries/${realId}/body?*`, (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503}',
            }),
        )
        await body()
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        await expect(body().getByRole('alert')).toBeVisible()
        await expect(summary()).toHaveCount(0)
        await page.unroute(`**/deliveries/${realId}/body?*`)
        // Pause via the already-tested API; this test is not the pending T3-08 control UI acceptance.
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        for (const id of [11, 12])
            expect(
                (
                    await page.request.put(
                        `/api/edge-admin/v1/connections/${id}/delivery-control`,
                        {
                            headers: { 'X-CSRF-Token': session.csrfToken },
                            data: { paused: true, confirmed: true },
                        },
                    )
                ).ok(),
            ).toBe(true)
        await context.close()
        context = undefined
        await host.stop()
        host = undefined

        // Offline, test-only queue fixtures cover pagination and raw-byte boundaries. Not real input/physical-device evidence.
        const db = new DatabaseSync(join(data, 'queue.db'))
        const largeId = randomUUID(),
            binaryId = randomUUID()
        try {
            db.prepare('DELETE FROM delivery WHERE delivery_id <> ?').run(
                realId,
            )
            const insert =
                db.prepare(`INSERT INTO delivery (delivery_id,connection_id,source_message_id,device_key,route_id,node_label,revision,payload,target_address,qos,headers,fingerprint,fingerprint_summary,created_at,failure_count,state,quarantine_reason,last_error,migration_id,failed_at,byte_size,clock_anomaly,protocol_metadata)
                SELECT ?,?,source_message_id,device_key,route_id,node_label,revision,?,target_address,qos,headers,fingerprint,fingerprint_summary,created_at,failure_count,?,?,?,migration_id,failed_at,byte_size-length(payload)+length(?3),clock_anomaly,protocol_metadata FROM delivery WHERE delivery_id=?`)
            for (let i = 1; i < 105; i++)
                insert.run(
                    randomUUID(),
                    i < 100 ? 11 : 12,
                    Buffer.from('fixture-private-body'),
                    'failed',
                    null,
                    'HTTP 422 fixture%_',
                    realId,
                )
            insert.run(
                largeId,
                11,
                Buffer.from('x'.repeat(65535) + '😀tail'),
                'quarantined',
                'targetChanged',
                null,
                realId,
            )
            insert.run(
                binaryId,
                11,
                Buffer.from([255, 254]),
                'quarantined',
                'targetMissing',
                null,
                realId,
            )
        } finally {
            for (const [id, state] of [
                [largeId, 2],
                [binaryId, 6],
            ] as const) {
                const summaryJson = JSON.stringify({
                    StatusCode: 422,
                    State: state,
                    Text: null,
                    Truncated: false,
                })
                db.prepare(
                    'UPDATE delivery SET response_summary=?, byte_size=byte_size+? WHERE delivery_id=?',
                ).run(summaryJson, Buffer.byteLength(summaryJson), id)
            }
            db.close()
        }
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        page.setDefaultTimeout(10_000)
        const bodyRequests: string[] = []
        page.on('request', (request) => {
            if (request.url().includes('/body?'))
                bodyRequests.push(request.url())
        })
        await login(page)
        await expect(records().locator('tbody tr')).toHaveCount(100)
        expect(bodyRequests).toHaveLength(0)
        await records()
            .getByRole('button', { name: '下一页', exact: true })
            .click()
        await expect(records().locator('tbody tr')).toHaveCount(5)
        await expect(records()).toContainText('第 2 页')
        await expect(
            records().getByRole('button', { name: '下一页', exact: true }),
        ).toBeDisabled()
        await records()
            .getByRole('button', { name: '上一页', exact: true })
            .click()
        await expect(records().locator('tbody tr')).toHaveCount(100)
        await page.getByLabel('目标连接编号', { exact: true }).fill('12')
        await page
            .getByLabel('原因包含（区分大小写）', { exact: true })
            .fill('%_')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await expect(records().locator('tbody tr')).toHaveCount(5)
        await page
            .getByLabel('设备业务标识', { exact: true })
            .fill('not-present')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await expect(records()).toContainText('当前筛选下没有记录')
        await page
            .getByLabel('设备业务标识', { exact: true })
            .fill(realPage.items[0].deviceKey)
        await page
            .getByLabel('首次入队自（含）', { exact: true })
            .fill('2099-01-01T00:00')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await expect(records()).toContainText('当前筛选下没有记录')
        await page.getByLabel('首次入队自（含）', { exact: true }).fill('')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await expect(records().locator('tbody tr')).toHaveCount(5)
        await page.route('**/api/edge-admin/v1/deliveries?*', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503}',
            }),
        )
        await records()
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        await expect(records().getByRole('alert')).toBeVisible()
        await expect(records().locator('tbody tr')).toHaveCount(5) // Explicitly stale metadata, not an empty success.
        await expect(
            records()
                .getByRole('button', { name: /^查看正文 / })
                .first(),
        ).toBeDisabled()
        await page.unroute('**/api/edge-admin/v1/deliveries?*')
        await page.getByLabel('目标连接编号', { exact: true }).fill('11')
        await page
            .getByLabel('原因包含（区分大小写）', { exact: true })
            .fill('')
        await page
            .getByLabel('区域', { exact: true })
            .selectOption('quarantined')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await expect(records().locator('tbody tr')).toHaveCount(2)
        await expect(records()).toContainText('接收方已变更')
        await page
            .getByRole('button', { name: `查看正文 ${largeId}`, exact: true })
            .click()
        await expect(body()).toContainText('正文已截断')
        await expect(body()).toContainText('65535 / 65543')
        await expect(summary()).toContainText('超过 64 KiB 读取上限')
        await expect(body().locator('pre')).toHaveText('x'.repeat(65535))
        await page
            .getByRole('button', { name: '关闭正文', exact: true })
            .click()
        await expect(body()).toHaveCount(0)
        await page
            .getByRole('button', { name: `查看正文 ${binaryId}`, exact: true })
            .click()
        await expect(body()).toContainText('Base64')
        await expect(summary()).toContainText('整段响应已隐藏')
        await expect(body().locator('pre')).toHaveText('//4=')
        await page.screenshot({
            path: '/tmp/tenonedge-queue-inspection-browser.png',
            fullPage: true,
        })
        await page.route(`**/deliveries/${binaryId}/body?*`, (route) =>
            route.fulfill({
                status: 404,
                contentType: 'application/problem+json',
                body: '{"status":404}',
            }),
        )
        await body()
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        await expect(body().getByRole('alert')).toBeVisible()
        await expect(body().locator('pre')).toHaveCount(0) // Removed/unauthorized body never remains visible after a failed refresh.
        await page.unroute(`**/deliveries/${binaryId}/body?*`)
        await page
            .getByRole('button', { name: '关闭正文', exact: true })
            .click()
        let requestStarted!: () => void
        const pending = new Promise<void>((resolve) => {
            requestStarted = resolve
        })
        let release!: () => void
        const gate = new Promise<void>((resolve) => {
            release = resolve
        })
        await page.route(`**/deliveries/${binaryId}/body?*`, async (route) => {
            requestStarted()
            await gate
            await route.abort()
        })
        const aborted = page.waitForEvent('requestfailed', {
            predicate: (request) => request.url().includes(`${binaryId}/body?`),
        })
        await page
            .getByRole('button', { name: `查看正文 ${binaryId}`, exact: true })
            .click()
        await pending
        await page
            .getByRole('navigation')
            .getByRole('link', { name: '账号安全', exact: true })
            .click()
        release()
        expect((await aborted).failure()?.errorText).toBe('net::ERR_ABORTED')
        await expect(body()).toHaveCount(0)
        await context.close()
        context = undefined
        await host.stop()
        host = undefined

        for (const role of ['Viewer', 'Configurator']) {
            const config = new DatabaseSync(join(data, 'config.db'))
            try {
                config
                    .prepare(
                        'UPDATE user_account SET role=?, security_stamp=? WHERE account_name=?',
                    )
                    .run(role, randomUUID(), 'queue-admin')
            } finally {
                config.close()
            }
            host = await startOwnedHost(data, 0, args)
            context = await browser.newContext({ baseURL: host.url })
            page = await context.newPage()
            let automaticBodies = 0
            page.on('request', (request) => {
                if (request.url().includes('/body?')) automaticBodies++
            })
            await login(page)
            await expect(records().locator('tbody tr')).toHaveCount(100)
            expect(automaticBodies).toBe(0)
            if (role === 'Viewer') {
                await expect(records()).toContainText('不能查看报文正文')
                await expect(
                    page.getByRole('button', { name: /^查看正文 / }),
                ).toHaveCount(0)
                expect(
                    (
                        await page.request.get(
                            `${root}${realId}/body?state=failed`,
                        )
                    ).status(),
                ).toBe(403)
                expect(
                    (
                        await page.request.get(
                            `${root}${binaryId}/body?state=quarantined`,
                        )
                    ).status(),
                ).toBe(403)
                await expect(page.locator('body')).not.toContainText(
                    'queue-body-private-marker',
                )
                await expect(page.locator('body')).not.toContainText(
                    'private-peer-response',
                )
                await expect(summary()).toHaveCount(0)
                await page.screenshot({
                    path: '/tmp/tenonedge-queue-inspection-viewer.png',
                    fullPage: false,
                })
            } else {
                await page
                    .getByRole('button', {
                        name: `查看正文 ${realId}`,
                        exact: true,
                    })
                    .click()
                await expect(body()).toContainText('queue-body-private-marker')
                await expect(summary()).toContainText('private-peer-response')
                await expect(summary()).toContainText('响应摘要已截断')
            }
            await context.close()
            context = undefined
            await host.stop()
            host = undefined
        }
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
