import { test, expect, type Locator, type Page } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'
import { mqttOutputPeer } from './support/mqtt-output-peer'
import type { OutputModule } from '../src/features/canvas/output-module-model'
import type { components } from '../src/api/schema'
import type { Diagnostics } from '../src/features/diagnostics/api'

const root = '/api/edge-admin/v1'
const password = 'Output-browser-7183!'
const sample = '{"value":9007199254740993}'
const acknowledge =
    '我已核对真实目标、账号及完整原报文，了解对方可能将其当作真实数据保存'
const httpAddress = new URL(
    process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
).hostname

async function httpOutputPeer() {
    const requests: {
        body: string
        id: string | string[] | undefined
        path: string | undefined
    }[] = []
    const server = createServer(async (request, response) => {
        const chunks: Buffer[] = []
        for await (const chunk of request) chunks.push(Buffer.from(chunk))
        requests.push({
            body: Buffer.concat(chunks).toString('utf8'),
            id: request.headers['idempotency-key'],
            path: request.url,
        })
        response.writeHead(204).end()
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('receiver')
    return {
        port: address.port,
        requests,
        bodies: () => requests.map((request) => request.body),
        close: async () => {
            server.closeAllConnections()
            await new Promise<void>((resolve, reject) =>
                server.close((error) => (error ? reject(error) : resolve())),
            )
        },
    }
}

async function logout(page: Page) {
    await page.getByRole('button', { name: '退出登录', exact: true }).click()
    // Do not navigate away while the real logout mutation is still in flight.
    await expect(
        page.getByRole('button', { name: '登录', exact: true }),
    ).toBeVisible()
}

async function login(page: Page, account: string, temporary = false) {
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill(account)
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByRole('button', { name: '登录', exact: true }).click()
    if (temporary) {
        await page.getByLabel('原密码', { exact: true }).fill(password)
        await page
            .getByLabel('新密码', { exact: true })
            .fill('Own-output-7183!')
        await page
            .getByRole('button', { name: '保存新密码', exact: true })
            .click()
    }
    await expect(page.getByRole('navigation')).toBeVisible()
}

async function refreshCatalog(page: Page, refresh: Locator, path: string) {
    const response = page.waitForResponse(
        (value) =>
            new URL(value.url()).pathname === root + path &&
            value.request().method() === 'GET',
    )
    await refresh.click()
    const completed = await response
    expect(await completed.finished()).toBeNull()
    // Accept is disabled both while loading and after a rejected catalog.
    // Wait for the actual response AND its query to settle before asserting
    // the rejection or removing the route and starting another refresh.
    await expect(refresh).toBeEnabled()
    return completed.status()
}

// Test assembly registers v2 declarations only. Connection/input/map are API fixtures;
// the output node, its parameters/references, preview/send and application are real UI.
for (const protocol of ['http', 'mqtt'] as const) {
    test(`${protocol} output module: automatic form, exact bytes, permissions and restart`, async ({
        browser,
    }) => {
        test.setTimeout(180_000)
        const data = await mkdtemp(join(tmpdir(), 'tenonedge-output-web-'))
        const httpPeer =
            protocol === 'http' ? await httpOutputPeer() : undefined
        const peer = httpPeer ?? (await mqttOutputPeer('events'))
        let host = await startOwnedHost(data, 0, [], httpAddress, true)
        const context = await browser.newContext({
            baseURL: host.url,
            viewport: { width: 1700, height: 1100 },
        })
        try {
            const page = await context.newPage()
            page.setDefaultTimeout(10_000)
            page.on('dialog', (dialog) => dialog.accept())
            await page.goto('/')
            await page.getByLabel('账号', { exact: true }).fill('output-admin')
            for (const name of ['密码', '确认密码'])
                await page.getByLabel(name, { exact: true }).fill(password)
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            async function headers() {
                const session = await (
                    await page.request.get(root + '/auth/session')
                ).json()
                return { 'X-CSRF-Token': session.csrfToken }
            }
            async function post(path: string, body: unknown) {
                const response = await page.request.post(root + path, {
                    headers: await headers(),
                    data: body,
                })
                expect(response.status(), await response.text()).toBe(200)
                return response.json()
            }
            const policy = await (
                await page.request.get(root + '/system/outbound-policy')
            ).json()
            expect(
                (
                    await page.request.put(root + '/system/outbound-policy', {
                        headers: await headers(),
                        data: {
                            expectedVersion: policy.version,
                            networks: policy.networks,
                            localPorts: [...policy.localPorts, peer.port],
                        },
                    })
                ).status(),
            ).toBe(200)
            const connection = await post('/connections', {
                expectedVersion: '0',
                key: 'receiver',
                name: 'Receiver',
                kind: protocol,
                ...(protocol === 'http'
                    ? {
                          http: {
                              baseAddress: `http://127.0.0.1:${peer.port}/`,
                          },
                      }
                    : { mqtt: { host: '127.0.0.1', port: peer.port } }),
            })
            const device = await post('/devices', {
                expectedVersion: connection.version,
                key: 'meter',
                name: 'Meter',
                kind: 'simulator',
            })
            const source = await post(`/devices/${device.device.id}/inputs`, {
                expectedVersion: device.version,
                key: 'values',
                name: 'Values',
                simulator: { periodMs: 86_400_000, sampleJson: sample },
            })
            const route = await post('/routes', {
                expectedVersion: source.version,
                key: 'extended',
                name: 'Extended output',
                enabled: true,
                graph: {
                    schemaVersion: 1,
                    nodes: [
                        {
                            id: 'in',
                            type: 'input.simulator',
                            version: 1,
                            name: 'Input',
                            config: { sourceIds: [source.input.id] },
                        },
                        {
                            id: 'map',
                            type: 'transform.map',
                            version: 1,
                            name: 'Map',
                            config: {
                                rules: [
                                    {
                                        target: 'scaled',
                                        type: 'integer',
                                        sourceField: 'value',
                                        offset: 1,
                                    },
                                ],
                            },
                        },
                    ],
                    edges: [{ source: 'in', target: 'map' }],
                    layout: {
                        nodes: { in: { x: 0, y: 20 }, map: { x: 220, y: 20 } },
                    },
                },
            })
            for (const role of ['configurator', 'viewer'])
                await post('/users', {
                    accountName: 'output-' + role,
                    displayName: role,
                    role,
                    temporaryPassword: password,
                    isEnabled: true,
                })
            if (protocol === 'mqtt') {
                await logout(page)
                await login(page, 'output-configurator', true)
            }
            const open = async () => {
                await page.goto('/routes')
                await page
                    .getByRole('button', {
                        name: '打开 Extended output',
                        exact: true,
                    })
                    .click()
            }
            await open()
            await page
                .getByRole('button', {
                    name: `测试 ${protocol.toUpperCase()} 输出 (v2)`,
                    exact: true,
                })
                .click()
            const form = page.getByRole('form', {
                name: '扩展输出参数',
                exact: true,
            })
            const precision = form.getByLabel('精确参数', { exact: true })
            await expect(precision).toHaveValue('9007199254740993')
            await page
                .getByRole('button', { name: '撤销', exact: true })
                .click()
            await expect(form).toHaveCount(0)
            await page
                .getByRole('button', { name: '重做', exact: true })
                .click()
            const graph = JSON.parse(
                (await page.getByTestId('graph-document').textContent())!,
            )
            const id = graph.definition.nodes.find(
                (node: { kind: string }) =>
                    node.kind === 'output.test' + protocol,
            ).id
            const node = (id: string) =>
                page.locator(`.react-flow__node[data-id="${id}"]`)
            await node(id).click()
            const reference = form.getByLabel('目标连接引用', { exact: true })
            const target = form.getByRole('combobox', {
                name: '模块投递目标',
                exact: true,
            })
            await expect(target).toHaveValue('')
            await reference.selectOption(String(connection.connection.id))
            await expect(target).toHaveValue('') // selecting a reference must not auto-select the delivery target
            await target.selectOption(String(connection.connection.id))
            await form.getByLabel('测试开关', { exact: true }).uncheck()
            await form
                .getByLabel('测试选项', { exact: true })
                .selectOption('two')
            await precision.fill('9007199254741000')
            await expect(
                form.getByText('数字不在声明范围内。', { exact: true }),
            ).toBeVisible()
            const accept = form.getByRole('button', {
                name: '确认节点配置',
                exact: true,
            })
            // Only read-only catalog responses are fault-injected; writes and network sends are real.
            for (const fault of ['error', 'missing', 'changed'] as const) {
                const metadata = '**/output-modules'
                await page.route(metadata, async (intercept) => {
                    if (fault === 'error')
                        return intercept.fulfill({
                            status: 500,
                            contentType: 'application/problem+json',
                            body: '{}',
                        })
                    const response = await intercept.fetch()
                    const modules = await response.json()
                    await intercept.fulfill({
                        response,
                        json:
                            fault === 'missing'
                                ? modules.filter(
                                      (item: { kind: string }) =>
                                          item.kind !==
                                          'output.test' + protocol,
                                  )
                                : modules.map((item: OutputModule) =>
                                      item.kind === 'output.test' + protocol
                                          ? {
                                                ...item,
                                                parameters: {
                                                    ...item.parameters,
                                                    parameters: (
                                                        item.parameters
                                                            .parameters ?? []
                                                    ).map((field) =>
                                                        field.name ===
                                                        'precision'
                                                            ? {
                                                                  ...field,
                                                                  maximumJson:
                                                                      '9007199254740998',
                                                              }
                                                            : field,
                                                    ),
                                                },
                                            }
                                          : item,
                                  ),
                    })
                })
                const refresh = page.getByRole('button', {
                    name: '重新读取输出模块',
                    exact: true,
                })
                expect(
                    await refreshCatalog(page, refresh, '/output-modules'),
                ).toBe(fault === 'error' ? 500 : 200)
                await expect(accept).toBeDisabled()
                await expect(precision).toHaveValue('9007199254741000')
                await expect(reference).toHaveValue(
                    String(connection.connection.id),
                )
                await expect(target).toHaveValue(
                    String(connection.connection.id),
                )
                await page.unroute(metadata)
                expect(
                    await refreshCatalog(page, refresh, '/output-modules'),
                ).toBe(200)
                await expect(accept).toBeEnabled()
            }
            for (const fault of ['error', 'version'] as const) {
                const catalog = '**/api/edge-admin/v1/connections'
                await page.route(catalog, async (intercept) => {
                    if (fault === 'error')
                        return intercept.fulfill({
                            status: 500,
                            contentType: 'application/problem+json',
                            body: '{}',
                        })
                    const response = await intercept.fetch()
                    await intercept.fulfill({
                        response,
                        json: { ...(await response.json()), version: '99999' },
                    })
                })
                const refresh = form.getByRole('button', {
                    name: '重新读取模块连接',
                    exact: true,
                })
                expect(
                    await refreshCatalog(page, refresh, '/connections'),
                ).toBe(fault === 'error' ? 500 : 200)
                await expect(accept).toBeDisabled()
                await expect(precision).toHaveValue('9007199254741000')
                await expect(target).toHaveValue(
                    String(connection.connection.id),
                )
                await page.unroute(catalog)
                expect(
                    await refreshCatalog(page, refresh, '/connections'),
                ).toBe(200)
                await expect(accept).toBeEnabled()
            }
            await precision.fill('')
            await expect(
                form.getByText('此参数必填。', { exact: true }),
            ).toBeVisible()
            await accept.click()
            // Fit the actual canvas before dragging. Form edits scroll the page;
            // dragging directly into its bottom auto-pan zone can move the target
            // between Playwright's last pointer move and release.
            await page
                .getByRole('button', { name: '适应画布', exact: true })
                .click()
            await node('map')
                .locator('.react-flow__handle.source')
                .dragTo(node(id).locator('.react-flow__handle.target'))
            await expect
                .poll(async () => {
                    const document = JSON.parse(
                        (await page
                            .getByTestId('graph-document')
                            .textContent())!,
                    )
                    return document.definition.edges.some(
                        (edge: { source: string; target: string }) =>
                            edge.source === 'map' && edge.target === id,
                    )
                })
                .toBe(true)
            const save = async () => {
                const response = page.waitForResponse(
                    (r) =>
                        r.request().method() === 'PUT' &&
                        r.url().endsWith(`/routes/${route.route.id}`),
                )
                await page
                    .getByRole('button', { name: '保存路线草稿', exact: true })
                    .click()
                expect((await response).status()).toBe(200)
            }
            await save()
            const incomplete = await (
                await page.request.get(`${root}/routes/${route.route.id}`)
            ).json()
            expect(
                (
                    await post('/routes/compile', {
                        expectedVersion: incomplete.version,
                    })
                ).compiled,
            ).toBe(false)
            expect(peer.bodies()).toHaveLength(0)
            await node(id).click()
            await precision.fill('9007199254740993')
            await accept.click()
            await save()
            const savedText = await (
                await page.request.get(`${root}/routes/${route.route.id}`)
            ).text()
            expect(savedText).toContain('"precision":9007199254740993')
            expect(savedText).toContain('"flag":false')
            expect(savedText).toContain('"mode":"two"')
            await page
                .getByLabel('预览数据来源', { exact: true })
                .selectOption('input.simulator:' + source.input.id)
            const preview = page.getByRole('region', {
                name: '路线样本预览',
                exact: true,
            })
            await preview
                .getByLabel('手工 JSON 样本（不保存）', { exact: true })
                .fill(sample)
            const previewReply = page.waitForResponse(
                (response) =>
                    response.request().method() === 'POST' &&
                    response.url().endsWith('/routes/preview'),
            )
            await preview
                .getByRole('button', {
                    name: '预览当前图（不发送）',
                    exact: true,
                })
                .click()
            await expect(preview).toContainText('9007199254740994')
            expect(peer.bodies()).toHaveLength(0)
            const send = preview.getByRole('region', {
                name: '发送预览测试报文',
                exact: true,
            })
            const previewId = (
                await preview.getByText(/^本次预览临时编号：/).textContent()
            )?.match(/[0-9a-f-]{36}/)?.[0]
            await send
                .getByRole('button', { name: '核对测试发送', exact: true })
                .click()
            await expect(
                send.getByRole('button', { name: '确认发送一次', exact: true }),
            ).toBeDisabled()
            await send.getByLabel(acknowledge, { exact: true }).check()
            await send
                .getByRole('button', { name: '确认发送一次', exact: true })
                .click()
            await expect(send).toContainText(
                protocol === 'http'
                    ? 'HTTP 状态码：204'
                    : '已收到 Broker PUBACK（QoS 1）',
            )
            await expect.poll(() => peer.bodies().length).toBe(1)
            expect(peer.bodies()[0]).toContain('"scaled":9007199254740994')
            const previewResult: components['schemas']['RoutePreviewResponse'] =
                await (await previewReply).json()
            const frozen = previewResult.result!.paths[0].output!
            expect(frozen.truncated).toBe(false)
            expect(peer.bodies()[0]).toBe(frozen.body)
            expect(frozen.deliveryId).toBe(previewId)
            expect(peer.bodies()[0]).toContain(previewId!)
            if (httpPeer)
                expect(httpPeer.requests[0]).toMatchObject({
                    id: previewId,
                    path: '/events',
                })
            await expect(
                send.getByRole('button', { name: '确认发送一次', exact: true }),
            ).toHaveCount(0)
            let status: Diagnostics = await (
                await page.request.get(root + '/runtime/status')
            ).json()
            expect(Number(status.queue.committedRecords)).toBe(0)
            await page.reload()
            expect(peer.bodies()).toHaveLength(1)
            await page.goto('/configuration')
            await page
                .getByRole('button', { name: '查看应用变更清单', exact: true })
                .click()
            const review = page.getByRole('region', { name: '应用前确认' })
            await review
                .getByLabel('我已检查变更及影响，确认应用这份草稿')
                .check()
            await review
                .getByRole('button', { name: '确认应用配置', exact: true })
                .click()
            await expect.poll(() => peer.bodies().length).toBe(2)
            expect(peer.bodies()[1]).toContain('"scaled":9007199254740994')
            await expect
                .poll(async () => {
                    status = await (
                        await page.request.get(root + '/runtime/status')
                    ).json()
                    return [
                        Number(status.queue.committedRecords),
                        Number(status.queue.deletedRecords),
                    ]
                })
                .toEqual([1, 1])
            await logout(page)
            await login(page, 'output-viewer', true)
            await open()
            await node(id).click()
            await expect(precision).toHaveValue('9007199254740993')
            await expect(precision).toBeDisabled()
            await expect(accept).toBeDisabled()
            await expect(
                page.getByRole('button', { name: '保存路线草稿', exact: true }),
            ).toBeDisabled()
            await expect(
                preview.getByRole('button', {
                    name: '预览当前图（不发送）',
                    exact: true,
                }),
            ).toBeDisabled()
            await expect(send).toHaveCount(0)
            for (const path of [
                '/routes/compile',
                '/routes/preview',
                '/routes/test-send/prepare',
                '/routes/test-send/confirm',
            ])
                expect(
                    (
                        await page.request.post(root + path, {
                            headers: await headers(),
                            data: {},
                        })
                    ).status(),
                    path,
                ).toBe(403)
            expect(
                (
                    await page.request.put(`${root}/routes/${route.route.id}`, {
                        headers: await headers(),
                        data: {},
                    })
                ).status(),
            ).toBe(403)
            await context.close()
            expect(await host.stop()).toBe(0)
            expect(peer.bodies()).toHaveLength(2)
            // A normal restart of the unmodified product has no test module. It must
            // preserve the graph and degrade only this unit, not send a fallback.
            if (protocol === 'http') {
                host = await startOwnedHost(data, 0, [], httpAddress)
                const missing = await browser.newContext({
                    baseURL: host.url,
                    viewport: { width: 1700, height: 1100 },
                })
                try {
                    const p = await missing.newPage()
                    await login(p, 'output-admin')
                    await p.goto('/routes')
                    await p
                        .getByRole('button', {
                            name: '打开 Extended output',
                            exact: true,
                        })
                        .click()
                    await p
                        .locator(`.react-flow__node[data-id="${id}"]`)
                        .click()
                    await expect(
                        p.getByText(
                            '此类型或版本未安装可用表单；原配置保留，不会自动替换版本。',
                            { exact: true },
                        ),
                    ).toBeVisible()
                    const historical = await (
                        await p.request.get(`${root}/routes/${route.route.id}`)
                    ).text()
                    expect(historical).toBe(savedText)
                    await expect
                        .poll(async () => {
                            const current: Diagnostics = await (
                                await p.request.get(root + '/runtime/status')
                            ).json()
                            expect(current.coordinator.state).toBe('active')
                            const output = current.runtime.units.find(
                                (unit) => unit.id.kind === 'output.testhttp',
                            )
                            expect(output?.state).toBe('buildFailed')
                            return Number(
                                current.runtime.units.find(
                                    (unit) =>
                                        unit.id.kind === 'input.simulator',
                                )?.reads?.readsCompleted ?? 0,
                            )
                        })
                        .toBeGreaterThanOrEqual(1)
                } finally {
                    await missing.close()
                    expect(await host.stop()).toBe(0)
                }
                expect(peer.bodies()).toHaveLength(2) // stopped/drained, not an absence sleep
            }
            host = await startOwnedHost(data, 0, [], httpAddress, true)
            await expect.poll(() => peer.bodies().length).toBe(3)
            expect(peer.bodies()[2]).toContain('"scaled":9007199254740994')
        } finally {
            await context.close()
            await host.stop()
            await peer.close()
            await rm(data, { recursive: true, force: true })
        }
    })
}
