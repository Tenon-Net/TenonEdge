import { test, expect, type BrowserContext, type Page } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('failed records: real single/batch retry, stale generation, lost reply, frozen FIFO, persistent pause and three roles', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const data = await mkdtemp(
        join(tmpdir(), 'tenonedge-browser-failed-retry-'),
    )
    let code = 422
    const attempts: string[] = []
    const peer = createServer(async (request, response) => {
        let body = ''
        for await (const chunk of request) body += chunk.toString()
        attempts.push(body)
        response.writeHead(code)
        response.end()
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('http-peer')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const root = '/api/edge-admin/v1'
    const retryPath = root + '/deliveries/retry'
    const password = 'Failed-retry-test-7421'
    const panel = (page: Page) =>
        page.getByRole('region', { name: '队列记录操作', exact: true })
    const button = (page: Page, name: string | RegExp) =>
        panel(page).getByRole('button', {
            name,
            exact: typeof name === 'string',
        })
    const readFailed = async (page: Page) => {
        const response = await page.request.get(
            root + '/deliveries?state=failed&connectionId=11',
        )
        expect(response.status(), await response.text()).toBe(200)
        return (await response.json()).items as {
            sequence: number
            deliveryId: string
        }[]
    }
    const csrf = async (page: Page) => ({
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    })
    async function confirm(page: Page) {
        await panel(page)
            .getByLabel('我已核对本批记录，理解可能产生重复并确认原样重试')
            .check()
        await button(page, '确认重试本批').click()
    }
    async function stop() {
        await context?.close()
        context = undefined
        await host?.stop()
        host = undefined
    }
    function role(value: string) {
        // Offline fixture only: not user-management acceptance.
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                'UPDATE user_account SET role=?, security_stamp=? WHERE account_name=?',
            ).run(value, `failed-retry-${value}`, 'retry-admin')
        } finally {
            db.close()
        }
    }
    function stored(id: string) {
        const db = new DatabaseSync(join(data, 'queue.db'), { readOnly: true })
        try {
            return db
                .prepare(
                    'SELECT seq,state,CAST(payload AS TEXT) AS body,created_at,last_retried_at,failure_count,last_error FROM delivery WHERE delivery_id=?',
                )
                .get(id)
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
        await page.getByLabel('账号', { exact: true }).fill('retry-admin')
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
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        let page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('retry-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByRole('link', { name: '输出目标诊断', exact: true }),
        ).toBeVisible()
        await expect
            .poll(async () => (await readFailed(page)).length)
            .toBeGreaterThanOrEqual(4)
        await page.goto('/runtime/outputs')
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        await page
            .getByRole('button', { name: '手动暂停目标', exact: true })
            .click()
        await page.getByLabel('我已核对目标并确认此发送状态变更').check()
        await page
            .getByRole('button', { name: '确认执行', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible()
        const sendsBefore = attempts.length
        const original = await readFailed(page)
        const before = original.map((item) => stored(item.deliveryId)!)
        await page.goto('/deliveries')
        await page
            .getByLabel('选择记录 ' + original[0].deliveryId, { exact: true })
            .check()
        await button(page, '核对本页已选 1 条').click()
        await expect(button(page, '确认重试本批')).toBeDisabled()
        await expect(panel(page)).toContainText('可能产生重复')
        expect(stored(original[0].deliveryId)?.state).toBe('failed')
        await button(page, '取消本批').click()
        await expect(
            page.getByLabel('选择记录 ' + original[0].deliveryId, {
                exact: true,
            }),
        ).not.toBeChecked()
        await page
            .getByLabel('选择记录 ' + original[0].deliveryId, { exact: true })
            .check()
        await button(page, '核对本页已选 1 条').click()
        await confirm(page)
        await expect(panel(page)).toContainText(
            '已将 1 条记录提交到待发队尾，不代表发送成功',
        )
        await expect(button(page, '确认重试本批')).toBeDisabled()
        expect(stored(original[0].deliveryId)?.state).toBe('pending')
        expect(attempts).toHaveLength(sendsBefore)

        await stop()
        role('Configurator')
        page = await login()
        await expect(panel(page)).toBeVisible()
        const second = original[1]
        await page
            .getByLabel('选择记录 ' + second.deliveryId, { exact: true })
            .check()
        await button(page, '核对本页已选 1 条').click()
        await expect(button(page, '确认重试本批')).toBeVisible()
        // A real concurrent actor wins. This stale generation must reject, not retry the new generation.
        expect(
            (
                await page.request.post(retryPath, {
                    headers: await csrf(page),
                    data: { revision: '1', items: [second] },
                })
            ).status(),
        ).toBe(200)
        await confirm(page)
        await expect(panel(page)).toContainText('重试失败或响应丢失')
        await expect(button(page, '确认重试本批')).toBeDisabled()
        await expect(
            panel(page).getByLabel(
                '我已核对本批记录，理解可能产生重复并确认原样重试',
            ),
        ).not.toBeChecked()
        await button(page, '重新读取并选择').click()
        await page.getByLabel('目标连接编号', { exact: true }).fill('11')
        await page
            .getByLabel('原因包含（区分大小写）', { exact: true })
            .fill('422')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        // An edited but unapplied filter must not alter the batch.
        await page
            .getByLabel('原因包含（区分大小写）', { exact: true })
            .fill('NOT-APPLIED')
        await button(page, '按已应用筛选准备本批（最多 1000 条）').click()
        await expect(panel(page)).toContainText(
            `本次冻结 ${original.length - 2} 条`,
        )
        await expect(panel(page)).toContainText('已读到当前筛选的末页')
        let writes = 0
        await page.route('**/deliveries/retry', async (route) => {
            writes++
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await confirm(page)
        await expect(panel(page)).toContainText('重试失败或响应丢失')
        await expect(button(page, '确认重试本批')).toBeDisabled()
        expect(await readFailed(page)).toEqual([])
        const after = original.map((item) => stored(item.deliveryId)!)
        after.forEach((row, index) => {
            expect(row.state).toBe('pending')
            expect(row.seq).toBeGreaterThan(original[index].sequence)
            if (index > 0)
                expect(row.seq).toBeGreaterThan(after[index - 1].seq as number)
            expect(row.body).toBe(before[index].body)
            expect(row.created_at).toBe(before[index].created_at)
            expect(row.last_retried_at).not.toBeNull()
            expect(row.last_error).toBeNull()
            expect(row.failure_count).toBe(0)
        })
        expect(attempts).toHaveLength(sendsBefore)
        // A fresh GET settles the observation; it must never replay the failed mutation.
        await page
            .getByRole('region', { name: '故障记录', exact: true })
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        expect(writes).toBe(1)
        await page.screenshot({
            path: '/tmp/tenonedge-failed-retry-lost-reply.png',
            fullPage: true,
        })
        await page.unroute('**/deliveries/retry')

        await stop()
        role('Viewer')
        page = await login()
        await expect(
            page.getByText(
                '只读角色只能查看元数据和原因，不能查看报文正文、重试或清理记录。',
            ),
        ).toBeVisible()
        await expect(panel(page)).toHaveCount(0)
        await expect(page.getByRole('checkbox')).toHaveCount(0)
        expect(
            (
                await page.request.post(retryPath, {
                    headers: await csrf(page),
                    data: { revision: '1', items: [original[0]] },
                })
            ).status(),
        ).toBe(403)
        expect(attempts).toHaveLength(sendsBefore)

        await stop()
        role('Administrator')
        code = 200
        page = await login()
        await page.goto('/runtime/outputs')
        await page
            .getByRole('button', { name: '查看目标 11', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: / · 手动暂停$/ }),
        ).toBeVisible()
        expect(attempts).toHaveLength(sendsBefore)
        await page
            .getByRole('button', { name: '恢复目标', exact: true })
            .click()
        await page.getByLabel('我已核对目标并确认此发送状态变更').check()
        await page
            .getByRole('button', { name: '确认执行', exact: true })
            .click()
        await expect
            .poll(() =>
                original.every((item) => stored(item.deliveryId) === undefined),
            )
            .toBe(true)
        const resent = attempts
            .slice(sendsBefore)
            .filter((body) => before.some((item) => item.body === body))
        expect(resent).toEqual(before.map((item) => item.body))
        const audit = await (
            await page.request.get(root + '/audit?action=delivery.retryFailed')
        ).json()
        expect(
            audit.items.filter(
                (entry: { action: string; result: string }) =>
                    entry.action === 'delivery.retryFailed' &&
                    entry.result === 'success',
            ),
        ).toHaveLength(3)
        await page.screenshot({
            path: '/tmp/tenonedge-failed-retry-recovered.png',
            fullPage: true,
        })
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
