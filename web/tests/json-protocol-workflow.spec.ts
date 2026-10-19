import {
    expect,
    test,
    type Browser,
    type Page,
    type APIRequestContext,
} from '@playwright/test'
import {
    startJsonWorkflow,
    sample,
    sampleTime,
} from './support/json-protocol-workflow'
import {
    adminPassword,
    temporaryPassword,
    createUser,
    login,
    applyRoute,
} from './support/workflow-ui'

const root = '/api/edge-admin/v1'
type Protocol = 'http' | 'mqtt'

async function connections(page: Page, httpPort: number, mqttPort: number) {
    await page.goto('/connections')
    const ids: Partial<Record<Protocol, number>> = {}
    for (const kind of ['http', 'mqtt'] as const) {
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page.getByLabel('连接 key', { exact: true }).fill(kind)
        await page
            .getByLabel('连接名称', { exact: true })
            .fill(kind.toUpperCase())
        await page
            .getByRole('combobox', { name: '协议类型', exact: true })
            .selectOption(kind)
        if (kind === 'http') {
            await page
                .getByLabel('HTTP 基础地址', { exact: true })
                .fill(`http://127.0.0.1:${httpPort}/`)
        } else {
            await page
                .getByLabel('主机或 IP', { exact: true })
                .fill('127.0.0.1')
            await page
                .getByLabel('端口', { exact: true })
                .fill(String(mqttPort))
        }
        const saved = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/connections'),
        )
        await page
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
        expect((await saved).status()).toBe(200)
        ids[kind] = (await (await saved).json()).connection.id
        await expect(
            page
                .getByRole('row')
                .filter({ hasText: `${kind.toUpperCase()} ·` }),
        ).toBeVisible()
    }
    return ids
}

async function source(page: Page, protocol: Protocol) {
    await page.goto('/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page.getByLabel('设备 key', { exact: true }).fill('meter')
    await page.getByLabel('设备名称', { exact: true }).fill('JSON 电表')
    await page
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption(protocol)
    await page
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({
            label: `${protocol.toUpperCase()} (${protocol})`,
        })
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page.getByRole('button', { name: /JSON 电表 meter/ }).click()
    await page
        .getByRole('link', { name: '数据来源与点位', exact: true })
        .click()
    await page
        .getByRole('button', { name: '新建数据来源', exact: true })
        .click()
    await page.getByLabel('来源 key', { exact: true }).fill('readings')
    await page.getByLabel('来源名称', { exact: true }).fill('读数')
    if (protocol === 'http') {
        await page.getByLabel('采集周期（毫秒）', { exact: true }).fill('1000')
        await page
            .getByLabel('HTTP 路径与非秘密查询参数', { exact: true })
            .fill('/readings?floor=1')
    } else {
        await page
            .getByLabel('MQTT Topic', { exact: true })
            .fill('workflow/input/meter')
        await page
            .getByRole('combobox', { name: '订阅 QoS', exact: true })
            .selectOption('1')
    }
    await page
        .getByLabel('上游时间 JSON 定位（可选）', { exact: true })
        .fill('/sampleTime')
    await page
        .getByRole('combobox', { name: '上游时间格式', exact: true })
        .selectOption('rfc3339')
    for (const [index, key, type] of [
        [1, 'energy', 'integer'],
        [2, 'temperature', 'number'],
    ] as const) {
        await page
            .getByRole('button', { name: '添加 JSON 字段', exact: true })
            .click()
        await page
            .getByLabel(`JSON 字段 ${index} key`, { exact: true })
            .fill(key)
        await page
            .getByLabel(`JSON 字段 ${index} path`, { exact: true })
            .fill('/' + key)
        await page
            .getByLabel(`JSON 字段 ${index} type`, { exact: true })
            .selectOption(type)
    }
    await page
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    await expect(page.getByRole('status')).toHaveText(
        '来源与点表已保存到网关草稿，尚未应用。',
    )
}

async function route(page: Page, input: Protocol) {
    const output = input === 'http' ? 'mqtt' : 'http'
    await page.goto('/routes')
    await page.getByRole('button', { name: '新建路线', exact: true }).click()
    await page.getByLabel('路线标识', { exact: true }).fill('json-relay')
    await page.getByLabel('路线名称', { exact: true }).fill('JSON 协议转发')
    await page.getByLabel('参与静态编译（草稿启用）', { exact: true }).check()
    const canvas = page.getByLabel('路线画布', { exact: true })
    await canvas.scrollIntoViewIfNeeded()
    for (const [name, x] of [
        [`${input.toUpperCase()} 输入`, 120],
        ['字段映射', 400],
        [`${output.toUpperCase()} 输出`, 680],
    ] as const) {
        await page
            .getByRole('button', { name, exact: true })
            .dragTo(canvas, { targetPosition: { x, y: 150 } })
        if (x === 120) {
            await page
                .getByLabel('JSON 电表 / 读数 (readings)', { exact: true })
                .check()
        } else if (x === 400) {
            const mapping = page.getByRole('form', {
                name: '字段映射规则表',
                exact: true,
            })
            for (const [row, target, original, type] of [
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
                    .getByLabel(`目标类型 ${row}`, { exact: true })
                    .selectOption(type)
                await mapping
                    .getByLabel(`来源字段 / Pointer / 固定值 ${row}`, {
                        exact: true,
                    })
                    .fill(original)
            }
            await mapping.getByLabel('比例 2', { exact: true }).fill('0.1')
            await mapping.getByLabel('偏移 2', { exact: true }).fill('-40')
            await mapping.getByLabel('单位 2', { exact: true }).fill('°C')
            await mapping
                .getByRole('button', { name: '确认映射规则', exact: true })
                .click()
        } else {
            await page
                .getByLabel(`${output.toUpperCase()} (${output})`, {
                    exact: true,
                })
                .check()
            const form = page.getByRole('form', {
                name: '输出参数',
                exact: true,
            })
            if (output === 'http') {
                await form
                    .getByLabel('输出相对路径', { exact: true })
                    .fill('/capture')
            } else {
                await form
                    .getByLabel('发布 Topic', { exact: true })
                    .fill('workflow/output/{deviceKey}')
                await form
                    .getByRole('combobox', { name: '发布 QoS', exact: true })
                    .selectOption('1')
            }
            await form
                .getByRole('button', { name: '确认输出参数', exact: true })
                .click()
        }
    }
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

for (const protocol of ['http', 'mqtt'] as const) {
    for (const creator of ['administrator', 'configurator'] as const) {
        test(`no JSON ${protocol} workflow: ${creator}, viewer, entire browser exit and product Host restart`, async ({
            browser,
            playwright,
        }, testInfo) => {
            test.setTimeout(180_000)
            const gateway = await startJsonWorkflow()
            let owned: Browser | undefined
            let page: Page | undefined
            let observer: APIRequestContext | undefined
            const pageErrors: string[] = []
            const external: string[] = []
            const launch = async () => {
                owned = await browser.browserType().launch({
                    executablePath: process.env.TENONEDGE_TEST_EXECUTABLE,
                })
                const next = await owned.newPage({
                    baseURL: gateway.url,
                    viewport: { width: 1800, height: 1200 },
                })
                next.setDefaultTimeout(15_000)
                next.on('pageerror', (error) => pageErrors.push(error.message))
                next.on('dialog', (dialog) => dialog.accept())
                await next.context().route('**/*', (request) => {
                    if (new URL(request.request().url()).origin === gateway.url)
                        return request.continue()
                    external.push(request.request().url())
                    return request.abort()
                })
                return next
            }
            const bodies = () =>
                protocol === 'http'
                    ? gateway.broker.bodies()
                    : gateway.deliveries.map((item) => item.body)
            try {
                page = await launch()
                await page.goto('/')
                await page
                    .getByLabel('账号', { exact: true })
                    .fill('workflow-admin')
                for (const name of ['密码', '确认密码'])
                    await page
                        .getByLabel(name, { exact: true })
                        .fill(adminPassword)
                await page
                    .getByRole('button', {
                        name: '创建管理员并登录',
                        exact: true,
                    })
                    .click()
                await expect(
                    page.getByRole('heading', { name: '总览', exact: true }),
                ).toBeVisible()
                if (new URL(gateway.url).hostname !== '127.0.0.1')
                    expect(await page.evaluate(() => isSecureContext)).toBe(
                        false,
                    )
                await createUser(page, 'workflow-viewer', 'viewer')
                if (creator === 'configurator') {
                    await createUser(page, 'workflow-editor', 'configurator')
                    await page
                        .getByRole('button', { name: '退出登录', exact: true })
                        .click()
                    await login(
                        page,
                        'workflow-editor',
                        temporaryPassword,
                        true,
                    )
                }
                // All configuration writes originate in visible controls. JSON below is upstream protocol data only.
                const ids = await connections(
                    page,
                    gateway.httpPort,
                    gateway.broker.port,
                )
                await source(page, protocol)
                await route(page, protocol)
                expect(gateway.reads).toHaveLength(0)
                expect(bodies()).toHaveLength(0)
                if (protocol === 'mqtt')
                    await gateway.broker.publish(
                        'workflow/input/meter',
                        sample,
                        true,
                    )
                const preview = page.getByRole('region', {
                    name: '路线样本预览',
                    exact: true,
                })
                await preview
                    .getByRole('combobox', {
                        name: '预览数据来源',
                        exact: true,
                    })
                    .selectOption({ index: 1 })
                const previewAction =
                    protocol === 'http'
                        ? '现场读取 HTTP 一次并预览'
                        : '抓取 MQTT 一条消息并预览'
                await preview
                    .getByRole('button', { name: previewAction, exact: true })
                    .click()
                await expect(preview).toContainText('9007199254740993')
                await expect(preview).toContainText('198.5')
                await expect(preview).toContainText('1 条路径中 1 条计算成功')
                expect(bodies()).toHaveLength(0) // Preview must never deliver to either independent receiver.
                await applyRoute(page)
                // Separate GET-only observer survives closing the browser; it never mutates gateway state.
                observer = await playwright.request.newContext({
                    baseURL: gateway.url,
                    storageState: await page.context().storageState(),
                })
                const waitForSubscription = async (minimum: number) => {
                    if (protocol !== 'mqtt') return
                    await expect
                        .poll(
                            async () => {
                                const result = await observer!.get(
                                    root + '/runtime/status',
                                )
                                expect(result.status()).toBe(200)
                                return (
                                    (await result.json()).persistentCounters[
                                        `connection/${ids.mqtt}/mqtt/subscribed`
                                    ] ?? 0
                                )
                            },
                            { timeout: 20_000 },
                        )
                        .toBeGreaterThanOrEqual(minimum)
                }
                const receiveMore = async () => {
                    const before = bodies().length
                    const readsBefore = gateway.reads.length
                    if (protocol === 'mqtt')
                        await gateway.broker.publish(
                            'workflow/input/meter',
                            sample,
                        )
                    else
                        // A previously queued delivery alone cannot prove that
                        // polling continues after browser exit / Host restart.
                        await expect
                            .poll(() => gateway.reads.length, {
                                timeout: 15_000,
                            })
                            .toBeGreaterThan(readsBefore)
                    await expect
                        .poll(() => bodies().length, { timeout: 15_000 })
                        .toBeGreaterThan(before)
                }
                await waitForSubscription(1)
                await receiveMore()
                const applied = await (
                    await observer.get(root + '/configuration/draft')
                ).json()
                expect(applied.effectiveRevision).toBe(1)
                await page.goto('/runtime/devices')
                await page
                    .getByRole('button', {
                        name: '查看 JSON 电表',
                        exact: true,
                    })
                    .click()
                const values = page.getByRole('table').filter({
                    has: page.getByRole('columnheader', {
                        name: '值 / 类型',
                        exact: true,
                    }),
                })
                await expect(values).toContainText('9007199254740993')
                await expect(values).toContainText('2385')
                await page.screenshot({
                    path: testInfo.outputPath('current-values.png'),
                    fullPage: true,
                })
                const beforeBrowserExit = bodies().length
                await owned!.close()
                expect(owned!.isConnected()).toBe(false)
                owned = undefined
                const readsWhenBrowserClosed = gateway.reads.length
                await receiveMore()
                const readsAfterBrowserClosed = gateway.reads.length
                const beforeHostRestart = bodies().length
                await gateway.restart()
                await waitForSubscription(2)
                const readsWhenHostRestarted = gateway.reads.length
                await receiveMore()
                const readsAfterHostRestarted = gateway.reads.length
                page = await launch() // New browser with no injected session or editor state.
                await login(page, 'workflow-viewer', temporaryPassword, true)
                await page.goto('/connections')
                await expect(
                    page.getByRole('button', { name: '新建连接', exact: true }),
                ).toHaveCount(0)
                await page.goto('/routes')
                await page
                    .getByRole('button', {
                        name: '打开 JSON 协议转发',
                        exact: true,
                    })
                    .click()
                await expect(
                    page.getByRole('button', {
                        name: '保存路线草稿',
                        exact: true,
                    }),
                ).toBeDisabled()
                await expect(
                    page.getByRole('button', {
                        name: previewAction,
                        exact: true,
                    }),
                ).toHaveCount(0)
                await page.reload()
                await expect(
                    page.getByRole('heading', {
                        name: '路线草稿',
                        exact: true,
                    }),
                ).toBeVisible()
                const restored = await (
                    await page.request.get(root + '/configuration/draft')
                ).json()
                expect(restored.effectiveRevision).toBe(1)
                expect(restored.version).toBe(applied.version)
                const session = await (
                    await page.request.get(root + '/auth/session')
                ).json()
                const denied = await page.request.post(
                    root + '/routes/compile',
                    {
                        headers: { 'X-CSRF-Token': session.csrfToken },
                        data: {},
                    },
                )
                expect(denied.status()).toBe(403) // Negative authorization probe, never a configuration fixture.
                const missing = await page.request.get(
                    root + '/missing-json-workflow-api',
                )
                expect(missing.status()).toBe(404)
                expect(missing.headers()['content-type']).toContain(
                    'application/problem+json',
                )
                for (const body of bodies()) {
                    expect(body).toContain('"energyTotal":9007199254740993')
                    expect(body).toContain('"temperatureC":198.5')
                    const metadata = JSON.parse(body) // Only metadata is parsed; large values are asserted in original text above.
                    expect(metadata.deviceKey).toBe('meter')
                    expect(Date.parse(metadata.collectedAt)).toBe(
                        Date.parse(sampleTime),
                    )
                    expect(metadata.timeSource).toBe('upstream')
                    expect(metadata.messageId).toBeTruthy()
                }
                for (const item of gateway.deliveries)
                    expect(JSON.parse(item.body).messageId).toBe(item.id)
                expect(gateway.errors).toEqual([])
                expect(pageErrors).toEqual([])
                expect(external).toEqual([])
                await testInfo.attach('protocol-workflow-evidence', {
                    body: Buffer.from(
                        JSON.stringify({
                            protocol,
                            creator,
                            beforeBrowserExit,
                            beforeHostRestart,
                            received: bodies().length,
                            httpReads: gateway.reads.length,
                            readsWhenBrowserClosed,
                            readsAfterBrowserClosed,
                            readsWhenHostRestarted,
                            readsAfterHostRestarted,
                            revision: restored.effectiveRevision,
                            origin: gateway.url,
                            browser: await page.evaluate(
                                () => navigator.userAgent,
                            ),
                            external,
                        }),
                    ),
                    contentType: 'application/json',
                })
            } catch (error) {
                if (page && !page.isClosed())
                    await testInfo
                        .attach('workflow-page', {
                            body: await page.locator('body').ariaSnapshot(),
                            contentType: 'text/plain',
                        })
                        .catch(() => {})
                throw error
            } finally {
                try {
                    await observer?.dispose()
                } finally {
                    try {
                        await owned?.close()
                    } finally {
                        await gateway.close()
                    }
                }
            }
        })
    }
}
