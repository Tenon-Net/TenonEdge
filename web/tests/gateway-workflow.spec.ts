import { expect, test, type Browser, type Page } from '@playwright/test'
import { startWorkflowGateway } from './support/gateway-workflow'

const root = '/api/edge-admin/v1'
const adminPassword = 'Workflow-admin-8416!'
const temporaryPassword = 'Workflow-temporary-4618!'
const userPassword = 'Workflow-personal-8164!'
const receiverSecret = 'Workflow-receiver-fixture-3187!'

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
async function login(
    page: Page,
    username: string,
    password: string,
    temporary = false,
) {
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill(username)
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByRole('button', { name: '登录', exact: true }).click()
    if (temporary) {
        await page.getByLabel('原密码', { exact: true }).fill(password)
        await page.getByLabel('新密码', { exact: true }).fill(userPassword)
        await page
            .getByRole('button', { name: '保存新密码', exact: true })
            .click()
    }
    await expect(page.getByRole('navigation')).toBeVisible()
}
async function createConnections(
    page: Page,
    modbusPort: number,
    httpPort: number,
) {
    await page.goto('/connections')
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('plc')
    await page.getByLabel('连接名称', { exact: true }).fill('现场 PLC')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('modbus')
    await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
    await page.getByLabel('端口', { exact: true }).fill(String(modbusPort))
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByRole('row').filter({ hasText: '现场 PLC' }),
    ).toBeVisible()
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('receiver')
    await page.getByLabel('连接名称', { exact: true }).fill('生产接收端')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('http')
    await page
        .getByLabel('HTTP 基础地址', { exact: true })
        .fill(`http://127.0.0.1:${httpPort}/`)
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await page
        .getByRole('row')
        .filter({ hasText: '生产接收端' })
        .getByRole('button', { name: '编辑连接', exact: true })
        .click()
    const credentials = page.getByRole('region', {
        name: '连接认证凭证',
        exact: true,
    })
    await credentials
        .getByRole('combobox', { name: '新认证方式', exact: true })
        .selectOption('basic')
    await credentials
        .getByLabel('认证用户名', { exact: true })
        .fill('receiver-user')
    await credentials
        .getByLabel('新密码或秘密', { exact: true })
        .fill(receiverSecret)
    await credentials
        .getByRole('button', { name: '核对凭证变更（不保存）', exact: true })
        .click()
    await credentials
        .getByRole('checkbox', {
            name: '我已核对原新身份和积存，了解凭证立即生效及隔离规则',
        })
        .check()
    await credentials
        .getByRole('button', { name: '立即保存凭证', exact: true })
        .click()
    await expect(credentials.getByText('已设置', { exact: true })).toBeVisible()
}
async function createDeviceAndPoints(page: Page) {
    await page.goto('/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page.getByLabel('设备 key', { exact: true }).fill('meter')
    await page.getByLabel('设备名称', { exact: true }).fill('现场电表')
    await page
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption('modbus')
    await page
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({ label: '现场 PLC (plc)' })
    await page.getByLabel('UnitId', { exact: true }).fill('1')
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page.getByRole('button', { name: /现场电表 meter/ }).click()
    await page
        .getByRole('link', { name: '数据来源与点位', exact: true })
        .click()
    await page.getByRole('button', { name: '新建采集组', exact: true }).click()
    await page.getByLabel('来源 key', { exact: true }).fill('readings')
    await page.getByLabel('来源名称', { exact: true }).fill('电表读数')
    await page.getByLabel('采集周期（毫秒）', { exact: true }).fill('1000')
    for (const [row, key, address, type] of [
        [1, 'energy', '0', 'uint64'],
        [2, 'temperature', '4', 'uint16'],
    ] as const) {
        await page
            .getByRole('button', { name: '添加空行', exact: true })
            .click()
        await page.getByLabel(`第 ${row} 行 key`, { exact: true }).fill(key)
        await page
            .getByLabel(`第 ${row} 行 地址`, { exact: true })
            .fill(address)
        await page
            .getByRole('combobox', {
                name: `第 ${row} 行 数据类型`,
                exact: true,
            })
            .selectOption(type)
        if (type === 'uint64')
            await page
                .getByRole('combobox', {
                    name: `第 ${row} 行 字节序`,
                    exact: true,
                })
                .selectOption('ABCD')
    }
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
    await page.getByLabel('路线标识', { exact: true }).fill('meter-to-http')
    await page.getByLabel('路线名称', { exact: true }).fill('电表采集转发')
    await page.getByLabel('路线说明', { exact: true }).fill('现场电表验收路线')
    await page.getByLabel('参与静态编译（草稿启用）', { exact: true }).check()
    const canvas = page.getByLabel('路线画布', { exact: true })
    await canvas.scrollIntoViewIfNeeded()
    const add = (name: string, x: number) =>
        page
            .getByRole('button', { name, exact: true })
            .dragTo(canvas, { targetPosition: { x, y: 150 } })
    await add('Modbus 输入', 120)
    await page
        .getByLabel('现场电表 / 电表读数 (readings)', { exact: true })
        .check()
    await add('字段映射', 400)
    const mapping = page.getByRole('form', {
        name: '字段映射规则表',
        exact: true,
    })
    for (const [row, target, source, type] of [
        [1, 'energyTotal', 'energy', 'integer'],
        [2, 'temperatureC', 'temperature', 'number'],
    ] as const) {
        await mapping
            .getByRole('button', { name: '添加映射规则', exact: true })
            .click()
        await mapping
            .getByLabel(`目标字段 ${row}`, { exact: true })
            .fill(target)
        await mapping
            .getByRole('combobox', { name: `目标类型 ${row}`, exact: true })
            .selectOption(type)
        await mapping
            .getByLabel(`来源字段 / Pointer / 固定值 ${row}`, { exact: true })
            .fill(source)
        if (row === 2) {
            await mapping.getByLabel('比例 2', { exact: true }).fill('0.1')
            await mapping.getByLabel('偏移 2', { exact: true }).fill('-40')
            await mapping.getByLabel('单位 2', { exact: true }).fill('°C')
        }
    }
    await mapping
        .getByRole('button', { name: '确认映射规则', exact: true })
        .click()
    await add('HTTP 输出', 680)
    await page.getByLabel('生产接收端 (receiver)', { exact: true }).check()
    const output = page.getByRole('form', { name: '输出参数', exact: true })
    await output.getByLabel('输出相对路径', { exact: true }).fill('/capture')
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
    await page.goto('/routes')
    const row = page.locator('li').filter({ hasText: '电表采集转发' })
    await expect(row).toContainText('meter-to-http')
    await expect(row).toContainText('现场电表验收路线')
    await expect(row).toContainText('已启用')
    await expect(row).toContainText('有未应用修改')
    await expect(row).toContainText('最后修改：workflow-')
    await row.getByRole('button', { name: '打开 电表采集转发' }).click()
}
async function previewRoute(page: Page) {
    const preview = page.getByRole('region', {
        name: '路线样本预览',
        exact: true,
    })
    const sources = preview.getByRole('combobox', {
        name: '预览数据来源',
        exact: true,
    })
    await sources.selectOption({ index: 1 })
    page.once('dialog', (dialog) => dialog.accept())
    await preview
        .getByRole('button', {
            name: '现场读取 Modbus 一次并预览',
            exact: true,
        })
        .click()
    await expect(preview).toContainText('9007199254740993')
    await expect(preview).toContainText('198.5')
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
    await page.goto('/routes')
    const row = page.locator('li').filter({ hasText: '电表采集转发' })
    await expect(row).toContainText('有效配置')
    await row.getByRole('button', { name: '打开 电表采集转发' }).click()
    const runtimeStatus = page.getByText(/运行协调状态：运行中/)
    await expect(runtimeStatus).toContainText('与有效运行配置一致')
}

for (const creator of ['administrator', 'configurator'] as const) {
    test(`no JSON workflow: ${creator} configures Modbus to HTTP; viewer boundaries, browser exit and Host restart`, async ({
        browser,
    }, testInfo) => {
        test.setTimeout(180_000)
        const gateway = await startWorkflowGateway()
        let owned: Browser | undefined
        let page: Page | undefined
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
                const requestUrl = route.request().url()
                if (new URL(requestUrl).origin === gateway.url)
                    return route.continue()
                externalRequests.push(requestUrl)
                return route.abort('blockedbyclient')
            })
            next.setDefaultTimeout(15_000)
            next.on('pageerror', (error) => pageErrors.push(error.message))
            return next
        }
        try {
            page = await launch()
            await page.goto('/')
            await page
                .getByLabel('账号', { exact: true })
                .fill('workflow-admin')
            await page.getByLabel('密码', { exact: true }).fill(adminPassword)
            await page
                .getByLabel('确认密码', { exact: true })
                .fill(adminPassword)
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            console.log(
                'No-JSON workflow browser:',
                await page.evaluate(() => navigator.userAgent),
                'origin:',
                gateway.url,
                'creator:',
                creator,
            )
            if (new URL(gateway.url).hostname !== '127.0.0.1')
                expect(await page.evaluate(() => isSecureContext)).toBe(false)
            await createUser(page, 'workflow-viewer', 'viewer')
            if (creator === 'configurator') {
                await createUser(page, 'workflow-editor', 'configurator')
                await page
                    .getByRole('button', { name: '退出登录', exact: true })
                    .click()
                await login(page, 'workflow-editor', temporaryPassword, true)
            }
            // Every successful configuration mutation below is generated by visible controls.
            // No API seeding, JSON editor, clipboard JSON, browser-store mutation or database fixture.
            await createConnections(page, gateway.modbusPort, gateway.httpPort)
            await createDeviceAndPoints(page)
            await drawRoute(page)
            expect(gateway.reads).toHaveLength(0)
            expect(gateway.deliveries).toHaveLength(0)
            await previewRoute(page)
            expect(gateway.reads.length).toBeGreaterThan(0)
            expect(gateway.deliveries).toHaveLength(0)
            await applyRoute(page)
            await expect
                .poll(() => gateway.deliveries.length, { timeout: 15_000 })
                .toBeGreaterThan(2)
            const verifyDeliveries = () => {
                for (const item of gateway.deliveries) {
                    expect(item.path).toBe('/capture')
                    expect(item.authorization).toBe(
                        'Basic ' +
                            Buffer.from(
                                'receiver-user:' + receiverSecret,
                            ).toString('base64'),
                    )
                    expect(item.body).toContain(
                        '"energyTotal":9007199254740993',
                    )
                    expect(item.body).toContain('"temperatureC":198.5')
                    expect(item.body).not.toContain(receiverSecret)
                    expect(JSON.parse(item.body).messageId).toBe(item.id)
                }
            }
            verifyDeliveries()
            const applied = await (
                await page.request.get(root + '/configuration/draft')
            ).json()
            expect(applied.effectiveRevision).toBe(1)
            await page.goto('/runtime/devices')
            await page
                .getByRole('button', { name: '查看 现场电表', exact: true })
                .click()
            const values = page.getByRole('table').filter({
                has: page.getByRole('columnheader', {
                    name: '值 / 类型',
                    exact: true,
                }),
            })
            await expect(values).toContainText('9007199254740993')
            await expect(values).toContainText('2385')
            await expect(
                values.getByRole('row').filter({ hasText: '良好' }),
            ).toHaveCount(2)
            await page.screenshot({
                path: testInfo.outputPath('current-values.png'),
                fullPage: true,
            })
            await page.goto('/runtime/outputs')
            await expect(
                page.getByRole('row').filter({ hasText: '生产接收端' }),
            ).toBeVisible()
            await page.screenshot({
                path: testInfo.outputPath('running-output.png'),
                fullPage: true,
            })
            const readsBeforeClose = gateway.reads.length
            const beforeClose = gateway.deliveries.length
            await owned!.close() // Entire owned browser process, not just the tab/context.
            expect(owned!.isConnected()).toBe(false)
            owned = undefined
            await expect
                .poll(() => gateway.deliveries.length, { timeout: 15_000 })
                .toBeGreaterThan(beforeClose + 3)
            expect(gateway.reads.length).toBeGreaterThan(readsBeforeClose)
            const readsBeforeRestart = gateway.reads.length
            const beforeRestart = gateway.deliveries.length
            await gateway.restart() // Original product Host, same data directory and port.
            await expect
                .poll(() => gateway.deliveries.length, { timeout: 20_000 })
                .toBeGreaterThan(beforeRestart + 2)
            expect(gateway.reads.length).toBeGreaterThan(readsBeforeRestart)
            page = await launch()
            await login(page, 'workflow-viewer', temporaryPassword, true)
            await page.goto('/connections')
            await expect(
                page.getByRole('button', { name: '新建连接', exact: true }),
            ).toHaveCount(0)
            await expect(
                page.getByRole('row').filter({ hasText: '现场 PLC' }),
            ).toBeVisible()
            await page.goto('/routes')
            await page
                .getByRole('button', { name: '打开 电表采集转发', exact: true })
                .click()
            await expect(
                page.getByRole('button', { name: '保存路线草稿', exact: true }),
            ).toBeDisabled()
            await expect(
                page.getByRole('button', {
                    name: '现场读取 Modbus 一次并预览',
                    exact: true,
                }),
            ).toHaveCount(0)
            await expect(
                page.getByRole('combobox', {
                    name: '预览数据来源',
                    exact: true,
                }),
            ).toBeDisabled()
            await page.reload() // Authenticated deep link refresh must render the actual page.
            await expect(
                page.getByRole('heading', { name: '路线草稿', exact: true }),
            ).toBeVisible()
            await page.goto('/configuration')
            const restored = await (
                await page.request.get(root + '/configuration/draft')
            ).json()
            expect(restored.effectiveRevision).toBe(1)
            expect(restored.version).toBe(applied.version)
            await expect(
                page.getByRole('button', { name: '确认应用配置', exact: true }),
            ).toHaveCount(0)
            const session = await (
                await page.request.get(root + '/auth/session')
            ).json()
            // Negative authorization probe only; never used to configure or apply the gateway.
            const denied = await page.request.post(root + '/routes/compile', {
                headers: { 'X-CSRF-Token': session.csrfToken },
                data: {},
            })
            expect(denied.status()).toBe(403)
            const missing = await page.request.get(root + '/not-a-real-api')
            expect(missing.status()).toBe(404)
            expect(missing.headers()['content-type']).toContain(
                'application/problem+json',
            )
            verifyDeliveries()
            expect(gateway.protocolErrors).toEqual([])
            expect(pageErrors).toEqual([])
            expect(externalRequests).toEqual([])
            await testInfo.attach('workflow-evidence', {
                body: Buffer.from(
                    JSON.stringify({
                        creator,
                        reads: gateway.reads.length,
                        accepted: gateway.deliveries.length,
                        beforeBrowserExit: beforeClose,
                        readsBeforeBrowserExit: readsBeforeClose,
                        readsBeforeHostRestart: readsBeforeRestart,
                        externalRequests,
                        beforeHostRestart: beforeRestart,
                        revision: restored.effectiveRevision,
                        browser: await page.evaluate(() => navigator.userAgent),
                    }),
                ),
                contentType: 'application/json',
            })
        } catch (error) {
            if (page && !page.isClosed()) {
                await testInfo
                    .attach('workflow-failure', {
                        body: await page.screenshot({ fullPage: true }),
                        contentType: 'image/png',
                    })
                    .catch(() => {})
                await testInfo
                    .attach('workflow-page', {
                        body: await page.locator('body').ariaSnapshot(),
                        contentType: 'text/plain',
                    })
                    .catch(() => {})
            }
            throw error
        } finally {
            try {
                await owned?.close()
            } finally {
                await gateway.close()
            }
        }
    })
}
