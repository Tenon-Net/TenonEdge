import { test, expect } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'
import type { Diagnostics } from '../src/features/diagnostics/api'
const root = '/api/edge-admin/v1'
// Only the test assembly adds a compiled module. No mocked drafts, preview,
// application or network outputs; metadata failure below is explicitly injected.
test('installed transform: automatic form, draft, real HTTP, missing-module isolation and restart', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-transform-web-'))
    const bodies: string[] = []
    const receiver = createServer((request, response) => {
        const chunks: Buffer[] = []
        request.on('data', (chunk) => chunks.push(chunk))
        request.on('end', () => {
            bodies.push(Buffer.concat(chunks).toString('utf8'))
            response.writeHead(204).end()
        })
    })
    await new Promise<void>((resolve) =>
        receiver.listen(0, '127.0.0.1', resolve),
    )
    const address = receiver.address()
    if (!address || typeof address === 'string') throw new Error('receiver')
    const port = address.port
    let host = await startOwnedHost(data, 0, [], '127.0.0.1', true)
    const context = await browser.newContext({
        baseURL: host.url,
        viewport: { width: 1700, height: 1100 },
    })
    try {
        const page = await context.newPage()
        page.on('dialog', (dialog) => dialog.accept())
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('transform-admin')
        for (const name of ['密码', '确认密码'])
            await page
                .getByLabel(name, { exact: true })
                .fill('Transform-browser-7183!')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        async function post(path: string, body: unknown) {
            const response = await page.request.post(root + path, {
                headers,
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
                    headers,
                    data: {
                        expectedVersion: policy.version,
                        networks: policy.networks,
                        localPorts: [...policy.localPorts, port],
                    },
                })
            ).status(),
        ).toBe(200)
        // Input and output are fixtures; the browser creates and connects the extension.
        const connection = await post('/connections', {
            expectedVersion: '0',
            key: 'receiver',
            name: 'Receiver',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${port}/` },
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
            // One immediate sample per process start; avoid another sampling cycle
            // racing the shutdown/no-new-delivery assertions below.
            simulator: {
                periodMs: 86_400_000,
                sampleJson: '{"value":9007199254740993}',
            },
        })
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'extended',
            name: 'Extension route',
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
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: { connectionId: connection.connection.id },
                    },
                ],
                edges: [],
                layout: {
                    nodes: { in: { x: 20, y: 40 }, out: { x: 650, y: 40 } },
                },
            },
        })
        await page.goto('/routes')
        await page
            .getByRole('button', { name: '打开 Extension route', exact: true })
            .click()
        await page
            .getByRole('button', { name: '测试增量 (v1)', exact: true })
            .click()
        const form = page.getByRole('form', { name: '转换参数', exact: true })
        const value = form.getByLabel('增量', { exact: true })
        await expect(value).toHaveValue('1')
        await page.getByRole('button', { name: '撤销', exact: true }).click()
        await expect(form).toHaveCount(0)
        await page.getByRole('button', { name: '重做', exact: true }).click()
        const graph = JSON.parse(
            (await page.getByTestId('graph-document').textContent())!,
        )
        const id = graph.definition.nodes.find(
            (n: { kind: string }) => n.kind === 'transform.test-offset',
        ).id
        const node = (id: string) =>
            page.locator(`.react-flow__node[data-id="${id}"]`)
        await node(id).click()
        await value.fill('101')
        await expect(
            form.getByText('数字不在声明范围内。', { exact: true }),
        ).toBeVisible()
        const metadata = '**/transform-modules'
        await page.route(metadata, async (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{}',
            }),
        )
        await page
            .getByRole('button', { name: '重新读取转换模块', exact: true })
            .click()
        await expect(
            form.getByRole('button', { name: '确认节点配置', exact: true }),
        ).toBeDisabled()
        await expect(value).toHaveValue('101')
        await expect(
            page.getByRole('button', { name: '重新读取转换模块', exact: true }),
        ).toBeEnabled()
        await page.unroute(metadata)
        // Successful metadata refresh with the selected version missing must also
        // keep the pending form, rather than unmount it or silently substitute map.
        await page.route(metadata, async (route) => {
            const response = await route.fetch()
            const modules = await response.json()
            await route.fulfill({
                response,
                json: modules.filter(
                    (module: { kind: string }) =>
                        module.kind !== 'transform.test-offset',
                ),
            })
        })
        await page
            .getByRole('button', { name: '重新读取转换模块', exact: true })
            .click()
        await expect(
            page.getByText(
                '此类型或版本未安装可用表单；原配置保留，不会自动替换版本。',
                { exact: true },
            ),
        ).toBeVisible()
        await expect(value).toHaveValue('101')
        await expect(
            form.getByRole('button', { name: '确认节点配置', exact: true }),
        ).toBeDisabled()
        await page.unroute(metadata)
        await page
            .getByRole('button', { name: '重新读取转换模块', exact: true })
            .click()
        await expect(value).toBeEnabled()
        await value.fill('')
        await expect(
            form.getByText('此参数必填。', { exact: true }),
        ).toBeVisible()
        await form
            .getByRole('button', { name: '确认节点配置', exact: true })
            .click()
        // Added module declarations can enlarge the palette and clip a new node.
        // Use the real canvas control before dragging, just as the input/output tests do.
        await page
            .getByRole('button', { name: '适应画布', exact: true })
            .click()
        for (const [from, to] of [
            ['in', id],
            [id, 'out'],
        ])
            await node(from)
                .locator('.react-flow__handle.source')
                .dragTo(node(to).locator('.react-flow__handle.target'))
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
            incomplete.route.graph.nodes.find(
                (n: { type: string }) => n.type === 'transform.test-offset',
            ).config,
        ).toEqual({})
        expect(
            (
                await post('/routes/compile', {
                    expectedVersion: incomplete.version,
                })
            ).compiled,
        ).toBe(false)
        await node(id).click()
        await value.fill('2')
        await form
            .getByRole('button', { name: '确认节点配置', exact: true })
            .click()
        await save()
        await page
            .getByLabel('预览数据来源', { exact: true })
            .selectOption('input.simulator:' + source.input.id)
        const preview = page.getByRole('region', {
            name: '路线样本预览',
            exact: true,
        })
        await preview
            .getByLabel('手工 JSON 样本（不保存）', { exact: true })
            .fill('{"value":9007199254740993}')
        await preview
            .getByRole('button', { name: '预览当前图（不发送）', exact: true })
            .click()
        await expect(preview).toContainText('9007199254740995')
        expect(bodies).toHaveLength(0)
        await page.goto('/configuration')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        const review = page.getByRole('region', { name: '应用前确认' })
        await review.getByLabel('我已检查变更及影响，确认应用这份草稿').check()
        await review
            .getByRole('button', { name: '确认应用配置', exact: true })
            .click()
        await expect
            .poll(() =>
                bodies.some((body) =>
                    body.includes('"value":9007199254740995'),
                ),
            )
            .toBe(true)
        await expect
            .poll(async () => {
                const response = await page.request.get(
                    root + '/runtime/status',
                )
                expect(response.status()).toBe(200)
                const status: Diagnostics = await response.json()
                return {
                    committed: Number(status.queue.committedRecords),
                    deleted: Number(status.queue.deletedRecords),
                }
            })
            .toEqual({ committed: 1, deleted: 1 })
        await context.close()
        expect(await host.stop()).toBe(0)
        const beforeRestart = bodies.length
        // Restart the unmodified product process, which does not register the test
        // transform. The input stays alive; this path must not send untransformed data.
        host = await startOwnedHost(data)
        const missingContext = await browser.newContext({ baseURL: host.url })
        try {
            const missingPage = await missingContext.newPage()
            await missingPage.goto('/')
            await missingPage
                .getByLabel('账号', { exact: true })
                .fill('transform-admin')
            await missingPage
                .getByLabel('密码', { exact: true })
                .fill('Transform-browser-7183!')
            await missingPage
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await expect(
                missingPage.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            await missingPage.goto('/diagnostics')
            await expect(
                missingPage.getByRole('heading', {
                    name: '运行诊断',
                    exact: true,
                }),
            ).toBeVisible()
            const units = missingPage.getByRole('region', {
                name: '运行单元',
                exact: true,
            })
            await expect(
                units
                    .getByRole('row')
                    .filter({ hasText: 'input.simulator/meter/values' }),
            ).toContainText('运行中')
            await expect(
                units
                    .getByRole('row')
                    .filter({ hasText: 'output.http/extended/' }),
            ).toContainText('构建失败')
            await expect
                .poll(async () => {
                    const response = await missingPage.request.get(
                        root + '/runtime/status',
                    )
                    expect(response.status()).toBe(200)
                    const status: Diagnostics = await response.json()
                    expect(status.coordinator.state).toBe('active')
                    expect(Number(status.queue.committedRecords)).toBe(0)
                    return Number(
                        status.runtime.units.find(
                            (unit) => unit.id.kind === 'input.simulator',
                        )?.reads?.readsCompleted ?? 0,
                    )
                })
                .toBeGreaterThanOrEqual(1)
        } finally {
            await missingContext.close()
            expect(await host.stop()).toBe(0)
        }
        // Stop/drain is the boundary, not a timed absence check or an arbitrary sleep.
        expect(bodies).toHaveLength(beforeRestart)
        host = await startOwnedHost(data, 0, [], '127.0.0.1', true)
        await expect
            .poll(() =>
                bodies
                    .slice(beforeRestart)
                    .some((body) => body.includes('"value":9007199254740995')),
            )
            .toBe(true)
    } finally {
        await context.close()
        await host.stop()
        await new Promise<void>((resolve, reject) =>
            receiver.close((error) => (error ? reject(error) : resolve())),
        )
        await rm(data, { recursive: true, force: true })
    }
})
