import { expect, test } from '@playwright/test'
import type { GraphDocument } from '../src/features/canvas/model'
import { startTrustedBrowser } from './support/trusted-browser'

// Actual executable selection is shared with the normal runner. This test adds
// trusted HTTPS/OS-clipboard coverage, not a claim about Excel or other browsers.
test('trusted HTTPS supports native clipboard, large point grids and canvas gestures', async ({
    browser,
}, testInfo) => {
    test.setTimeout(60_000)
    const host = await startTrustedBrowser()
    const errors: string[] = []
    try {
        expect(host.browser.version()).toBe(browser.version())
        const page = await host.context.newPage()
        page.on('pageerror', (error) => errors.push(error.message))
        await page.setViewportSize({ width: 1600, height: 1100 })
        await test.step('real setup, logout, login and deep-link refresh', async () => {
            await page.goto(host.url + '/prototypes/points')
            expect(await page.evaluate(() => isSecureContext)).toBe(true)
            await page.getByLabel('账号', { exact: true }).fill('browser-admin')
            const password = 'Browser-Compatibility-5938'
            await page.getByLabel('密码', { exact: true }).fill(password)
            await page.getByLabel('确认密码', { exact: true }).fill(password)
            await page.getByRole('button', { name: '创建管理员并登录' }).click()
            await expect(
                page.getByRole('heading', { name: '点表交互原型' }),
            ).toBeVisible()
            await page
                .getByRole('button', { name: '退出登录', exact: true })
                .click()
            await page.getByLabel('账号', { exact: true }).fill('browser-admin')
            await page.getByLabel('密码', { exact: true }).fill(password)
            await page
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '点表交互原型' }),
            ).toBeVisible()
            await page.reload()
            await expect(
                page.getByRole('heading', { name: '点表交互原型' }),
            ).toBeVisible()
            const cookies = await host.context.cookies(host.url)
            expect(
                cookies.some(
                    (cookie) =>
                        cookie.name.startsWith('__Host-') &&
                        cookie.secure &&
                        cookie.httpOnly,
                ),
            ).toBe(true)
        })

        // Only this owned, certificate-verified HTTPS origin gets clipboard access.
        // Fill the real browser clipboard, then use a trusted keyboard paste;
        // never dispatch ClipboardEvent or mutate the application store.
        await host.context.grantPermissions(
            ['clipboard-read', 'clipboard-write'],
            { origin: host.url },
        )
        async function paste(count: number, start: number, invalid = false) {
            const text = Array.from({ length: count }, (_, offset) => {
                const index = start + offset
                return [
                    invalid && offset === 0 ? 'bad key' : `point_${index}`,
                    `测点${index}`,
                    '3',
                    String(index),
                    'uint16',
                    '',
                    '',
                    '',
                    'true',
                ].join('\t')
            }).join('\n')
            await page.evaluate(async (value) => {
                await navigator.clipboard.writeText(value)
            }, text)
            const grid = page.getByTestId('point-grid')
            const observed = grid.evaluate(
                (element) =>
                    new Promise<{ trusted: boolean; length: number }>(
                        (resolve) => {
                            element.addEventListener(
                                'paste',
                                (event) => {
                                    const paste = event as ClipboardEvent
                                    resolve({
                                        trusted: paste.isTrusted,
                                        length:
                                            paste.clipboardData?.getData(
                                                'text/plain',
                                            ).length ?? 0,
                                    })
                                },
                                { once: true, capture: true },
                            )
                        },
                    ),
            )
            await grid.click()
            await grid.focus()
            await page.keyboard.press('Control+v')
            expect(await observed).toEqual({
                trusted: true,
                length: text.length,
            })
        }
        const cdp = await host.context.newCDPSession(page)
        const before = await cdp.send('Runtime.getHeapUsage')
        await test.step('1000-row trusted paste, error localization and correction', async () => {
            await paste(1000, 0, true)
            await expect(page.getByTestId('point-count')).toContainText(
                '1000 行 · 0 行选中 · 1 行错误',
            )
            await expect(
                page.locator('[data-point-index="0"]'),
            ).toHaveAttribute('data-invalid', 'true')
            await page
                .getByLabel('第 1 行 key', { exact: true })
                .fill('point_0')
            await expect(page.getByTestId('point-count')).toContainText(
                '0 行错误',
            )
        })
        const thousand = {
            heap: await cdp.send('Runtime.getHeapUsage'),
            timings: await page.getByTestId('point-timing').textContent(),
        }
        await test.step('10000-row virtual grid and last-row editing', async () => {
            await paste(9000, 1000)
            await expect(page.getByTestId('point-count')).toContainText(
                '10000 行 · 0 行选中 · 0 行错误',
            )
            expect(
                await page.locator('[data-point-index]').count(),
            ).toBeLessThan(40)
            await page.getByTestId('point-grid').evaluate((element) => {
                element.scrollTop = element.scrollHeight
            })
            const last = page.getByLabel('第 10000 行 key', { exact: true })
            await expect(last).toHaveValue('point_9999')
            await last.fill('edited_last')
            await expect(last).toHaveValue('edited_last')
        })
        await testInfo.attach('browser-and-point-observations', {
            contentType: 'application/json',
            body: JSON.stringify(
                {
                    version: host.browser.version(),
                    origin: host.url,
                    userAgent: await page.evaluate(() => navigator.userAgent),
                    certificateFingerprint: host.certificateFingerprint,
                    before,
                    thousand,
                    after: await cdp.send('Runtime.getHeapUsage'),
                    timings: await page
                        .getByTestId('point-timing')
                        .textContent(),
                    renderedRows: await page
                        .locator('[data-point-index]')
                        .count(),
                },
                null,
                2,
            ),
        })

        await page.goto(host.url + '/prototypes/canvas')
        await expect(
            page.getByRole('heading', { name: '画布交互原型' }),
        ).toBeVisible()
        await page
            .getByText('查看自有格式（定义与布局分离）', { exact: true })
            .click()
        const graph = async () =>
            JSON.parse(
                await page.getByTestId('graph-document').innerText(),
            ) as GraphDocument
        const area = page.getByLabel('路线画布', { exact: true })
        await test.step('dragged nodes, pointer connections and cycle rejection', async () => {
            const box = (await area.boundingBox())!
            const middle = (box.width - 212) / 2
            const nodes: [string, number, number][] = [
                ['Modbus 输入', 20, 100],
                ['字段映射', middle, 100],
                ['字段映射', middle, 280],
                ['MQTT 输出', box.width - 212, 280],
            ]
            for (const [name, x, y] of nodes)
                await page
                    .getByRole('button', { name, exact: true })
                    .dragTo(area, { targetPosition: { x, y } })
            await expect
                .poll(async () => (await graph()).definition.nodes.length)
                .toBe(4)
            const ids = (await graph()).definition.nodes.map((node) => node.id)
            const node = (id: string) =>
                page.locator(`.react-flow__node[data-id="${id}"]`)
            const connect = (source: string, target: string) =>
                node(source)
                    .locator('.react-flow__handle.source')
                    .dragTo(node(target).locator('.react-flow__handle.target'))
            for (let i = 0; i < 3; i++) await connect(ids[i], ids[i + 1])
            await expect
                .poll(async () => (await graph()).definition.edges.length)
                .toBe(3)
            await connect(ids[2], ids[1])
            await expect(page.getByRole('alert')).toContainText(
                '不允许自环或循环路线',
            )
            expect((await graph()).definition.edges).toHaveLength(3)
            await expect(page.locator('.react-flow__minimap-node')).toHaveCount(
                4,
            )
            await node(ids[1]).click()
        })
        await test.step('50-step keyboard undo and redo', async () => {
            const selectedId = (await graph()).definition.nodes[1].id
            for (let i = 0; i < 55; i++) {
                await page
                    .getByLabel('节点名称', { exact: true })
                    .fill(`映射${i}`)
                await page
                    .getByRole('button', { name: '修改名称', exact: true })
                    .click()
            }
            await area.focus()
            for (let i = 0; i < 50; i++) await page.keyboard.press('Control+z')
            expect(
                (await graph()).definition.nodes.find(
                    (node) => node.id === selectedId,
                )?.name,
            ).toBe('映射4')
            await expect(
                page.getByRole('button', { name: '撤销', exact: true }),
            ).toBeDisabled()
            for (let i = 0; i < 50; i++) await page.keyboard.press('Control+y')
            expect(
                (await graph()).definition.nodes.find(
                    (node) => node.id === selectedId,
                )?.name,
            ).toBe('映射54')
            await expect(
                page.getByRole('button', { name: '重做', exact: true }),
            ).toBeDisabled()
        })
        expect(errors).toEqual([])
        await testInfo.attach('trusted-https-canvas', {
            contentType: 'image/png',
            body: await page.screenshot(),
        })
    } catch (error) {
        const page = host.context.pages()[0]
        if (page && !page.isClosed())
            await testInfo.attach('trusted-https-failure', {
                contentType: 'image/png',
                body: await page.screenshot(),
            })
        throw error
    } finally {
        await host.close()
    }
})
