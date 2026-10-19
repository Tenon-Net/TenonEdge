import { expect, test, type Browser, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { startWorkflowGateway } from './support/gateway-workflow'
import { startMqttWorkflowBroker } from './support/mqtt-workflow-broker'

const password = 'Http-workflow-admin-7234!'
const temporaryPassword = 'Http-workflow-temporary-4826!'
const configuratorPassword = 'Http-workflow-configurator-5937!'

async function createConfigurator(page: Page) {
    await page.goto('/users')
    await page.getByRole('button', { name: '新建用户', exact: true }).click()
    await page
        .getByLabel('用户账号', { exact: true })
        .fill('http-workflow-configurator')
    await page.getByLabel('显示名', { exact: true }).fill('HTTP配置人员')
    await page.getByLabel('角色', { exact: true }).selectOption('configurator')
    await page.getByLabel('临时密码', { exact: true }).fill(temporaryPassword)
    await page.getByRole('button', { name: '核对操作', exact: true }).click()
    await page.getByLabel('我已核对账号与操作，确认执行').check()
    await page.getByRole('button', { name: '确认执行', exact: true }).click()
    await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
    await page.getByRole('button', { name: '退出登录', exact: true }).click()
    await page.goto('/')
    await page
        .getByLabel('账号', { exact: true })
        .fill('http-workflow-configurator')
    await page.getByLabel('密码', { exact: true }).fill(temporaryPassword)
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await page.getByLabel('原密码', { exact: true }).fill(temporaryPassword)
    await page.getByLabel('新密码', { exact: true }).fill(configuratorPassword)
    await page.getByRole('button', { name: '保存新密码', exact: true }).click()
    await expect(page.getByRole('navigation')).toBeVisible()
}

async function createHttpConnection(
    page: Page,
    port: number,
    mqttPort?: number,
) {
    await page.goto('/connections')
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('plant-http')
    await page.getByLabel('连接名称', { exact: true }).fill('现场 HTTP 服务')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('http')
    await page
        .getByLabel('HTTP 基础地址', { exact: true })
        .fill(`http://127.0.0.1:${port}/`)
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    if (mqttPort !== undefined) {
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page.getByLabel('连接 key', { exact: true }).fill('plant-broker')
        await page
            .getByLabel('连接名称', { exact: true })
            .fill('现场 MQTT Broker')
        await page
            .getByRole('combobox', { name: '协议类型', exact: true })
            .selectOption('mqtt')
        await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
        await page.getByLabel('端口', { exact: true }).fill(String(mqttPort))
        await page
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
    }
}

async function createDeviceAndSource(page: Page) {
    await page.goto('/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page.getByLabel('设备 key', { exact: true }).fill('http-meter')
    await page.getByLabel('设备名称', { exact: true }).fill('HTTP 电表')
    await page
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption('http')
    await page
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({ label: '现场 HTTP 服务 (plant-http)' })
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page.getByRole('button', { name: /HTTP 电表 http-meter/ }).click()
    await page
        .getByRole('link', { name: '数据来源与点位', exact: true })
        .click()
    await page
        .getByRole('button', { name: '新建数据来源', exact: true })
        .click()
    await page.getByLabel('来源 key', { exact: true }).fill('poll')
    await page.getByLabel('来源名称', { exact: true }).fill('实时 HTTP 读数')
    await page.getByLabel('采集周期（毫秒）', { exact: true }).fill('1000')
    await page
        .getByLabel('HTTP 路径与非秘密查询参数', { exact: true })
        .fill('/source')
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

async function createRoute(page: Page, target: 'http' | 'mqtt' = 'http') {
    await page.goto('/routes')
    await page.getByRole('button', { name: '新建路线', exact: true }).click()
    await page.getByLabel('路线标识', { exact: true }).fill('http-to-http')
    await page.getByLabel('路线名称', { exact: true }).fill('HTTP 采集转发')
    await page
        .getByLabel('路线说明', { exact: true })
        .fill('真实 HTTP 拉取路线')
    await page.getByLabel('参与静态编译（草稿启用）', { exact: true }).check()
    const canvas = page.getByLabel('路线画布', { exact: true })
    await canvas.scrollIntoViewIfNeeded()
    const add = (name: string, x: number) =>
        page
            .getByRole('button', { name, exact: true })
            .dragTo(canvas, { targetPosition: { x, y: 150 } })
    await add('HTTP 输入', 120)
    await page
        .getByLabel('HTTP 电表 / 实时 HTTP 读数 (poll)', { exact: true })
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
    await add(target === 'http' ? 'HTTP 输出' : 'MQTT 输出', 680)
    await page
        .getByLabel(
            target === 'http'
                ? '现场 HTTP 服务 (plant-http)'
                : '现场 MQTT Broker (plant-broker)',
            { exact: true },
        )
        .check()
    const output = page.getByRole('form', { name: '输出参数', exact: true })
    if (target === 'http')
        await output
            .getByLabel('输出相对路径', { exact: true })
            .fill('/capture')
    else
        await output
            .getByLabel('发布 Topic', { exact: true })
            .fill('plant/http/energy')
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
        page.getByText('配置已提交并激活。后续运行健康情况仍须查看诊断。', {
            exact: true,
        }),
    ).toBeVisible()
}

async function runHttpPullWorkflow(
    browser: Browser,
    target: 'http' | 'mqtt',
    creator: 'administrator' | 'configurator' = 'administrator',
) {
    test.setTimeout(180_000)
    const broker =
        target === 'mqtt' ? await startMqttWorkflowBroker() : undefined
    const gateway = await startWorkflowGateway(
        false,
        broker ? [broker.port] : [],
    )
    let owned: Browser | undefined
    let page: Page | undefined
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
        return next
    }
    try {
        page = await launch()
        await page.goto('/')
        await page
            .getByLabel('账号', { exact: true })
            .fill('http-workflow-admin')
        for (const label of ['密码', '确认密码'])
            await page.getByLabel(label, { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        if (creator === 'configurator') await createConfigurator(page)

        await createHttpConnection(page, gateway.httpPort, broker?.port)
        await createDeviceAndSource(page)
        await createRoute(page, target)
        await expect(
            page.getByRole('status', { name: '有效运行状态' }),
        ).toContainText('当前画布尚未对应有效路线')
        const preview = page.getByRole('region', {
            name: '路线样本预览',
            exact: true,
        })
        await preview
            .getByLabel('预览数据来源', { exact: true })
            .selectOption({ index: 1 })
        page.once('dialog', (dialog) => dialog.accept())
        await preview
            .getByRole('button', {
                name: '现场读取 HTTP 一次并预览',
                exact: true,
            })
            .click()
        await expect(preview).toContainText('9007199254740993')
        await expect(preview).toContainText('1 条路径中 1 条计算成功')
        await expect(gateway.httpReads).toHaveLength(1)
        const capture = async () => {
            const marker = `ready-${randomUUID()}`
            await broker!.publish('plant/http/energy', marker, true)
            const subscription =
                await broker!.subscribeForReadinessAndOneMessage(
                    'plant/http/energy',
                )
            await subscription.waitForRetained(marker)
            return subscription
        }
        let mqttCapture = target === 'mqtt' ? await capture() : undefined
        await applyRoute(page)

        const before = gateway.deliveries.length
        if (target === 'http') {
            await expect
                .poll(() => gateway.deliveries.length, { timeout: 20_000 })
                .toBeGreaterThan(before)
            const firstDelivery = gateway.deliveries.at(-1)!
            expect(firstDelivery.path).toBe('/capture')
            expect(firstDelivery.body).toContain(
                '"energyTotal":9007199254740993',
            )
        } else {
            const messages = await mqttCapture!.waitForMessages(2)
            expect(messages.split('\n')[1]).toContain('9007199254740993')
            expect(messages.split('\n')[1]).toContain('messageId')
        }
        const browserProcess = owned
        expect(browserProcess).toBeDefined()
        if (target === 'mqtt') mqttCapture = await capture()
        const readsBeforeBrowserExit = gateway.httpReads.length
        const deliveriesBeforeBrowserExit = gateway.deliveries.length
        await browserProcess!.close()
        owned = undefined
        await expect
            .poll(() => gateway.httpReads.length, { timeout: 20_000 })
            .toBeGreaterThan(readsBeforeBrowserExit)
        if (target === 'http') {
            await expect
                .poll(() => gateway.deliveries.length, { timeout: 20_000 })
                .toBeGreaterThan(deliveriesBeforeBrowserExit)
        } else {
            const messages = await mqttCapture!.waitForMessages(2)
            expect(messages.split('\n')[1]).toContain('9007199254740993')
        }
        const deliveriesBeforeRestart = gateway.deliveries.length
        if (target === 'mqtt') mqttCapture = await capture()
        await gateway.restart()
        if (target === 'http') {
            await expect
                .poll(() => gateway.deliveries.length, { timeout: 20_000 })
                .toBeGreaterThan(deliveriesBeforeRestart)
        } else {
            const messages = await mqttCapture!.waitForMessages(2)
            expect(messages.split('\n')[1]).toContain('9007199254740993')
        }
        expect(externalRequests).toEqual([])
        expect(gateway.protocolErrors).toEqual([])
    } finally {
        try {
            await owned?.close()
        } finally {
            try {
                await gateway.close()
            } finally {
                await broker?.close()
            }
        }
    }
}

test('no JSON workflow: administrator configures real HTTP pull to HTTP output and Host restart', async ({
    browser,
}) => runHttpPullWorkflow(browser, 'http'))

test('no JSON workflow: administrator configures real HTTP pull to MQTT output and Host restart', async ({
    browser,
}) => runHttpPullWorkflow(browser, 'mqtt'))

test('no JSON workflow: configurator configures real HTTP pull to MQTT output and Host restart', async ({
    browser,
}) => runHttpPullWorkflow(browser, 'mqtt', 'configurator'))
