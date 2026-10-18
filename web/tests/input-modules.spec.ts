import { test, expect } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'
import type { Diagnostics } from '../src/features/diagnostics/api'

const root = '/api/edge-admin/v1'
test('installed input: page-created exact parameters, graph, real HTTP and missing-module restart', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-input-web-'))
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
    const httpAddress = new URL(
        process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
    ).hostname
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
        await page.getByLabel('账号', { exact: true }).fill('input-admin')
        for (const name of ['密码', '确认密码'])
            await page
                .getByLabel(name, { exact: true })
                .fill('Input-browser-7183!')
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
                        localPorts: [...policy.localPorts, address.port],
                    },
                })
            ).status(),
        ).toBe(200)
        // Output is a fixture; the device, input parameters and input graph are created through the UI.
        const connection = await post('/connections', {
            expectedVersion: '0',
            key: 'receiver',
            name: 'Receiver',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${address.port}/` },
        })
        await page.goto('/devices')
        await page
            .getByRole('button', { name: '新建设备', exact: true })
            .click()
        await page.getByLabel('设备 key', { exact: true }).fill('sensor')
        await page.getByLabel('设备名称', { exact: true }).fill('Module sensor')
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('module')
        await page
            .getByRole('combobox', { name: '输入模块类型', exact: true })
            .selectOption('input.testscalar')
        const deviceSaved = page.waitForResponse(
            (r) =>
                r.request().method() === 'POST' && r.url().endsWith('/devices'),
        )
        await page
            .getByRole('button', { name: '保存设备草稿', exact: true })
            .click()
        expect((await deviceSaved).status()).toBe(200)
        const device = (await (await deviceSaved).json()).device
        await page.getByRole('button', { name: /Module sensor/ }).click()
        await page
            .getByRole('link', { name: '数据来源与点位', exact: true })
            .click()
        await page
            .getByRole('button', { name: '新建数据来源', exact: true })
            .click()
        await page.getByLabel('来源 key', { exact: true }).fill('values')
        await page.getByLabel('来源名称', { exact: true }).fill('Values')
        const save = page.getByRole('button', {
            name: '保存来源草稿',
            exact: true,
        })
        await expect(save).toBeDisabled()
        const editor = page.getByRole('region', {
            name: '扩展输入参数',
            exact: true,
        })
        await editor
            .getByRole('combobox', { name: '输入模块版本', exact: true })
            .selectOption('3')
        const value = editor.getByLabel('初值', { exact: true })
        await expect(value).toHaveValue('')
        await expect(
            editor.getByText('此参数必填。', { exact: true }),
        ).toBeVisible()
        await value.fill('-1')
        await expect(
            editor.getByText('数字不在声明范围内。', { exact: true }),
        ).toBeVisible()
        await expect(save).toBeDisabled()
        await value.fill('9007199254740993')
        const metadata = '**/input-modules'
        await page.route(metadata, (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{}',
            }),
        )
        const refresh = editor.getByRole('button', {
            name: '重新读取输入模块',
            exact: true,
        })
        await refresh.click()
        await expect(save).toBeDisabled()
        await expect(refresh).toBeEnabled()
        await expect(value).toHaveValue('9007199254740993')
        await page.unroute(metadata)
        await page.route(metadata, async (route) => {
            const response = await route.fetch()
            await route.fulfill({ response, json: [] })
        })
        await refresh.click()
        await expect(editor.getByRole('alert')).toContainText('所选版本不可用')
        await expect(value).toHaveValue('9007199254740993')
        await expect(value).toBeDisabled()
        await expect(save).toBeDisabled()
        await page.unroute(metadata)
        await refresh.click()
        await expect(value).toBeEnabled()
        await expect(value).toHaveValue('9007199254740993')
        const sourceSaved = page.waitForResponse(
            (r) =>
                r.request().method() === 'POST' &&
                r.url().endsWith(`/devices/${device.id}/inputs`),
        )
        await save.click()
        const response = await sourceSaved
        expect(response.status(), await response.text()).toBe(200)
        expect(response.request().postData()).toContain(
            '"value":9007199254740993',
        )
        expect(await response.text()).toContain('"value":9007199254740993')
        const source = await response.json()
        await page
            .getByRole('button', {
                name: 'Values (values) · 扩展输入',
                exact: true,
            })
            .click()
        await expect(value).toHaveValue('9007199254740993')
        expect(bodies).toHaveLength(0)
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'extended',
            name: 'Input route',
            enabled: true,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: { connectionId: connection.connection.id },
                    },
                ],
                edges: [],
                layout: { nodes: { out: { x: 600, y: 200 } } },
            },
        })
        await page.goto('/routes')
        await page
            .getByRole('button', { name: '打开 Input route', exact: true })
            .click()
        await page
            .getByRole('button', { name: '测试标量输入 (v3)', exact: true })
            .click()
        const graph = JSON.parse(
            (await page.getByTestId('graph-document').textContent())!,
        )
        const id = graph.definition.nodes.find(
            (node: { kind: string }) => node.kind === 'input.testscalar',
        ).id
        const node = (id: string) =>
            page.locator(`.react-flow__node[data-id="${id}"]`)
        await page
            .getByRole('button', { name: '适应画布', exact: true })
            .click()
        await node(id).click()
        await page
            .getByLabel('Module sensor / Values (values)', { exact: true })
            .check()
        await node(id)
            .locator('.react-flow__handle.source')
            .dragTo(node('out').locator('.react-flow__handle.target'))
        const routeSaved = page.waitForResponse(
            (r) =>
                r.request().method() === 'PUT' &&
                r.url().endsWith(`/routes/${route.route.id}`),
        )
        await page
            .getByRole('button', { name: '保存路线草稿', exact: true })
            .click()
        expect((await routeSaved).status()).toBe(200)
        const stored = await (
            await page.request.get(`${root}/routes/${route.route.id}`)
        ).json()
        expect(
            stored.route.graph.nodes.find(
                (node: { type: string }) => node.type === 'input.testscalar',
            ),
        ).toMatchObject({
            version: 3,
            config: { sourceIds: [source.input.id] },
        })
        await page
            .getByLabel('预览数据来源', { exact: true })
            .selectOption('input.testscalar:' + source.input.id)
        const preview = page.getByRole('region', {
            name: '路线样本预览',
            exact: true,
        })
        await expect(preview).toContainText('Module sensor / Values (values)')
        await preview
            .getByLabel('手工 JSON 样本（不保存）', { exact: true })
            .fill('{"value":9007199254740993}')
        await preview
            .getByRole('button', { name: '预览当前图（不发送）', exact: true })
            .click()
        await expect(preview).toContainText('9007199254740993')
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
                    body.includes('"value":9007199254740993'),
                ),
            )
            .toBe(true)
        await expect
            .poll(async () => {
                const status: Diagnostics = await (
                    await page.request.get(root + '/runtime/status')
                ).json()
                return {
                    committed: Number(status.queue.committedRecords),
                    deleted: Number(status.queue.deletedRecords),
                }
            })
            .toEqual({ committed: 1, deleted: 1 })
        await context.close()
        expect(await host.stop()).toBe(0)
        const beforeRestart = bodies.length
        // The original product process has no test module. It keeps administration
        // and the unrelated output running, without inventing another input.
        host = await startOwnedHost(data, 0, [], httpAddress)
        const missingContext = await browser.newContext({ baseURL: host.url })
        try {
            const missing = await missingContext.newPage()
            await missing.goto('/')
            await missing
                .getByLabel('账号', { exact: true })
                .fill('input-admin')
            await missing
                .getByLabel('密码', { exact: true })
                .fill('Input-browser-7183!')
            await missing
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await expect(
                missing.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            await missing.goto('/diagnostics')
            const units = missing.getByRole('region', {
                name: '运行单元',
                exact: true,
            })
            await expect(
                units
                    .getByRole('row')
                    .filter({ hasText: 'input.testscalar/sensor/values' }),
            ).toContainText('构建失败')
            await expect(
                units
                    .getByRole('row')
                    .filter({ hasText: 'output.http/extended/' }),
            ).toContainText('运行中')
            await missing.goto(`/devices/${device.id}/inputs`)
            await missing
                .getByRole('button', {
                    name: 'Values (values) · 扩展输入',
                    exact: true,
                })
                .click()
            await expect(
                missing.getByRole('region', { name: '扩展输入参数' }),
            ).toContainText('所选版本不可用')
            await expect(
                missing.getByRole('button', {
                    name: '保存来源草稿',
                    exact: true,
                }),
            ).toBeDisabled()
            expect(
                await (
                    await missing.request.get(
                        `${root}/devices/${device.id}/inputs`,
                    )
                ).text(),
            ).toContain('"value":9007199254740993')
        } finally {
            await missingContext.close()
            expect(await host.stop()).toBe(0)
        }
        expect(bodies).toHaveLength(beforeRestart)
        host = await startOwnedHost(data, 0, [], httpAddress, true)
        await expect
            .poll(() =>
                bodies
                    .slice(beforeRestart)
                    .some((body) => body.includes('"value":9007199254740993')),
            )
            .toBe(true)
    } finally {
        await context.close().catch(() => {})
        await host.stop()
        await new Promise<void>((resolve, reject) =>
            receiver.close((error) => (error ? reject(error) : resolve())),
        )
        await rm(data, { recursive: true, force: true })
    }
})
