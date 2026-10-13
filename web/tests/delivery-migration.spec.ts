import {
    test,
    expect,
    type Page,
    type BrowserContext,
    type Route,
} from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { randomUUID } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('administrator reviews frozen mixed-state recipients, cancels, recovers preparation, migrates once after a lost reply and sends original HTTP data to a queue-only target', async ({
    browser,
}) => {
    test.setTimeout(180_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-migration-browser-'))
    const root = '/api/edge-admin/v1'
    const password = 'Migration-fixture-6832'
    let sourceCalls = 0
    const delivered: {
        body: string
        id: string | undefined
        path: string | undefined
    }[] = []
    const peer = createServer(async (request, response) => {
        let body = ''
        for await (const chunk of request) body += chunk.toString()
        if (request.url?.startsWith('/source/')) {
            response.writeHead(++sourceCalls === 1 ? 422 : 401)
        } else {
            delivered.push({
                body,
                id: request.headers['idempotency-key'] as string | undefined,
                path: request.url,
            })
            response.writeHead(204)
        }
        response.end()
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('peer-address')
    const seed = join(data, 'seed.json')
    const args = [
        `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
        `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
    ]
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    async function stop() {
        await context?.close()
        context = undefined
        await host?.stop()
        host = undefined
    }
    async function login(first = false) {
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('migration-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page
            .getByRole('button', {
                name: first ? '创建管理员并登录' : '登录',
                exact: true,
            })
            .click()
        await expect(
            page.getByRole('link', { name: '输出目标诊断', exact: true }),
        ).toBeVisible()
        return page
    }
    async function csrf(page: Page) {
        return {
            'X-CSRF-Token': (
                await (await page.request.get(root + '/auth/session')).json()
            ).csrfToken,
        }
    }
    async function report(page: Page, id: number) {
        return (await page.request.get(root + `/runtime/outputs/${id}`)).json()
    }
    async function pause(page: Page, id: number) {
        const version = await (
            await page.request.get(root + `/connections/${id}/delivery-control`)
        ).json()
        expect(
            (
                await page.request.put(
                    root + `/connections/${id}/delivery-control`,
                    {
                        headers: await csrf(page),
                        data: {
                            paused: true,
                            confirmed: true,
                            expectedVersion: version.resumeVersion,
                        },
                    },
                )
            ).status(),
        ).toBe(200)
    }
    type Row = {
        seq: number
        delivery_id: string
        source_message_id: string
        state: string
        payload: Uint8Array
        target_address: string
        created_at: number
        connection_id: number
    }
    function rows(id: number): Row[] {
        const db = new DatabaseSync(join(data, 'queue.db'), { readOnly: true })
        try {
            return db
                .prepare(
                    'SELECT seq,delivery_id,source_message_id,state,payload,target_address,created_at,connection_id FROM delivery WHERE connection_id=? ORDER BY seq',
                )
                .all(id) as unknown as Row[]
        } finally {
            db.close()
        }
    }
    const button = (page: Page, name: string) =>
        page.getByRole('button', { name, exact: true })
    async function enter(page: Page, id = 11) {
        await page.goto('/runtime/outputs')
        await button(page, `查看目标 ${id}`).click()
        await page
            .getByRole('link', {
                name: '选择记录并迁移到另一连接',
                exact: true,
            })
            .click()
        await expect(
            page.getByRole('heading', {
                name: '迁移到另一个输出连接',
                exact: true,
            }),
        ).toBeVisible()
    }
    async function choose(page: Page, row: Row) {
        await page
            .getByLabel('选择队列区域', { exact: true })
            .selectOption(row.state)
        await page
            .getByLabel(`选择迁移记录 ${row.delivery_id}`, { exact: true })
            .check()
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
                        configuration: { periodMs: 1000, sample: { v: 42 } },
                    },
                    {
                        kind: 'output.http',
                        key: 'source',
                        configuration: {
                            connectionId: 11,
                            baseAddress: `http://127.0.0.1:${address.port}/source/`,
                            path: 'send',
                            inputs: ['meter/sample'],
                        },
                    },
                    {
                        kind: 'output.http',
                        key: 'target',
                        configuration: {
                            connectionId: 12,
                            baseAddress: `http://127.0.0.1:${address.port}/target/`,
                            queueOnly: true,
                            inputs: [],
                        },
                    },
                ],
            }),
        )
        let page = await login(true)
        await expect
            .poll(async () => (await report(page, 11)).usage.failedCount)
            .toBe(1)
        await expect
            .poll(async () => (await report(page, 11)).usage.pendingCount)
            .toBeGreaterThanOrEqual(4)
        await pause(page, 11)
        await pause(page, 12)
        await expect
            .poll(async () => (await report(page, 11)).items[0].state)
            .toBe('manuallyPaused')
        await stop()
        // The HTTP failed/pending records and frozen payloads are real. The old Basic summary
        // and one quarantine state are explicit offline fixtures, not a historical-version or
        // real credential-change acceptance claim. Never update a running Host's database.
        const snapshot = rows(11)
        const failed = snapshot.find((row) => row.state === 'failed')!
        const pending = snapshot.filter((row) => row.state === 'pending')[1]
        const quarantine = snapshot.filter((row) => row.state === 'pending')[2]
        const db = new DatabaseSync(join(data, 'queue.db'))
        try {
            const summary = 'http://original.test/; authentication=Basic'
            db.prepare(
                'UPDATE delivery SET byte_size=byte_size-length(CAST(fingerprint_summary AS BLOB))+?,fingerprint_summary=? WHERE seq=?',
            ).run(Buffer.byteLength(summary), summary, failed.seq)
            db.prepare(
                "UPDATE delivery SET state='quarantined',quarantine_reason='targetChanged' WHERE seq=?",
            ).run(quarantine.seq)
        } finally {
            db.close()
        }
        quarantine.state = 'quarantined'
        const originals = [failed, pending, quarantine]
        page = await login()
        await enter(page)
        await choose(page, pending)
        await page
            .getByLabel('请选择目标连接', { exact: true })
            .selectOption('12')
        // Controlled response fixture for the disabled confirmation UI, not a server-quota test.
        // The real preparation is cancelled below; the gateway never receives confirmation here.
        async function insufficientQuota(route: Route) {
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            const body = await response.json()
            const proposal = body.preparation ?? body
            proposal.quota = {
                pendingRecords: 2,
                pendingBytes: 64,
                globalBytes: 0,
                failedRecords: 0,
                exceeded: true,
            }
            proposal.canConfirm = false
            await route.fulfill({ response, body: JSON.stringify(body) })
        }
        await page.route('**/deliveries/migrations/prepare', insufficientQuota)
        await page.route(
            '**/deliveries/migrations/preparation',
            insufficientQuota,
        )
        await button(page, '准备迁移所选记录').click()
        await expect(button(page, '确认迁移冻结清单')).toBeDisabled()
        await expect(
            page.getByLabel('我确认新接收方与原接收方属于同一数据所有者', {
                exact: true,
            }),
        ).toBeDisabled()
        await expect(
            page.getByRole('region', { name: '核对冻结迁移清单', exact: true }),
        ).toContainText('当前配额不足')
        await button(page, '取消迁移准备').click()
        await page.unroute(
            '**/deliveries/migrations/prepare',
            insufficientQuota,
        )
        await page.unroute(
            '**/deliveries/migrations/preparation',
            insufficientQuota,
        )
        await expect(button(page, '准备迁移所选记录')).toBeVisible()
        expect(rows(12)).toHaveLength(0)
        for (const row of originals) await choose(page, row)
        await page
            .getByLabel('请选择目标连接', { exact: true })
            .selectOption('12')
        await button(page, '准备迁移所选记录').click()
        const review = page.getByRole('region', {
            name: '核对冻结迁移清单',
            exact: true,
        })
        await expect(review).toContainText(
            '选择 3 条：待发 1，失败区 1，隔离区 1',
        )
        await expect(review).toContainText('http://original.test/')
        await expect(review).toContainText('未记录，无法核实')
        await expect(review).toContainText(`/target/`)
        await expect(button(page, '确认迁移冻结清单')).toBeDisabled()
        // The server retains preparation for this login; no write or checkbox is replayed on reload.
        await page.reload()
        await expect(review).toContainText('选择 3 条')
        await expect(
            page.getByLabel('我确认新接收方与原接收方属于同一数据所有者', {
                exact: true,
            }),
        ).not.toBeChecked()
        await page.route('**/deliveries/migrations/preparation', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"title":"fixture read error"}',
            }),
        )
        await button(page, '重新读取本会话确认清单').click()
        await expect(button(page, '确认迁移冻结清单')).toBeDisabled()
        await expect(page.getByRole('alert')).toContainText('不能当作空队列')
        await page.unroute('**/deliveries/migrations/preparation')
        await button(page, '重新读取本会话确认清单').click()
        await expect(page.getByRole('alert')).toHaveCount(0)
        await page.screenshot({
            path: '/tmp/tenonedge-migration-browser-review.png',
            fullPage: true,
        })
        let confirmations = 0
        await page.route(
            '**/deliveries/migrations/*/confirm',
            async (route) => {
                const response = await route.fetch()
                expect(response.status()).toBe(200)
                confirmations++
                await route.abort('connectionreset')
            },
        )
        await page
            .getByLabel('我确认新接收方与原接收方属于同一数据所有者', {
                exact: true,
            })
            .check()
        await button(page, '确认迁移冻结清单').click()
        await expect(page).toHaveURL(/operationId=[0-9a-f-]{36}/)
        const operation = new URL(page.url()).searchParams.get('operationId')!
        await expect(page.getByRole('alert')).toContainText('不自动重放')
        await expect(
            page.getByRole('region', { name: '查询迁移持久结果', exact: true }),
        ).toContainText('迁移已完成（不表示已发送）')
        expect(confirmations).toBe(1)
        const migrated = rows(12)
        expect(migrated).toHaveLength(3)
        for (const original of originals) {
            const actual = migrated.find((row) => row.seq === original.seq)!
            expect(actual.delivery_id).toBe(original.delivery_id)
            expect(actual.source_message_id).toBe(original.source_message_id)
            expect(Buffer.from(actual.payload)).toEqual(
                Buffer.from(original.payload),
            )
            expect(actual.target_address).toBe(original.target_address)
            expect(actual.created_at).toBe(original.created_at)
            expect(actual.state).toBe(
                original.state === 'failed' ? 'failed' : 'pending',
            )
        }
        expect(rows(11).length).toBeGreaterThan(0)
        expect(delivered).toHaveLength(0)
        expect((await report(page, 12)).items[0].state).toBe('manuallyPaused')
        await page.reload()
        await expect(
            page.getByRole('region', { name: '查询迁移持久结果', exact: true }),
        ).toContainText('实际迁移 3 条')
        await expect(button(page, '确认迁移冻结清单')).toHaveCount(0)
        expect(confirmations).toBe(1)
        await page.unroute('**/deliveries/migrations/*/confirm')
        await page.screenshot({
            path: '/tmp/tenonedge-migration-browser-result.png',
            fullPage: true,
        })
        // Resume explicitly through existing UI: migration itself has not resumed or created a sender.
        await page.goto('/runtime/outputs')
        await button(page, '查看目标 12').click()
        const controls = page.getByRole('region', {
            name: '目标发送操作',
            exact: true,
        })
        await controls
            .getByRole('button', { name: '恢复目标', exact: true })
            .click()
        await controls
            .getByLabel('我已核对目标并确认此发送状态变更', { exact: true })
            .check()
        await controls
            .getByRole('button', { name: '确认执行', exact: true })
            .click()
        await expect.poll(() => delivered.length).toBe(2)
        for (const [index, row] of [pending, quarantine].entries()) {
            expect(delivered[index]).toEqual({
                id: row.delivery_id,
                path: '/target/send',
                body: Buffer.from(row.payload).toString('utf8'),
            })
        }
        await expect.poll(() => rows(12).length).toBe(1)
        expect(rows(12)[0].state).toBe('failed')
        const result = await (
            await page.request.get(root + `/deliveries/migrations/${operation}`)
        ).json()
        expect(result.status).toBe('completed')
        expect(result.affectedRecords).toBe(3)
        for (const action of [
            'delivery.migrationStarted',
            'delivery.migrationFinished',
        ]) {
            const audit = await (
                await page.request.get(root + `/audit?action=${action}`)
            ).json()
            expect(audit.items).toHaveLength(1)
        }
        // Fixture role changes exercise route/link/API protection, not a user-management UI.
        for (const role of ['Configurator', 'Viewer']) {
            await stop()
            const config = new DatabaseSync(join(data, 'config.db'))
            try {
                config
                    .prepare(
                        'UPDATE user_account SET role=?,security_stamp=? WHERE account_name=?',
                    )
                    .run(role, randomUUID(), 'migration-admin')
            } finally {
                config.close()
            }
            page = await login()
            await page.goto('/runtime/outputs')
            await button(page, '查看目标 11').click()
            await expect(
                page.getByRole('link', {
                    name: '选择记录并迁移到另一连接',
                    exact: true,
                }),
            ).toHaveCount(0)
            await page.goto('/deliveries/migrate?connectionId=11')
            await expect(page.getByRole('alert')).toContainText('仅管理员')
            expect(
                (
                    await page.request.post(
                        root + `/deliveries/migrations/${operation}/confirm`,
                        {
                            headers: await csrf(page),
                            data: { sameOwnerConfirmed: true },
                        },
                    )
                ).status(),
            ).toBe(403)
        }
    } finally {
        await stop()
        peer.closeAllConnections()
        await new Promise<void>((resolve) => peer.close(() => resolve()))
        await rm(data, { recursive: true, force: true })
    }
})
