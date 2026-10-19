import { expect, test } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

for (const height of [700, 1200]) {
    test(`palette click after scrolling places a reachable node inside the canvas (${height}px)`, async ({
        browser,
    }) => {
        const data = await mkdtemp(
            join(tmpdir(), 'tenonedge-canvas-placement-'),
        )
        const host = await startOwnedHost(data, 0, [], '127.0.0.1', true)
        const context = await browser.newContext({
            baseURL: host.url,
            viewport: { width: 1800, height },
        })
        try {
            const page = await context.newPage()
            await page.goto('/')
            await page.getByLabel('账号', { exact: true }).fill('canvas-admin')
            for (const name of ['密码', '确认密码'])
                await page
                    .getByLabel(name, { exact: true })
                    .fill('Canvas-placement-8437!')
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await expect(
                page.getByRole('heading', { name: '总览', exact: true }),
            ).toBeVisible()
            await page.goto('/routes')
            await page
                .getByRole('button', { name: '新建路线', exact: true })
                .click()
            const canvas = page.getByLabel('路线画布', { exact: true })
            await canvas.scrollIntoViewIfNeeded()
            await page
                .getByRole('button', { name: '测试标量输入 (v3)', exact: true })
                .dragTo(canvas, { targetPosition: { x: 120, y: 150 } })
            // The real extension palette extends below the canvas. Clicking an
            // output must not interpret a fixed browser Y as a canvas position.
            const paletteItem = page.getByRole('button', {
                name: 'output.authwiremodule (v1)',
                exact: true,
            })
            await paletteItem.scrollIntoViewIfNeeded()
            await paletteItem.click()
            const nodes = page.locator('.react-flow__node')
            await expect(nodes).toHaveCount(2)
            const output = nodes.nth(1)
            const area = (await canvas.boundingBox())!
            const placed = (await output.boundingBox())!
            expect(placed.x).toBeGreaterThanOrEqual(area.x)
            expect(placed.y).toBeGreaterThanOrEqual(area.y)
            expect(placed.x + placed.width).toBeLessThanOrEqual(
                area.x + area.width,
            )
            expect(placed.y + placed.height).toBeLessThanOrEqual(
                area.y + area.height,
            )
            expect(
                await output.evaluate((element) => {
                    const box = element.getBoundingClientRect()
                    return element.contains(
                        document.elementFromPoint(
                            box.x + box.width / 2,
                            box.y + box.height / 2,
                        ),
                    )
                }),
            ).toBe(true)
            await nodes
                .nth(0)
                .locator('.react-flow__handle.source')
                .dragTo(output.locator('.react-flow__handle.target'))
            await expect(page.locator('.react-flow__edge')).toHaveCount(1)
        } finally {
            try {
                await context.close()
            } finally {
                try {
                    expect(await host.stop()).toBe(0)
                } finally {
                    await rm(data, { recursive: true, force: true })
                }
            }
        }
    })
}
