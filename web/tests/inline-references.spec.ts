import { expect, test as base, type Locator, type Page } from '@playwright/test'
import { startWorkflowGateway } from './support/gateway-workflow'

const root = '/api/edge-admin/v1'
const password = 'Inline-fixture-9347!'
const test = base.extend<{
    gateway: Awaited<ReturnType<typeof startWorkflowGateway>>
}>({
    gateway: async ({ page }, use) => {
        const gateway = await startWorkflowGateway()
        try {
            await page.goto(gateway.url)
            await page.getByLabel('账号', { exact: true }).fill('inline-admin')
            await page.getByLabel('密码', { exact: true }).fill(password)
            await page.getByLabel('确认密码', { exact: true }).fill(password)
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            await page.goto(gateway.url + '/routes')
            await use(gateway)
        } finally {
            await gateway.close()
        }
    },
})
test.use({ actionTimeout: 15_000, viewport: { width: 1800, height: 1200 } })

async function newRoute(page: Page) {
    await page.getByRole('button', { name: '新建路线', exact: true }).click()
    await page.getByLabel('路线标识', { exact: true }).fill('inline-route')
    await page.getByLabel('路线名称', { exact: true }).fill('本地路线')
}
async function addNode(page: Page, name: string, x: number) {
    const canvas = page.getByLabel('路线画布', { exact: true })
    await canvas.scrollIntoViewIfNeeded()
    await page
        .getByRole('button', { name, exact: true })
        .dragTo(canvas, { targetPosition: { x, y: 150 } })
}
async function connectionDrawer(page: Page) {
    await page
        .getByRole('button', { name: '在侧栏新建连接', exact: true })
        .click()
    const drawer = page.getByRole('dialog', {
        name: '画布内配置引用',
        exact: true,
    })
    await expect(drawer).toBeVisible()
    return drawer
}
async function createConnection(
    drawer: Locator,
    key: string,
    kind: 'modbus' | 'http' | 'mqtt',
    port: number,
    tls = false,
) {
    await drawer.getByRole('button', { name: '新建连接', exact: true }).click()
    await drawer.getByLabel('连接 key', { exact: true }).fill(key)
    await drawer.getByLabel('连接名称', { exact: true }).fill(key)
    await drawer
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption(kind)
    if (kind === 'http')
        await drawer
            .getByLabel('HTTP 基础地址', { exact: true })
            .fill(`${tls ? 'https' : 'http'}://127.0.0.1:${port}/`)
    else {
        await drawer.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
        await drawer.getByLabel('端口', { exact: true }).fill(String(port))
        if (tls) await drawer.getByLabel('启用 TLS', { exact: true }).check()
    }
    await drawer
        .getByRole('button', { name: '保存到草稿', exact: true })
        .click()
    await expect(drawer.getByRole('row').filter({ hasText: key })).toBeVisible()
}
async function closeDrawer(page: Page, drawer: Locator) {
    await drawer
        .getByRole('button', { name: '返回画布并核对引用', exact: true })
        .click()
    await expect(drawer).toHaveCount(0)
    await expect(
        page.getByRole('button', { name: '保存路线草稿', exact: true }),
    ).toBeEnabled()
}
async function saveRoute(page: Page) {
    await page
        .getByRole('button', { name: '保存路线草稿', exact: true })
        .click()
    await expect(
        page.getByText('路线草稿已保存，未应用。', { exact: true }),
    ).toBeVisible()
}

test('inline references: unsaved canvas creates connection, device and points, preserves undo/sample/exact config and really sends', async ({
    page,
    gateway,
}, testInfo) => {
    test.setTimeout(150_000)
    const errors: string[] = []
    page.on('pageerror', (error) => errors.push(error.message))
    await newRoute(page)
    await page.getByLabel('参与静态编译（草稿启用）', { exact: true }).check()
    await addNode(page, 'Modbus 输入', 120)
    await page.getByLabel('节点名称', { exact: true }).fill('未保存的采集节点')
    const drawer = await connectionDrawer(page)
    await page.keyboard.press('Escape')
    await expect(drawer).toBeVisible()
    expect(
        await drawer.evaluate((element) =>
            element.contains(document.activeElement),
        ),
    ).toBe(true)
    await expect(page.getByLabel('路线名称', { exact: true })).toBeDisabled()
    await createConnection(drawer, 'inline-plc', 'modbus', gateway.modbusPort)
    await closeDrawer(page, drawer)
    await expect(
        page.getByRole('button', { name: '在侧栏新建连接', exact: true }),
    ).toBeFocused()
    await expect(page.getByLabel('节点名称', { exact: true })).toHaveValue(
        '未保存的采集节点',
    )
    // No canvas reload: the rename is still undoable after a global draft version change.
    await page.getByRole('button', { name: '撤销', exact: true }).click()
    await page.locator('.react-flow__node').first().click()
    await expect(page.getByLabel('节点名称', { exact: true })).toHaveValue(
        'Modbus 输入',
    )
    await page.getByRole('button', { name: '重做', exact: true }).click()
    await page.locator('.react-flow__node').first().click()
    await expect(page.getByLabel('节点名称', { exact: true })).toHaveValue(
        '未保存的采集节点',
    )
    await page
        .getByRole('button', { name: '在侧栏新建设备 / 来源', exact: true })
        .click()
    await drawer.getByRole('button', { name: '新建设备', exact: true }).click()
    await drawer.getByLabel('设备名称', { exact: true }).fill('侧栏电表')
    await drawer.getByLabel('设备 key', { exact: true }).fill('inline-meter')
    await drawer
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption('modbus')
    await drawer
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({ label: 'inline-plc (inline-plc)' })
    await drawer.getByLabel('UnitId', { exact: true }).fill('1')
    await drawer
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await drawer.getByRole('button', { name: /侧栏电表 inline-meter/ }).click()
    await drawer
        .getByRole('button', { name: '数据来源与点位', exact: true })
        .click()
    await drawer
        .getByRole('button', { name: '新建采集组', exact: true })
        .click()
    await drawer.getByLabel('来源 key', { exact: true }).fill('readings')
    await drawer.getByLabel('来源名称', { exact: true }).fill('侧栏读数')
    await drawer.getByLabel('采集周期（毫秒）', { exact: true }).fill('1000')
    await drawer.getByRole('button', { name: '添加空行', exact: true }).click()
    await drawer.getByLabel('第 1 行 key', { exact: true }).fill('energy')
    await drawer.getByLabel('第 1 行 地址', { exact: true }).fill('0')
    await drawer
        .getByRole('combobox', { name: '第 1 行 数据类型', exact: true })
        .selectOption('uint64')
    await drawer
        .getByRole('combobox', { name: '第 1 行 字节序', exact: true })
        .selectOption('ABCD')
    await drawer
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    await expect(drawer.getByRole('status')).toHaveText(
        '来源与点表已保存到网关草稿，尚未应用。',
    )
    // File selection is unsaved work even with no point editor open.
    await drawer.getByLabel('点表 CSV 文件', { exact: true }).setInputFiles({
        name: 'pending.csv',
        mimeType: 'text/csv',
        buffer: Buffer.from('not imported'),
    })
    page.once('dialog', (dialog) => dialog.dismiss())
    await drawer
        .getByRole('button', { name: '返回侧栏设备列表', exact: true })
        .click()
    await expect(
        drawer.getByLabel('点表 CSV 文件', { exact: true }),
    ).toBeVisible()
    page.once('dialog', (dialog) => dialog.accept())
    await drawer
        .getByRole('button', { name: '返回侧栏设备列表', exact: true })
        .click()
    await closeDrawer(page, drawer)
    await page
        .getByLabel('侧栏电表 / 侧栏读数 (readings)', { exact: true })
        .check()
    const sample = page.getByLabel('手工 JSON 样本（不保存）', { exact: true })
    await sample.fill('{"energy":9007199254740993}')
    await addNode(page, 'HTTP 输出', 600)
    const output = page.getByRole('form', { name: '输出参数', exact: true })
    await output
        .getByRole('combobox', { name: '输出报文格式', exact: true })
        .selectOption('custom')
    await output
        .getByRole('button', { name: '添加组装字段', exact: true })
        .click()
    await output.getByLabel('目标字段名', { exact: true }).fill('exact')
    await output
        .getByRole('combobox', { name: '取值方式', exact: true })
        .selectOption('constant')
    await output
        .getByLabel('字段取值', { exact: true })
        .fill('9007199254740993')
    await output
        .getByRole('button', { name: '确认输出参数', exact: true })
        .click()
    await connectionDrawer(page)
    await createConnection(drawer, 'inline-http', 'http', gateway.httpPort)
    await closeDrawer(page, drawer)
    await expect(output.getByLabel('字段取值', { exact: true })).toHaveValue(
        '9007199254740993',
    )
    await expect(sample).toHaveValue('{"energy":9007199254740993}')
    await page.getByLabel('inline-http (inline-http)', { exact: true }).check()
    const nodes = page.locator('.react-flow__node')
    await expect(nodes).toHaveCount(2)
    await nodes
        .nth(0)
        .locator('.react-flow__handle.source')
        .dragTo(nodes.nth(1).locator('.react-flow__handle.target'))
    await expect(page.locator('.react-flow__edge')).toHaveCount(1)
    await saveRoute(page)
    const saved = await (
        await page.request.get(gateway.url + root + '/routes')
    ).json()
    const text = await (
        await page.request.get(
            gateway.url + root + '/routes/' + saved.routes[0].id,
        )
    ).text()
    expect(text).toContain('9007199254740993')
    expect(text).not.toContain('9007199254740992')
    expect(gateway.deliveries).toHaveLength(0)
    const preview = page.getByRole('region', {
        name: '路线样本预览',
        exact: true,
    })
    await preview
        .getByRole('combobox', { name: '预览数据来源', exact: true })
        .selectOption({ index: 1 })
    page.once('dialog', (dialog) => dialog.accept())
    await preview
        .getByRole('button', {
            name: '现场读取 Modbus 一次并预览',
            exact: true,
        })
        .click()
    await expect(preview).toContainText('9007199254740993')
    await expect(preview).toContainText('1 条路径中 1 条计算成功')
    expect(gateway.deliveries).toHaveLength(0)
    await page.goto(gateway.url + '/configuration')
    await page
        .getByRole('button', { name: '查看应用变更清单', exact: true })
        .click()
    await page
        .getByLabel('我已检查变更及影响，确认应用这份草稿', { exact: true })
        .check()
    await page
        .getByRole('button', { name: '确认应用配置', exact: true })
        .click()
    await expect
        .poll(() => gateway.deliveries.length, { timeout: 20_000 })
        .toBeGreaterThan(0)
    expect(
        gateway.deliveries.every(
            (item) => item.body === '{"exact":9007199254740993}',
        ),
    ).toBe(true)
    await page.screenshot({
        path: testInfo.outputPath('inline-applied.png'),
        fullPage: true,
    })
    expect(errors).toEqual([])
    expect(gateway.protocolErrors).toEqual([])
})

test('inline references: failed GET does not replay writes; explicit review preserves saved-route local edits; concurrent edit stays a conflict', async ({
    page,
    gateway,
}) => {
    test.setTimeout(90_000)
    await newRoute(page)
    await addNode(page, 'HTTP 输出', 300)
    await saveRoute(page)
    const routes = await (
        await page.request.get(gateway.url + root + '/routes')
    ).json()
    const id = routes.routes[0].id
    const endpoint = gateway.url + root + '/routes/' + id
    await page.locator('.react-flow__node').first().click()
    await page.getByLabel('路线名称', { exact: true }).fill('保留本地名称')
    await page.getByLabel('节点名称', { exact: true }).fill('保留本地节点')
    const drawer = await connectionDrawer(page)
    let writes = 0
    page.on('request', (request) => {
        if (
            request.method() === 'POST' &&
            new URL(request.url()).pathname === root + '/connections'
        )
            writes++
    })
    await createConnection(drawer, 'inline-failure', 'http', gateway.httpPort)
    let reads = 0
    await page.route(endpoint, (route) => {
        if (route.request().method() !== 'GET') return route.continue()
        reads++
        return route.fulfill({
            status: 500,
            contentType: 'application/problem+json',
            body: '{"title":"inline review unavailable"}',
        })
    })
    await closeDrawer(page, drawer)
    await expect(
        page.getByRole('button', {
            name: '重新核对路线与引用版本',
            exact: true,
        }),
    ).toBeVisible()
    await page.waitForTimeout(1200) // Negative observation: no automatic retry or write replay.
    expect(reads).toBe(1)
    expect(writes).toBe(1)
    await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
        '保留本地名称',
    )
    await expect(page.getByLabel('节点名称', { exact: true })).toHaveValue(
        '保留本地节点',
    )
    await expect(
        page.getByLabel('inline-failure (inline-failure)', { exact: true }),
    ).toBeDisabled()
    await page.unroute(endpoint)
    await page
        .getByRole('button', { name: '重新核对路线与引用版本', exact: true })
        .click()
    await expect(
        page.getByLabel('inline-failure (inline-failure)', { exact: true }),
    ).toBeEnabled()
    await page
        .getByLabel('inline-failure (inline-failure)', { exact: true })
        .check()
    await saveRoute(page)
    await page.locator('.react-flow__node').first().click()
    await page.getByLabel('路线名称', { exact: true }).fill('不能覆盖远端')
    await connectionDrawer(page)
    // A second writer is deliberate fault injection, not the successful UI configuration path.
    const remote = await (await page.request.get(endpoint)).json()
    const session = await (
        await page.request.get(gateway.url + root + '/auth/session')
    ).json()
    const changed = await page.request.put(endpoint, {
        headers: { 'X-CSRF-Token': session.csrfToken },
        data: {
            expectedVersion: remote.version,
            key: remote.route.key,
            description: remote.route.description,
            enabled: remote.route.enabled,
            graph: remote.route.graph,
            name: '其他客户端修改',
        },
    })
    expect(changed.status()).toBe(200)
    await closeDrawer(page, drawer)
    await expect(page.getByText(/服务端这条路线已被修改或删除/)).toBeVisible()
    await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
        '不能覆盖远端',
    )
    const conflict = page.waitForResponse(
        (response) =>
            response.url() === endpoint &&
            response.request().method() === 'PUT',
    )
    await page
        .getByRole('button', { name: '保存路线草稿', exact: true })
        .click()
    expect((await conflict).status()).toBe(409)
    expect((await (await page.request.get(endpoint)).json()).route.name).toBe(
        '其他客户端修改',
    )
    expect(writes).toBe(1)
})

for (const [protocol, kind] of [
    ['http', 'basic'],
    ['http', 'bearer'],
    ['http', 'apiKeyHeader'],
    ['http', 'apiKeyQuery'],
    ['http', 'clientCertificate'],
    ['mqtt', 'basic'],
    ['mqtt', 'clientCertificate'],
] as const) {
    test(`inline references: ${protocol}/${kind} dirty credentials require discard and never survive closing`, async ({
        page,
        gateway,
    }) => {
        await newRoute(page)
        await addNode(page, 'HTTP 输出', 300)
        const drawer = await connectionDrawer(page)
        await createConnection(
            drawer,
            'inline-secret',
            protocol,
            gateway.httpPort,
            kind === 'clientCertificate',
        )
        const openCredentials = async () => {
            await drawer
                .getByRole('row')
                .filter({ hasText: 'inline-secret' })
                .getByRole('button', { name: '编辑连接', exact: true })
                .click()
            await credentials
                .getByRole('combobox', { name: '新认证方式', exact: true })
                .selectOption(kind)
        }
        const credentials = drawer.getByRole('region', {
            name: '连接认证凭证',
            exact: true,
        })
        let writes = 0
        page.on('request', (request) => {
            if (
                ['POST', 'PUT', 'DELETE'].includes(request.method()) &&
                /credential/.test(new URL(request.url()).pathname)
            )
                writes++
        })
        await openCredentials()
        if (kind === 'clientCertificate') {
            // Tests retaining/discarding selected material, not validating a real certificate.
            await credentials
                .getByLabel('PKCS#12 文件（最多 64 KiB）', { exact: true })
                .setInputFiles({
                    name: 'unsaved.p12',
                    mimeType: 'application/x-pkcs12',
                    buffer: Buffer.from('test material, never submitted'),
                })
            await expect(
                credentials.getByText('已载入（不回显）', { exact: true }),
            ).toBeVisible()
        } else {
            if (kind === 'basic')
                await credentials
                    .getByLabel('认证用户名', { exact: true })
                    .fill('fixture-user')
            if (kind === 'apiKeyHeader' || kind === 'apiKeyQuery')
                await credentials
                    .getByLabel('API Key 参数名', { exact: true })
                    .fill('X-Api-Key')
            await credentials
                .getByLabel('新密码或秘密', { exact: true })
                .fill('Unsaved-fixture-secret-2934!')
        }
        let confirmations = 0
        page.once('dialog', (dialog) => {
            confirmations++
            return dialog.dismiss()
        })
        await drawer
            .getByRole('button', { name: '返回画布并核对引用', exact: true })
            .click()
        expect(confirmations).toBe(1)
        await expect(drawer).toBeVisible()
        if (kind === 'clientCertificate')
            await expect(
                credentials.getByText('已载入（不回显）', { exact: true }),
            ).toBeVisible()
        else
            await expect(
                credentials.getByLabel('新密码或秘密', { exact: true }),
            ).toHaveValue('Unsaved-fixture-secret-2934!')
        page.once('dialog', (dialog) => dialog.accept())
        await closeDrawer(page, drawer)
        await connectionDrawer(page)
        await openCredentials()
        if (kind === 'clientCertificate')
            await expect(
                credentials.getByText('已载入（不回显）', { exact: true }),
            ).toHaveCount(0)
        else
            await expect(
                credentials.getByLabel('新密码或秘密', { exact: true }),
            ).toHaveValue('')
        expect(writes).toBe(0)
    })
}

test('inline references: pending save cannot close or replay', async ({
    page,
    gateway,
}) => {
    await newRoute(page)
    await addNode(page, 'HTTP 输出', 300)
    const drawer = await connectionDrawer(page)
    let release!: () => void
    const held = new Promise<void>((resolve) => {
        release = resolve
    })
    let posts = 0
    await page.route(gateway.url + root + '/connections', async (route) => {
        if (route.request().method() !== 'POST') return route.continue()
        posts++
        await held
        await route.continue()
    })
    const creating = createConnection(
        drawer,
        'inline-secret',
        'http',
        gateway.httpPort,
    )
    try {
        await expect.poll(() => posts).toBe(1)
        await expect(
            drawer.getByRole('button', {
                name: '返回画布并核对引用',
                exact: true,
            }),
        ).toBeDisabled()
    } finally {
        release()
        await creating
    }
    await closeDrawer(page, drawer)
    expect(posts).toBe(1)
})

test('inline references: concurrent connection refresh preserves local credential material and never rebinds it', async ({
    page,
    gateway,
}) => {
    await newRoute(page)
    await addNode(page, 'HTTP 输出', 300)
    const drawer = await connectionDrawer(page)
    await createConnection(drawer, 'inline-changed', 'http', gateway.httpPort)
    await drawer
        .getByRole('row')
        .filter({ hasText: 'inline-changed' })
        .getByRole('button', { name: '编辑连接', exact: true })
        .click()
    const credentials = drawer.getByRole('region', {
        name: '连接认证凭证',
        exact: true,
    })
    await credentials
        .getByRole('combobox', { name: '新认证方式', exact: true })
        .selectOption('bearer')
    await credentials
        .getByLabel('新密码或秘密', { exact: true })
        .fill('Unsaved-fixture-secret-2934!')
    const snapshot = await (
        await page.request.get(gateway.url + root + '/connections')
    ).json()
    const connection = snapshot.connections.find(
        (item: { key: string }) => item.key === 'inline-changed',
    )
    const session = await (
        await page.request.get(gateway.url + root + '/auth/session')
    ).json()
    expect(
        (
            await page.request.put(
                gateway.url + root + '/connections/' + connection.id,
                {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        expectedVersion: snapshot.version,
                        key: connection.key,
                        name: connection.name,
                        kind: 'http',
                        enabled: true,
                        http: {
                            baseAddress: `http://127.0.0.1:${gateway.httpPort}/changed/`,
                        },
                    },
                },
            )
        ).status(),
    ).toBe(200)
    await drawer
        .getByRole('button', { name: '刷新连接列表', exact: true })
        .click()
    await expect(
        drawer.getByRole('row').filter({ hasText: 'inline-changed' }),
    ).toContainText('/changed/')
    await expect(credentials).toBeVisible()
    await expect(
        credentials.getByLabel('新密码或秘密', { exact: true }),
    ).toHaveValue('Unsaved-fixture-secret-2934!')
    await expect(
        credentials.getByRole('button', {
            name: '核对凭证变更（不保存）',
            exact: true,
        }),
    ).toBeDisabled()
    await expect(
        drawer.getByLabel('HTTP 基础地址', { exact: true }),
    ).toHaveValue(`http://127.0.0.1:${gateway.httpPort}/`)
    page.once('dialog', (dialog) => dialog.dismiss())
    await drawer
        .getByRole('button', { name: '重新载入此连接', exact: true })
        .click()
    await expect(
        credentials.getByLabel('新密码或秘密', { exact: true }),
    ).toHaveValue('Unsaved-fixture-secret-2934!')
    page.once('dialog', (dialog) => dialog.accept())
    await drawer
        .getByRole('button', { name: '重新载入此连接', exact: true })
        .click()
    await expect(
        credentials.getByLabel('新密码或秘密', { exact: true }),
    ).toHaveValue('')
    await expect(
        drawer.getByLabel('HTTP 基础地址', { exact: true }),
    ).toHaveValue(`http://127.0.0.1:${gateway.httpPort}/changed/`)
})
