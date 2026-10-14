import { test, expect, type Page, type Locator } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { startOwnedHost } from './support/owned-host'
const api = '/api/edge-admin/v1'
const endpoint = api + '/system/trusted-proxies'
const password = 'Proxy-rules-8341!'
const panel = (page: Page) =>
    page.getByRole('region', { name: '受信任的反向代理', exact: true })
async function setup(page: Page) {
    await page.goto('/system')
    await page.getByLabel('账号', { exact: true }).fill('proxy-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        panel(page).getByRole('button', { name: '编辑代理信任', exact: true }),
    ).toBeEnabled()
}
async function edit(view: Locator, value: string) {
    await view
        .getByRole('button', { name: '编辑代理信任', exact: true })
        .click()
    await view
        .getByLabel('代理 IP 或 CIDR（每行一项）', { exact: true })
        .fill(value)
}
async function save(view: Locator) {
    await view.getByRole('checkbox').check()
    await view
        .getByRole('button', { name: '确认保存代理信任', exact: true })
        .click()
}

test('proxy trust editor confirms risk, persists normalized rules and cannot change deployment trust or role permissions', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-proxy-browser-'))
    const args = ['--TenonEdge:TrustedProxies:0=198.51.100.1']
    let host = await startOwnedHost(data, 0, args)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        let view = panel(page)
        await expect(view.locator('[data-proxy="deployment"]')).toHaveText(
            '198.51.100.1/32',
        )
        await edit(view, '192.0.2.1\n192.0.2.1/32\n2001:db8::/64')
        await expect(view.getByText(/不要信任客户端网段/)).toBeVisible()
        await expect(
            view.getByRole('button', { name: '确认保存代理信任', exact: true }),
        ).toBeDisabled()
        await save(view)
        await expect(view.getByText(/代理信任已保存/)).toBeVisible()
        await expect(view.locator('[data-proxy="networks"] li')).toHaveText([
            '192.0.2.1/32',
            '2001:db8::/64',
        ])
        const current = await (await page.request.get(endpoint)).json()
        const session = await (
            await page.request.get(api + '/auth/session')
        ).json()
        const boundary = await page.evaluate(
            async ({ endpoint, current, csrf }) => {
                const body = JSON.stringify({
                    networks: current.networks,
                    expectedVersion: current.version,
                    confirmed: true,
                })
                const results = []
                for (const size of [131072, 131073]) {
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
        expect(boundary.map((item) => item.status)).toEqual([200, 413])
        expect(boundary[1].body.status).toBe(413)
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system')
        await page.getByLabel('账号', { exact: true }).fill('proxy-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        view = panel(page)
        await expect(view.locator('[data-proxy="networks"] li')).toHaveText(
            current.networks,
        )
        expect(await (await page.request.get(endpoint)).json()).toEqual(current)
        const auth = await (
            await page.request.get(api + '/auth/session')
        ).json()
        for (const role of ['viewer', 'configurator']) {
            expect(
                (
                    await page.request.post(api + '/users', {
                        headers: { 'X-CSRF-Token': auth.csrfToken },
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
                    .fill('New-proxy-user-5192!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    panel(user).locator('[data-proxy="networks"] li'),
                ).toHaveText(current.networks)
                await expect(
                    panel(user).getByRole('button', {
                        name: '编辑代理信任',
                        exact: true,
                    }),
                ).toHaveCount(0)
                const own = await (
                    await user.request.get(api + '/auth/session')
                ).json()
                expect(
                    (
                        await user.request.put(endpoint, {
                            headers: { 'X-CSRF-Token': own.csrfToken },
                            data: {
                                networks: [],
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
        await edit(view, '')
        await save(view)
        await expect(view.getByText(/代理信任已保存/)).toBeVisible()
        await expect(view.locator('[data-proxy="networks"]')).toHaveText(
            '无附加信任',
        )
        await expect(view.locator('[data-proxy="deployment"]')).toHaveText(
            '198.51.100.1/32',
        )
        const audits = await (
            await page.request.get(
                api + '/audit?action=system.trustedProxiesChanged',
            )
        ).json()
        expect(audits.items).toHaveLength(2)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('proxy trust retains validation input and never overwrites a conflict or replays a lost commit reply', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-proxy-errors-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        const view = panel(page)
        let writes = 0
        page.on('request', (request) => {
            if (
                request.method() === 'PUT' &&
                request.url().endsWith('/trusted-proxies')
            )
                writes++
        })
        await edit(view, 'proxy.example.test')
        await save(view)
        await expect(view.getByRole('form')).toBeVisible()
        await expect(
            view.getByLabel('代理 IP 或 CIDR（每行一项）'),
        ).toHaveValue('proxy.example.test')
        expect(writes).toBe(1)
        const auth = await (
            await page.request.get(api + '/auth/session')
        ).json()
        expect(
            (
                await page.request.put(endpoint, {
                    headers: { 'X-CSRF-Token': auth.csrfToken },
                    data: {
                        networks: ['192.0.2.1'],
                        expectedVersion: null,
                        confirmed: true,
                    },
                })
            ).status(),
        ).toBe(200)
        await view.getByLabel('代理 IP 或 CIDR（每行一项）').fill('192.0.2.2')
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(2)
        await expect(
            view.getByRole('button', { name: '编辑代理信任', exact: true }),
        ).toBeDisabled()
        await view
            .getByRole('button', { name: '重新读取代理信任', exact: true })
            .click()
        await edit(view, '192.0.2.3')
        await page.route('**/system/trusted-proxies', async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            expect((await route.fetch()).status()).toBe(200)
            await route.abort('failed')
        })
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(3)
        await page.unroute('**/system/trusted-proxies')
        await page.reload()
        await expect(panel(page).locator('[data-proxy="networks"]')).toHaveText(
            '192.0.2.3/32',
        )
        expect(writes).toBe(3)
        await page.route('**/system/trusted-proxies', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503}',
            }),
        )
        await panel(page)
            .getByRole('button', { name: '重新读取代理信任', exact: true })
            .click()
        await expect(panel(page).getByText(/代理信任读取失败/)).toBeVisible()
        await expect(panel(page).locator('[data-proxy]')).toHaveCount(0)
        await expect(
            panel(page).getByRole('button', {
                name: '编辑代理信任',
                exact: true,
            }),
        ).toBeDisabled()
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
