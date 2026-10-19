import { test, expect, type Page } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Nested-browser-7183!'
const editor = (page: Page) =>
    page.getByRole('form', { name: '扩展连接参数', exact: true })
const row = (page: Page, key: string) =>
    page
        .getByRole('table', { name: '连接列表', exact: true })
        .getByRole('row')
        .filter({
            has: page.locator('td:first-child').getByText(key, { exact: true }),
        })

for (const role of ['administrator', 'configurator'] as const) {
    test(`nested connections: ${role} edits two hops, protects references, forwards and restarts frozen`, async ({
        browser,
    }) => {
        test.setTimeout(150_000)
        const data = await mkdtemp(join(tmpdir(), 'tenonedge-nested-browser-'))
        const reads: string[] = [],
            bodies: string[] = []
        const peer = createServer(async (request, response) => {
            if (request.method === 'GET') {
                reads.push(request.url ?? '')
                response
                    .writeHead(200, { 'Content-Type': 'application/json' })
                    .end('{"value":9007199254740993}')
            } else {
                const chunks: Buffer[] = []
                for await (const chunk of request)
                    chunks.push(Buffer.from(chunk))
                bodies.push(Buffer.concat(chunks).toString('utf8'))
                response.writeHead(204).end()
            }
        })
        await new Promise<void>((resolve) =>
            peer.listen(0, '127.0.0.1', resolve),
        )
        const endpoint = peer.address()
        if (!endpoint || typeof endpoint === 'string') throw new Error('port')
        const address = new URL(
            process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
        ).hostname
        const args = [`--TenonEdge:Outbound:LocalPorts:0=${endpoint.port}`]
        let host = await startOwnedHost(data, 0, args, address, true)
        let context = await browser.newContext({
            baseURL: host.url,
            viewport: { width: 1700, height: 1100 },
        })
        try {
            const page = await context.newPage()
            page.setDefaultTimeout(15_000)
            page.on('dialog', (dialog) => dialog.accept())
            await page.goto('/')
            await page.getByLabel('账号', { exact: true }).fill('nested-admin')
            for (const name of ['密码', '确认密码'])
                await page.getByLabel(name, { exact: true }).fill(password)
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            async function get(path: string) {
                const response = await page.request.get(root + path)
                expect(response.status(), await response.text()).toBe(200)
                return response.json()
            }
            async function write(
                method: 'post' | 'put',
                path: string,
                body: unknown,
            ) {
                const session = await get('/auth/session')
                const response = await page.request[method](root + path, {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: body,
                })
                expect(response.status(), await response.text()).toBe(200)
                return response.json()
            }
            async function switchUser(account: string) {
                await page
                    .getByRole('button', { name: '退出登录', exact: true })
                    .click()
                await page.getByLabel('账号', { exact: true }).fill(account)
                await page.getByLabel('密码', { exact: true }).fill(password)
                await page
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await page.getByLabel('原密码', { exact: true }).fill(password)
                await page
                    .getByLabel('新密码', { exact: true })
                    .fill('Own-nested-browser-8562!')
                await page
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(page.getByRole('navigation')).toBeVisible()
            }
            for (const userRole of ['configurator', 'viewer'])
                await write('post', '/users', {
                    accountName: `nested-${userRole}`,
                    displayName: userRole,
                    role: userRole,
                    temporaryPassword: password,
                    isEnabled: true,
                })
            // Only the native leaf and later device/graph are API fixtures. All nested
            // connection declarations/selections, deletion review and apply use real UI.
            const leaf = await write('post', '/connections', {
                expectedVersion: '0',
                key: 'http-leaf',
                name: 'HTTP leaf',
                kind: 'http',
                http: { baseAddress: `http://127.0.0.1:${endpoint.port}/` },
            })
            if (role === 'configurator') await switchUser('nested-configurator')
            async function create(key: string, upstream: number) {
                await page.goto('/connections')
                await page
                    .getByRole('button', { name: '新建扩展连接', exact: true })
                    .click()
                await editor(page)
                    .getByLabel('扩展类型与版本', { exact: true })
                    .selectOption('nestedhttp@1')
                await editor(page)
                    .getByLabel('连接 key', { exact: true })
                    .fill(key)
                await editor(page)
                    .getByLabel('连接名称', { exact: true })
                    .fill(key + ' name')
                await editor(page)
                    .getByLabel('连接标签', { exact: true })
                    .fill(key + ' label')
                const reference = editor(page).getByLabel('上游连接', {
                    exact: true,
                })
                await expect(reference).toHaveValue('')
                await reference.selectOption(String(upstream))
                await expect(
                    editor(page).getByLabel('精确参数', { exact: true }),
                ).toHaveValue('9007199254740993')
                await editor(page)
                    .getByLabel('测试开关', { exact: true })
                    .uncheck()
                await editor(page)
                    .getByLabel('测试选项', { exact: true })
                    .selectOption('two')
                await expect(
                    editor(page).locator('textarea,input[type=password]'),
                ).toHaveCount(0)
                const saved = page.waitForResponse(
                    (r) =>
                        r.request().method() === 'POST' &&
                        r.url().endsWith('/connections'),
                )
                await editor(page)
                    .getByRole('button', { name: '保存到草稿', exact: true })
                    .click()
                const response = await saved
                expect(response.status(), await response.text()).toBe(200)
                expect(response.request().postData()).toContain(
                    '"precision":9007199254740993',
                )
                expect(
                    response.request().postDataJSON().module.connections,
                ).toEqual({ upstream })
                expect(
                    response.request().postDataJSON().module.parameters,
                ).not.toHaveProperty('upstream')
                await expect(editor(page)).toHaveCount(0)
                return response.json()
            }
            const first = await create('first-hop', leaf.connection.id)
            // Sixth parameter type remains on the separate, real credential path,
            // even when this connection has an explicit upstream reference.
            await row(page, 'first-hop')
                .getByRole('button', { name: '编辑连接', exact: true })
                .click()
            const credential = page.getByRole('region', {
                name: '扩展连接独立凭证',
                exact: true,
            })
            const secret = 'Nested-fixture-secret-only'
            await credential
                .getByLabel('公开账户标识（非秘密）', { exact: true })
                .fill('nested-account')
            await credential
                .getByLabel('认证秘密', { exact: true })
                .fill(secret)
            const credentialReview = page.waitForRequest((request) =>
                request.url().endsWith('/module-credential/review'),
            )
            await credential
                .getByRole('button', {
                    name: '核对凭证变更（不保存）',
                    exact: true,
                })
                .click()
            expect((await credentialReview).postData()).not.toContain(secret)
            await credential
                .getByLabel(
                    '我已核对原新身份和积存，了解凭证立即生效及隔离规则',
                    { exact: true },
                )
                .check()
            const credentialSaved = page.waitForResponse(
                (response) =>
                    response.request().method() === 'PUT' &&
                    response.url().endsWith('/module-credential'),
            )
            await credential
                .getByRole('button', { name: '立即保存凭证', exact: true })
                .click()
            expect((await credentialSaved).status()).toBe(200)
            await expect(
                credential.getByLabel('认证秘密', { exact: true }),
            ).toHaveValue('')
            await expect(
                credential.getByRole('button', {
                    name: '刷新凭证状态',
                    exact: true,
                }),
            ).toBeEnabled()
            const exported = await page.request.get(
                root + '/configuration/export',
            )
            expect(exported.status()).toBe(200)
            expect(await exported.text()).not.toContain(secret)
            expect(
                (await exported.json()).connections.find(
                    (item: { connectionKey: string }) =>
                        item.connectionKey === 'first-hop',
                ).module.connections,
            ).toEqual({ upstream: 'http-leaf' })
            await editor(page)
                .getByRole('button', { name: '关闭编辑', exact: true })
                .click()
            const second = await create('second-hop', first.connection.id)
            await row(page, 'http-leaf')
                .getByRole('button', { name: '删除连接', exact: true })
                .click()
            const deletion = page.getByRole('region', {
                name: '删除前核对',
                exact: true,
            })
            await expect(deletion).toContainText('引用此连接的连接草稿（1）')
            await expect(deletion).toContainText('first-hop')
            await expect(
                deletion.getByRole('button', { name: '确认删除', exact: true }),
            ).toBeDisabled()
            await deletion
                .getByRole('button', { name: '取消', exact: true })
                .click()
            await row(page, 'second-hop')
                .getByRole('button', { name: '编辑连接', exact: true })
                .click()
            const reference = editor(page).getByLabel('上游连接', {
                exact: true,
            })
            await expect(reference).toHaveValue(String(first.connection.id))
            await expect(
                reference.locator(`option[value="${second.connection.id}"]`),
            ).toHaveCount(0)
            await editor(page)
                .getByLabel('连接标签', { exact: true })
                .fill('Unsaved label')
            await page.route('**/api/edge-admin/v1/connections', (route) =>
                route.fulfill({
                    status: 500,
                    contentType: 'application/problem+json',
                    body: '{}',
                }),
            )
            await page
                .getByRole('button', { name: '刷新连接列表', exact: true })
                .click()
            await expect(editor(page)).toContainText('连接目录读取失败')
            await expect(reference).toBeDisabled()
            await expect(
                editor(page).getByRole('button', {
                    name: '保存到草稿',
                    exact: true,
                }),
            ).toBeDisabled()
            await expect(
                editor(page).getByLabel('连接标签', { exact: true }),
            ).toHaveValue('Unsaved label')
            await page.unroute('**/api/edge-admin/v1/connections')
            await page
                .getByRole('button', { name: '刷新连接列表', exact: true })
                .click()
            await expect(reference).toBeEnabled()
            await expect(reference).toHaveValue(String(first.connection.id))
            let writes = 0
            const editUrl = `**/connections/${second.connection.id}`
            await page.route(editUrl, async (route) => {
                if (route.request().method() !== 'PUT') return route.continue()
                writes++
                const response = await route.fetch()
                expect(response.status()).toBe(200)
                await route.abort('failed')
            })
            await editor(page)
                .getByRole('button', { name: '保存到草稿', exact: true })
                .click()
            await expect(editor(page)).toContainText('保存失败或结果未知')
            await page.waitForTimeout(1000)
            expect(writes).toBe(1)
            await expect(
                editor(page).getByRole('button', {
                    name: '保存到草稿',
                    exact: true,
                }),
            ).toBeDisabled()
            await page.unroute(editUrl)
            await editor(page)
                .getByRole('button', { name: '重新载入此连接', exact: true })
                .click()
            await expect(reference).toHaveValue(String(first.connection.id))
            await expect(
                editor(page).getByLabel('连接标签', { exact: true }),
            ).toHaveValue('Unsaved label')
            await editor(page)
                .getByRole('button', { name: '关闭编辑', exact: true })
                .click()
            expect(reads).toEqual([])
            expect(bodies).toEqual([])
            const draft = await get('/connections')
            const device = await write('post', '/devices', {
                expectedVersion: draft.version,
                key: 'meter',
                name: 'Nested meter',
                kind: 'module',
                moduleKind: 'input.nestedhttp',
            })
            const source = await write(
                'post',
                `/devices/${device.device.id}/inputs`,
                {
                    expectedVersion: device.version,
                    key: 'readings',
                    name: 'Readings',
                    enabled: true,
                    module: {
                        kind: 'input.nestedhttp',
                        version: 1,
                        parameters: {},
                        connections: { connection: second.connection.id },
                    },
                },
            )
            const route = await write('post', '/routes', {
                expectedVersion: source.version,
                key: 'nested-route',
                name: 'Nested route',
                enabled: true,
                graph: {
                    schemaVersion: 1,
                    nodes: [
                        {
                            id: 'in',
                            type: 'input.nestedhttp',
                            version: 1,
                            name: 'Input',
                            config: { sourceIds: [source.input.id] },
                        },
                        {
                            id: 'out',
                            type: 'output.http',
                            version: 1,
                            name: 'Output',
                            config: { connectionId: leaf.connection.id },
                        },
                    ],
                    edges: [{ source: 'in', target: 'out' }],
                    layout: {
                        nodes: {
                            in: { x: 100, y: 200 },
                            out: { x: 600, y: 200 },
                        },
                    },
                },
            })
            await page.goto('/configuration')
            await page
                .getByRole('button', { name: '查看应用变更清单', exact: true })
                .click()
            const review = page.getByRole('region', {
                name: '应用前确认',
                exact: true,
            })
            await review
                .getByLabel('我已检查变更及影响，确认应用这份草稿')
                .check()
            const accepted = page.waitForResponse(
                (response) =>
                    response.request().method() === 'POST' &&
                    response.url().endsWith('/configuration/apply'),
            )
            await review
                .getByRole('button', { name: '确认应用配置', exact: true })
                .click()
            const operation = await (await accepted).json()
            await expect
                .poll(async () => {
                    const status = await get(
                        `/configuration/operations/${operation.id}`,
                    )
                    return status.result?.activated
                        ? 'activated'
                        : (status.errorCode ?? status.stage)
                })
                .toBe('activated')
            await expect
                .poll(
                    () =>
                        bodies.some((body) =>
                            body.includes('"value":9007199254740993'),
                        ),
                    { timeout: 20_000 },
                )
                .toBe(true)
            expect(reads.every((path) => path === '/nested')).toBe(true)
            await write('put', `/connections/${leaf.connection.id}`, {
                expectedVersion: route.version,
                key: 'http-leaf',
                name: 'Unapplied leaf',
                kind: 'http',
                http: { baseAddress: 'http://unapplied.invalid/' },
            })
            // Viewer sees saved nested references but cannot mutate via UI or direct API.
            await switchUser('nested-viewer')
            await page.goto('/connections')
            await row(page, 'second-hop')
                .getByRole('button', { name: '查看连接', exact: true })
                .click()
            await expect(
                editor(page).getByLabel('上游连接', { exact: true }),
            ).toHaveValue(String(first.connection.id))
            await expect(
                editor(page).getByLabel('上游连接', { exact: true }),
            ).toBeDisabled()
            await expect(
                editor(page).getByRole('button', {
                    name: '保存到草稿',
                    exact: true,
                }),
            ).toHaveCount(0)
            const viewer = await get('/auth/session')
            const version = (await get('/connections')).version
            expect(
                (
                    await page.request.put(
                        root + `/connections/${second.connection.id}`,
                        {
                            headers: { 'X-CSRF-Token': viewer.csrfToken },
                            data: {
                                ...second.connection,
                                expectedVersion: version,
                            },
                        },
                    )
                ).status(),
            ).toBe(403)
            expect((await get('/connections')).version).toBe(version)
            await context.close()
            const closedCount = bodies.length
            await expect.poll(() => bodies.length).toBeGreaterThan(closedCount)
            expect(await host.stop()).toBe(0)
            const beforeRestart = bodies.length
            host = await startOwnedHost(data, 0, args, address, true)
            await expect
                .poll(() => bodies.length, { timeout: 20_000 })
                .toBeGreaterThan(beforeRestart)
            expect(reads.every((path) => path === '/nested')).toBe(true)
            expect(
                bodies.every((body) =>
                    body.includes('"value":9007199254740993'),
                ),
            ).toBe(true)
            context = await browser.newContext({ baseURL: host.url })
            const login = await context.request.post(root + '/auth/login', {
                data: { accountName: 'nested-admin', password },
            })
            expect(login.status()).toBe(200)
            const persisted = await (
                await context.request.get(root + '/connections')
            ).json()
            expect(persisted.version).toBe(version)
            expect(
                persisted.connections.find(
                    (item: { key: string }) => item.key === 'http-leaf',
                ).http.baseAddress,
            ).toBe('http://unapplied.invalid/')
        } finally {
            await context.close().catch(() => {})
            await host.stop()
            peer.closeAllConnections()
            await new Promise<void>((resolve) => peer.close(() => resolve()))
            await rm(data, { recursive: true, force: true })
        }
    })
}
