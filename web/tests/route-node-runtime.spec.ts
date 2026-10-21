import { test, expect, type Page } from '@playwright/test'
import { startWorkflowGateway } from './support/gateway-workflow'

const root = '/api/edge-admin/v1'
const password = 'Node-runtime-fixture-7284!'
test.use({ actionTimeout: 15_000, viewport: { width: 1800, height: 1200 } })

async function apply(page: Page) {
    await page.getByRole('link', { name: /配置应用$/ }).click()
    await page
        .getByRole('button', { name: '查看应用变更清单', exact: true })
        .click()
    const review = page.getByRole('region', { name: '应用前确认', exact: true })
    await review.getByLabel('我已检查变更及影响，确认应用这份草稿').check()
    const accepted = page.waitForResponse(
        (response) =>
            response.request().method() === 'POST' &&
            response.url().endsWith('/configuration/apply'),
    )
    await review
        .getByRole('button', { name: '确认应用配置', exact: true })
        .click()
    const response = await accepted
    expect(response.status()).toBe(202)
    const operation = await response.json()
    await expect(
        page.locator(`[data-operation-id="${operation.id}"]`),
    ).toContainText('配置已提交并激活。后续运行健康情况仍须查看诊断。')
}

// API-created drafts are fixtures, not evidence for the separate no-JSON gate.
// Runtime, queue and receiver are real; only the explicit query-500 is injected.
test('node metadata follows real paths, preview errors and versions without leaking bodies or losing edits', async ({
    page,
    browser,
}) => {
    test.setTimeout(120_000)
    const gateway = await startWorkflowGateway()
    const node = (id: string) =>
        page.locator(`.react-flow__node[data-id="${id}"]`)
    page.on('dialog', (dialog) => dialog.accept())
    try {
        await page.goto(gateway.url)
        await page.getByLabel('账号', { exact: true }).fill('node-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const session = await (
            await page.request.get(gateway.url + root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        async function post(path: string, data: unknown) {
            const response = await page.request.post(
                gateway.url + root + path,
                { headers, data },
            )
            expect(response.status(), await response.text()).toBe(200)
            return response.json()
        }
        const connection = await post('/connections', {
            expectedVersion: '0',
            key: 'receiver',
            name: 'Receiver',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${gateway.httpPort}/` },
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
            simulator: {
                periodMs: 1000,
                sampleJson: '{"value":"private-nonnumeric"}',
            },
        })
        const graph = {
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
                                target: 'value',
                                sourceField: 'value',
                                type: 'integer',
                                required: true,
                            },
                        ],
                    },
                },
                ...['good', 'bad'].map((id) => ({
                    id,
                    type: 'output.http',
                    version: 1,
                    name: id,
                    config: {
                        connectionId: connection.connection.id,
                        path: '/' + id,
                    },
                })),
            ],
            edges: [
                { source: 'in', target: 'good' },
                { source: 'in', target: 'map' },
                { source: 'map', target: 'bad' },
            ],
            layout: {
                nodes: {
                    in: { x: 20, y: 30 },
                    map: { x: 280, y: 30 },
                    bad: { x: 540, y: 30 },
                    good: { x: 280, y: 310 },
                },
            },
        }
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'node-route',
            name: 'Node route',
            enabled: true,
            graph,
        })
        await post('/users', {
            accountName: 'node-viewer',
            displayName: 'Node viewer',
            role: 'viewer',
            temporaryPassword: password,
        })
        await page.goto(gateway.url + '/routes')
        await page
            .getByRole('button', { name: '打开 Node route', exact: true })
            .click()
        await expect(node('good')).toContainText('未核对有效版节点状态')
        // Real preview failure is marked on the failing node and cleared as soon as the sample changes.
        await page
            .getByLabel('预览数据来源', { exact: true })
            .selectOption(`input.simulator:${source.input.id}`)
        await page
            .getByLabel('手工 JSON 样本（不保存）', { exact: true })
            .fill('{}')
        await page
            .getByRole('button', { name: '预览当前图（不发送）', exact: true })
            .click()
        await expect(node('map').locator('[data-node-error]')).toHaveCount(1)
        await expect(page.locator('.route-edge-error')).toHaveCount(2)
        await expect(node('good').locator('[data-node-error]')).toHaveCount(0)
        expect(gateway.deliveries).toHaveLength(0)
        await page
            .getByLabel('手工 JSON 样本（不保存）', { exact: true })
            .fill('{"value":42}')
        await expect(node('map').locator('[data-node-error]')).toHaveCount(0)
        await expect(page.locator('.route-edge-error')).toHaveCount(0)
        await apply(page)
        await page.goto(gateway.url + '/routes')
        await page
            .getByRole('button', { name: '打开 Node route', exact: true })
            .click()
        await expect(node('in')).toContainText('输入循环运行中')
        await expect(node('good')).toContainText('已入队（不代表发送成功）')
        await expect(node('map')).toContainText('转换失败')
        await expect(node('bad')).toContainText('上游失败，未执行')
        await expect
            .poll(
                () =>
                    gateway.deliveries.filter((item) => item.path === '/good')
                        .length,
            )
            .toBeGreaterThan(0)
        expect(
            gateway.deliveries.filter((item) => item.path === '/bad'),
        ).toHaveLength(0)
        await expect(page.locator('body')).not.toContainText(
            'private-nonnumeric',
        )
        const metadata = await (
            await page.request.get(
                `${gateway.url}${root}/routes/${route.route.id}/runtime?expectedVersion=${route.version}`,
            )
        ).json()
        expect(JSON.stringify(metadata)).not.toContain('private-nonnumeric')
        expect(
            metadata.nodes.find((value: { id: string }) => value.id === 'map')
                .failedPaths,
        ).toBe(1)
        // Query failure removes old node observations; the open editor is not replaced.
        const pattern = '**/routes/*/runtime?*'
        await page.route(pattern, (intercept) =>
            intercept.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await page
            .getByRole('button', { name: '重新核对节点状态', exact: true })
            .click()
        await expect(node('good')).toContainText('未核对有效版节点状态')
        await expect(node('good')).not.toContainText('已入队')
        await page
            .getByLabel('路线名称', { exact: true })
            .fill('Unsaved node edit')
        await page.unroute(pattern)
        await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
            'Unsaved node edit',
        )
        await expect(node('good')).toContainText('未核对有效版节点状态')
        await page.getByLabel('路线名称', { exact: true }).fill('Node route')
        await expect(node('good')).toContainText('已入队（不代表发送成功）')

        // A viewer reads the same bounded metadata, not message bodies or write controls.
        const viewerContext = await browser.newContext({
            viewport: { width: 1800, height: 1200 },
        })
        try {
            const viewer = await viewerContext.newPage()
            await viewer.goto(gateway.url + '/routes')
            await viewer.getByLabel('账号', { exact: true }).fill('node-viewer')
            await viewer.getByLabel('密码', { exact: true }).fill(password)
            await viewer
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await viewer.getByLabel('原密码', { exact: true }).fill(password)
            await viewer
                .getByLabel('新密码', { exact: true })
                .fill('Node-viewer-changed-7284!')
            const changedPassword = viewer.waitForResponse(
                (response) =>
                    response.request().method() === 'POST' &&
                    response.url().endsWith('/auth/password'),
            )
            await viewer
                .getByRole('button', { name: '保存新密码', exact: true })
                .click()
            expect((await changedPassword).status()).toBe(200)
            await expect(
                viewer.getByRole('navigation', { name: '主导航', exact: true }),
            ).toBeVisible()
            await viewer.goto(gateway.url + '/routes')
            await viewer
                .getByRole('button', { name: '打开 Node route', exact: true })
                .click()
            await expect(
                viewer.locator('.react-flow__node[data-id="good"]'),
            ).toContainText('已入队（不代表发送成功）')
            await expect(
                viewer.getByRole('button', {
                    name: '保存路线草稿',
                    exact: true,
                }),
            ).toBeDisabled()
            await expect(viewer.locator('body')).not.toContainText(
                'private-nonnumeric',
            )
        } finally {
            await viewerContext.close()
        }

        // Saved graph changes must not reuse the old node's runtime identity.
        const changed = await page.request.put(
            `${gateway.url}${root}/routes/${route.route.id}`,
            {
                headers,
                data: {
                    expectedVersion: route.version,
                    key: 'node-route',
                    name: 'Node route',
                    enabled: true,
                    graph: {
                        ...graph,
                        nodes: graph.nodes.map((node) =>
                            node.id === 'good'
                                ? { ...node, id: 'new-good' }
                                : node,
                        ),
                        edges: graph.edges.map((edge) =>
                            edge.target === 'good'
                                ? { ...edge, target: 'new-good' }
                                : edge,
                        ),
                        layout: {
                            nodes: {
                                in: graph.layout.nodes.in,
                                map: graph.layout.nodes.map,
                                bad: graph.layout.nodes.bad,
                                'new-good': graph.layout.nodes.good,
                            },
                        },
                    },
                },
            },
        )
        expect(changed.status(), await changed.text()).toBe(200)
        await expect(node('good')).toContainText('未核对有效版节点状态')
        await page
            .getByRole('button', { name: '打开 Node route', exact: true })
            .click()
        await expect(node('new-good')).toContainText('未核对有效版节点状态')
        await apply(page)
        await page.goto(gateway.url + '/routes')
        await page
            .getByRole('button', { name: '打开 Node route', exact: true })
            .click()
        await expect(node('new-good')).toContainText('已入队（不代表发送成功）')
        await expect(node('good')).toHaveCount(0)
    } finally {
        await gateway.close()
    }
})
