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

test('connection deletion confirms once, removes the draft and never sends a second DELETE', async ({
    browser,
}) => {
    const f = await fixture(browser)
    try {
        const { page } = f
        const saved = await f.write('post', '/connections', {
            expectedVersion: '0',
            key: 'unused',
            name: 'Unused receiver',
            kind: 'http',
            http: { baseAddress: f.target },
        })
        const deleted: string[] = []
        page.on('request', (request) => {
            if (request.method() === 'DELETE')
                deleted.push(new URL(request.url()).pathname)
        })
        await page.goto('/connections')
        const row = page.getByRole('row').filter({ hasText: 'Unused receiver' })
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        const review = page.getByRole('region', {
            name: '删除前核对',
            exact: true,
        })
        const confirm = review.getByRole('button', {
            name: '确认删除',
            exact: true,
        })
        await expect(confirm).toBeEnabled()
        expect(deleted).toHaveLength(0)
        page.once('dialog', (dialog) => dialog.dismiss())
        await confirm.click()
        await expect(confirm).toBeEnabled()
        expect(deleted).toHaveLength(0)
        const response = page.waitForResponse(
            (value) => value.request().method() === 'DELETE',
        )
        page.once('dialog', (dialog) => dialog.accept())
        await confirm.click()
        const result = await response
        expect(result.status()).toBe(200)
        const removed = await result.json()
        await expect(row).toHaveCount(0)
        await expect(page.getByRole('status')).toHaveText(
            '已删除草稿连接，尚未应用。',
        )
        expect(deleted).toEqual([`${root}/connections/${saved.connection.id}`])
        const current = await (
            await page.request.get(root + '/connections')
        ).json()
        expect(current.version).toBe(removed.version)
        expect(current.version).not.toBe(saved.version)
        expect(current.connections).toEqual([])
        await refresh(page)
        await expect(
            page.getByText('没有匹配的连接。', { exact: true }),
        ).toBeVisible()
        expect(deleted).toHaveLength(1)
    } finally {
        await f.close()
    }
})

test('connection deletion rejects failed review and a reference added after confirmation preparation', async ({
    browser,
}) => {
    const f = await fixture(browser)
    try {
        const { page } = f
        const saved = await f.write('post', '/connections', {
            expectedVersion: '0',
            key: 'referenced',
            name: 'Protected receiver',
            kind: 'http',
            http: { baseAddress: f.target },
        })
        let deletes = 0
        page.on('request', (request) => {
            if (request.method() === 'DELETE') deletes++
        })
        const pattern = `**/connections/${saved.connection.id}/deletion-review?*`
        await page.route(pattern, (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{}',
            }),
        )
        await page.goto('/connections')
        const row = page
            .getByRole('row')
            .filter({ hasText: 'Protected receiver' })
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        const review = page.getByRole('region', {
            name: '删除前核对',
            exact: true,
        })
        const confirm = review.getByRole('button', {
            name: '确认删除',
            exact: true,
        })
        await expect(review.getByRole('alert')).toBeVisible()
        await expect(confirm).toHaveCount(0)
        expect(deletes).toBe(0)
        await page.unroute(pattern)
        await review
            .getByRole('button', { name: '重新核对', exact: true })
            .click()
        await expect(confirm).toBeEnabled()
        // Another client adds a real reference after the successful review.
        const device = await f.write('post', '/devices', {
            expectedVersion: saved.version,
            key: 'meter',
            name: 'Referenced meter',
            kind: 'http',
            connectionId: saved.connection.id,
        })
        const response = page.waitForResponse(
            (value) => value.request().method() === 'DELETE',
        )
        page.once('dialog', (dialog) => dialog.accept())
        await confirm.click()
        expect((await response).status()).toBe(409)
        await expect(
            review.getByText('操作结果未知。请重新读取，不要重放删除。', {
                exact: true,
            }),
        ).toBeVisible()
        await expect(confirm).toHaveCount(0)
        const current = await (
            await page.request.get(root + '/connections')
        ).json()
        expect(current.version).toBe(device.version)
        expect(
            current.connections.map((item: { id: number }) => item.id),
        ).toEqual([saved.connection.id])
        await review.getByRole('button', { name: '取消', exact: true }).click()
        await refresh(page)
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        await expect(review).toContainText('Referenced meter')
        await expect(review).toContainText('仍有引用或积存记录，不能删除。')
        await expect(confirm).toBeDisabled()
        expect(deletes).toBe(1)
    } finally {
        await f.close()
    }
})

test('connection deletion with committed but lost reply stays consumed until explicit reread, without replay', async ({
    browser,
}) => {
    const f = await fixture(browser)
    let release!: () => void
    const held = new Promise<void>((resolve) => {
        release = resolve
    })
    try {
        const { page } = f
        const saved = await f.write('post', '/connections', {
            expectedVersion: '0',
            key: 'lost-reply',
            name: 'Lost reply receiver',
            kind: 'http',
            http: { baseAddress: f.target },
        })
        let deletes = 0
        let signalCommitted!: () => void
        let committedVersion: string | undefined
        const committed = new Promise<void>((resolve) => {
            signalCommitted = resolve
        })
        page.on('request', (request) => {
            if (request.method() === 'DELETE') deletes++
        })
        await page.route(
            `**/connections/${saved.connection.id}?*`,
            async (route) => {
                if (route.request().method() !== 'DELETE')
                    return route.fallback()
                const response = await route.fetch()
                expect(response.status()).toBe(200)
                committedVersion = (await response.json()).version
                signalCommitted()
                await held
                await route.abort('failed')
            },
        )
        await page.goto('/connections')
        const row = page
            .getByRole('row')
            .filter({ hasText: 'Lost reply receiver' })
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        const review = page.getByRole('region', {
            name: '删除前核对',
            exact: true,
        })
        const confirm = review.getByRole('button', {
            name: '确认删除',
            exact: true,
        })
        await expect(confirm).toBeEnabled()
        page.once('dialog', (dialog) => dialog.accept())
        await confirm.click()
        await committed
        await expect(
            review.getByRole('button', { name: '取消', exact: true }),
        ).toBeDisabled()
        await expect(
            review.getByRole('button', { name: '重新核对', exact: true }),
        ).toBeDisabled()
        expect(deletes).toBe(1)
        release()
        await expect(
            review.getByText('操作结果未知。请重新读取，不要重放删除。', {
                exact: true,
            }),
        ).toBeVisible()
        await expect(confirm).toHaveCount(0)
        const response = page.waitForResponse((value) =>
            new URL(value.url()).pathname.endsWith('/deletion-review'),
        )
        await review
            .getByRole('button', { name: '重新核对', exact: true })
            .click()
        // The old draft version is no longer current. Only a fresh list can
        // confirm the result; refetch must not send another deletion.
        expect((await response).status()).toBe(409)
        await expect(review.getByRole('alert')).toBeVisible()
        await expect(confirm).toHaveCount(0)
        await review.getByRole('button', { name: '取消', exact: true }).click()
        await refresh(page)
        await expect(row).toHaveCount(0)
        const current = await (
            await page.request.get(root + '/connections')
        ).json()
        expect(current.version).toBe(committedVersion)
        expect(current.version).not.toBe(saved.version)
        expect(current.connections).toEqual([])
        await page.reload()
        await expect(
            page.getByText('没有匹配的连接。', { exact: true }),
        ).toBeVisible()
        expect(deletes).toBe(1)
    } finally {
        release()
        await f.close()
    }
})

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
        const deletion = page.waitForResponse(
            (value) =>
                new URL(value.url()).pathname ===
                `${root}/connections/${id}/deletion-review`,
        )
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        const deletionResponse = await deletion
        expect(deletionResponse.status()).toBe(200)
        const deletionData = await deletionResponse.json()
        expect(deletionData.canDelete).toBe(false)
        expect(Number(deletionData.pendingCount)).toBeGreaterThan(0)
        const deletionPanel = page.getByRole('region', {
            name: '删除前核对',
            exact: true,
        })
        await expect(deletionPanel).toContainText('Meter')
        await expect(deletionPanel).toContainText('Route')
        await expect(deletionPanel).toContainText(
            `待发 ${deletionData.pendingCount}`,
        )
        await expect(deletionPanel).toContainText('失败 0')
        await expect(deletionPanel).toContainText('隔离 0')
        await deletionPanel
            .locator('summary')
            .filter({ hasText: /^r1 / })
            .click()
        await expect(deletionPanel).toContainText('input.http')
        await expect(deletionPanel).toContainText('output.http')
        await expect(
            deletionPanel.getByRole('button', {
                name: '确认删除',
                exact: true,
            }),
        ).toBeDisabled()
        await deletionPanel
            .getByRole('button', { name: '取消', exact: true })
            .click()
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
        const recoveredDeletion = await (
            await page.request.get(
                root +
                    `/connections/${id}/deletion-review?expectedVersion=${off.version}`,
            )
        ).json()
        await row.getByRole('button', { name: '删除连接', exact: true }).click()
        const recoveredDeletionPanel = page.getByRole('region', {
            name: '删除前核对',
            exact: true,
        })
        await expect(recoveredDeletionPanel).toBeVisible()
        await expect(recoveredDeletionPanel).toContainText('Meter')
        await expect(recoveredDeletionPanel).toContainText('来源 read')
        await expect(recoveredDeletionPanel).toContainText('来源 disabled')
        await expect(recoveredDeletionPanel).toContainText('output.http/route/')
        await expect(recoveredDeletionPanel).toContainText('保留运行版本')
        await expect(recoveredDeletionPanel).toContainText(
            `待发 ${recoveredDeletion.pendingCount}`,
        )
        await expect(recoveredDeletionPanel).toContainText(
            `失败 ${recoveredDeletion.failedCount}`,
        )
        await expect(recoveredDeletionPanel).toContainText(
            `隔离 ${recoveredDeletion.quarantinedCount}`,
        )
        await expect(recoveredDeletionPanel).toContainText(
            '仍有引用或积存记录，不能删除。',
        )
        await expect(
            recoveredDeletionPanel.getByRole('button', {
                name: '确认删除',
                exact: true,
            }),
        ).toBeDisabled()
        await recoveredDeletionPanel
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
