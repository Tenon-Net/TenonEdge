import { test, expect, type Page } from '@playwright/test'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const endpoint = root + '/configuration/export'
const password = 'Export-browser-5918!'
const owned = test.extend<{ exportHost: string }>({
    baseURL: async ({ exportHost }, use) => {
        await use(exportHost)
    },
    exportHost: async ({ browserName }, use) => {
        const data = await mkdtemp(
            join(tmpdir(), `tenonedge-export-${browserName}-`),
        )
        try {
            const host = await startOwnedHost(data)
            try {
                await use(host.url)
            } finally {
                await host.stop()
            }
        } finally {
            await rm(data, { recursive: true, force: true })
        }
    },
})
async function setup(page: Page, url: string) {
    await page.goto(url + '/system')
    await page.getByLabel('账号', { exact: true }).fill('export-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '系统信息与设置', exact: true }),
    ).toBeVisible()
}
function panel(page: Page) {
    return page.getByRole('region', { name: '配置草稿导出', exact: true })
}
function button(page: Page) {
    return panel(page).getByRole('button', {
        name: '下载已保存草稿 JSON',
        exact: true,
    })
}
async function download(page: Page) {
    const ready = page.waitForEvent('download')
    await button(page).click()
    const file = await ready
    expect(file.suggestedFilename()).toBe('tenonedge-configuration.json')
    const path = await file.path()
    if (!path) throw new Error('download-path-missing')
    return readFile(path, 'utf8')
}
async function csrf(page: Page) {
    return (await (await page.request.get(root + '/auth/session')).json())
        .csrfToken
}
// These API-created drafts are explicit fixtures for file/role verification, not
// evidence for T3-11's no-JSON configuration workflow.
async function fixture(page: Page) {
    const headers = { 'X-CSRF-Token': await csrf(page) }
    let version = (await (await page.request.get(root + '/connections')).json())
        .version
    async function create(path: string, data: object) {
        const response = await page.request.post(root + path, {
            headers,
            data: { ...data, expectedVersion: version },
        })
        expect(response.status(), await response.text()).toBe(200)
        const result = await response.json()
        version = result.version
        return result
    }
    const connection = (
        await create('/connections', {
            key: 'receiver',
            name: 'Receiver',
            kind: 'http',
            http: { baseAddress: 'https://example.com/base/' },
        })
    ).connection
    const device = (
        await create('/devices', {
            key: 'meter',
            name: 'Meter',
            kind: 'simulator',
        })
    ).device
    const input = (
        await create(`/devices/${device.id}/inputs`, {
            key: 'values',
            name: 'Values',
            enabled: false,
            simulator: { sampleJson: '{"value":9223372036854775807}' },
        })
    ).input
    const routeBody = {
        key: 'route',
        name: 'Route',
        enabled: false,
        graph: {
            schemaVersion: 1,
            nodes: [
                {
                    id: 'source',
                    type: 'input.simulator',
                    version: 1,
                    name: 'Source',
                    config: { sourceIds: [input.id] },
                },
                {
                    id: 'target',
                    type: 'output.http',
                    version: 1,
                    name: 'Target',
                    config: {
                        connectionId: connection.id,
                        path: '/data',
                        payload: {
                            mode: 'custom',
                            fields: [{ target: 'id', constant: '__INTEGER__' }],
                        },
                    },
                },
            ],
            edges: [{ source: 'source', target: 'target' }],
            layout: { nodes: { source: { x: 1, y: 2 } } },
        },
        expectedVersion: version,
    }
    const response = await page.request.post(root + '/routes', {
        headers: { ...headers, 'Content-Type': 'application/json' },
        data: JSON.stringify(routeBody).replace(
            '"__INTEGER__"',
            '9223372036854775807',
        ),
    })
    expect(response.status(), await response.text()).toBe(200)
    const route = await response.json()
    version = route.version
    const credential = await page.request.put(
        root + `/connections/${connection.id}/http-credential`,
        {
            headers,
            data: {
                recipient: 'https://example.com/base/',
                kind: 'bearer',
                secret: 'not-a-real-export-secret',
                expectedDraftVersion: version,
            },
        },
    )
    expect(credential.status(), await credential.text()).toBe(200)
    return {
        connection,
        device,
        input,
        route: route.route,
        credential: await credential.json(),
        version,
    }
}

owned(
    'real draft downloads use business keys and exact JSON bytes without secrets or changing the draft/runtime',
    async ({ page, exportHost }) => {
        await setup(page, exportHost)
        const saved = await fixture(page)
        const state = async () => ({
            draft: await (
                await page.request.get(root + '/configuration/draft')
            ).json(),
            runtime: await (
                await page.request.get(root + '/configuration/runtime')
            ).json(),
            versions: await (
                await page.request.get(root + '/configuration/versions')
            ).json(),
        })
        const before = await state()
        const text = await download(page)
        const value = JSON.parse(text)
        expect(Object.keys(value)).toEqual([
            'formatVersion',
            'connections',
            'devices',
            'inputs',
            'routes',
        ])
        expect(value.connections[0].connectionKey).toBe('receiver')
        expect(value.connections[0].credential.status).toBe('requiresInput')
        expect(value.connections[0].credential.accountIdentity).toMatch(
            /^sha256:[a-f0-9]{64}$/,
        )
        expect(value.inputs[0].deviceKey).toBe('meter')
        expect(value.inputs[0].inputKey).toBe('values')
        expect(value.inputs[0].enabled).toBe(false)
        expect(value.inputs[0].simulator.sampleJson).toBe(
            '{"value":9223372036854775807}',
        )
        expect(value.routes[0].graph.nodes[0].config.sourceKeys).toEqual([
            'meter/values',
        ])
        expect(value.routes[0].graph.nodes[1].config.connectionKey).toBe(
            'receiver',
        )
        expect(value.routes[0].graph.layout.nodes.source).toEqual({
            x: 1,
            y: 2,
        })
        expect(text).toContain('"constant":9223372036854775807')
        for (const omitted of [
            saved.connection.id,
            saved.device.id,
            saved.input.id,
            saved.route.id,
            saved.credential.version,
            saved.version,
            'not-a-real-export-secret',
            'sourceIds',
            'connectionId',
        ])
            expect(text).not.toContain(String(omitted))
        expect(await state()).toEqual(before)
        expect(
            await (
                await page.request.get(
                    root +
                        `/connections/${saved.connection.id}/http-credential`,
                )
            ).json(),
        ).toEqual(saved.credential)
        await expect(panel(page).getByRole('status')).toHaveText(
            '文件已交给浏览器下载；不会自动重试。',
        )
    },
)

owned(
    'viewer cannot export while configurator can download through the real session and API',
    async ({ browser, page, exportHost }) => {
        await setup(page, exportHost)
        const headers = { 'X-CSRF-Token': await csrf(page) }
        for (const role of ['viewer', 'configurator']) {
            const created = await page.request.post(root + '/users', {
                headers,
                data: {
                    accountName: role,
                    role,
                    temporaryPassword: password,
                    isEnabled: true,
                },
            })
            expect(created.status()).toBe(200)
            const context = await browser.newContext({ baseURL: exportHost })
            try {
                const user = await context.newPage()
                await user.goto('/system')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user.getByLabel('密码', { exact: true }).fill(password)
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user.getByLabel('原密码', { exact: true }).fill(password)
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Export-changed-7291!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(panel(user)).toBeVisible()
                expect((await user.request.get(endpoint)).status()).toBe(
                    role === 'viewer' ? 403 : 200,
                )
                if (role === 'viewer') {
                    await expect(button(user)).toHaveCount(0)
                    await expect(panel(user)).toContainText(
                        '仅配置人员和管理员可以导出配置。',
                    )
                } else
                    expect(
                        JSON.parse(await download(user)).connections,
                    ).toEqual([])
            } finally {
                await context.close()
            }
        }
    },
)

owned(
    'invalid graph references reject the complete file with an explicit message; lost responses never replay',
    async ({ page, exportHost }) => {
        await setup(page, exportHost)
        const headers = { 'X-CSRF-Token': await csrf(page) }
        const created = await page.request.post(root + '/routes', {
            headers,
            data: {
                expectedVersion: '0',
                key: 'broken',
                name: 'Broken',
                enabled: false,
                graph: {
                    schemaVersion: 1,
                    nodes: [
                        {
                            id: 'source',
                            type: 'input.simulator',
                            version: 1,
                            name: 'Source',
                            config: { sourceIds: [999] },
                        },
                    ],
                    edges: [],
                    layout: { nodes: {} },
                },
            },
        })
        expect(created.status(), await created.text()).toBe(200)
        let requests = 0
        let downloads = 0
        page.on('request', (request) => {
            if (request.url().endsWith('/configuration/export')) requests++
        })
        page.on('download', () => downloads++)
        await button(page).click()
        await expect(panel(page).getByRole('alert')).toContainText(
            '草稿含不存在的设备、来源或输出连接引用',
        )
        expect(requests).toBe(1)
        expect(downloads).toBe(0)
        await page.route('**/configuration/export', (route) =>
            route.abort('failed'),
        )
        await button(page).click()
        await expect(panel(page).getByRole('alert')).not.toContainText(
            '草稿含不存在的设备',
        )
        await expect(button(page)).toBeEnabled()
        expect(requests).toBe(2)
        expect(downloads).toBe(0)
        await page.unroute('**/configuration/export')
        await button(page).click()
        await expect(panel(page).getByRole('alert')).toContainText(
            '草稿含不存在的设备',
        )
        expect(requests).toBe(3)
        expect(downloads).toBe(0)
    },
)

for (const leave of ['navigate', 'logout']) {
    owned(
        `late real export bytes do not download after ${leave}`,
        async ({ page, exportHost }) => {
            await setup(page, exportHost)
            let release!: () => void
            let captured!: () => void
            const wait = new Promise<void>((resolve) => {
                release = resolve
            })
            const received = new Promise<void>((resolve) => {
                captured = resolve
            })
            let downloads = 0
            let requests = 0
            page.on('download', () => downloads++)
            await page.route('**/configuration/export', async (route) => {
                requests++
                const response = await route.fetch()
                expect(response.status()).toBe(200)
                captured()
                await wait
                // The browser aborted the request on unmount; late transport data must not download.
                await route.fulfill({ response })
            })
            try {
                await button(page).click()
                await received
                await expect(button(page)).toHaveCount(0) // pending label replaces download text
                if (leave === 'navigate') {
                    await page
                        .getByRole('link', { name: '总览', exact: true })
                        .click()
                    await expect(
                        page.getByRole('heading', {
                            name: '总览',
                            exact: true,
                        }),
                    ).toBeVisible()
                } else {
                    await page
                        .getByRole('button', { name: '退出登录', exact: true })
                        .click()
                    await expect(
                        page.getByRole('button', { name: '登录', exact: true }),
                    ).toBeVisible()
                }
            } finally {
                release()
            }
            await page.unrouteAll({ behavior: 'wait' })
            // Cross a render frame after the held response was delivered/cancelled.
            await page.evaluate(
                () =>
                    new Promise<void>((resolve) =>
                        requestAnimationFrame(() =>
                            requestAnimationFrame(() => resolve()),
                        ),
                    ),
            )
            expect(requests).toBe(1)
            expect(downloads).toBe(0)
        },
    )
}
