import { test, expect } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const password = 'Design-fixture-3419!'
test('product page families are responsive; settings keyboard navigation preserves unsaved edits and performs no writes', async ({
    browser,
}, testInfo) => {
    test.setTimeout(180_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-app-design-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        const errors: string[] = []
        page.on('pageerror', (error) => errors.push(error.message))
        await page.goto('/system')
        await page.getByLabel('账号', { exact: true }).fill('design-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await page
            .getByRole('button', { name: '修改网关名称', exact: true })
            .click()
        await page
            .getByLabel('新网关名称', { exact: true })
            .fill('车间一号 · 未保存')
        const writes: string[] = []
        page.on('request', (request) => {
            if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()))
                writes.push(request.url())
        })
        const tabs = page.getByRole('tablist', {
            name: '设置分类',
            exact: true,
        })
        await tabs.getByRole('tab', { name: '基本信息', exact: true }).focus()
        await page.keyboard.press('ArrowRight')
        await expect(
            tabs.getByRole('tab', { name: '网络与访问', exact: true }),
        ).toBeFocused()
        await expect(
            page.getByRole('region', { name: '出站访问规则', exact: true }),
        ).toBeVisible()
        await page.keyboard.press('End')
        await expect(
            tabs.getByRole('tab', { name: '配置导入导出', exact: true }),
        ).toBeFocused()
        await page.keyboard.press('Home')
        await expect(
            page.getByLabel('新网关名称', { exact: true }),
        ).toHaveValue('车间一号 · 未保存')
        await tabs.getByRole('tab', { name: '网络与访问', exact: true }).click()
        page.once('dialog', (dialog) => dialog.dismiss())
        await page
            .getByRole('navigation', { name: '主导航' })
            .getByRole('link', { name: '连接', exact: true })
            .click()
        await expect(page).toHaveURL(/\/system$/)
        await tabs.getByRole('tab', { name: '基本信息', exact: true }).click()
        await expect(
            page.getByLabel('新网关名称', { exact: true }),
        ).toHaveValue('车间一号 · 未保存')
        await page
            .getByRole('button', { name: '取消改名', exact: true })
            .click()
        expect(writes).toEqual([])
        const routes = [
            '/connections',
            '/devices',
            '/routes',
            '/configuration',
            '/configuration/migrations',
            '/configuration/migration-intents',
            '/diagnostics',
            '/runtime/devices',
            '/runtime/outputs',
            '/runtime/messages',
            '/deliveries',
            '/deliveries/migrate',
            '/system',
            '/users',
            '/tokens',
            '/audit',
            '/api-docs',
            '/account',
            '/unknown-design-page',
        ]
        const menuPages = new Set([
            '/connections',
            '/devices',
            '/routes',
            '/diagnostics',
            '/deliveries',
            '/system',
            '/users',
            '/tokens',
            '/audit',
            '/api-docs',
        ])
        for (const route of routes) {
            await page.goto(route)
            await expect(page.locator('#content h1')).toHaveCount(1)
            await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1)
            if (route === '/api-docs')
                await expect(
                    page.getByLabel('筛选方法、路径、说明或 operationId', {
                        exact: true,
                    }),
                ).toBeVisible()
            for (const width of [320, 375, 414, 768, 1440]) {
                await page.setViewportSize({ width, height: 900 })
                await page.evaluate(() => window.scrollTo(0, 0))
                const titleBox = await page.locator('#content h1').boundingBox()
                if (menuPages.has(route))
                    expect(
                        titleBox?.height,
                        `${route} has no visible menu title`,
                    ).toBeLessThanOrEqual(1)
                else
                    expect(
                        titleBox?.height,
                        `${route} keeps its contextual title`,
                    ).toBeGreaterThan(1)
                const overflow = await page.evaluate(() => ({
                    width: innerWidth,
                    root: document.documentElement.scrollWidth,
                    body: document.body.scrollWidth,
                }))
                expect(
                    overflow.root,
                    `${route} at ${width}: ${JSON.stringify(overflow)}`,
                ).toBeLessThanOrEqual(width)
                const guide = page.locator('#content .product-help').first()
                if (width === 320 && (await guide.count())) {
                    await guide.locator('summary').click()
                    expect(
                        await page.evaluate(
                            () => document.documentElement.scrollWidth,
                        ),
                        `${route} expanded help`,
                    ).toBeLessThanOrEqual(width)
                    await guide.locator('summary').click()
                }
                if ([320, 1440].includes(width))
                    await page.screenshot({
                        path: testInfo.outputPath(
                            `${route.replaceAll('/', '_')}-${width}.png`,
                        ),
                        fullPage: route !== '/api-docs',
                    })
            }
        }
        expect(errors).toEqual([])
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('selected connection, device, point table and route workspaces retain edits across sizes and themes', async ({
    browser,
}, testInfo) => {
    test.setTimeout(180_000)
    const { startWorkflowGateway } = await import('./support/gateway-workflow')
    const gateway = await startWorkflowGateway()
    const context = await browser.newContext({
        baseURL: gateway.url,
        viewport: { width: 1800, height: 1100 },
    })
    try {
        const page = await context.newPage()
        const errors: string[] = []
        page.on('pageerror', (e) => errors.push(e.message))
        await page.goto('/connections')
        await page.getByLabel('账号', { exact: true }).fill('workbench-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        async function capture(name: string) {
            for (const theme of ['light', 'dark'] as const) {
                await page.emulateMedia({ colorScheme: theme })
                await expect(page.locator('html')).toHaveClass(theme)
                for (const width of [
                    320, 375, 414, 768, 1280, 1440, 1700, 1800,
                ]) {
                    await page.setViewportSize({ width, height: 1100 })
                    await page.evaluate(() => window.scrollTo(0, 0))
                    await expect(page.locator('#content h1')).toHaveCount(1)
                    const rootWidth = await page.evaluate(
                        () => document.documentElement.scrollWidth,
                    )
                    if (rootWidth > width) {
                        await testInfo.attach('overflow-elements', {
                            body: JSON.stringify(
                                await page
                                    .locator('body *')
                                    .evaluateAll((nodes) =>
                                        nodes
                                            .map((n) => {
                                                const b =
                                                    n.getBoundingClientRect()
                                                return {
                                                    tag: n.tagName,
                                                    cls: n.className,
                                                    text: n.textContent?.slice(
                                                        0,
                                                        100,
                                                    ),
                                                    left: b.left,
                                                    right: b.right,
                                                    width: b.width,
                                                }
                                            })
                                            .filter(
                                                (n) =>
                                                    n.right > innerWidth ||
                                                    n.left < 0,
                                            )
                                            .slice(0, 60),
                                    ),
                                null,
                                2,
                            ),
                            contentType: 'application/json',
                        })
                        await testInfo.attach('overflow-screen', {
                            body: await page.screenshot({ fullPage: true }),
                            contentType: 'image/png',
                        })
                    }
                    expect(
                        rootWidth,
                        `${name}/${theme}/${width}`,
                    ).toBeLessThanOrEqual(width)
                    if (name.includes('route') && width >= 1280)
                        expect(
                            (await page
                                .getByLabel('路线画布', { exact: true })
                                .boundingBox())!.width,
                            `${name}/${theme}/${width}: usable canvas`,
                        ).toBeGreaterThan(500)
                    if (name.includes('route') && width >= 1700) {
                        const area = (await page
                            .getByLabel('路线画布', { exact: true })
                            .boundingBox())!
                        const properties = (await page
                            .getByRole('complementary', {
                                name: '节点属性',
                                exact: true,
                            })
                            .boundingBox())!
                        expect(properties.x).toBeGreaterThanOrEqual(
                            area.x + area.width,
                        )
                    }
                    if (name.includes('route') && width === 1280) {
                        // Fitting a graph must reveal the canvas, not only pan
                        // an off-screen box while the page remains in a form.
                        await page
                            .getByRole('complementary', {
                                name: '节点属性',
                                exact: true,
                            })
                            .evaluate((element) =>
                                element.scrollIntoView({ block: 'end' }),
                            )
                        await page
                            .getByRole('button', {
                                name: '适应画布',
                                exact: true,
                            })
                            .click()
                        const area = (await page
                            .getByLabel('路线画布', { exact: true })
                            .boundingBox())!
                        const header = (await page
                            .locator('.header-fixed')
                            .boundingBox())!
                        expect(area.y).toBeGreaterThanOrEqual(
                            header.y + header.height,
                        )
                        expect(area.y + area.height).toBeLessThanOrEqual(1100)
                        await page.evaluate(() => window.scrollTo(0, 0))
                    }
                    if ([320, 1280, 1800].includes(width))
                        await page.screenshot({
                            path: testInfo.outputPath(
                                `${name}-${theme}-${width}.png`,
                            ),
                            fullPage: true,
                        })
                }
            }
        }
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page.getByLabel('连接 key', { exact: true }).fill('plc')
        await page.getByLabel('连接名称', { exact: true }).fill('现场 PLC')
        await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
        await page
            .getByLabel('端口', { exact: true })
            .fill(String(gateway.modbusPort))
        await capture('new-connection')
        await page
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
        await expect(
            page.getByRole('row').filter({ hasText: '现场 PLC' }),
        ).toBeVisible()
        await page
            .getByRole('row')
            .filter({ hasText: '现场 PLC' })
            .getByRole('button', { name: '编辑连接', exact: true })
            .click()
        await capture('connection-editor')
        await expect(page.getByLabel('连接名称', { exact: true })).toHaveValue(
            '现场 PLC',
        )
        await page.goto('/devices')
        await page
            .getByRole('button', { name: '新建设备', exact: true })
            .click()
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
        await capture('device-editor')
        await page
            .getByRole('link', { name: '数据来源与点位', exact: true })
            .click()
        await page
            .getByRole('button', { name: '新建采集组', exact: true })
            .click()
        await page.getByLabel('来源 key', { exact: true }).fill('readings')
        await page.getByLabel('来源名称', { exact: true }).fill('电表读数')
        await page
            .getByRole('button', { name: '添加空行', exact: true })
            .click()
        await page
            .getByLabel('第 1 行 key', { exact: true })
            .fill('temperature')
        await page.getByLabel('第 1 行 地址', { exact: true }).fill('4')
        await capture('point-editor')
        await expect(
            page.getByLabel('第 1 行 key', { exact: true }),
        ).toHaveValue('temperature')
        await page
            .getByRole('button', { name: '保存来源草稿', exact: true })
            .click()
        await expect(page.getByRole('status')).toHaveText(
            '来源与点表已保存到网关草稿，尚未应用。',
        )
        await page.goto('/routes')
        await page
            .getByRole('button', { name: '新建路线', exact: true })
            .click()
        await page.getByLabel('路线标识', { exact: true }).fill('saved-route')
        await page.getByLabel('路线名称', { exact: true }).fill('已保存路线')
        await capture('new-route-editor')
        await page
            .getByRole('button', { name: '保存路线草稿', exact: true })
            .click()
        await expect(
            page
                .getByRole('status')
                .filter({ hasText: '路线草稿已保存，未应用。' }),
        ).toBeVisible()
        await expect(
            page.getByRole('button', { name: '打开 已保存路线', exact: true }),
        ).toBeVisible()
        await page
            .getByRole('button', { name: '新建路线', exact: true })
            .click()
        await page.getByLabel('路线标识', { exact: true }).fill('meter-route')
        await page
            .getByLabel('路线名称', { exact: true })
            .fill('电表采集转发 · 未保存')
        await page
            .getByRole('button', { name: 'Modbus 输入', exact: true })
            .click()
        await page
            .getByLabel('现场电表 / 电表读数 (readings)', { exact: true })
            .check()
        await capture('route-editor')
        await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
            '电表采集转发 · 未保存',
        )
        expect(
            (await page.getByLabel('路线画布', { exact: true }).boundingBox())!
                .width,
        ).toBeGreaterThan(600)
        expect(errors).toEqual([])
    } finally {
        await context.close()
        await gateway.close()
    }
})
