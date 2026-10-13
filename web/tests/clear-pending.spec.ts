import { test, expect, type Page, type BrowserContext } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { randomUUID } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('clear all pending uses the original in-flight HTTP outcome, explicit live boundary, manual pause, durable loss counts, lost reply and roles', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-clear-browser-'))
    const root = '/api/edge-admin/v1'
    const endpoint = root + '/connections/11/clear-pending'
    const password = 'Clear-test-6372'
    const bodies: string[] = []
    let release: (() => void) | undefined
    const blocked = new Promise<void>((resolve) => {
        release = resolve
    })
    const peer = createServer(async (request, response) => {
        let body = ''
        for await (const chunk of request) body += chunk.toString()
        bodies.push(body)
        if (bodies.length === 1) {
            response.writeHead(401)
            response.end()
            return
        }
        await blocked
        response.writeHead(204)
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
    const panel = (page: Page) =>
        page.getByRole('region', { name: '清空目标全部待发', exact: true })
    const button = (page: Page, name: string) =>
        panel(page).getByRole('button', { name, exact: true })
    async function diagnostics(page: Page) {
        return await (await page.request.get(root + '/runtime/status')).json()
    }
    async function report(page: Page) {
        return await (
            await page.request.get(root + '/runtime/outputs/11')
        ).json()
    }
    async function csrf(page: Page) {
        return {
            'X-CSRF-Token': (
                await (await page.request.get(root + '/auth/session')).json()
            ).csrfToken,
        }
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
    }
    async function confirm(page: Page) {
        await panel(page)
            .getByLabel(
                '我确认永久删除执行时此目标的全部待发记录，包括期间新入队的数据',
            )
            .check()
        await button(page, '确认清空全部待发').click()
    }
    async function stop() {
        await context?.close()
        context = undefined
        await host?.stop()
        host = undefined
    }
    async function login() {
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        await page.goto('/runtime/outputs')
        await page.getByLabel('账号', { exact: true }).fill('clear-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
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
                        configuration: { periodMs: 1000, sample: { v: 42 } },
                    },
                    {
                        kind: 'output.http',
                        key: 'http',
                        configuration: {
                            connectionId: 11,
                            baseAddress: `http://127.0.0.1:${address.port}/`,
                            path: 'send',
                            timeoutMs: 60000,
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
        await page.getByLabel('账号', { exact: true }).fill('clear-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await page
            .getByRole('link', { name: '输出目标诊断', exact: true })
            .click()
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        await expect
            .poll(async () => (await report(page)).items[0].sender.state)
            .toBe('automaticallyPaused')
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeGreaterThanOrEqual(3)
        await button(page, '准备清空全部待发').click()
        await expect(button(page, '确认清空全部待发')).toBeDisabled()
        await expect(panel(page)).toContainText('不是冻结清单')
        await button(page, '取消操作').click()
        expect(
            (await diagnostics(page)).persistentCounters
                .manuallyClearedPendingRecords ?? 0,
        ).toBe(0)
        // Resume only the original sender, then manually pause while its real HTTP request is in flight.
        await pause(page, false)
        await expect.poll(() => bodies.length).toBe(2)
        expect(bodies[1]).toBe(bodies[0])
        await pause(page, true)
        await button(page, '准备清空全部待发').click()
        const before = (await report(page)).usage.pendingCount
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeGreaterThan(before)
        await confirm(page)
        await expect
            .poll(
                async () =>
                    (await diagnostics(page)).coordinator
                        .queueOperationInProgress,
            )
            .toBe(true)
        await expect(panel(page)).toContainText('正在等待原发送结果落库')
        expect(
            (await diagnostics(page)).persistentCounters
                .manuallyClearedPendingRecords ?? 0,
        ).toBe(0)
        release?.()
        await expect(panel(page)).toContainText('已永久清空')
        const first = (await diagnostics(page)).persistentCounters
        expect(first.acknowledgedRecords).toBe(1)
        expect(first.manuallyClearedPendingRecords).toBeGreaterThanOrEqual(
            before,
        )
        expect(bodies).toHaveLength(2)
        expect((await report(page)).items[0].state).toBe('manuallyPaused')
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeGreaterThan(0)
        await expect(
            page.getByRole('cell', { name: /人工清空的待发记录（条，全局）/ }),
        ).toBeVisible()
        // A query error must not turn the next confirmation into a zero-count queue.
        await page.route('**/runtime/outputs/11?*', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"title":"fixture read failure"}',
            }),
        )
        await button(page, '重新读取清空后的目标状态').click()
        await expect(button(page, '准备清空全部待发')).toBeDisabled()
        await page.unroute('**/runtime/outputs/11?*')
        await button(page, '重新读取清空后的目标状态').click()
        await button(page, '准备清空全部待发').click()
        let writes = 0
        await page.route('**/connections/11/clear-pending', async (route) => {
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            writes++
            await route.abort('connectionreset')
        })
        await confirm(page)
        await expect(panel(page)).toContainText('结果不确定，不自动重放')
        await expect(button(page, '准备清空全部待发')).toBeDisabled()
        const total = (await diagnostics(page)).persistentCounters
            .manuallyClearedPendingRecords
        expect(total).toBeGreaterThan(first.manuallyClearedPendingRecords)
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeGreaterThan(0)
        expect(writes).toBe(1)
        expect(
            (await diagnostics(page)).persistentCounters
                .manuallyClearedPendingRecords,
        ).toBe(total)
        await page.unroute('**/connections/11/clear-pending')
        await button(page, '重新读取清空后的目标状态').click()
        await expect(panel(page)).not.toContainText('结果不确定，不自动重放')
        const audit = (
            await (
                await page.request.get(
                    root + '/audit?action=delivery.clearPending',
                )
            ).json()
        ).items
        expect(
            audit.filter(
                (entry: { result: string }) => entry.result === 'success',
            ),
        ).toHaveLength(2)
        await page.screenshot({
            path: '/tmp/tenonedge-clear-pending-browser.png',
            fullPage: true,
        })
        await stop()
        page = await login()
        expect(
            (await diagnostics(page)).persistentCounters
                .manuallyClearedPendingRecords,
        ).toBe(total)
        expect((await report(page)).items[0].state).toBe('manuallyPaused')
        expect(bodies).toHaveLength(2)
        for (const role of ['Configurator', 'Viewer']) {
            await stop()
            // Offline role fixture, not the user-management page acceptance.
            const db = new DatabaseSync(join(data, 'config.db'))
            try {
                db.prepare(
                    'UPDATE user_account SET role=?,security_stamp=? WHERE account_name=?',
                ).run(role, randomUUID(), 'clear-admin')
            } finally {
                db.close()
            }
            page = await login()
            await expect(panel(page)).toHaveCount(0)
            expect(
                (
                    await page.request.post(endpoint, {
                        headers: await csrf(page),
                        data: { revision: 1, confirmed: true },
                    })
                ).status(),
            ).toBe(403)
            expect(
                (await diagnostics(page)).persistentCounters
                    .manuallyClearedPendingRecords,
            ).toBe(total)
        }
    } finally {
        release?.()
        await stop()
        peer.closeAllConnections()
        await new Promise<void>((resolve) => peer.close(() => resolve()))
        await rm(data, { recursive: true, force: true })
    }
})
