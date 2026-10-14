import { test, expect, type Page, type Locator } from '@playwright/test'
import { createServer, type ServerResponse } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const endpoint = root + '/system/runtime-parameters'
const password = 'Runtime-parameters-7831!'
function panel(page: Page) {
    return page.getByRole('region', {
        name: '运行参数：预览、审计与调试缓存',
        exact: true,
    })
}
async function setup(page: Page) {
    await page.goto('/system')
    await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        panel(page).getByRole('button', { name: '编辑运行参数', exact: true }),
    ).toBeEnabled()
}
async function edit(
    view: Locator,
    days: string,
    global: string,
    session: string,
) {
    await view
        .getByRole('button', { name: '编辑运行参数', exact: true })
        .click()
    await view.getByLabel('审计保留期（天）', { exact: true }).fill(days)
    await view.getByLabel('预览全局并发上限', { exact: true }).fill(global)
    await view.getByLabel('每个会话预览并发上限', { exact: true }).fill(session)
}
async function save(view: Locator) {
    await view.getByRole('checkbox').check()
    await view
        .getByRole('button', { name: '确认保存运行参数', exact: true })
        .click()
}
async function saved(view: Locator) {
    await expect(
        view.getByText('运行参数已保存；变更已记审计，无变化不重复写入。', {
            exact: true,
        }),
    ).toBeVisible()
    await expect(
        view.getByRole('button', { name: '编辑运行参数', exact: true }),
    ).toBeEnabled()
}

test('runtime parameter confirmation changes real preview admission, persists across Host restart and respects read-only roles', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    let held: ServerResponse | undefined
    let received!: () => void
    const arrived = new Promise<void>((resolve) => {
        received = resolve
    })
    const server = createServer((_request, response) => {
        held = response
        received()
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('listener')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-parameters-browser-'))
    let host = await startOwnedHost(data, 0, [
        `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
    ])
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        let view = panel(page)
        const originalDraft = await (
            await page.request.get(root + '/devices')
        ).json()
        await edit(view, '365', '1', '1')
        await view
            .getByLabel('调试缓存每对象字节上限', { exact: true })
            .fill('67584')
        await view
            .getByLabel('调试缓存全局字节上限', { exact: true })
            .fill('135168')
        await view
            .getByLabel('调试缓存每对象条数上限', { exact: true })
            .fill('2')
        await expect(
            view.getByRole('button', { name: '确认保存运行参数', exact: true }),
        ).toBeDisabled()
        await expect(
            view.getByText(/调高资源上限前必须在本机实测/),
        ).toBeVisible()
        await save(view)
        await saved(view)
        await expect(
            view.locator('[data-parameter="debugObjectBytes"]'),
        ).toHaveText('67584')
        await expect(
            view.locator('[data-parameter="debugGlobalBytes"]'),
        ).toHaveText('135168')
        await expect(
            view.locator('[data-parameter="debugObjectRecords"]'),
        ).toHaveText('2')
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const pending = page.request.post(root + '/connections/test/http', {
            headers,
            data: {
                connection: {
                    baseAddress: `http://127.0.0.1:${address.port}/`,
                    timeoutMs: 30000,
                },
            },
        })
        await arrived
        const busy = await page.request.post(
            root + '/connections/inspect-outbound',
            { headers, data: { host: '203.0.113.1', port: 443 } },
        )
        expect(busy.status()).toBe(429)
        // A real in-progress HTTP diagnostic still owns the lease; saving a higher
        // limit does not replace the budget or cancel that request.
        await edit(view, '730', '6', '3')
        await save(view)
        await saved(view)
        expect(held?.writableEnded).toBe(false)
        const admitted = await page.request.post(
            root + '/connections/inspect-outbound',
            { headers, data: { host: '203.0.113.1', port: 443 } },
        )
        expect(admitted.status()).toBe(200)
        held!.end('ok')
        expect((await pending).status()).toBe(200)
        const current = await (await page.request.get(endpoint)).json()
        // Native Chromium fetch, not APIRequestContext, exercises Kestrel's body limit.
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
        expect((await (await page.request.get(endpoint)).json()).version).toBe(
            current.version,
        )
        expect(
            await (await page.request.get(root + '/devices')).json(),
        ).toEqual(originalDraft)
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .coordinator.activeRevision,
        ).toBeNull()
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system')
        await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        view = panel(page)
        await expect(
            view.locator('[data-parameter="auditRetentionDays"]'),
        ).toHaveText('730')
        await expect(
            view.locator('[data-parameter="previewGlobalLimit"]'),
        ).toHaveText('6')
        expect(await (await page.request.get(endpoint)).json()).toEqual(current)
        const audit = await (
            await page.request.get(
                root + '/audit?action=system.runtimeParametersChanged',
            )
        ).json()
        expect(audit.items).toHaveLength(2)
        expect(
            JSON.parse(audit.items[0].detail).current.Values.AuditRetentionDays,
        ).toBe(730)
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
                await user.goto('/system')
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
                        '[data-parameter="auditRetentionDays"]',
                    ),
                ).toHaveText('730')
                await expect(
                    panel(user).getByRole('button', {
                        name: '编辑运行参数',
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
        held?.end()
        server.closeAllConnections()
        await new Promise<void>((resolve) => server.close(() => resolve()))
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('parameter editor retains validation input, freezes version and never replays conflicts or lost replies', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-parameters-errors-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        const view = panel(page)
        let writes = 0
        page.on('request', (r) => {
            if (r.method() === 'PUT' && r.url().endsWith('/runtime-parameters'))
                writes++
        })
        await edit(view, '179', '4', '2')
        await save(view)
        await expect(view.getByText(/请填写范围内的整数/)).toBeVisible()
        expect(writes).toBe(0)
        await expect(
            view.getByLabel('审计保留期（天）', { exact: true }),
        ).toHaveValue('179')
        await view.getByLabel('审计保留期（天）', { exact: true }).fill('365')
        page.once('dialog', (dialog) => dialog.dismiss())
        await page.getByRole('link', { name: '总览', exact: true }).click()
        await expect(page).toHaveURL(/\/system$/)
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        expect(
            (
                await page.request.put(endpoint, {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        values: {
                            auditRetentionDays: 730,
                            previewGlobalLimit: 4,
                            previewSessionLimit: 2,
                            debugObjectBytes: 1048576,
                            debugGlobalBytes: 33554432,
                            debugObjectRecords: 100,
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
            view.getByRole('button', { name: '编辑运行参数', exact: true }),
        ).toBeDisabled()
        await view
            .getByRole('button', { name: '重新读取运行参数', exact: true })
            .click()
        await edit(view, '1000', '8', '4')
        await page.route('**/system/runtime-parameters', async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(2)
        await page.unroute('**/system/runtime-parameters')
        await page.reload()
        await expect(
            panel(page).locator('[data-parameter="auditRetentionDays"]'),
        ).toHaveText('1000')
        expect(writes).toBe(2)
        const audits = await (
            await page.request.get(
                root + '/audit?action=system.runtimeParametersChanged',
            )
        ).json()
        expect(audits.items).toHaveLength(2)
        await page.route('**/system/runtime-parameters', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503,"detail":"fixture"}',
            }),
        )
        await panel(page)
            .getByRole('button', { name: '重新读取运行参数', exact: true })
            .click()
        await expect(panel(page).getByText(/参数读取失败/)).toBeVisible()
        await expect(panel(page).locator('[data-parameter]')).toHaveCount(0)
        await expect(
            panel(page).getByRole('button', {
                name: '编辑运行参数',
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
