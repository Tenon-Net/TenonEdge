import { test, expect, type Page } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const owned = test.extend<{ dashboardUrl: string }>({
    dashboardUrl: async ({ browserName }, use) => {
        const data = await mkdtemp(
            join(tmpdir(), `tenonedge-dashboard-${browserName}-`),
        )
        try {
            const seed = join(data, 'seed.json')
            // Real runtime samples from software inputs, not physical-device evidence or fabricated chart data.
            await writeFile(
                seed,
                JSON.stringify({
                    schemaVersion: 1,
                    units: [
                        {
                            kind: 'input.simulator',
                            key: 'meter/sample',
                            configuration: { periodMs: 1000, seed: 42 },
                        },
                    ],
                }),
            )
            const host = await startOwnedHost(data, 0, [
                `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
            ])
            try {
                await use(host.url)
            } finally {
                await host.stop()
            }
        } finally {
            await rm(data, { recursive: true, force: true })
        }
    },
})
async function setup(page: Page, url: string) {
    await page.goto(url)
    await page.getByLabel('账号', { exact: true }).fill('dashboard-admin')
    await page.getByLabel('密码', { exact: true }).fill('Dashboard-test-8294!')
    await page
        .getByLabel('确认密码', { exact: true })
        .fill('Dashboard-test-8294!')
    await page.getByLabel('网关名称（可选）').fill('装配车间网关')
    await page.getByRole('button', { name: '创建管理员并登录' }).click()
    await expect(page.locator('[data-dashboard="operations"]')).toBeVisible()
}
owned(
    'real dashboard uses full width, sampled rates, keyboard chart navigation and responsive shadcn themes without business writes',
    async ({ page, dashboardUrl }, testInfo) => {
        test.setTimeout(90_000)
        const errors: string[] = [],
            writes: string[] = [],
            external: string[] = []
        page.on('pageerror', (error) => errors.push(error.message))
        await page.setViewportSize({ width: 1920, height: 1080 })
        await setup(page, dashboardUrl)
        page.on('request', (request) => {
            if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()))
                writes.push(request.url())
            if (new URL(request.url()).origin !== new URL(dashboardUrl).origin)
                external.push(request.url())
        })
        const dashboard = page.locator('[data-dashboard="operations"]')
        expect(
            await page.evaluate(
                () => getComputedStyle(document.documentElement).overflowX,
            ),
        ).toBe('clip')
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toHaveClass('sr-only')
        await expect(
            page.getByRole('region', { name: '设备状态', exact: true }),
        ).toContainText('1')
        const plot = page.getByRole('img', { name: '最近一小时趋势' })
        await expect(plot).toBeVisible({ timeout: 25_000 })
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                dashboardUrl +
                                    '/api/edge-admin/v1/runtime/trends',
                            )
                        ).json()
                    ).length,
                { timeout: 25_000 },
            )
            .toBeGreaterThanOrEqual(2)
        await page.getByRole('button', { name: '刷新看板' }).click()
        await expect(dashboard).toContainText('原始采样：收到')
        await expect(plot.locator('path').first()).toHaveAttribute('d', /L/)
        await plot.focus()
        await plot.press('Home')
        const first = await dashboard.getByText(/^采样 \d/).textContent()
        await plot.press('End')
        await expect(dashboard.getByText(/^采样 \d/)).not.toHaveText(first!)
        const before = await page.evaluate(() => scrollY)
        await page.getByRole('button', { name: '近 15 分钟' }).click()
        await expect(
            page.getByRole('button', { name: '近 15 分钟' }),
        ).toHaveAttribute('aria-pressed', 'true')
        expect(await page.evaluate(() => scrollY)).toBe(before)
        await page.getByRole('button', { name: '近 1 小时' }).click()
        await page.screenshot({
            path: testInfo.outputPath('dashboard-light-1920.png'),
            fullPage: true,
        })
        for (const width of [2560, 1440, 768, 414, 375, 320]) {
            await page.setViewportSize({
                width,
                height: width < 800 ? 900 : 1080,
            })
            await expect
                .poll(() =>
                    page.evaluate(
                        () =>
                            document.documentElement.scrollWidth <= innerWidth,
                    ),
                )
                .toBe(true)
            expect(
                await dashboard.evaluate(
                    (element) => element.scrollWidth <= element.clientWidth,
                ),
            ).toBe(true)
            const outside = await dashboard
                .locator('a,button,svg,[data-slot="card"]')
                .evaluateAll((elements) =>
                    elements
                        .filter((element) => {
                            const r = element.getBoundingClientRect()
                            return (
                                r.width > 0 &&
                                (r.left < -1 || r.right > innerWidth + 1)
                            )
                        })
                        .map((element) => element.textContent),
                )
            expect(outside).toEqual([])
            const bounds = await dashboard.boundingBox()
            expect(bounds!.x).toBeLessThanOrEqual(width < 768 ? 24 : 280)
            if (width === 2560) expect(bounds!.width).toBeGreaterThan(2200)
            await expect(
                page.getByRole('region', { name: '队列压力' }),
            ).toBeVisible()
            await page.screenshot({
                path: testInfo.outputPath(`dashboard-light-${width}.png`),
                fullPage: true,
            })
        }
        await page.setViewportSize({ width: 1440, height: 1000 })
        await page.getByRole('button', { name: '切换主题' }).click()
        await page.getByRole('menuitem', { name: '深色', exact: true }).click()
        await expect(page.locator('html')).toHaveClass('dark')
        await page.screenshot({
            path: testInfo.outputPath('dashboard-dark-1440.png'),
            fullPage: true,
        })
        await page.setViewportSize({ width: 375, height: 900 })
        await page.screenshot({
            path: testInfo.outputPath('dashboard-dark-375.png'),
            fullPage: true,
        })
        await page
            .getByRole('link', { name: '设备运行与当前值', exact: true })
            .click()
        await expect(
            page.getByRole('heading', {
                name: '设备运行与当前值',
                exact: true,
            }),
        ).toBeVisible()
        expect(
            await page.evaluate(
                () => getComputedStyle(document.documentElement).overflowX,
            ),
        ).not.toBe('clip')
        expect(errors).toEqual([])
        expect(writes).toEqual([])
        expect(external).toEqual([])
    },
)

owned(
    'failed dashboard panels do not turn unavailable queues or exception history into healthy zeros',
    async ({ page, dashboardUrl }) => {
        await setup(page, dashboardUrl)
        const exceptions = page.getByRole('region', { name: '最近异常' })
        await expect(exceptions).toContainText('setup.windowOpen')
        await page.route('**/runtime/events?**', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await page.route('**/runtime/outputs?**', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await page.getByRole('button', { name: '刷新看板' }).click()
        await expect(exceptions.getByRole('alert')).toBeVisible({
            timeout: 15_000,
        })
        await expect(exceptions).not.toContainText('暂无警告与错误')
        await expect(exceptions).not.toContainText('setup.windowOpen')
        await expect(page.getByRole('progressbar')).toHaveAttribute(
            'aria-valuetext',
            '未知',
        )
        const queue = page.getByRole('region', { name: '队列压力' })
        await expect(queue).toContainText('—')
        await expect(
            page.getByRole('region', { name: '设备状态', exact: true }),
        ).toContainText('1')
        await page.unroute('**/runtime/events?**')
        await page.unroute('**/runtime/outputs?**')
        await page.getByRole('button', { name: '刷新看板' }).click()
        await expect(exceptions.getByRole('alert')).toHaveCount(0)
        await expect(exceptions).toContainText('setup.windowOpen')
        await expect(page.getByRole('meter')).toHaveAttribute(
            'aria-valuenow',
            '0',
        )
    },
)
