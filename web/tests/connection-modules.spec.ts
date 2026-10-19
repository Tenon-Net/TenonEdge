import { test, expect, type Browser, type Page } from '@playwright/test'
import { createServer as tcpServer, type Socket } from 'node:net'
import { createServer as httpServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Module-browser-7836!'
const acknowledge = '我已核对原新身份和积存，了解凭证立即生效及隔离规则'
const address = new URL(process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1')
    .hostname
const editor = (page: Page) =>
    page.getByRole('form', { name: '扩展连接参数', exact: true })
const panel = (page: Page) =>
    page.getByRole('region', { name: '扩展连接独立凭证', exact: true })
async function fixture(browser: Browser) {
    const received: string[] = [],
        bodies: string[] = [],
        sockets = new Set<Socket>()
    const wire = tcpServer((socket) => {
        sockets.add(socket)
        socket.on('close', () => sockets.delete(socket))
        socket.on('error', () => {})
        let buffer = Buffer.alloc(0)
        socket.on('data', (data) => {
            buffer = Buffer.concat([buffer, data])
            while (buffer.length && buffer.length >= buffer[0] + 1) {
                const size = buffer[0]
                received.push(buffer.subarray(1, size + 1).toString('utf8'))
                buffer = buffer.subarray(size + 1)
                socket.write(Buffer.from([42]))
            }
        })
    })
    const sink = httpServer(async (request, response) => {
        const chunks: Buffer[] = []
        for await (const chunk of request) chunks.push(Buffer.from(chunk))
        bodies.push(Buffer.concat(chunks).toString('utf8'))
        response.writeHead(204).end()
    })
    await new Promise<void>((resolve) => wire.listen(0, '127.0.0.1', resolve))
    await new Promise<void>((resolve) => sink.listen(0, '127.0.0.1', resolve))
    const a = wire.address(),
        b = sink.address()
    if (!a || typeof a === 'string' || !b || typeof b === 'string')
        throw new Error('ports')
    const wirePort = a.port
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-module-web-'))
    const args = [
        `--TenonEdge:Outbound:LocalPorts:0=${a.port}`,
        `--TenonEdge:Outbound:LocalPorts:1=${b.port}`,
    ]
    let host = await startOwnedHost(data, 0, args, address, true)
    const context = await browser.newContext({
        baseURL: host.url,
        viewport: { width: 1700, height: 1100 },
    })
    const page = await context.newPage()
    page.setDefaultTimeout(15_000)
    page.on('dialog', (dialog) => dialog.accept())
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill('module-admin')
    for (const name of ['密码', '确认密码'])
        await page.getByLabel(name, { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    async function get(path: string) {
        return (await page.request.get(host.url + root + path)).json()
    }
    async function write(
        method: 'post' | 'put',
        path: string,
        body: unknown,
        expected = 200,
    ) {
        const session = await get('/auth/session')
        const response = await page.request[method](host.url + root + path, {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: body,
        })
        expect(response.status(), await response.text()).toBe(expected)
        return response.json()
    }
    async function open() {
        await page
            .getByRole('table', { name: '连接列表', exact: true })
            .getByRole('row')
            .filter({ hasText: 'web-wire' })
            .getByRole('button', { name: /编辑连接|查看连接/ })
            .click()
        await expect(editor(page)).toBeVisible()
    }
    async function create() {
        await page.goto('/connections')
        await page
            .getByRole('button', { name: '新建扩展连接', exact: true })
            .click()
        await expect(
            editor(page).getByRole('button', {
                name: '保存到草稿',
                exact: true,
            }),
        ).toBeDisabled()
        await editor(page)
            .getByLabel('扩展类型与版本', { exact: true })
            .selectOption('authwire@1')
        await editor(page)
            .getByLabel('连接 key', { exact: true })
            .fill('web-wire')
        await editor(page)
            .getByLabel('连接名称', { exact: true })
            .fill('Web wire')
        await editor(page).getByLabel('主机', { exact: true }).fill('127.0.0.1')
        await editor(page)
            .getByLabel('端口', { exact: true })
            .fill(String(wirePort))
        await expect(
            editor(page).getByLabel('精确参数', { exact: true }),
        ).toHaveValue('9007199254740993')
        await editor(page)
            .getByLabel('测试选项', { exact: true })
            .selectOption('two')
        await editor(page).getByLabel('测试开关', { exact: true }).uncheck()
        await expect(editor(page).locator('input[type=password]')).toHaveCount(
            0,
        )
        await editor(page)
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
        await expect(editor(page)).toHaveCount(0)
        const list = await get('/connections')
        await open()
        return { id: list.connections[0].id, version: list.version }
    }
    async function credential(secret: string) {
        await panel(page)
            .getByLabel('公开账户标识（非秘密）', { exact: true })
            .fill('wire-account')
        await panel(page).getByLabel('认证秘密', { exact: true }).fill(secret)
        const request = page.waitForRequest((request) =>
            request.url().endsWith('/module-credential/review'),
        )
        await panel(page)
            .getByRole('button', {
                name: '核对凭证变更（不保存）',
                exact: true,
            })
            .click()
        expect((await request).postData()).not.toContain(secret)
        await expect(
            panel(page).getByRole('region', {
                name: '确认凭证身份与积存',
                exact: true,
            }),
        ).toBeVisible()
        await expect(
            panel(page).getByRole('button', {
                name: '立即保存凭证',
                exact: true,
            }),
        ).toBeDisabled()
        await panel(page).getByLabel(acknowledge, { exact: true }).check()
    }
    async function save() {
        await panel(page)
            .getByRole('button', { name: '立即保存凭证', exact: true })
            .click()
        await expect(
            panel(page).getByLabel('认证秘密', { exact: true }),
        ).toHaveValue('')
        await expect(
            panel(page).getByRole('button', {
                name: '刷新凭证状态',
                exact: true,
            }),
        ).toBeEnabled()
    }
    return {
        page,
        create,
        open,
        credential,
        save,
        get,
        write,
        received,
        bodies,
        port: a.port,
        sink: `http://127.0.0.1:${b.port}/`,
        restart: async () => {
            const old = new URL(host.url)
            await host.stop()
            host = await startOwnedHost(
                data,
                Number(old.port),
                args,
                address,
                true,
            )
        },
        close: async () => {
            await context.close()
            await host.stop()
            for (const socket of sockets) socket.destroy()
            sink.closeAllConnections()
            await Promise.all([
                new Promise<void>((resolve) => wire.close(() => resolve())),
                new Promise<void>((resolve) => sink.close(() => resolve())),
            ])
            await rm(data, { recursive: true, force: true })
        },
    }
}

test('extension input reference: actual connection kind is selectable without a JSON editor', async ({
    browser,
}) => {
    const f = await fixture(browser)
    const { page } = f
    try {
        const created = await f.create()
        await page.goto('/devices')
        await page
            .getByRole('button', { name: '新建设备', exact: true })
            .click()
        await page.getByLabel('设备 key', { exact: true }).fill('wire-device')
        await page
            .getByLabel('设备名称', { exact: true })
            .fill('Wire input device')
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('module')
        await page
            .getByRole('combobox', { name: '输入模块类型', exact: true })
            .selectOption('input.authwire')
        await page
            .getByRole('button', { name: '保存设备草稿', exact: true })
            .click()
        await page.getByRole('button', { name: /Wire input device/ }).click()
        await page
            .getByRole('link', { name: '数据来源与点位', exact: true })
            .click()
        await page
            .getByRole('button', { name: '新建数据来源', exact: true })
            .click()
        await page.getByLabel('来源 key', { exact: true }).fill('sample')
        await page.getByLabel('来源名称', { exact: true }).fill('Wire sample')
        await page
            .getByRole('combobox', { name: '输入模块版本', exact: true })
            .selectOption('1')
        await page
            .getByLabel('Wire', { exact: true })
            .selectOption({ label: 'Web wire (web-wire)' })
        const saved = page.waitForResponse(
            (r) => r.request().method() === 'POST' && /\/inputs$/.test(r.url()),
        )
        await page
            .getByRole('button', { name: '保存来源草稿', exact: true })
            .click()
        const response = await saved
        expect(response.status(), await response.text()).toBe(200)
        const body = await response.json()
        expect(body.input.module.connections.wire).toBe(created.id)
        expect(f.received).toEqual([]) // Editing a reference must not connect or sample.
    } finally {
        await f.close()
    }
})

// Only the test assembly knows authwire. Connections and credentials are real UI;
// device/source/route creation and apply below are explicit API fixtures, not a no-JSON E2E claim.
test('connection module automatic form, independent credentials, real TCP rotation and restart', async ({
    browser,
}) => {
    test.setTimeout(180_000)
    const f = await fixture(browser)
    const { page } = f
    try {
        const created = await f.create()
        await f.credential('wire-secret-first')
        await f.save()
        const exact = await (
            await page.request.get(root + '/connections')
        ).text()
        expect(exact).toContain('9007199254740993')
        expect(exact).not.toContain('wire-secret-first')
        await expect(
            editor(page).getByLabel('连接 key', { exact: true }),
        ).toBeDisabled()
        const output = await f.write('post', '/connections', {
            expectedVersion: created.version,
            key: 'sink',
            name: 'Sink',
            kind: 'http',
            http: { baseAddress: f.sink },
        })
        const device = await f.write('post', '/devices', {
            expectedVersion: output.version,
            key: 'meter',
            name: 'Meter',
            kind: 'module',
            moduleKind: 'input.authwire',
        })
        const source = await f.write(
            'post',
            `/devices/${device.device.id}/inputs`,
            {
                expectedVersion: device.version,
                key: 'read',
                name: 'Read',
                module: {
                    kind: 'input.authwire',
                    version: 1,
                    parameters: {},
                    connections: { wire: created.id },
                },
            },
        )
        const route = await f.write('post', '/routes', {
            expectedVersion: source.version,
            key: 'route',
            name: 'Route',
            enabled: true,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'in',
                        type: 'input.authwire',
                        version: 1,
                        name: 'Input',
                        config: { sourceIds: [source.input.id] },
                    },
                    {
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: { connectionId: output.connection.id },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: { nodes: {} },
            },
        })
        const review = await f.write('post', '/configuration/review', {
            expectedVersion: route.version,
        })
        const operation = await f.write(
            'post',
            '/configuration/apply',
            {
                expectedVersion: route.version,
                expectedEffectiveRevision: review.effectiveRevision,
                expectedPlanHash: review.planHash,
                confirmed: true,
            },
            202,
        )
        await expect
            .poll(
                async () =>
                    (await f.get('/configuration/operations/' + operation.id))
                        .stage,
            )
            .toBe('completed')
        await expect
            .poll(() => f.received.includes('wire-secret-first'))
            .toBe(true)
        await expect.poll(() => f.bodies.length).toBeGreaterThan(0)
        expect(f.bodies.join('')).toContain('42')
        expect(f.bodies.join('')).not.toContain('wire-secret-first')
        await page.goto('/connections')
        await f.open()
        await f.credential('wire-secret-next')
        await f.save()
        await expect
            .poll(() => f.received.includes('wire-secret-next'))
            .toBe(true)
        expect((await f.get('/connections')).version).toBe(route.version)
        const metadata = await f.get(
            `/connections/${created.id}/module-credential`,
        )
        expect(JSON.stringify(metadata)).not.toContain('wire-secret-next')
        await f.restart()
        await page.reload()
        // SPA navigation exercises the independent plain-DTO and lexical query caches.
        await page.getByRole('link', { name: '设备草稿', exact: true }).click()
        await expect(
            page.getByRole('heading', { name: '设备草稿', exact: true }),
        ).toBeVisible()
        await page.getByRole('link', { name: '连接', exact: true }).click()
        await f.open()
        await expect(
            editor(page).getByLabel('精确参数', { exact: true }),
        ).toHaveValue('9007199254740993')
        const count = f.received.length
        await expect.poll(() => f.received.length).toBeGreaterThan(count)
        expect(f.received.at(-1)).toBe('wire-secret-next')
        expect(
            await page.evaluate(() =>
                JSON.stringify({
                    local: { ...localStorage },
                    session: { ...sessionStorage },
                }),
            ),
        ).not.toContain('wire-secret')
        await f.write('post', '/users', {
            accountName: 'module-viewer',
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
        await page.getByLabel('账号', { exact: true }).fill('module-viewer')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await page.getByLabel('原密码', { exact: true }).fill(password)
        await page
            .getByLabel('新密码', { exact: true })
            .fill('Own-module-8562!')
        await page
            .getByRole('button', { name: '保存新密码', exact: true })
            .click()
        await expect(page.getByRole('navigation')).toBeVisible()
        await page.goto('/connections')
        await f.open()
        await expect(
            editor(page).getByLabel('主机', { exact: true }),
        ).toBeDisabled()
        await expect(panel(page)).toContainText('wire-account')
        await expect(panel(page).locator('input[type=password]')).toHaveCount(0)
        await expect(
            page.getByRole('button', { name: '新建扩展连接', exact: true }),
        ).toHaveCount(0)
    } finally {
        await f.close()
    }
})

test('module writes are never replayed; conflicts clear secrets, reload checks actual draft, and retain/clear require confirmation', async ({
    browser,
}) => {
    test.setTimeout(150_000)
    const f = await fixture(browser)
    const { page } = f
    try {
        const created = await f.create()
        // A list failure leaves the local non-secret editor intact.
        await editor(page)
            .getByLabel('连接名称', { exact: true })
            .fill('Local name')
        await page.route('**/connections', (route) =>
            route.request().method() === 'GET'
                ? route.fulfill({ status: 500, body: '{}' })
                : route.continue(),
        )
        await page
            .getByRole('button', { name: '刷新连接列表', exact: true })
            .click()
        await expect(
            editor(page).getByLabel('连接名称', { exact: true }),
        ).toHaveValue('Local name')
        await expect(
            editor(page).getByRole('button', {
                name: '保存到草稿',
                exact: true,
            }),
        ).toBeDisabled()
        await page.unroute('**/connections')
        await editor(page)
            .getByRole('button', { name: '重新载入此连接', exact: true })
            .click()
        await expect(
            editor(page).getByLabel('连接名称', { exact: true }),
        ).toHaveValue('Web wire')
        // Commit, then lose the reply. The captured version cannot be automatically retried.
        let draftWrites = 0
        await page.route(`**/connections/${created.id}`, async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            draftWrites++
            await route.fetch()
            await route.abort('failed')
        })
        await editor(page)
            .getByLabel('连接名称', { exact: true })
            .fill('Committed name')
        await editor(page)
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
        await expect(editor(page)).toContainText('保存失败或结果未知')
        await page.waitForTimeout(1000)
        expect(draftWrites).toBe(1)
        await page.unroute(`**/connections/${created.id}`)
        await editor(page)
            .getByRole('button', { name: '重新载入此连接', exact: true })
            .click()
        await expect(
            editor(page).getByLabel('连接名称', { exact: true }),
        ).toHaveValue('Committed name')
        await f.credential('lost-reply-secret')
        let credentialWrites = 0
        await page.route(
            `**/connections/${created.id}/module-credential`,
            async (route) => {
                if (route.request().method() !== 'PUT') return route.continue()
                credentialWrites++
                await route.fetch()
                await route.abort('failed')
            },
        )
        await f.save()
        await expect(panel(page)).toContainText('秘密已清除，不自动重放')
        await page.waitForTimeout(1000)
        expect(credentialWrites).toBe(1)
        await expect(
            panel(page).getByRole('button', {
                name: '核对凭证变更（不保存）',
                exact: true,
            }),
        ).toBeDisabled()
        await page.unroute(`**/connections/${created.id}/module-credential`)
        await panel(page)
            .getByRole('button', { name: '刷新凭证状态', exact: true })
            .click()
        await expect(panel(page)).toContainText('wire-account')
        const before = await f.get(
            `/connections/${created.id}/module-credential`,
        )
        await f.credential('conflicting-secret')
        await f.write('put', `/connections/${created.id}/module-credential`, {
            expectedDraftVersion: before.draftVersion,
            expectedVersion: before.identity.version,
            account: 'wire-account',
            secrets: { auth: 'concurrent-secret' },
        })
        await f.save()
        await expect(panel(page)).toContainText('秘密已清除，不自动重放')
        await panel(page)
            .getByRole('button', { name: '刷新凭证状态', exact: true })
            .click()
        await expect(
            panel(page).getByRole('button', {
                name: '核对凭证变更（不保存）',
                exact: true,
            }),
        ).toBeEnabled()
        // New recipient is independently reviewed before administrator retention.
        await editor(page).getByLabel('主机', { exact: true }).fill('localhost')
        await editor(page)
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
        await expect(editor(page)).toHaveCount(0)
        await f.open()
        await panel(page)
            .getByRole('button', {
                name: '保留旧凭证用于新地址（管理员）',
                exact: true,
            })
            .click()
        await expect(
            panel(page).getByRole('region', {
                name: '确认凭证身份与积存',
                exact: true,
            }),
        ).toContainText('localhost')
        await panel(page).getByLabel(acknowledge, { exact: true }).check()
        await f.save()
        expect(
            (await f.get(`/connections/${created.id}/module-credential`))
                .matchesDraft,
        ).toBe(true)
        await panel(page)
            .getByRole('button', { name: '清除凭证', exact: true })
            .click()
        await expect
            .poll(async () =>
                (
                    await page.request.get(
                        root + `/connections/${created.id}/module-credential`,
                    )
                ).status(),
            )
            .toBe(404)
        // Missing binding in metadata cannot manufacture a writable credential capability.
        await page.route('**/connection-modules', async (route) => {
            const response = await route.fetch()
            const definitions: { recipientParameters: string[] }[] =
                await response.json()
            await route.fulfill({
                response,
                json: definitions.map((item) => ({
                    ...item,
                    recipientParameters: [],
                })),
            })
        })
        await page.reload()
        await f.open()
        await expect(panel(page)).toContainText('此声明没有接收方绑定')
        await expect(panel(page).locator('input[type=password]')).toHaveCount(0)
    } finally {
        await f.close()
    }
})
