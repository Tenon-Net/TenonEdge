import { expect, type Page } from '@playwright/test'

export const adminPassword = 'Workflow-admin-8416!'
export const temporaryPassword = 'Workflow-temporary-4618!'
export const userPassword = 'Workflow-personal-8164!'

export async function createUser(page: Page, username: string, role: string) {
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
export async function login(
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
export async function applyRoute(page: Page) {
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
