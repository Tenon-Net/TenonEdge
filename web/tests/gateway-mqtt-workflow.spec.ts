import {
    expect,
    request,
    test,
    type APIRequestContext,
    type Browser,
    type Page,
} from '@playwright/test'
import { startMqttWorkflowBroker } from './support/mqtt-workflow-broker'
import { startWorkflowGateway } from './support/gateway-workflow'

const root = '/api/edge-admin/v1'
const adminPassword = 'Mqtt-workflow-admin-6317!'
const temporaryPassword = 'Mqtt-workflow-temporary-4826!'
const userPassword = 'Mqtt-workflow-user-7829!'

async function createUser(page: Page, username: string, role: string) {
    await page.goto('/users')
    await page.getByRole('button', { name: '新建用户', exact: true }).click()
    await page.getByLabel('用户账号', { exact: true }).fill(username)
    await page.getByLabel('显示名', { exact: true }).fill(username)
    await page.getByLabel('角色', { exact: true }).selectOption(role)
    await page.getByLabel('临时密码', { exact: true }).fill(temporaryPassword)
    await page.getByRole('button', { name: '核对操作', exact: true }).click()
    await page.getByLabel('我已核对账号与操作，确认执行').check()
    await page.getByRole('button', { name: '确认执行', exact: true }).click()
    await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
}

async function createConnections(
    page: Page,
    mqttPort: number,
    httpPort: number,
) {
    await page.goto('/connections')
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('plant-broker')
    await page.getByLabel('连接名称', { exact: true }).fill('现场 MQTT Broker')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('mqtt')
    await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
    await page.getByLabel('端口', { exact: true }).fill(String(mqttPort))
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByRole('row').filter({ hasText: '现场 MQTT Broker' }),
    ).toBeVisible()

    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('http-receiver')
    await page.getByLabel('连接名称', { exact: true }).fill('真实 HTTP 接收端')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('http')
    await page
        .getByLabel('HTTP 基础地址', { exact: true })
        .fill(`http://127.0.0.1:${httpPort}/`)
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByRole('row').filter({ hasText: '真实 HTTP 接收端' }),
    ).toBeVisible()
}

async function createDeviceAndSource(page: Page) {
    await page.goto('/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page.getByLabel('设备 key', { exact: true }).fill('mqtt-meter')
    await page.getByLabel('设备名称', { exact: true }).fill('MQTT 电表')
    await page
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption('mqtt')
    await page
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({ label: '现场 MQTT Broker (plant-broker)' })
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page.getByRole('button', { name: /MQTT 电表 mqtt-meter/ }).click()
    await page
        .getByRole('link', { name: '数据来源与点位', exact: true })
        .click()
    await page
        .getByRole('button', { name: '新建数据来源', exact: true })
        .click()
    await page.getByLabel('来源 key', { exact: true }).fill('telemetry')
    await page.getByLabel('来源名称', { exact: true }).fill('实时遥测')
    await page.getByLabel('MQTT Topic', { exact: true }).fill('plant/meter')
    await page
        .getByRole('button', { name: '添加 JSON 字段', exact: true })
        .click()
    await page.getByLabel('JSON 字段 1 key', { exact: true }).fill('energy')
    await page.getByLabel('JSON 字段 1 path', { exact: true }).fill('/energy')
    await page
        .getByRole('combobox', { name: 'JSON 字段 1 type', exact: true })
        .selectOption('integer')
    await page.getByLabel('JSON 字段 1 name', { exact: true }).fill('总电量')
    await page.getByLabel('JSON 字段 1 unit', { exact: true }).fill('Wh')
    await page
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    await expect(page.getByRole('status')).toHaveText(
        '来源与点表已保存到网关草稿，尚未应用。',
    )
}

async function drawRoute(page: Page) {
    await page.goto('/routes')
    await page.getByRole('button', { name: '新建路线', exact: true }).click()
    await page.getByLabel('路线标识', { exact: true }).fill('mqtt-to-http')
    await page.getByLabel('路线名称', { exact: true }).fill('实时 MQTT 转发')
    await page
        .getByLabel('路线说明', { exact: true })
        .fill('真实 Broker 遥测路线')
    await page.getByLabel('参与静态编译（草稿启用）', { exact: true }).check()
    const canvas = page.getByLabel('路线画布', { exact: true })
    await canvas.scrollIntoViewIfNeeded()
    const add = (name: string, x: number) =>
        page
            .getByRole('button', { name, exact: true })
            .dragTo(canvas, { targetPosition: { x, y: 150 } })
    await add('MQTT 输入', 120)
    await page
        .getByLabel('MQTT 电表 / 实时遥测 (telemetry)', { exact: true })
        .check()
    await add('字段映射', 400)
    const mapping = page.getByRole('form', {
        name: '字段映射规则表',
        exact: true,
    })
    await mapping
        .getByRole('button', { name: '添加映射规则', exact: true })
        .click()
    await mapping.getByLabel('目标字段 1', { exact: true }).fill('energyTotal')
    await mapping
        .getByRole('combobox', { name: '目标类型 1', exact: true })
        .selectOption('integer')
    await mapping
        .getByLabel('来源字段 / Pointer / 固定值 1', { exact: true })
        .fill('energy')
    await mapping
        .getByRole('button', { name: '确认映射规则', exact: true })
        .click()
    await add('HTTP 输出', 680)
    await page
        .getByLabel('真实 HTTP 接收端 (http-receiver)', { exact: true })
        .check()
    const output = page.getByRole('form', { name: '输出参数', exact: true })
    await output
        .getByLabel('输出相对路径', { exact: true })
        .fill('/mqtt-capture')
    await output
        .getByRole('button', { name: '确认输出参数', exact: true })
        .click()
    const nodes = page.locator('.react-flow__node')
    await expect(nodes).toHaveCount(3)
    for (const index of [0, 1])
        await nodes
            .nth(index)
            .locator('.react-flow__handle.source')
            .dragTo(nodes.nth(index + 1).locator('.react-flow__handle.target'))
    await expect(page.locator('.react-flow__edge')).toHaveCount(2)
    await page
        .getByRole('button', { name: '保存路线草稿', exact: true })
        .click()
    await expect(
        page.getByText('路线草稿已保存，未应用。', { exact: true }),
    ).toBeVisible()
}

async function previewFromBroker(
    page: Page,
    publish: (
        topic: string,
        payload: string,
        retained?: boolean,
    ) => Promise<unknown>,
    value: string,
) {
    const preview = page.getByRole('region', {
        name: '路线样本预览',
        exact: true,
    })
    await preview
        .getByLabel('预览数据来源', { exact: true })
        .selectOption({ index: 1 })
    page.once('dialog', (dialog) => dialog.accept())
    const captureRequest = page.waitForRequest((request) =>
        request.url().endsWith('/routes/preview/mqtt'),
    )
    await preview
        .getByRole('button', { name: '抓取 MQTT 一条消息并预览', exact: true })
        .click()
    await captureRequest
    // Retained delivery removes the subscribe/first-publish race without a
    // timing sleep; the real capture endpoint still performs the subscription.
    await publish('plant/meter', JSON.stringify({ energy: value }), true)
    await expect(preview).toContainText(value)
    await expect(preview).toContainText('1 条路径中 1 条计算成功')
}

async function applyRoute(page: Page) {
    await page.goto('/configuration')
    await page
        .getByRole('button', { name: '查看应用变更清单', exact: true })
        .click()
    await page
        .getByLabel('我已检查变更及影响，确认应用这份草稿', { exact: true })
        .check()
    await page
        .getByRole('button', { name: '确认应用配置', exact: true })
        .click()
    await expect(
        page
            .getByText('配置已提交并激活。后续运行健康情况仍须查看诊断。', {
                exact: true,
            })
            .first(),
    ).toBeVisible()
}

for (const creator of ['administrator', 'configurator'] as const) {
    test(`no JSON workflow: ${creator} configures live MQTT to HTTP; browser exit and Host restart`, async ({
        browser,
    }, testInfo) => {
        test.setTimeout(180_000)
        const broker = await startMqttWorkflowBroker()
        const gateway = await startWorkflowGateway(false, [broker.port])
        let owned: Browser | undefined
        let page: Page | undefined
        let statusClient: APIRequestContext | undefined
        let browserUserAgent = ''
        const pageErrors: string[] = []
        const externalRequests: string[] = []
        const launch = async () => {
            owned = await browser.browserType().launch({
                executablePath: process.env.TENONEDGE_TEST_EXECUTABLE,
            })
            const next = await owned.newPage({
                baseURL: gateway.url,
                viewport: { width: 1800, height: 1200 },
            })
            await next.context().route('**/*', (route) => {
                const url = route.request().url()
                if (new URL(url).origin === gateway.url) return route.continue()
                externalRequests.push(url)
                return route.abort('blockedbyclient')
            })
            next.setDefaultTimeout(15_000)
            next.on('pageerror', (error) => pageErrors.push(error.message))
            return next
        }
        try {
            page = await launch()
            await page.goto('/')
            browserUserAgent = await page.evaluate(() => navigator.userAgent)
            await page
                .getByLabel('账号', { exact: true })
                .fill('mqtt-workflow-admin')
            for (const label of ['密码', '确认密码'])
                await page
                    .getByLabel(label, { exact: true })
                    .fill(adminPassword)
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            console.log(
                'MQTT no-JSON workflow browser:',
                await page.evaluate(() => navigator.userAgent),
                'origin:',
                gateway.url,
                'creator:',
                creator,
            )
            if (new URL(gateway.url).hostname !== '127.0.0.1')
                expect(await page.evaluate(() => isSecureContext)).toBe(false)
            await createUser(page, 'mqtt-workflow-viewer', 'viewer')
            if (creator === 'configurator') {
                await createUser(
                    page,
                    'mqtt-workflow-configurator',
                    'configurator',
                )
                await page
                    .getByRole('button', { name: '退出登录', exact: true })
                    .click()
                await page.goto('/')
                await page
                    .getByLabel('账号', { exact: true })
                    .fill('mqtt-workflow-configurator')
                await page
                    .getByLabel('密码', { exact: true })
                    .fill(temporaryPassword)
                await page
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await page
                    .getByLabel('原密码', { exact: true })
                    .fill(temporaryPassword)
                await page
                    .getByLabel('新密码', { exact: true })
                    .fill(userPassword)
                await page
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(page.getByRole('navigation')).toBeVisible()
            }
            const cookies = await page.context().cookies(gateway.url)
            statusClient = await request.newContext({
                baseURL: gateway.url,
                extraHTTPHeaders: {
                    Cookie: cookies
                        .map(({ name, value }) => `${name}=${value}`)
                        .join('; '),
                },
            })
            await createConnections(page, broker.port, gateway.httpPort)
            await createDeviceAndSource(page)
            await drawRoute(page)
            await expect(
                page.getByRole('status', { name: '有效运行状态' }),
            ).toContainText('当前画布尚未对应有效路线')
            expect(gateway.deliveries).toHaveLength(0)
            await previewFromBroker(page, broker.publish, '9007199254740993')
            expect(gateway.deliveries).toHaveLength(0)
            await applyRoute(page)
            await page.goto('/routes')
            await page
                .getByRole('button', {
                    name: '打开 实时 MQTT 转发',
                    exact: true,
                })
                .click()
            await expect(
                page.getByRole('status', { name: '有效运行状态' }),
            ).toContainText('此画布对应当前有效路线')

            const draft = await (
                await page.request.get(root + '/configuration/draft')
            ).json()
            expect(draft.effectiveRevision).toBe(1)
            const configured = await (
                await page.request.get(root + '/connections')
            ).json()
            const mqtt = configured.connections.find(
                (connection: { key: string }) =>
                    connection.key === 'plant-broker',
            )
            const waitForSubscriptions = async (count: number) =>
                expect
                    .poll(
                        async () => {
                            const status = await (
                                await statusClient!.get(
                                    root + '/runtime/status',
                                )
                            ).json()
                            return status.persistentCounters[
                                `connection/${mqtt.id}/mqtt/subscribed`
                            ]
                        },
                        { timeout: 20_000 },
                    )
                    .toBe(count)
            const publishAndCheck = async (value: string) => {
                const before = gateway.deliveries.length
                await broker.publish(
                    'plant/meter',
                    JSON.stringify({ energy: value }),
                )
                await expect
                    .poll(() => gateway.deliveries.length, { timeout: 20_000 })
                    .toBe(before + 1)
                const delivery = gateway.deliveries.at(-1)!
                expect(delivery.path).toBe('/mqtt-capture')
                expect(delivery.body).toContain(`"energyTotal":${value}`)
                expect(JSON.parse(delivery.body).messageId).toBe(delivery.id)
            }

            await waitForSubscriptions(1)
            await publishAndCheck('9007199254740993')
            await page.goto('/runtime/devices')
            await expect(
                page.getByRole('row').filter({ hasText: 'MQTT 电表' }),
            ).toBeVisible()
            await page.goto('/runtime/outputs')
            await expect(
                page.getByRole('row').filter({ hasText: '真实 HTTP 接收端' }),
            ).toBeVisible()

            const beforeBrowserExit = gateway.deliveries.length
            const ownedBrowser = owned
            expect(ownedBrowser).toBeDefined()
            await ownedBrowser!.close()
            expect(ownedBrowser!.isConnected()).toBe(false)
            owned = undefined
            await publishAndCheck('9007199254740994')
            expect(gateway.deliveries.length).toBeGreaterThan(beforeBrowserExit)

            const beforeRestart = gateway.deliveries.length
            await gateway.restart()
            await waitForSubscriptions(2)
            await publishAndCheck('9007199254740995')
            expect(gateway.deliveries.length).toBeGreaterThan(beforeRestart)

            expect(externalRequests).toEqual([])
            expect(pageErrors).toEqual([])
            expect(gateway.protocolErrors).toEqual([])
            await testInfo.attach('mqtt-no-json-workflow', {
                body: Buffer.from(
                    JSON.stringify({
                        creator,
                        deliveries: gateway.deliveries.length,
                        subscriptions: 2,
                        externalRequests,
                        browser: browserUserAgent,
                    }),
                ),
                contentType: 'application/json',
            })
        } finally {
            try {
                await owned?.close()
            } finally {
                try {
                    await statusClient?.dispose()
                } finally {
                    await gateway.close()
                    await broker.close()
                }
            }
        }
    })
}
