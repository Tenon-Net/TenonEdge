import { test, expect, type Page } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const password = 'Workspace-4827-fixture!'
const owned = test.extend<{ workspaceUrl: string }>({
    workspaceUrl: async ({ browserName }, use) => {
        const data = await mkdtemp(
            join(tmpdir(), `tenonedge-workspace-${browserName}-`),
        )
        try {
            const host = await startOwnedHost(data)
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
    await page.getByLabel('账号', { exact: true }).fill('workspace-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page.getByLabel('网关名称（可选）').fill('装配车间网关')
    await page.getByRole('button', { name: '创建管理员并登录' }).click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
}

owned(
    'sidebar collapse, theme changes and responsive layout preserve unsaved edits without writes',
    async ({ page, workspaceUrl }, testInfo) => {
        await page.setViewportSize({ width: 1440, height: 1000 })
        const errors: string[] = []
        page.on('pageerror', (error) => errors.push(error.message))
        await setup(page, workspaceUrl)
        const navigation = page.getByRole('navigation', { name: '主导航' })
        await expect(navigation).not.toContainText('原型')
        await expect(
            page.getByText('已连接到真实网关', { exact: true }),
        ).toHaveCount(0)
        await expect(
            navigation.getByRole('link', { name: '用户管理', exact: true }),
        ).toBeVisible()
        await page.screenshot({
            path: testInfo.outputPath('workspace-light.png'),
            fullPage: true,
        })
        await page.getByRole('link', { name: '账号安全', exact: true }).click()
        const input = page.getByLabel('新密码', { exact: true })
        await input.fill('Unsaved-local-only-5738!')
        const writes: string[] = []
        page.on('request', (request) => {
            if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()))
                writes.push(request.url())
        })
        const header = page.getByRole('banner')
        await header
            .getByRole('button', { name: '收起导航', exact: true })
            .click()
        await expect(
            header.getByRole('button', { name: '展开导航', exact: true }),
        ).toHaveAttribute('aria-expanded', 'false')
        await expect(input).toHaveValue('Unsaved-local-only-5738!')
        await navigation
            .getByRole('link', { name: '连接', exact: true })
            .hover()
        await expect(page.getByRole('tooltip')).toHaveText('连接')
        await header
            .getByRole('button', { name: '展开导航', exact: true })
            .click()
        await header.getByRole('button', { name: '切换主题' }).click()
        await page.getByRole('menuitem', { name: '深色', exact: true }).click()
        await expect(page.locator('html')).toHaveClass('dark')
        await expect(input).toHaveValue('Unsaved-local-only-5738!')
        await page.screenshot({
            path: testInfo.outputPath('workspace-dark.png'),
            fullPage: true,
        })
        await header.getByRole('button', { name: '切换主题' }).click()
        await page
            .getByRole('menuitem', { name: '跟随系统', exact: true })
            .click()
        await page.emulateMedia({ colorScheme: 'light' })
        await expect(page.locator('html')).toHaveClass('light')
        await page.emulateMedia({ colorScheme: 'dark' })
        await expect(page.locator('html')).toHaveClass('dark')
        await page.setViewportSize({ width: 390, height: 844 })
        await expect(input).toHaveValue('Unsaved-local-only-5738!')
        expect(writes).toEqual([])
        expect(errors).toEqual([])
        expect(
            await page.evaluate(() => ({ ...localStorage, ...sessionStorage })),
        ).toEqual({ 'tenonedge-theme': 'system' })
    },
)

owned(
    'mobile navigation traps focus, closes on Escape and navigation, and restores its trigger',
    async ({ page, workspaceUrl }, testInfo) => {
        await page.setViewportSize({ width: 390, height: 844 })
        await setup(page, workspaceUrl)
        await expect(page.getByRole('navigation')).toHaveCount(0)
        const trigger = page
            .getByRole('banner')
            .getByRole('button', { name: '展开导航', exact: true })
        await trigger.click()
        const drawer = page.getByRole('dialog', { name: '主导航', exact: true })
        await expect(drawer).toBeVisible()
        // Tab must remain inside the modal drawer, not reach background actions.
        for (let i = 0; i < 20; i++) {
            await page.keyboard.press('Tab')
            expect(
                await drawer.evaluate((element) =>
                    element.contains(document.activeElement),
                ),
            ).toBe(true)
        }
        await page.screenshot({
            path: testInfo.outputPath('workspace-mobile-menu.png'),
            fullPage: true,
        })
        await page.keyboard.press('Escape')
        await expect(drawer).toHaveCount(0)
        await expect(trigger).toBeFocused()
        await trigger.click()
        await drawer
            .getByRole('link', { name: '设备草稿', exact: true })
            .click()
        await expect(drawer).toHaveCount(0)
        await expect(
            page.getByRole('heading', { name: '设备草稿', exact: true }),
        ).toBeVisible()
        await expect(
            page
                .getByRole('banner')
                .getByRole('button', { name: '展开导航', exact: true }),
        ).toHaveAttribute('aria-expanded', 'false')
        expect(
            await page.evaluate(
                () => document.documentElement.scrollWidth <= innerWidth,
            ),
        ).toBe(true)
        await page.screenshot({
            path: testInfo.outputPath('workspace-mobile.png'),
            fullPage: true,
        })
    },
)
