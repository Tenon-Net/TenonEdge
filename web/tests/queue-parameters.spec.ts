import { test, expect, type Page, type Locator } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const endpoint = root + '/system/queue-parameters'
const password = 'Queue-parameters-7831!'
function panel(page: Page) {
    return page.getByRole('region', {
        name: '队列配额、保留期与磁盘保护',
        exact: true,
    })
}
async function setup(page: Page) {
    await page.goto('/system?section=resources')
    await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        panel(page).getByRole('button', { name: '编辑队列参数', exact: true }),
    ).toBeEnabled()
}
async function edit(view: Locator, count: string) {
    await view
        .getByRole('button', { name: '编辑队列参数', exact: true })
        .click()
    await view.getByLabel('每个目标待发条数上限', { exact: true }).fill(count)
}
async function save(view: Locator) {
    await view.getByRole('checkbox').check()
    await view
        .getByRole('button', { name: '确认保存队列参数', exact: true })
        .click()
}
async function saved(view: Locator) {
    await expect(
        view.getByText('队列参数已保存；变更已记审计，无变化不重复写入。', {
            exact: true,
        }),
    ).toBeVisible()
    await expect(
        view.getByRole('button', { name: '编辑队列参数', exact: true }),
    ).toBeEnabled()
}

test('queue settings change real admission without deleting rows, persist across Host restart, require confirmation and protect all roles', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    let sends = 0
    const server = createServer(async (request, response) => {
        for await (const _ of request) {
            /* consume actual output */
        }
        sends++
        response.writeHead(503)
        response.end()
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('listener')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-queue-browser-'))
    const seed = join(data, 'seed.json')
    await writeFile(
        seed,
        JSON.stringify({
            schemaVersion: 1,
            units: [
                {
                    kind: 'input.simulator',
                    key: 'meter/sample',
                    configuration: { periodMs: 1000, sample: { reading: 42 } },
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
    const args = [
        `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
        `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
    ]
    let host = await startOwnedHost(data, 0, args)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        let view = panel(page)
        const originalDraft = await (
            await page.request.get(root + '/devices')
        ).json()
        const report = async () => {
            const response = await page.request.get(
                root + '/runtime/outputs/11',
            )
            expect(response.status(), await response.text()).toBe(200)
            return response.json()
        }
        await expect
            .poll(async () => (await report()).usage.pendingCount)
            .toBeGreaterThanOrEqual(3)
        await expect.poll(() => sends).toBeGreaterThan(0)
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const control = await (
            await page.request.get(root + '/connections/11/delivery-control')
        ).json()
        expect(
            (
                await page.request.put(
                    root + '/connections/11/delivery-control',
                    {
                        headers: { 'X-CSRF-Token': session.csrfToken },
                        data: {
                            paused: true,
                            confirmed: true,
                            expectedVersion: control.resumeVersion,
                        },
                    },
                )
            ).status(),
        ).toBe(200)
        await expect
            .poll(async () => (await report()).items[0].state)
            .toBe('manuallyPaused')
        const revision = (await report()).revision
        await edit(view, '1')
        await expect(
            view.getByRole('button', { name: '确认保存队列参数', exact: true }),
        ).toBeDisabled()
        await expect(view.getByText(/降低配额不立即删除旧记录/)).toBeVisible()
        await save(view)
        await saved(view)
        const first = await report()
        const count = first.usage.pendingCount
        expect(count).toBeGreaterThanOrEqual(3)
        expect(first.limits.targetCount).toBe(1)
        await expect
            .poll(async () => (await report()).counters.quotaRejected)
            .toBeGreaterThan(first.counters.quotaRejected)
        expect((await report()).usage.pendingCount).toBe(count)
        // No second writer or runtime is created. The original simulator resumes
        // admission into the same paused queue as soon as the limit increases.
        await edit(view, String(count + 2))
        await view.getByLabel('待发保留期（小时）', { exact: true }).fill('72')
        await save(view)
        await saved(view)
        await expect
            .poll(async () => (await report()).usage.pendingCount)
            .toBe(count + 2)
        expect((await report()).revision).toBe(revision)
        expect((await report()).items[0].state).toBe('manuallyPaused')
        const current = await (await page.request.get(endpoint)).json()
        const boundary = await page.evaluate(
            async ({ endpoint, current, csrf }) => {
                const body = JSON.stringify({
                    values: current.values,
                    expectedVersion: current.version,
                    confirmed: true,
                })
                const results = []
                for (const size of [4096, 4097]) {
                    const response = await fetch(endpoint, {
                        method: 'PUT',
                        headers: {
                            'Content-Type': 'application/json',
                            'X-CSRF-Token': csrf,
                        },
                        body: body.padEnd(size, ' '),
                    })
                    results.push({
                        status: response.status,
                        body: await response.json(),
                    })
                }
                return results
            },
            { endpoint, current, csrf: session.csrfToken },
        )
        expect(boundary.map((r) => r.status)).toEqual([200, 413])
        expect(boundary[1].body.status).toBe(413)
        expect(await (await page.request.get(endpoint)).json()).toEqual(current)
        expect(
            await (await page.request.get(root + '/devices')).json(),
        ).toEqual(originalDraft)
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system?section=resources')
        await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        view = panel(page)
        await expect(
            view.locator('[data-queue-parameter="maximumPendingPerTarget"]'),
        ).toHaveText(String(count + 2))
        await expect(
            view.locator('[data-queue-parameter="pendingRetentionHours"]'),
        ).toHaveText('72')
        expect(await (await page.request.get(endpoint)).json()).toEqual(current)
        expect((await report()).usage.pendingCount).toBe(count + 2)
        expect((await report()).revision).toBe(revision)
        const audit = await (
            await page.request.get(
                root + '/audit?action=system.queueParametersChanged',
            )
        ).json()
        expect(audit.items).toHaveLength(2)
        expect(
            JSON.parse(audit.items[0].detail).current.Values
                .PendingRetentionHours,
        ).toBe(72)
        const own = await (
            await page.request.get(root + '/auth/session')
        ).json()
        for (const role of ['viewer', 'configurator']) {
            expect(
                (
                    await page.request.post(root + '/users', {
                        headers: { 'X-CSRF-Token': own.csrfToken },
                        data: {
                            accountName: role,
                            role,
                            temporaryPassword: password,
                            isEnabled: true,
                        },
                    })
                ).status(),
            ).toBe(200)
            const other = await browser.newContext({ baseURL: host.url })
            try {
                const user = await other.newPage()
                await user.goto('/system?section=resources')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user.getByLabel('密码', { exact: true }).fill(password)
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user.getByLabel('原密码', { exact: true }).fill(password)
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Changed-parameters-5613!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    panel(user).locator(
                        '[data-queue-parameter="pendingRetentionHours"]',
                    ),
                ).toHaveText('72')
                await expect(
                    panel(user).getByRole('button', {
                        name: '编辑队列参数',
                        exact: true,
                    }),
                ).toHaveCount(0)
                const auth = await (
                    await user.request.get(root + '/auth/session')
                ).json()
                expect(
                    (
                        await user.request.put(endpoint, {
                            headers: { 'X-CSRF-Token': auth.csrfToken },
                            data: {
                                values: current.values,
                                expectedVersion: current.version,
                                confirmed: true,
                            },
                        })
                    ).status(),
                ).toBe(403)
            } finally {
                await other.close()
            }
        }
    } finally {
        server.closeAllConnections()
        await new Promise<void>((resolve) => server.close(() => resolve()))
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('queue parameter editor retains validation input, freezes version and never replays conflicts or lost replies', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const data = await mkdtemp(
        join(tmpdir(), 'tenonedge-queue-parameters-errors-'),
    )
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        const view = panel(page)
        let writes = 0
        page.on('request', (r) => {
            if (r.method() === 'PUT' && r.url().endsWith('/queue-parameters'))
                writes++
        })
        await edit(view, '0')
        await save(view)
        await expect(view.getByText(/请填写规定范围内的整数/)).toBeVisible()
        expect(writes).toBe(0)
        await expect(
            view.getByLabel('每个目标待发条数上限', { exact: true }),
        ).toHaveValue('0')
        await view
            .getByLabel('每个目标待发条数上限', { exact: true })
            .fill('365')
        page.once('dialog', (dialog) => dialog.dismiss())
        await page.getByRole('link', { name: '总览', exact: true }).click()
        await expect(page).toHaveURL(/\/system\?section=resources$/)
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const initial = await (await page.request.get(endpoint)).json()
        expect(
            (
                await page.request.put(endpoint, {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        values: {
                            ...initial.values,
                            maximumPendingPerTarget: 730,
                        },
                        expectedVersion: null,
                        confirmed: true,
                    },
                })
            ).status(),
        ).toBe(200)
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(1)
        await expect(view.getByRole('form')).toHaveCount(0)
        await expect(
            view.getByRole('button', { name: '编辑队列参数', exact: true }),
        ).toBeDisabled()
        await view
            .getByRole('button', { name: '重新读取队列参数', exact: true })
            .click()
        await edit(view, '1000')
        await page.route('**/system/queue-parameters', async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(2)
        await page.unroute('**/system/queue-parameters')
        await page.reload()
        await expect(
            panel(page).locator(
                '[data-queue-parameter="maximumPendingPerTarget"]',
            ),
        ).toHaveText('1000')
        expect(writes).toBe(2)
        const audits = await (
            await page.request.get(
                root + '/audit?action=system.queueParametersChanged',
            )
        ).json()
        expect(audits.items).toHaveLength(2)
        await page.route('**/system/queue-parameters', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503,"detail":"fixture"}',
            }),
        )
        await panel(page)
            .getByRole('button', { name: '重新读取队列参数', exact: true })
            .click()
        await expect(panel(page).getByText(/队列参数读取失败/)).toBeVisible()
        await expect(panel(page).locator('[data-queue-parameter]')).toHaveCount(
            0,
        )
        await expect(
            panel(page).getByRole('button', {
                name: '编辑队列参数',
                exact: true,
            }),
        ).toBeDisabled()
        expect(writes).toBe(2)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
