import { test, expect, type BrowserContext, type Page } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('real target controls: bound retry, pause CAS/confirmation/lost response, restart, recovery and three roles', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const data = await mkdtemp(
        join(tmpdir(), 'tenonedge-browser-output-controls-'),
    )
    let code = 503
    const bodies: string[] = []
    const peer = createServer(async (request, response) => {
        let body = ''
        for await (const chunk of request) body += chunk.toString()
        bodies.push(body)
        response.writeHead(code, { 'Retry-After': '60' })
        response.end()
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('http-peer')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const root = '/api/edge-admin/v1'
    const controlPath = root + '/connections/11/delivery-control'
    const retryPath = root + '/connections/11/retry-now'
    const password = 'Controls-test-7419'
    const detail = (page: Page) =>
        page.getByRole('region', { name: '目标详情', exact: true })
    const controls = (page: Page) =>
        page.getByRole('region', { name: '目标发送操作', exact: true })
    const button = (page: Page, name: string) =>
        controls(page).getByRole('button', { name, exact: true })
    const report = async (page: Page) =>
        await (await page.request.get(root + '/runtime/outputs/11')).json()
    const readControl = async (page: Page) => {
        const response = await page.request.get(controlPath)
        expect(response.headers()['cache-control']).toBe('no-store')
        return await response.json()
    }
    const csrf = async (page: Page) => ({
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    })
    async function login(page: Page) {
        await page.goto('/runtime/outputs')
        await page.getByLabel('账号', { exact: true }).fill('controls-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
    }
    async function confirm(page: Page) {
        await controls(page)
            .getByLabel('我已核对目标并确认此发送状态变更')
            .check()
        await button(page, '确认执行').click()
    }
    async function stop() {
        await context?.close()
        context = undefined
        await host?.stop()
        host = undefined
    }
    function role(value: string) {
        // Offline role fixture only, not user-management page acceptance.
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                'UPDATE user_account SET role=?, security_stamp=? WHERE account_name=?',
            ).run(value, `controls-${value}`, 'controls-admin')
        } finally {
            db.close()
        }
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
        let page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('controls-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await page
            .getByRole('link', { name: '输出目标诊断', exact: true })
            .click()
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        await expect(
            detail(page).getByRole('heading', { name: / · 退避中$/ }),
        ).toBeVisible()
        const original = (await report(page)).items[0].sender.backoffVersion
        expect(bodies).toHaveLength(1)
        await button(page, '立即重试').click()
        await expect(controls(page)).toContainText(
            '已接受当前退避窗口的重试信号',
        )
        await expect.poll(() => bodies.length).toBe(2)
        expect(bodies[1]).toBe(bodies[0])
        expect(
            (
                await page.request.post(retryPath, {
                    headers: await csrf(page),
                    data: { revision: 1, backoffVersion: original },
                })
            ).status(),
        ).toBe(409)
        expect(bodies).toHaveLength(2)
        let retryRequests = 0
        await page.route('**/connections/11/retry-now', async (route) => {
            retryRequests++
            const response = await route.fetch()
            expect(response.status()).toBe(202)
            await route.abort('failed')
        })
        await button(page, '立即重试').click()
        await expect(controls(page)).toContainText('重试请求失败或响应丢失')
        await expect.poll(() => bodies.length).toBe(3)
        expect(bodies[2]).toBe(bodies[0])
        const beforeRetryRead = (await report(page)).usage.pendingCount
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeGreaterThan(beforeRetryRead)
        expect(retryRequests).toBe(1)
        expect(bodies).toHaveLength(3)
        await page.unroute('**/connections/11/retry-now')
        await button(page, '重新读取发送状态').click()
        await expect(controls(page)).not.toContainText('重试请求失败或响应丢失')

        await button(page, '手动暂停目标').click()
        await expect(button(page, '确认执行')).toBeDisabled()
        expect((await readControl(page)).resumeVersion).toBeNull()
        await button(page, '取消操作').click()
        expect((await readControl(page)).manuallyPaused).toBe(false)
        // Another admin wins the CAS after the UI's read. The stale confirmation cannot overwrite it.
        await button(page, '手动暂停目标').click()
        await expect(button(page, '确认执行')).toBeVisible()
        expect(
            (
                await page.request.put(controlPath, {
                    headers: await csrf(page),
                    data: {
                        paused: true,
                        confirmed: true,
                        expectedVersion: null,
                    },
                })
            ).status(),
        ).toBe(200)
        await confirm(page)
        await expect(controls(page)).toContainText('操作失败或响应丢失')
        await expect(button(page, '确认执行')).toBeDisabled()
        await expect(
            controls(page).getByLabel('我已核对目标并确认此发送状态变更'),
        ).not.toBeChecked()
        await button(page, '重新核对控制版本').click()
        // Drop only the reply after the real product has durably committed. Never stub the successful write.
        let writes = 0
        await page.route(
            '**/connections/11/delivery-control',
            async (route) => {
                if (route.request().method() !== 'PUT') return route.continue()
                writes++
                const response = await route.fetch()
                expect(response.status()).toBe(200)
                await route.abort('failed')
            },
        )
        await confirm(page)
        await expect(controls(page)).toContainText('操作失败或响应丢失')
        await expect(button(page, '确认执行')).toBeDisabled()
        await expect(
            detail(page).getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible()
        const pausedCount = (await report(page)).usage.pendingCount
        const stoppedAttempts = bodies.length
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeGreaterThan(pausedCount)
        expect(bodies).toHaveLength(stoppedAttempts)
        expect(writes).toBe(1)
        expect((await readControl(page)).manuallyPaused).toBe(true)
        await page.unroute('**/connections/11/delivery-control')
        await button(page, '重新核对控制版本').click()
        await expect(
            controls(page).getByLabel('我已核对目标并确认此发送状态变更'),
        ).not.toBeChecked()
        await confirm(page)
        await expect(controls(page)).toContainText('手动暂停状态已保存')
        await expect(button(page, '立即重试')).toBeDisabled()
        await page.screenshot({
            path: '/tmp/tenonedge-output-controls-paused.png',
            fullPage: true,
        })

        await stop()
        code = 200
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await login(page)
        await expect(
            detail(page).getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible()
        expect(bodies).toHaveLength(stoppedAttempts)
        await button(page, '恢复目标').click()
        await expect(button(page, '确认执行')).toBeDisabled()
        await confirm(page)
        await expect(controls(page)).toContainText('恢复状态已保存')
        await expect.poll(() => bodies.length).toBeGreaterThan(stoppedAttempts)
        expect(bodies[stoppedAttempts]).toBe(bodies[0])
        await expect
            .poll(async () => (await report(page)).usage.pendingCount)
            .toBeLessThan(2)
        code = 401
        await expect(
            detail(page).getByRole('heading', { name: / · 自动暂停$/ }),
        ).toBeVisible({
            timeout: 10_000,
        })
        await expect(button(page, '立即重试')).toBeDisabled()
        code = 200
        await button(page, '恢复目标').click()
        await confirm(page)
        await expect(detail(page)).toContainText('本次发送成功', {
            timeout: 10_000,
        })
        await page.screenshot({
            path: '/tmp/tenonedge-output-controls-recovered.png',
            fullPage: true,
        })

        await stop()
        role('Configurator')
        code = 503
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await login(page)
        await expect(
            detail(page).getByRole('heading', { name: / · 退避中$/ }),
        ).toBeVisible()
        await expect(button(page, '手动暂停目标')).toHaveCount(0)
        await expect(button(page, '恢复目标')).toHaveCount(0)
        expect(
            (
                await page.request.put(controlPath, {
                    headers: await csrf(page),
                    data: {
                        paused: false,
                        confirmed: true,
                        expectedVersion: (await readControl(page))
                            .resumeVersion,
                    },
                })
            ).status(),
        ).toBe(403)
        const editorAttempts = bodies.length
        code = 401
        await button(page, '立即重试').click()
        await expect(
            detail(page).getByRole('heading', { name: / · 自动暂停$/ }),
        ).toBeVisible()
        expect(bodies).toHaveLength(editorAttempts + 1)
        await expect(button(page, '立即重试')).toBeDisabled()
        await expect(button(page, '恢复目标')).toHaveCount(0)

        await stop()
        role('Viewer')
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await login(page)
        await expect(
            detail(page).getByRole('heading', { name: / · 自动暂停$/ }),
        ).toBeVisible()
        await expect(controls(page)).toHaveCount(0)
        const headers = await csrf(page)
        expect(
            (
                await page.request.post(retryPath, {
                    headers,
                    data: { revision: 1, backoffVersion: original },
                })
            ).status(),
        ).toBe(403)
        expect(
            (
                await page.request.put(controlPath, {
                    headers,
                    data: { paused: true, confirmed: true },
                })
            ).status(),
        ).toBe(403)
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
