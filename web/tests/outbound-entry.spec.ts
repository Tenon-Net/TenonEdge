import { test, expect, type Page, type Locator } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Outbound-entry-5821!'
const policyUrl = root + '/system/outbound-policy'
function panel(page: Page) {
    return page.getByRole('region', {
        name: '已保存目标的出站授权',
        exact: true,
    })
}
async function setup(page: Page) {
    await page.goto('/connections')
    await page.getByLabel('账号', { exact: true }).fill('outbound-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('button', { name: '新建连接', exact: true }),
    ).toBeEnabled()
}
async function saveConnection(
    page: Page,
    key: string,
    host: string,
    port = '443',
    protocol: 'modbus' | 'mqtt' | 'http' = 'modbus',
) {
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill(key)
    await page.getByLabel('连接名称', { exact: true }).fill(key)
    if (protocol !== 'modbus')
        await page
            .getByRole('combobox', { name: '协议类型', exact: true })
            .selectOption(protocol)
    if (protocol === 'http') {
        await page
            .getByLabel('HTTP 基础地址', { exact: true })
            .fill(`https://${host}:${port}/api/`)
    } else {
        await page.getByLabel('主机或 IP', { exact: true }).fill(host)
        await page.getByLabel('端口', { exact: true }).fill(port)
    }
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(page.getByLabel('连接 key', { exact: true })).toHaveCount(0)
    await expect(panel(page)).toBeVisible()
}
async function grant(value: Locator) {
    await value.getByRole('checkbox').check()
    await value
        .getByRole('button', { name: '确认加入公网白名单', exact: true })
        .click()
}
async function csrf(page: Page) {
    return {
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    }
}

test('saved public targets require administrator confirmation; exact grants preserve existing rules and persist without applying the draft', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-outbound-entry-'))
    let host = await startOwnedHost(data)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        const headers = await csrf(page)
        // Real Kestrel endpoint limit, not TestServer's model validation: complete responses at 4096 and 4097 bytes.
        const boundary = await page.evaluate(async (token) => {
            const body = JSON.stringify({ host: '203.0.113.5', port: 443 })
            const results: number[] = []
            for (const bytes of [4096, 4097]) {
                const response = await fetch(
                    '/api/edge-admin/v1/connections/inspect-outbound',
                    {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'X-CSRF-Token': token,
                        },
                        body: body.padEnd(bytes, ' '),
                    },
                )
                await response.text()
                results.push(response.status)
            }
            return results
        }, headers['X-CSRF-Token'])
        expect(boundary).toEqual([200, 413])
        expect(
            (await (await page.request.get(policyUrl)).json()).version,
        ).toBeNull()
        expect(
            (
                await page.request.put(policyUrl, {
                    headers,
                    data: {
                        networks: [{ cidr: '198.51.100.5/32', port: 80 }],
                        localPorts: [1883],
                        expectedVersion: null,
                    },
                })
            ).status(),
        ).toBe(200)
        let writes = 0
        page.on('request', (request) => {
            if (
                request.method() === 'PUT' &&
                request.url().endsWith('/system/outbound-policy')
            )
                writes++
        })
        await saveConnection(page, 'public-plc', '203.0.113.5')
        const value = panel(page)
        await expect(value.getByText(/此公网 IP 与端口尚未授权/)).toBeVisible()
        await expect(
            value.getByRole('button', {
                name: '确认加入公网白名单',
                exact: true,
            }),
        ).toBeDisabled()
        expect(writes).toBe(0)
        const draft = await (
            await page.request.get(root + '/connections')
        ).json()
        await grant(value)
        await expect(
            value.getByText(/公网主机授权已保存并记审计/),
        ).toBeVisible()
        await expect(value.getByText(/此 IP 与端口当前允许/)).toBeVisible()
        expect(writes).toBe(1)
        const saved = await (await page.request.get(policyUrl)).json()
        expect(saved.networks).toEqual([
            { cidr: '198.51.100.5/32', port: 80 },
            { cidr: '203.0.113.5/32', port: 443 },
        ])
        expect(saved.localPorts).toEqual([1883])
        expect(
            await (await page.request.get(root + '/connections')).json(),
        ).toEqual(draft)
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .coordinator.activeRevision,
        ).toBeNull()
        await saveConnection(
            page,
            'management-denied',
            '127.0.0.1',
            new URL(host.url).port,
        )
        await expect(
            panel(page).getByText(/危险地址或本机管理端口/),
        ).toBeVisible()
        await expect(
            panel(page).getByRole('button', {
                name: '确认加入公网白名单',
                exact: true,
            }),
        ).toHaveCount(0)
        expect(writes).toBe(1)
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/connections')
        await page.getByLabel('账号', { exact: true }).fill('outbound-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await expect(
            page.getByRole('heading', { name: '连接', exact: true }),
        ).toBeVisible()
        expect(await (await page.request.get(policyUrl)).json()).toEqual(saved)
        const audits = await (
            await page.request.get(
                root + '/audit?action=system.outboundPolicyChanged',
            )
        ).json()
        expect(audits.items).toHaveLength(2)
        for (const role of ['configurator', 'viewer']) {
            expect(
                (
                    await page.request.post(root + '/users', {
                        headers: await csrf(page),
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
                await user.goto('/connections')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user.getByLabel('密码', { exact: true }).fill(password)
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user.getByLabel('原密码', { exact: true }).fill(password)
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Changed-outbound-8826!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    user.getByRole('heading', {
                        name: '连接',
                        exact: true,
                    }),
                ).toBeVisible()
                if (role === 'configurator') {
                    await saveConnection(
                        user,
                        'public-http',
                        '203.0.113.22',
                        '443',
                        'http',
                    )
                    await expect(
                        panel(user).getByText(
                            '目标不在允许范围内，请联系管理员。',
                            { exact: true },
                        ),
                    ).toBeVisible()
                    await expect(
                        panel(user).getByRole('button', {
                            name: '确认加入公网白名单',
                            exact: true,
                        }),
                    ).toHaveCount(0)
                    expect(
                        (
                            await user.request.put(policyUrl, {
                                headers: await csrf(user),
                                data: {
                                    networks: [],
                                    localPorts: [],
                                    expectedVersion: saved.version,
                                },
                            })
                        ).status(),
                    ).toBe(403)
                } else {
                    await expect(
                        user.getByRole('button', {
                            name: '新建连接',
                            exact: true,
                        }),
                    ).toHaveCount(0)
                    expect(
                        (
                            await user.request.post(
                                root + '/connections/inspect-outbound',
                                {
                                    headers: await csrf(user),
                                    data: { host: '203.0.113.22', port: 443 },
                                },
                            )
                        ).status(),
                    ).toBe(403)
                }
            } finally {
                await other.close()
            }
        }
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('stale and lost grant responses require fresh inspection without replay and read failures never offer stale advice', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-outbound-errors-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        await saveConnection(
            page,
            'public-mqtt-target',
            '203.0.113.11',
            '8883',
            'mqtt',
        )
        const value = panel(page)
        await expect(value.getByText(/此公网 IP 与端口尚未授权/)).toBeVisible()
        let writes = 0
        page.on('request', (request) => {
            if (
                request.method() === 'PUT' &&
                request.url().endsWith('/system/outbound-policy')
            )
                writes++
        })
        expect(
            (
                await page.request.put(policyUrl, {
                    headers: await csrf(page),
                    data: {
                        networks: [{ cidr: '198.51.100.6/32', port: 443 }],
                        localPorts: [],
                        expectedVersion: null,
                    },
                })
            ).status(),
        ).toBe(200)
        await grant(value)
        await expect(value.getByText(/授权请求失败或结果未确认/)).toBeVisible()
        await expect(
            value.getByRole('button', {
                name: '确认加入公网白名单',
                exact: true,
            }),
        ).toBeDisabled()
        expect(writes).toBe(1)
        await value
            .getByRole('button', { name: '重新解析并检查规则', exact: true })
            .click()
        await expect(value.getByRole('checkbox')).toBeEnabled()
        await page.route('**/system/outbound-policy', async (route) => {
            if (route.request().method() !== 'PUT') {
                await route.continue()
                return
            }
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await grant(value)
        await expect(value.getByText(/授权请求失败或结果未确认/)).toBeVisible()
        await page.unroute('**/system/outbound-policy')
        await value
            .getByRole('button', { name: '重新解析并检查规则', exact: true })
            .click()
        await expect(value.getByText(/此 IP 与端口当前允许/)).toBeVisible()
        expect(writes).toBe(2)
        const current = await (await page.request.get(policyUrl)).json()
        expect(current.networks).toEqual([
            { cidr: '198.51.100.6/32', port: 443 },
            { cidr: '203.0.113.11/32', port: 8883 },
        ])
        expect(
            (
                await (
                    await page.request.get(
                        root + '/audit?action=system.outboundPolicyChanged',
                    )
                ).json()
            ).items,
        ).toHaveLength(2)
        // Explicit response fixture for UI recovery; real DNS/invalid-policy/timeout are covered by integration tests.
        await page.route('**/connections/inspect-outbound', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503,"detail":"inspection-read-fixture"}',
            }),
        )
        await value
            .getByRole('button', { name: '重新解析并检查规则', exact: true })
            .click()
        await expect(
            value.getByText('请求失败，请检查网关连接后重试。', {
                exact: true,
            }),
        ).toBeVisible()
        await expect(value.getByText(/inspection-read-fixture/)).toHaveCount(0) // Generic errors deliberately do not echo arbitrary server detail.
        await expect(value.getByRole('list')).toHaveCount(0)
        await expect(
            value.getByRole('button', {
                name: '确认加入公网白名单',
                exact: true,
            }),
        ).toHaveCount(0)
        await page.unroute('**/connections/inspect-outbound')
        await value
            .getByRole('button', { name: '重新解析并检查规则', exact: true })
            .click()
        await expect(value.getByText(/此 IP 与端口当前允许/)).toBeVisible()
        expect(writes).toBe(2)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
