import { test, expect, type Page } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const adminPassword = 'Users-Browser-4827'
const temporaryPassword = 'Temporary-Browser-4827'
const personalPassword = 'Personal-Browser-5938'
const owned = test.extend<{ userHost: string }>({
    userHost: async ({ browserName }, use) => {
        const data = await mkdtemp(
            join(tmpdir(), `tenonedge-users-${browserName}-`),
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
    await page.goto(url + '/users')
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page.getByLabel('密码', { exact: true }).fill(adminPassword)
    await page.getByRole('button', { name: '创建管理员并登录' }).click()
    await expect(
        page.getByRole('heading', { name: '用户管理', exact: true }),
    ).toBeVisible()
}
async function reviewAndConfirm(page: Page) {
    await page.getByRole('button', { name: '核对操作', exact: true }).click()
    await expect(
        page.getByRole('button', { name: '确认执行', exact: true }),
    ).toBeDisabled()
    await page.getByLabel('我已核对账号与操作，确认执行').check()
    await page.getByRole('button', { name: '确认执行', exact: true }).click()
}
async function create(page: Page, name: string, role: string = 'viewer') {
    await page.getByRole('button', { name: '新建用户', exact: true }).click()
    await page.getByLabel('用户账号', { exact: true }).fill(name)
    await page.getByLabel('显示名', { exact: true }).fill('现场人员')
    await page.getByLabel('角色', { exact: true }).selectOption(role)
    await page.getByLabel('临时密码', { exact: true }).fill(temporaryPassword)
    await reviewAndConfirm(page)
    await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
}
function row(page: Page, name: string) {
    return page
        .getByRole('row')
        .filter({ has: page.getByRole('cell', { name, exact: true }) })
}
async function login(page: Page, url: string, name: string, password: string) {
    await page.goto(url + '/')
    await page.getByLabel('账号', { exact: true }).fill(name)
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByRole('button', { name: '登录', exact: true }).click()
}
owned(
    'real user creation, temporary password, reset and forced logout of two browser sessions',
    async ({ page, browser, userHost }) => {
        await setup(page, userHost)
        await expect(
            page.getByText('上次登录：无记录', { exact: true }),
        ).toBeVisible()
        await create(page, 'operator', 'configurator')
        await page.screenshot({
            path: '/tmp/tenonedge-users-browser.png',
            fullPage: true,
        })
        const firstContext = await browser.newContext()
        const secondContext = await browser.newContext()
        try {
            const first = await firstContext.newPage()
            const second = await secondContext.newPage()
            await login(first, userHost, 'operator', temporaryPassword)
            await expect(
                first.getByLabel('原密码', { exact: true }),
            ).toBeVisible()
            expect(
                (
                    await first.request.get(
                        userHost + '/api/edge-admin/v1/system/info',
                    )
                ).status(),
            ).toBe(403)
            await first
                .getByLabel('原密码', { exact: true })
                .fill(temporaryPassword)
            await first
                .getByLabel('新密码', { exact: true })
                .fill(personalPassword)
            await first.getByRole('button', { name: '保存新密码' }).click()
            await expect(first.getByRole('navigation')).toBeVisible()
            await login(second, userHost, 'operator', personalPassword)
            await expect(
                second.getByText(/上次登录：.*来源 IP：/),
            ).toBeVisible()
            // 用户刚自行改密，管理员必须重新读取安全版本再确认下线。
            await page.getByRole('button', { name: '重新读取用户' }).click()
            await row(page, 'operator')
                .getByRole('button', { name: '强制下线' })
                .click()
            await reviewAndConfirm(page)
            await expect(page.getByRole('status')).toHaveText(
                '用户操作已完成。',
            )
            for (const target of [first, second]) {
                await target.reload()
                await expect(
                    target.getByRole('button', { name: '登录', exact: true }),
                ).toBeVisible()
            }
            await row(page, 'operator')
                .getByRole('button', { name: '重置密码' })
                .click()
            await page
                .getByLabel('临时密码', { exact: true })
                .fill(temporaryPassword)
            await reviewAndConfirm(page)
            await expect(page.getByRole('status')).toHaveText(
                '用户操作已完成。',
            )
            await login(first, userHost, 'operator', temporaryPassword)
            await expect(
                first.getByLabel('原密码', { exact: true }),
            ).toBeVisible()
            expect(
                await page.evaluate(() =>
                    JSON.stringify([localStorage, sessionStorage]),
                ),
            ).not.toContain(temporaryPassword)
            await expect(page.locator('body')).not.toContainText(
                temporaryPassword,
            )
        } finally {
            await firstContext.close()
            await secondContext.close()
        }
    },
)
owned(
    'last enabled administrator protection is real, and stale user edit does not overwrite a newer change',
    async ({ page, userHost }) => {
        await setup(page, userHost)
        await expect(
            row(page, 'browser-admin').getByRole('button', {
                name: '删除用户',
            }),
        ).toBeDisabled()
        await row(page, 'browser-admin')
            .getByRole('button', { name: '编辑用户' })
            .click()
        await page.getByLabel('角色', { exact: true }).selectOption('viewer')
        await reviewAndConfirm(page)
        await expect(
            page.getByText('必须保留至少一个启用中的管理员。'),
        ).toBeVisible()
        await expect(
            page.getByRole('button', { name: '新建用户', exact: true }),
        ).toBeDisabled()
        await page.getByRole('button', { name: '重新读取用户' }).click()
        await create(page, 'operator')
        await row(page, 'operator')
            .getByRole('button', { name: '编辑用户' })
            .click()
        await page.getByLabel('显示名', { exact: true }).fill('stale-edit')
        const session = await (
            await page.request.get(userHost + '/api/edge-admin/v1/auth/session')
        ).json()
        const users = await (
            await page.request.get(userHost + '/api/edge-admin/v1/users')
        ).json()
        const target = users.items.find(
            (u: { accountName: string }) => u.accountName === 'operator',
        )
        const changed = await page.request.put(
            userHost + '/api/edge-admin/v1/users/' + target.id,
            {
                headers: { 'X-CSRF-Token': session.csrfToken },
                data: {
                    expectedVersion: target.version,
                    displayName: 'newer-edit',
                    role: 'viewer',
                    isEnabled: true,
                    confirmed: true,
                },
            },
        )
        expect(changed.status()).toBe(200)
        await reviewAndConfirm(page)
        await expect(
            page.getByText('用户状态已变化，请重新读取并核对。'),
        ).toBeVisible()
        await page.getByRole('button', { name: '重新读取用户' }).click()
        await expect(row(page, 'operator')).toContainText('newer-edit')
        await expect(row(page, 'operator')).not.toContainText('stale-edit')
    },
)
owned(
    'a committed create with lost reply is never replayed and clears the temporary password',
    async ({ page, userHost }) => {
        await setup(page, userHost)
        let writes = 0
        await page.route('**/api/edge-admin/v1/users', async (route) => {
            if (route.request().method() !== 'POST') {
                await route.continue()
                return
            }
            writes++
            expect((await route.fetch()).status()).toBe(200)
            await route.abort('failed')
        })
        await page
            .getByRole('button', { name: '新建用户', exact: true })
            .click()
        await page.getByLabel('用户账号', { exact: true }).fill('lost-reply')
        await page
            .getByLabel('临时密码', { exact: true })
            .fill(temporaryPassword)
        await reviewAndConfirm(page)
        await expect(
            page.getByText(/未确认操作成功；请先重新读取/),
        ).toBeVisible()
        await expect(page.getByLabel('临时密码', { exact: true })).toHaveCount(
            0,
        )
        await expect(
            page.getByRole('button', { name: '新建用户', exact: true }),
        ).toBeDisabled()
        await page.getByRole('button', { name: '重新读取用户' }).click()
        await expect(row(page, 'lost-reply')).toBeVisible()
        await page.reload()
        await expect(row(page, 'lost-reply')).toBeVisible()
        expect(writes).toBe(1)
    },
)
for (const role of ['viewer', 'configurator']) {
    owned(
        `${role} cannot enter user management by URL or API`,
        async ({ page, browser, userHost }) => {
            await setup(page, userHost)
            await create(page, 'restricted', role)
            const context = await browser.newContext()
            try {
                const other = await context.newPage()
                await login(other, userHost, 'restricted', temporaryPassword)
                await other
                    .getByLabel('原密码', { exact: true })
                    .fill(temporaryPassword)
                await other
                    .getByLabel('新密码', { exact: true })
                    .fill(personalPassword)
                await other.getByRole('button', { name: '保存新密码' }).click()
                await expect(other.getByRole('navigation')).toBeVisible()
                await expect(
                    other.getByRole('link', { name: '用户管理', exact: true }),
                ).toHaveCount(0)
                await other.goto(userHost + '/users')
                await expect(
                    other.getByText('仅管理员可以管理用户。'),
                ).toBeVisible()
                expect(
                    (
                        await other.request.get(
                            userHost + '/api/edge-admin/v1/users',
                        )
                    ).status(),
                ).toBe(403)
                await expect(
                    other.getByRole('link', { name: 'API 令牌', exact: true }),
                ).toHaveCount(0)
                await other.goto(userHost + '/tokens')
                await expect(
                    other.getByText('仅管理员可以管理 API 令牌。'),
                ).toBeVisible()
                expect(
                    (
                        await other.request.get(
                            userHost + '/api/edge-admin/v1/tokens',
                        )
                    ).status(),
                ).toBe(403)
            } finally {
                await context.close()
            }
        },
    )
}

owned(
    'real lockout, disable, unlock, enable and deletion are confirmed and audited',
    async ({ page, userHost }) => {
        await setup(page, userHost)
        await create(page, 'locked-user')
        await row(page, 'locked-user')
            .getByRole('button', { name: '编辑用户' })
            .click()
        await page.getByLabel('启用账号', { exact: true }).uncheck()
        await reviewAndConfirm(page)
        await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
        await expect(row(page, 'locked-user')).toContainText('停用')
        for (let attempt = 0; attempt < 5; attempt++) {
            const failure = await page.request.post(
                userHost + '/api/edge-admin/v1/auth/login',
                {
                    data: {
                        accountName: 'locked-user',
                        password: 'Wrong-test-password-21',
                    },
                },
            )
            expect(failure.status()).toBe(attempt === 4 ? 429 : 401)
        }
        await page.getByRole('button', { name: '重新读取用户' }).click()
        await expect(row(page, 'locked-user')).toContainText('锁定至')
        await row(page, 'locked-user')
            .getByRole('button', { name: '解锁', exact: true })
            .click()
        await reviewAndConfirm(page)
        await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
        await expect(row(page, 'locked-user')).not.toContainText('锁定至')
        await expect(row(page, 'locked-user')).toContainText('停用')
        await row(page, 'locked-user')
            .getByRole('button', { name: '编辑用户' })
            .click()
        await page.getByLabel('启用账号', { exact: true }).check()
        await reviewAndConfirm(page)
        await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
        await expect(row(page, 'locked-user')).toContainText('启用')
        await row(page, 'locked-user')
            .getByRole('button', { name: '删除用户', exact: true })
            .click()
        await reviewAndConfirm(page)
        await expect(page.getByRole('status')).toHaveText('用户操作已完成。')
        await expect(row(page, 'locked-user')).toHaveCount(0)
        const audit = await (
            await page.request.get(
                userHost + '/api/edge-admin/v1/audit?target=locked-user',
            )
        ).json()
        expect(
            audit.items.map((item: { action: string }) => item.action).sort(),
        ).toEqual([
            'user.create',
            'user.delete',
            'user.disable',
            'user.enable',
            'user.unlock',
        ])
        expect(
            audit.items.every(
                (item: { accountName: string; result: string }) =>
                    item.accountName === 'browser-admin' &&
                    item.result === 'success',
            ),
        ).toBe(true)
    },
)
