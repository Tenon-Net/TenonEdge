import { expect, test, type Browser, type Page } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Connection-list-browser-8361!'
const httpAddress = new URL(
    process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
).hostname
async function fixture(browser: Browser, legacy = false) {
    let status = 200
    let requests = 0
    const server = createServer((request, response) => {
        request.resume()
        request.on('end', () => {
            requests++
            response
                .writeHead(status, { 'Content-Type': 'application/json' })
                .end(status === 200 ? '{"value":42}' : 'private-peer-body')
        })
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('missing-port')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-connection-list-'))
    const target = `http://127.0.0.1:${address.port}/`
    const args = [`--TenonEdge:Outbound:LocalPorts:0=${address.port}`]
    if (legacy) {
        const seed = join(data, 'seed.json')
        await writeFile(
            seed,
            JSON.stringify({
                schemaVersion: 1,
                units: [
                    {
                        kind: 'input.http',
                        key: 'legacy/read',
                        configuration: {
                            connectionId: 77,
                            baseAddress: target,
                            periodMs: 1000,
                            fields: [
                                {
                                    key: 'value',
                                    path: '/value',
                                    type: 'integer',
                                },
                            ],
                        },
                    },
                ],
            }),
        )
        args.push(`--TenonEdge:Runtime:SeedSnapshot=${seed}`)
    }
    const host = await startOwnedHost(data, 0, args, httpAddress)
    const context = await browser.newContext({
        baseURL: host.url,
        viewport: { width: 1700, height: 1100 },
    })
    const page = await context.newPage()
    page.setDefaultTimeout(15_000)
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill('connection-admin')
    for (const name of ['密码', '确认密码'])
        await page.getByLabel(name, { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    async function write(
        method: 'post' | 'put',
        path: string,
        body: unknown,
        expected = 200,
    ) {
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const response = await page.request[method](root + path, {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: body,
        })
        expect(response.status(), await response.text()).toBe(expected)
        return response.json()
    }
    return {
        page,
        data,
        target,
        write,
        requests: () => requests,
        fail: (value: boolean) => {
            status = value ? 503 : 200
        },
        close: async () => {
            await context.close()
            await host.stop()
            server.closeAllConnections()
            await new Promise<void>((resolve, reject) =>
                server.close((error) => (error ? reject(error) : resolve())),
            )
            await rm(data, { recursive: true, force: true })
        },
    }
}
async function refresh(page: Page) {
    await page
        .getByRole('button', { name: '刷新连接列表', exact: true })
        .click()
}

// UI compatibility guard only: this response fixture is not evidence of a
// module configuration form or protocol operation through the browser.
test('unknown module remains inspectable but never opens the built-in protocol editor', async ({
    browser,
}) => {
    const f = await fixture(browser)
    try {
        await f.page.route(
            '**/api/edge-admin/v1/connections',
            async (route) => {
                const response = await route.fetch()
                const json = await response.json()
                await route.fulfill({
                    response,
                    json: {
                        ...json,
                        connections: [
                            {
                                id: 100,
                                key: 'wire',
                                name: 'Wire fixture',
                                kind: 'module',
                                enabled: true,
                                modbus: null,
                                mqtt: null,
                                http: null,
                                module: {
                                    kind: 'testwire',
                                    version: 2,
                                    parameters: {
                                        host: 'not-displayed.invalid',
                                    },
                                },
                            },
                        ],
                    },
                })
            },
        )
        await f.page.goto('/connections')
        const row = f.page
            .getByRole('table', { name: '连接列表', exact: true })
            .getByRole('row')
            .filter({ hasText: 'Wire fixture' })
        await expect(row).toContainText('扩展连接 · testwire v2')
        await expect(
            row.getByRole('button', { name: '编辑连接', exact: true }),
        ).toBeEnabled()
        await row.getByRole('button', { name: '编辑连接', exact: true }).click()
        const editor = f.page.getByRole('form', {
            name: '扩展连接参数',
            exact: true,
        })
        await expect(editor).toContainText('请选择已安装的准确声明版本')
        await expect(
            editor.getByRole('button', { name: '保存到草稿', exact: true }),
        ).toBeDisabled()
        await expect(row).not.toContainText('not-displayed.invalid')
        await expect(
            f.page.getByLabel('Broker 主机', { exact: true }),
        ).not.toBeVisible()
    } finally {
        await f.close()
    }
})

// Configuration is an explicit API fixture. Status, references, editing, query
// failures and role checks use the real product page and original HTTP loop.
test('connection list separates effective and draft data, observes real HTTP failures, preserves editing and protects viewers', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const f = await fixture(browser)
    const { page, write, target } = f
    try {
        const connection = await write('post', '/connections', {
            expectedVersion: '0',
            key: 'shared-http',
            name: 'Applied HTTP',
            kind: 'http',
            http: { baseAddress: target },
        })
        const id = connection.connection.id
        const device = await write('post', '/devices', {
            expectedVersion: connection.version,
            key: 'meter',
            name: 'Meter',
            kind: 'http',
            connectionId: id,
        })
        const source = await write(
            'post',
            `/devices/${device.device.id}/inputs`,
            {
                expectedVersion: device.version,
                key: 'read',
                name: 'Read',
                http: {
                    path: '/read',
                    periodMs: 1000,
                    fields: [{ key: 'value', path: '/value', type: 'integer' }],
                },
            },
        )
        const disabled = await write(
            'post',
            `/devices/${device.device.id}/inputs`,
            {
                expectedVersion: source.version,
                key: 'disabled',
                name: 'Disabled read',
                enabled: false,
                http: { path: '/disabled', fields: [] },
            },
        )
        const route = await write('post', '/routes', {
            expectedVersion: disabled.version,
            key: 'route',
            name: 'Route',
            enabled: true,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'in',
                        type: 'input.http',
                        version: 1,
                        name: 'Input',
                        config: { sourceIds: [source.input.id] },
                    },
                    {
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: { connectionId: id, path: '/send' },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: { nodes: {} },
            },
        })
        const unused = await write('post', '/connections', {
            expectedVersion: route.version,
            key: 'idle',
            name: 'Unused MQTT',
            kind: 'mqtt',
            mqtt: { host: '127.0.0.1' },
        })
        const off = await write('post', '/connections', {
            expectedVersion: unused.version,
            key: 'off',
            name: 'Disabled Modbus',
            kind: 'modbus',
            enabled: false,
            modbus: { host: '127.0.0.1' },
        })
        await write('put', `/connections/${id}/delivery-control`, {
            paused: true,
            confirmed: true,
        })
        await page.goto('/connections')
        const table = page.getByRole('table', { name: '连接列表', exact: true })
        const row = table.getByRole('row').filter({ hasText: 'shared-http' })
        await expect(row.getByText('未应用', { exact: true })).toBeVisible()
        const review = await write('post', '/configuration/review', {
            expectedVersion: off.version,
        })
        const operation = await write(
            'post',
            '/configuration/apply',
            {
                expectedVersion: off.version,
                expectedEffectiveRevision: review.effectiveRevision,
                expectedPlanHash: review.planHash,
                confirmed: true,
            },
            202,
        )
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                root +
                                    '/configuration/operations/' +
                                    operation.id,
                            )
                        ).json()
                    ).stage,
            )
            .toBe('completed')
        await expect.poll(f.requests).toBeGreaterThan(0)
        await refresh(page)
        await expect(row.getByText('已连接', { exact: true })).toBeVisible()
        await expect(
            table
                .getByRole('row')
                .filter({ hasText: 'Unused MQTT' })
                .getByText('未使用', { exact: true }),
        ).toBeVisible()
        await expect(
            table
                .getByRole('row')
                .filter({ hasText: 'Disabled Modbus' })
                .getByText('已禁用', { exact: true }),
        ).toBeVisible()
        await row.getByText('有效引用（3）', { exact: true }).click()
        await expect(
            row
                .locator('details[open]')
                .getByText('Meter / Read', { exact: false }),
        ).toBeVisible()
        await expect(row).toContainText('（配置停用）')
        await expect(row).toContainText('route/out')
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                root + '/runtime/connections',
                            )
                        ).json()
                    ).connections.find((item: { id: number }) => item.id === id)
                        .pendingCount,
            )
            .toBeGreaterThan(0)
        // Communication failure does not hide already queued records or expose the peer body.
        f.fail(true)
        await expect(row.getByText('降级', { exact: true })).toBeVisible({
            timeout: 20_000,
        })
        await expect(row).toContainText('http.status')
        await expect(row).not.toContainText('private-peer-body')
        f.fail(false)
        await expect(row.getByText('已连接', { exact: true })).toBeVisible({
            timeout: 20_000,
        })
        await expect(row).toContainText('http.status') // last error survives recovery
        const deletion = await (
            await page.request.get(
                root +
                    `/connections/${id}/deletion-review?expectedVersion=${off.version}`,
            )
        ).json()
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        const deletionPanel = page.getByRole('region', {
            name: '删除前核对',
            exact: true,
        })
        await expect(deletionPanel).toBeVisible()
        await expect(deletionPanel).toContainText('Meter')
        await expect(deletionPanel).toContainText('来源 read')
        await expect(deletionPanel).toContainText('来源 disabled')
        await expect(deletionPanel).toContainText('output.http/route/')
        await expect(deletionPanel).toContainText('保留运行版本')
        await expect(deletionPanel).toContainText(
            `待发 ${deletion.pendingCount}`,
        )
        await expect(deletionPanel).toContainText(
            `失败 ${deletion.failedCount}`,
        )
        await expect(deletionPanel).toContainText(
            `隔离 ${deletion.quarantinedCount}`,
        )
        await expect(deletionPanel).toContainText(
            '仍有引用或积存记录，不能删除。',
        )
        await expect(
            deletionPanel.getByRole('button', {
                name: '确认删除',
                exact: true,
            }),
        ).toBeDisabled()
        await deletionPanel
            .getByRole('button', { name: '取消', exact: true })
            .click()
        const changed = await write('put', `/connections/${id}`, {
            expectedVersion: off.version,
            key: 'shared-http',
            name: 'Draft HTTP',
            kind: 'http',
            http: { baseAddress: target + 'unapplied/', timeoutMs: 7000 },
        })
        await refresh(page)
        await expect(row).toContainText('Applied HTTP')
        await expect(row).toContainText('Draft HTTP')
        await expect(row).toContainText('有未应用的连接变更')
        await expect(row.getByText('已连接', { exact: true })).toBeVisible()
        await row.getByRole('button', { name: '编辑连接', exact: true }).click()
        await page
            .getByLabel('连接名称', { exact: true })
            .fill('Unsaved local edit')
        for (const status of [500, 409]) {
            await page.route('**/runtime/connections?*', (route) =>
                route.fulfill({
                    status,
                    contentType: 'application/problem+json',
                    body: JSON.stringify({
                        status,
                        title: 'Injected query failure',
                    }),
                }),
            )
            const rejected = page.waitForResponse(
                (response) =>
                    response.url().includes('/runtime/connections?') &&
                    response.status() === status,
            )
            await refresh(page)
            await rejected
            await expect(row.getByText('已连接', { exact: true })).toHaveCount(
                0,
            )
            await expect(row).toContainText('当前未确认')
            await expect(
                page.getByLabel('连接名称', { exact: true }),
            ).toHaveValue('Unsaved local edit')
            await page.unroute('**/runtime/connections?*')
        }
        await refresh(page)
        await expect(row.getByText('已连接', { exact: true })).toBeVisible()
        await expect(page.getByLabel('连接名称', { exact: true })).toHaveValue(
            'Unsaved local edit',
        )
        // A real queue read failure is unknown, never a manufactured zero.
        const db = new DatabaseSync(join(f.data, 'queue.db'))
        try {
            db.exec(
                'ALTER TABLE delivery RENAME TO connection_list_unavailable',
            )
            await refresh(page)
            await expect(
                page.getByText('队列暂不可读，待发数量显示未知，不按零处理。', {
                    exact: true,
                }),
            ).toBeVisible()
            await expect(row.getByText('未知', { exact: true })).toBeVisible()
        } finally {
            db.exec(
                'ALTER TABLE connection_list_unavailable RENAME TO delivery',
            )
            db.close()
        }
        page.on('dialog', (dialog) => dialog.accept())
        await page
            .getByRole('button', { name: '关闭编辑', exact: true })
            .click()
        await page.getByLabel('协议筛选', { exact: true }).selectOption('mqtt')
        await expect(table.getByRole('row')).toHaveCount(2)
        await page.getByLabel('协议筛选', { exact: true }).selectOption('')
        await page
            .getByLabel('按名称或 key 搜索', { exact: true })
            .fill('Applied HTTP')
        await expect(table.getByRole('row')).toHaveCount(2)
        await write('post', '/users', {
            accountName: 'connection-viewer',
            displayName: 'Viewer',
            role: 'viewer',
            temporaryPassword: password,
            isEnabled: true,
        })
        await page
            .getByRole('button', { name: '退出登录', exact: true })
            .click()
        await expect(
            page.getByRole('button', { name: '登录', exact: true }),
        ).toBeVisible()
        await page.getByLabel('账号', { exact: true }).fill('connection-viewer')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await page.getByLabel('原密码', { exact: true }).fill(password)
        await page
            .getByLabel('新密码', { exact: true })
            .fill('Own-viewer-browser-8361!')
        await page
            .getByRole('button', { name: '保存新密码', exact: true })
            .click()
        await expect(page.getByRole('navigation')).toBeVisible()
        await page.goto('/connections')
        await expect(row.getByText('已连接', { exact: true })).toBeVisible()
        await expect(
            page.getByRole('button', { name: '新建连接', exact: true }),
        ).toHaveCount(0)
        await expect(
            page.getByRole('button', { name: '删除连接', exact: true }),
        ).toHaveCount(0)
        await row.getByRole('button', { name: '查看连接', exact: true }).click()
        await expect(
            page.getByRole('button', { name: '测试 HTTP 连接', exact: true }),
        ).toHaveCount(0)
        expect(
            (await (await page.request.get(root + '/connections')).json())
                .version,
        ).toBe(changed.version)
    } finally {
        await f.close()
    }
})

test('legacy effective-only connection is visible with real communication but never converted into a fake editable draft', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const f = await fixture(browser, true)
    try {
        const { page } = f
        await page.goto('/connections')
        const row = page
            .getByRole('table', { name: '连接列表', exact: true })
            .getByRole('row')
            .filter({ hasText: '#77' })
        await expect(row.getByText('已连接', { exact: true })).toBeVisible()
        await expect(row).toContainText(f.target)
        await expect(row).toContainText('仅存在于有效配置；无可编辑草稿')
        await expect(row).toContainText(
            '旧种子未保留完整草稿，无法比较全部参数',
        )
        await expect(
            row.getByRole('button', { name: '编辑连接', exact: true }),
        ).toBeDisabled()
        await expect(
            row.getByRole('button', { name: '删除连接', exact: true }),
        ).toBeDisabled()
        expect(
            (await (await page.request.get(root + '/connections')).json())
                .connections,
        ).toEqual([])
        await row.getByText('有效引用（1）', { exact: true }).click()
        await expect(row).toContainText('legacy/read')
        const polled: string[] = []
        page.on('request', (request) => {
            if (request.url().includes('/runtime/connections'))
                polled.push(request.url())
        })
        await page.goto('/system')
        await expect(
            page.getByRole('heading', { name: '系统信息与设置', exact: true }),
        ).toBeVisible()
        const count = polled.length
        await page.waitForTimeout(6000)
        expect(polled).toHaveLength(count)
    } finally {
        await f.close()
    }
})
