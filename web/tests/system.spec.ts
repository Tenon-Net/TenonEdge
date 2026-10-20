import {
    test,
    expect,
    type Page,
    type Browser,
    type Locator,
} from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const policyUrl = root + '/system/outbound-policy'
const password = 'System-browser-5918!'
const gatewayName = '现场一号 <网关>'
async function setup(page: Page) {
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill('system-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page.getByLabel('网关名称（可选）', { exact: true }).fill(gatewayName)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    await page
        .getByRole('link', { name: '系统信息与设置', exact: true })
        .click()
    await page.getByRole('tab', { name: '网络与访问', exact: true }).click()
    await expect(
        page.getByRole('button', { name: '编辑网页规则', exact: true }),
    ).toBeEnabled()
}
function panel(page: Page) {
    return page.getByRole('region', { name: '出站访问规则', exact: true })
}
async function save(rules: Locator) {
    await rules.getByRole('checkbox').check()
    await rules
        .getByRole('button', { name: '确认保存出站规则', exact: true })
        .click()
}
async function saved(rules: Locator) {
    await expect(
        rules.getByText(
            '网页出站规则已保存并记审计。已建立的连接不会被强制断开。',
            { exact: true },
        ),
    ).toBeVisible()
    await expect(
        rules.getByRole('button', { name: '编辑网页规则', exact: true }),
    ).toBeEnabled()
}
async function peer() {
    let requests = 0
    const server = createServer((_request, response) => {
        requests++
        response.writeHead(204)
        response.end()
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('peer-address')
    return {
        port: address.port,
        requests: () => requests,
        close: () =>
            new Promise<void>((resolve, reject) =>
                server.close((error) => (error ? reject(error) : resolve())),
            ),
    }
}
async function roleChecks(browser: Browser, page: Page, url: string) {
    const session = await (
        await page.request.get(root + '/auth/session')
    ).json()
    for (const role of ['viewer', 'configurator']) {
        expect(
            (
                await page.request.post(root + '/users', {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        accountName: role,
                        role,
                        temporaryPassword: password,
                        isEnabled: true,
                    },
                })
            ).status(),
        ).toBe(200)
        const other = await browser.newContext({ baseURL: url })
        try {
            const user = await other.newPage()
            await user.goto('/system?section=network')
            await user.getByLabel('账号', { exact: true }).fill(role)
            await user.getByLabel('密码', { exact: true }).fill(password)
            await user
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await user.getByLabel('原密码', { exact: true }).fill(password)
            await user
                .getByLabel('新密码', { exact: true })
                .fill('System-changed-7291!')
            await user
                .getByRole('button', { name: '保存新密码', exact: true })
                .click()
            await expect(
                user.getByRole('heading', {
                    name: '系统信息与设置',
                    exact: true,
                }),
            ).toBeVisible()
            await expect(
                panel(user).getByText(
                    '所有角色可查看；仅管理员可修改网页持久规则。',
                    { exact: true },
                ),
            ).toBeVisible()
            await expect(
                panel(user).getByRole('button', {
                    name: '编辑网页规则',
                    exact: true,
                }),
            ).toHaveCount(0)
            const current = await (
                await user.request.get(root + '/auth/session')
            ).json()
            expect((await user.request.get(policyUrl)).status()).toBe(200)
            expect(
                (await user.request.get(root + '/system/info')).status(),
            ).toBe(200)
            expect(
                (
                    await user.request.put(policyUrl, {
                        headers: { 'X-CSRF-Token': current.csrfToken },
                        data: { networks: [], localPorts: [] },
                    })
                ).status(),
            ).toBe(403)
        } finally {
            await other.close()
        }
    }
}

test('system info matches real Host; web grants control real local HTTP, persist across restart and cannot revoke deployment grants', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-system-browser-'))
    const local = await peer()
    const deployment = await peer()
    const args = [
        `--TenonEdge:Outbound:LocalPorts:0=${deployment.port}`,
        '--TenonEdge:Outbound:Networks:0:Cidr=198.51.100.0/24',
        '--TenonEdge:Outbound:Networks:0:Port=8883',
    ]
    let host = await startOwnedHost(data, 0, args)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        await expect(page).toHaveTitle(`${gatewayName} · TenonEdge`)
        await expect(page.getByRole('banner')).toContainText(gatewayName)
        const info = await (
            await page.request.get(root + '/system/info')
        ).json()
        await page.getByRole('tab', { name: '基本信息', exact: true }).click()
        const infoPanel = page.getByRole('region', {
            name: '系统信息',
            exact: true,
        })
        for (const value of [
            info.nodeId,
            info.version,
            info.dataDirectory,
            info.httpsCertificate.sha256Fingerprint,
            String(info.dataDirectoryDisk.totalBytes),
        ])
            await expect(infoPanel).toContainText(value)
        await infoPanel.getByText('SQLite 编译选项', { exact: true }).click()
        await expect(infoPanel).toContainText(
            info.configStore.sqliteCompileOptions[0],
        )
        await page.getByRole('tab', { name: '网络与访问', exact: true }).click()
        let rules = panel(page)
        await expect(
            rules.getByRole('region', {
                name: '部署规则（只读）',
                exact: true,
            }),
        ).toContainText(String(deployment.port))
        await expect(
            rules.getByRole('region', {
                name: '网页持久规则（可编辑）',
                exact: true,
            }),
        ).not.toContainText(String(deployment.port))
        async function probe(port: number) {
            const session = await (
                await page.request.get(root + '/auth/session')
            ).json()
            const response = await page.request.post(
                root + '/connections/test/http',
                {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        connection: {
                            baseAddress: `http://127.0.0.1:${port}/`,
                            timeoutMs: 1000,
                        },
                        authentication: 'none',
                    },
                },
            )
            expect(response.status()).toBe(200)
            return await response.json()
        }
        expect((await probe(local.port)).code).toBe('outbound.denied')
        expect(local.requests()).toBe(0)
        expect((await probe(deployment.port)).success).toBe(true)
        expect(deployment.requests()).toBe(1)
        await rules
            .getByRole('button', { name: '编辑网页规则', exact: true })
            .click()
        await expect(
            rules.getByRole('button', {
                name: '确认保存出站规则',
                exact: true,
            }),
        ).toBeDisabled()
        await rules
            .getByLabel('本机端口例外', { exact: true })
            .fill(`${local.port}\n${new URL(host.url).port}`)
        await rules
            .getByRole('button', { name: '添加 CIDR 规则', exact: true })
            .click()
        await rules.getByLabel('CIDR 1', { exact: true }).fill('203.0.113.0/24')
        await save(rules)
        await saved(rules)
        expect((await probe(local.port)).success).toBe(true)
        expect(local.requests()).toBe(1)
        expect((await probe(Number(new URL(host.url).port))).code).toBe(
            'outbound.denied',
        )
        const stored = await (await page.request.get(policyUrl)).json()
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system?section=network')
        await page.getByLabel('账号', { exact: true }).fill('system-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        rules = panel(page)
        await expect(
            rules.getByRole('button', { name: '编辑网页规则', exact: true }),
        ).toBeEnabled()
        expect(await (await page.request.get(policyUrl)).json()).toEqual(stored)
        expect((await probe(local.port)).success).toBe(true)
        expect(local.requests()).toBe(2)
        await rules
            .getByRole('button', { name: '编辑网页规则', exact: true })
            .click()
        await rules.getByLabel('本机端口例外', { exact: true }).fill('')
        await rules
            .getByRole('button', { name: '删除规则 1', exact: true })
            .click()
        await save(rules)
        await saved(rules)
        expect((await probe(local.port)).code).toBe('outbound.denied')
        expect(local.requests()).toBe(2)
        expect((await probe(deployment.port)).success).toBe(true)
        expect(deployment.requests()).toBe(2)
        const audits = await (
            await page.request.get(
                root + '/audit?action=system.outboundPolicyChanged',
            )
        ).json()
        expect(audits.items).toHaveLength(2)
        expect(
            audits.items.every(
                (row: { accountName: string }) =>
                    row.accountName === 'system-admin',
            ),
        ).toBe(true)
        await roleChecks(browser, page, host.url)
        await page.screenshot({
            path: 'test-results/system-settings.png',
            fullPage: true,
        })
    } finally {
        await context.close()
        await host.stop()
        await local.close()
        await deployment.close()
        await rm(data, { recursive: true, force: true })
    }
})

test('outbound editor preserves correctable errors but refuses stale versions and lost-response replay', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-policy-browser-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        // Ordinary HTTP on a remote LAN does not expose Crypto.randomUUID.
        await page.addInitScript(() =>
            Object.defineProperty(window, 'crypto', { value: undefined }),
        )
        await setup(page)
        const rules = panel(page)
        let writes = 0
        page.on('request', (request) => {
            if (
                request.method() === 'PUT' &&
                request.url().endsWith('/system/outbound-policy')
            )
                writes++
        })
        await rules
            .getByRole('button', { name: '编辑网页规则', exact: true })
            .click()
        await rules
            .getByRole('button', { name: '添加 CIDR 规则', exact: true })
            .click()
        await rules.getByLabel('CIDR 1', { exact: true }).fill('not-a-cidr')
        await rules.getByLabel('端口 1', { exact: true }).fill('0')
        await save(rules)
        await expect(rules.getByText(/请核对 CIDR 非空/)).toBeVisible()
        expect(writes).toBe(0)
        await rules.getByLabel('端口 1', { exact: true }).fill('443')
        await save(rules)
        await expect(rules.getByText(/出站 CIDR 无效/)).toBeVisible()
        await expect(rules.getByLabel('CIDR 1', { exact: true })).toHaveValue(
            'not-a-cidr',
        )
        expect(writes).toBe(1)
        await expect(rules.getByRole('checkbox')).not.toBeChecked()
        await rules.getByLabel('CIDR 1', { exact: true }).fill('203.0.113.0/24')
        page.once('dialog', (dialog) => dialog.dismiss())
        await page.getByRole('link', { name: '总览', exact: true }).click()
        await expect(page).toHaveURL(/\/system$/)
        await expect(rules.getByLabel('CIDR 1', { exact: true })).toHaveValue(
            '203.0.113.0/24',
        )
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        expect(
            (
                await page.request.put(policyUrl, {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        expectedVersion: null,
                        networks: [],
                        localPorts: [1883],
                    },
                })
            ).status(),
        ).toBe(200)
        await save(rules)
        await expect(rules.getByText(/版本冲突也不能直接覆盖/)).toBeVisible()
        await expect(
            rules.getByRole('region', {
                name: '网页持久规则（可编辑）',
                exact: true,
            }),
        ).toHaveCount(0)
        await expect(rules.getByRole('form')).toHaveCount(0)
        await expect(
            rules.getByRole('button', { name: '编辑网页规则', exact: true }),
        ).toBeDisabled()
        expect(writes).toBe(2)
        await rules
            .getByRole('button', { name: '重新读取规则', exact: true })
            .click()
        await rules
            .getByRole('button', { name: '编辑网页规则', exact: true })
            .click()
        await expect(
            rules.getByLabel('本机端口例外', { exact: true }),
        ).toHaveValue('1883')
        await rules.getByLabel('本机端口例外', { exact: true }).fill('2883')
        await page.route('**/system/outbound-policy', async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await save(rules)
        await expect(rules.getByText(/版本冲突也不能直接覆盖/)).toBeVisible()
        await expect(
            rules.getByRole('region', {
                name: '网页持久规则（可编辑）',
                exact: true,
            }),
        ).toHaveCount(0)
        await expect(
            rules.getByRole('button', { name: '编辑网页规则', exact: true }),
        ).toBeDisabled()
        await page.unroute('**/system/outbound-policy')
        await rules
            .getByRole('button', { name: '重新读取规则', exact: true })
            .click()
        await expect(
            rules.getByRole('region', {
                name: '网页持久规则（可编辑）',
                exact: true,
            }),
        ).toContainText('2883')
        await expect(
            rules.getByRole('button', { name: '编辑网页规则', exact: true }),
        ).toBeEnabled()
        expect(writes).toBe(3)
        expect(
            (
                await (
                    await page.request.get(
                        root + '/audit?action=system.outboundPolicyChanged',
                    )
                ).json()
            ).items,
        ).toHaveLength(2)
        await page.route('**/system/outbound-policy', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await rules
            .getByRole('button', { name: '重新读取规则', exact: true })
            .click()
        await expect(
            rules.getByText('规则读取失败，不把旧数据或空列表当作当前规则。', {
                exact: true,
            }),
        ).toBeVisible()
        await expect(
            rules.getByRole('region', {
                name: '网页持久规则（可编辑）',
                exact: true,
            }),
        ).toHaveCount(0)
        await expect(
            rules.getByRole('button', { name: '编辑网页规则', exact: true }),
        ).toBeDisabled()
        await page.unroute('**/system/outbound-policy')
        await rules
            .getByRole('button', { name: '重新读取规则', exact: true })
            .click()
        await expect(
            rules.getByRole('button', { name: '编辑网页规则', exact: true }),
        ).toBeEnabled()
        expect(writes).toBe(3)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('invalid deployment stays explicit and system information does not round bytes or invent zero disk space', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-system-invalid-'))
    const host = await startOwnedHost(data, 0, [
        '--TenonEdge:Outbound:Networks:0:Cidr=invalid',
        '--TenonEdge:Outbound:Networks:0:Port=443',
    ])
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        await expect(
            panel(page).getByText(
                /部署规则无效，出站校验会拒绝连接；不是空白名单/,
            ),
        ).toBeVisible()
        expect(
            (await (await page.request.get(policyUrl)).json()).deployment,
        ).toBeNull()
        // Response fixtures only for disk-report boundary display, not physical-disk acceptance.
        let disk: string =
            '{"totalBytes":9007199254740993,"availableBytes":9007199254740992}'
        await page.route('**/system/info', async (route) => {
            const response = await route.fetch()
            const info = await response.json()
            await route.fulfill({
                response,
                body: JSON.stringify({
                    ...info,
                    dataDirectoryDisk: '__DISK__',
                }).replace('"__DISK__"', disk),
            })
        })
        await page.reload()
        await page.getByRole('tab', { name: '基本信息', exact: true }).click()
        const info = page.getByRole('region', { name: '系统信息', exact: true })
        await expect(info).toContainText('9007199254740993')
        await expect(info).toContainText('9007199254740992')
        disk = 'null'
        await info
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        await expect(
            info.getByText('无法读取，不表示空间为零', { exact: true }),
        ).toHaveCount(2)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
