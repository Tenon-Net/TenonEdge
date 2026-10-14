import { test, expect, type Page, type BrowserContext } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('admin cleanup: real failures, immutable filtered batch, cancel/stale/lost reply, quarantine fixtures, durable loss counts and roles', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-cleanup-'))
    let sends = 0
    const peer = createServer(async (request, response) => {
        for await (const _ of request) {
            /* genuine HTTP output */
        }
        sends++
        response.writeHead(422)
        response.end()
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('peer-address')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const root = '/api/edge-admin/v1'
    const password = 'Cleanup-test-9472'
    const panel = (page: Page) =>
        page.getByRole('region', { name: '队列记录操作', exact: true })
    const button = (page: Page, name: string) =>
        panel(page).getByRole('button', { name, exact: true })
    type Selected = {
        sequence: number
        deliveryId: string
        connectionId: number
        migrationId: number | null
    }
    const rows = async (page: Page, state = 'failed'): Promise<Selected[]> => {
        const response = await page.request.get(
            root + '/deliveries?state=' + state,
        )
        expect(response.status()).toBe(200)
        return (await response.json()).items
    }
    const headers = async (page: Page) => ({
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    })
    async function pause(page: Page, paused: boolean) {
        const control = await (
            await page.request.get(root + '/connections/11/delivery-control')
        ).json()
        expect(
            (
                await page.request.put(
                    root + '/connections/11/delivery-control',
                    {
                        headers: await headers(page),
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
    async function confirm(page: Page) {
        await panel(page)
            .getByLabel('我已核对本批记录，确认永久删除且无法撤销')
            .check()
        await button(page, '确认永久清理本批').click()
    }
    async function stop() {
        try {
            await context?.close()
        } finally {
            context = undefined
            await host?.stop()
            host = undefined
        }
    }
    function changeRole(role: string) {
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                'UPDATE user_account SET role=?,security_stamp=? WHERE account_name=?',
            ).run(role, 'cleanup-' + role, 'cleanup-admin')
        } finally {
            db.close()
        }
    }
    const seed = join(data, 'seed.json')
    const args = [
        `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
        `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
    ]
    async function login() {
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        await page.goto('/deliveries')
        await page.getByLabel('账号', { exact: true }).fill('cleanup-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await expect(
            page.getByRole('heading', { name: '失败区与隔离区', exact: true }),
        ).toBeVisible()
        return page
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
                            sample: { private: 'cleanup-private-body' },
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
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('cleanup-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByRole('link', { name: '输出目标诊断', exact: true }),
        ).toBeVisible()
        await expect
            .poll(async () => (await rows(page)).length)
            .toBeGreaterThanOrEqual(3)
        await pause(page, true)
        const initial = await rows(page)
        await page.goto('/deliveries')
        await page
            .getByLabel('选择记录 ' + initial[0].deliveryId, { exact: true })
            .check()
        await button(page, '核对清理本页已选 1 条').click()
        await expect(button(page, '确认永久清理本批')).toBeDisabled()
        await expect(panel(page)).toContainText('无法撤销')
        await expect(panel(page)).not.toContainText('cleanup-private-body')
        await button(page, '取消清理').click()
        expect(await rows(page)).toHaveLength(initial.length)
        await expect(
            page.getByLabel('选择记录 ' + initial[0].deliveryId, {
                exact: true,
            }),
        ).not.toBeChecked()
        await page
            .getByLabel('选择记录 ' + initial[0].deliveryId, { exact: true })
            .check()
        await button(page, '核对清理本页已选 1 条').click()
        await expect(button(page, '确认永久清理本批')).toBeVisible()
        // A concurrent real administrator clears this generation after the browser has frozen it.
        expect(
            (
                await page.request.post(root + '/deliveries/cleanup', {
                    headers: await headers(page),
                    data: {
                        state: 'failed',
                        confirmed: true,
                        items: [initial[0]],
                    },
                })
            ).status(),
        ).toBe(200)
        await confirm(page)
        await expect(panel(page)).toContainText('整批未执行')
        await expect(button(page, '确认永久清理本批')).toBeDisabled()
        await button(page, '重新读取并选择').click()
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
        await button(page, '按已应用筛选准备清理本批（最多 1000 条）').click()
        await expect(panel(page)).toContainText(
            `准备永久清理 ${initial.length - 1} 条`,
        )
        await expect(panel(page)).toContainText('原因 422')
        await expect(panel(page)).not.toContainText('UNAPPLIED')
        // New genuine failed records after review are not part of the confirmed batch.
        await pause(page, false)
        await expect
            .poll(
                async () =>
                    (await rows(page)).filter(
                        (row) =>
                            !initial.some(
                                (old) => old.deliveryId === row.deliveryId,
                            ),
                    ).length,
            )
            .toBeGreaterThanOrEqual(2)
        await pause(page, true)
        const fresh = (await rows(page)).filter(
            (row) => !initial.some((old) => old.deliveryId === row.deliveryId),
        )
        const stoppedSends = sends
        let writes = 0
        await page.route('**/deliveries/cleanup', async (route) => {
            writes++
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await confirm(page)
        await expect(panel(page)).toContainText('清理失败或响应丢失')
        await expect(button(page, '确认永久清理本批')).toBeDisabled()
        expect((await rows(page)).map((item) => item.deliveryId)).toEqual(
            fresh.map((item) => item.deliveryId),
        )
        await page
            .getByRole('region', { name: '故障记录', exact: true })
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        expect(writes).toBe(1)
        expect(sends).toBe(stoppedSends)
        await page.screenshot({
            path: '/tmp/tenonedge-cleanup-lost-reply.png',
            fullPage: true,
        })
        await page.unroute('**/deliveries/cleanup')
        await stop()

        // Offline quarantine/migration-generation fixture. This is NOT a real migration acceptance test.
        const db = new DatabaseSync(join(data, 'queue.db'))
        try {
            db.prepare(
                "UPDATE delivery SET state='quarantined',quarantine_reason='targetMissing',connection_id=77,migration_id=9223372036854775807 WHERE delivery_id IN (?,?)",
            ).run(fresh[0].deliveryId, fresh[1].deliveryId)
        } finally {
            db.close()
        }
        page = await login()
        await page
            .getByLabel('区域', { exact: true })
            .selectOption('quarantined')
        await page.getByLabel('目标连接编号', { exact: true }).fill('77')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        await expect(button(page, '核对本页已选 0 条')).toHaveCount(0)
        await button(page, '按已应用筛选准备清理本批（最多 1000 条）').click()
        await expect(panel(page)).toContainText('准备永久清理 2 条 · 隔离区')
        await confirm(page)
        await expect(panel(page)).toContainText('已永久清理 2 条记录')
        expect(await rows(page, 'quarantined')).toEqual([])
        expect(sends).toBe(stoppedSends)
        await page.goto('/runtime/outputs')
        await expect(
            page.getByRole('cell', { name: /人工清理的失败记录（条，全局）/ }),
        ).toBeVisible()
        await expect(
            page.getByRole('cell', { name: /人工清理的隔离记录（条，全局）/ }),
        ).toBeVisible()
        const counters = (
            await (await page.request.get(root + '/runtime/status')).json()
        ).persistentCounters
        expect(counters.manuallyClearedFailedRecords).toBe(initial.length)
        expect(counters.manuallyClearedQuarantinedRecords).toBe(2)
        expect(counters.acknowledgedRecords ?? 0).toBe(0)
        const audits = (
            await (
                await page.request.get(root + '/audit?action=delivery.cleanup')
            ).json()
        ).items
        expect(
            audits.filter(
                (entry: { action: string; result: string }) =>
                    entry.action === 'delivery.cleanup' &&
                    entry.result === 'success',
            ),
        ).toHaveLength(3)
        await page.screenshot({
            path: '/tmp/tenonedge-cleanup-counts.png',
            fullPage: true,
        })

        for (const role of ['Configurator', 'Viewer']) {
            await stop()
            changeRole(role) // role fixture, not acceptance of a user-management UI.
            page = await login()
            await expect(
                page.getByRole('button', {
                    name: '按已应用筛选准备清理本批（最多 1000 条）',
                    exact: true,
                }),
            ).toHaveCount(0)
            expect(
                (
                    await page.request.post(root + '/deliveries/cleanup', {
                        headers: await headers(page),
                        data: {
                            state: 'failed',
                            confirmed: true,
                            items: [initial[0]],
                        },
                    })
                ).status(),
            ).toBe(403)
            await page
                .getByLabel('区域', { exact: true })
                .selectOption('quarantined')
            await page
                .getByRole('button', { name: '应用筛选', exact: true })
                .click()
            await expect(panel(page)).toHaveCount(0)
            await expect(page.getByRole('checkbox')).toHaveCount(0)
            expect(
                (
                    await (
                        await page.request.get(root + '/runtime/status')
                    ).json()
                ).persistentCounters.manuallyClearedFailedRecords,
            ).toBe(initial.length)
            expect(sends).toBe(stoppedSends)
        }
    } finally {
        try {
            await stop()
        } finally {
            peer.closeAllConnections()
            await new Promise<void>((resolve, reject) =>
                peer.close((error) => (error ? reject(error) : resolve())),
            )
            await rm(data, { recursive: true, force: true })
        }
    }
})
