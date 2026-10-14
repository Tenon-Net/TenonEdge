import { test, expect, type Page } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
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
    await page.getByLabel('确认密码', { exact: true }).fill(adminPassword)
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
async function changePassword(page: Page, current: string, next: string) {
    await page.getByLabel('原密码', { exact: true }).fill(current)
    await page.getByLabel('新密码', { exact: true }).fill(next)
    const response = page.waitForResponse(
        (r) =>
            r.url().endsWith('/auth/password') &&
            r.request().method() === 'POST',
    )
    await page.getByRole('button', { name: '保存新密码', exact: true }).click()
    expect((await response).status()).toBe(200)
    await expect(page.getByRole('navigation')).toBeVisible()
}
async function openDevices(page: Page) {
    await page.getByRole('link', { name: '设备草稿', exact: true }).click()
    await expect(
        page.getByRole('button', { name: '刷新设备与连接', exact: true }),
    ).toBeEnabled()
}
async function nextRequestEndsSession(page: Page) {
    // The already-open application must process a real 401, without reload or a
    // test-side fetch standing in for the browser client's authorization handling.
    const denied = page.waitForResponse(
        (r) => /\/(devices|connections)$/.test(r.url()) && r.status() === 401,
    )
    await page
        .getByRole('button', { name: '刷新设备与连接', exact: true })
        .click()
    await denied
    await expect(
        page.getByRole('button', { name: '登录', exact: true }),
    ).toBeVisible()
    await expect(page.getByRole('navigation')).toHaveCount(0)
    expect(
        await page.evaluate(() => ({ ...localStorage, ...sessionStorage })),
    ).toEqual({})
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
            await openDevices(first)
            await openDevices(second)
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
                await nextRequestEndsSession(target)
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

for (const action of [
    'role',
    'disable',
    'temporary-password',
    'own-password',
] as const) {
    owned(
        `real ${action} change invalidates old sessions on the next browser request`,
        async ({ page, browser, userHost }) => {
            await setup(page, userHost)
            await create(page, 'operator', 'configurator')
            const firstContext = await browser.newContext()
            const secondContext = await browser.newContext()
            try {
                const first = await firstContext.newPage()
                const second = await secondContext.newPage()
                await login(first, userHost, 'operator', temporaryPassword)
                await changePassword(first, temporaryPassword, personalPassword)
                await login(second, userHost, 'operator', personalPassword)
                await expect(second.getByRole('navigation')).toBeVisible()
                await openDevices(first)
                await openDevices(second)
                if (action === 'own-password') {
                    await first
                        .getByRole('link', { name: '账号安全', exact: true })
                        .click()
                    await changePassword(
                        first,
                        personalPassword,
                        'Changed-again-9812',
                    )
                    // Only the caller receives the replacement session.
                    expect(
                        (
                            await first.request.get(
                                userHost + '/api/edge-admin/v1/system/info',
                            )
                        ).status(),
                    ).toBe(200)
                } else {
                    await page
                        .getByRole('button', { name: '重新读取用户' })
                        .click()
                    await row(page, 'operator')
                        .getByRole('button', {
                            name:
                                action === 'temporary-password'
                                    ? '重置密码'
                                    : '编辑用户',
                        })
                        .click()
                    if (action === 'temporary-password')
                        await page
                            .getByLabel('临时密码', { exact: true })
                            .fill(temporaryPassword)
                    else if (action === 'role')
                        await page
                            .getByLabel('角色', { exact: true })
                            .selectOption('viewer')
                    else
                        await page
                            .getByLabel('启用账号', { exact: true })
                            .uncheck()
                    await reviewAndConfirm(page)
                    await expect(page.getByRole('status')).toHaveText(
                        '用户操作已完成。',
                    )
                    await nextRequestEndsSession(first)
                }
                await nextRequestEndsSession(second)
                // A different user's administrator session is not revoked.
                expect(
                    (
                        await page.request.get(
                            userHost + '/api/edge-admin/v1/users',
                        )
                    ).status(),
                ).toBe(200)
                if (action === 'disable') {
                    await login(second, userHost, 'operator', personalPassword)
                    await expect(second.getByRole('alert')).toContainText(
                        '账号或密码错误',
                    )
                    // The denied login increments the account's failure counter,
                    // so enabling must use a newly reviewed security version.
                    await page
                        .getByRole('button', { name: '重新读取用户' })
                        .click()
                    await row(page, 'operator')
                        .getByRole('button', { name: '编辑用户' })
                        .click()
                    await page.getByLabel('启用账号', { exact: true }).check()
                    await reviewAndConfirm(page)
                    await expect(page.getByRole('status')).toHaveText(
                        '用户操作已完成。',
                    )
                }
                await login(
                    second,
                    userHost,
                    'operator',
                    action === 'temporary-password'
                        ? temporaryPassword
                        : action === 'own-password'
                          ? 'Changed-again-9812'
                          : personalPassword,
                )
                if (action === 'temporary-password') {
                    await expect(
                        second.getByText(
                            '当前账号必须先修改临时密码，才能继续操作。',
                        ),
                    ).toBeVisible()
                    await expect(second.getByRole('navigation')).toHaveCount(0)
                    expect(
                        (
                            await second.request.get(
                                userHost + '/api/edge-admin/v1/system/info',
                            )
                        ).status(),
                    ).toBe(403)
                    await changePassword(
                        second,
                        temporaryPassword,
                        personalPassword,
                    )
                }
                await expect(second.getByRole('navigation')).toBeVisible()
                await openDevices(second)
                if (action === 'role') {
                    await expect(
                        second.getByRole('button', {
                            name: '新建设备',
                            exact: true,
                        }),
                    ).toHaveCount(0)
                    const session = await (
                        await second.request.get(
                            userHost + '/api/edge-admin/v1/auth/session',
                        )
                    ).json()
                    expect(session.role).toBe('viewer')
                    expect(
                        (
                            await second.request.post(
                                userHost + '/api/edge-admin/v1/devices',
                                {
                                    headers: {
                                        'X-CSRF-Token': session.csrfToken,
                                    },
                                    data: {},
                                },
                            )
                        ).status(),
                    ).toBe(403)
                } else
                    await expect(
                        second.getByRole('button', {
                            name: '新建设备',
                            exact: true,
                        }),
                    ).toBeEnabled()
            } finally {
                await firstContext.close()
                await secondContext.close()
            }
        },
    )
}

owned(
    'first setup requires matching confirmation, defaults to admin, and validates weak passwords on the real server',
    async ({ page, userHost }) => {
        await page.goto(userHost + '/')
        await expect(page.getByLabel('账号', { exact: true })).toHaveValue(
            'admin',
        )
        const writes: unknown[] = []
        page.on('request', (request) => {
            if (request.url().endsWith('/setup') && request.method() === 'POST')
                writes.push(request.postDataJSON())
        })
        await page.getByLabel('密码', { exact: true }).fill(adminPassword)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByLabel('确认密码', { exact: true }),
        ).toHaveAttribute('aria-invalid', 'true')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill(adminPassword + '-typo')
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(page.getByRole('alert')).toHaveText(
            '两次输入的密码不一致。',
        )
        expect(writes).toEqual([])
        for (const weak of ['password1', 'abcdefghij', '1234567890']) {
            await page.getByLabel('密码', { exact: true }).fill(weak)
            await page.getByLabel('确认密码', { exact: true }).fill(weak)
            const response = page.waitForResponse(
                (r) =>
                    r.url().endsWith('/setup') &&
                    r.request().method() === 'POST',
            )
            await page.getByRole('button', { name: '创建管理员并登录' }).click()
            expect((await response).status()).toBe(400)
            await expect(page.getByRole('alert')).toContainText('密码')
        }
        await page.getByLabel('密码', { exact: true }).fill(adminPassword)
        await page.getByLabel('确认密码', { exact: true }).fill(adminPassword)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(page.getByRole('navigation')).toBeVisible()
        expect(writes).toHaveLength(4)
        for (const write of writes)
            expect(write).not.toHaveProperty('confirmPassword')
        await page.getByRole('button', { name: '退出登录' }).click()
        await expect(
            page.getByRole('button', { name: '登录', exact: true }),
        ).toBeVisible()
        await expect(page.getByLabel('确认密码', { exact: true })).toHaveCount(
            0,
        )
        await expect(
            page.getByRole('button', { name: '创建管理员并登录' }),
        ).toHaveCount(0)
    },
)

async function resetAdministrator(data: string) {
    // Real product command, no test hook/SQL mutation. Passwords never enter argv.
    const root = fileURLToPath(new URL('../../', import.meta.url))
    try {
        const result = await promisify(execFile)(
            'dotnet',
            [
                join(
                    root,
                    'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll',
                ),
                'reset-admin',
                '--account',
                'browser-admin',
                `--TenonEdge:DataDirectory=${data}`,
            ],
            { cwd: root, timeout: 30_000 },
        )
        return { code: 0, output: result.stdout + result.stderr }
    } catch (error) {
        if (
            typeof error !== 'object' ||
            error === null ||
            !('code' in error) ||
            error.code !== 3 ||
            !('stdout' in error) ||
            !('stderr' in error)
        )
            throw error
        return {
            code: error.code,
            output: String(error.stdout) + String(error.stderr),
        }
    }
}

test('real host reset refuses a live directory, reopens only the named account, and preserves another administrator login', async ({
    page,
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-host-reset-'))
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    const otherContext = await browser.newContext()
    const freshContext = await browser.newContext()
    try {
        host = await startOwnedHost(data)
        const url = host.url
        await setup(page, url)
        await create(page, 'other-admin', 'administrator')
        const other = await otherContext.newPage()
        await login(other, url, 'other-admin', temporaryPassword)
        await changePassword(other, temporaryPassword, personalPassword)
        const originalInfo = await (
            await page.request.get(url + '/api/edge-admin/v1/system/info')
        ).json()
        const refused = await resetAdministrator(data)
        expect(refused.code).toBe(3)
        expect(refused.output).toContain('执行主机侧维护命令前须先停止服务')
        expect(
            (
                await page.request.get(url + '/api/edge-admin/v1/auth/session')
            ).status(),
        ).toBe(200)
        expect(await host.stop()).toBe(0)
        host = undefined
        const reset = await resetAdministrator(data)
        expect(reset.code).toBe(0)
        expect(reset.output).toContain('已清除管理员账号 browser-admin 的密码')
        for (const secret of [
            adminPassword,
            temporaryPassword,
            personalPassword,
        ])
            expect(reset.output + refused.output).not.toContain(secret)
        host = await startOwnedHost(data, Number(new URL(url).port))
        // The target's old browser cookie no longer authenticates after the command.
        await page.reload()
        await expect(
            page.getByText('重置管理员密码', { exact: true }),
        ).toBeVisible()
        await expect(page.getByLabel('账号', { exact: true })).toHaveValue(
            'browser-admin',
        )
        await expect(page.getByLabel('账号', { exact: true })).toHaveAttribute(
            'readonly',
            '',
        )
        await expect(page.getByLabel('网关名称（可选）')).toHaveCount(0)
        expect(
            (
                await page.request.get(url + '/api/edge-admin/v1/system/info')
            ).status(),
        ).toBe(401)
        // Existing unaffected sessions still work; fresh browsers have an ordinary login entry.
        await other.reload()
        await expect(other.getByRole('navigation')).toBeVisible()
        const fresh = await freshContext.newPage()
        await fresh.goto(url + '/users')
        await fresh
            .getByRole('button', { name: '使用其他已有账号登录' })
            .click()
        await fresh.getByLabel('账号', { exact: true }).fill('other-admin')
        await fresh.getByLabel('密码', { exact: true }).fill(personalPassword)
        await fresh.getByRole('button', { name: '登录', exact: true }).click()
        await expect(
            fresh.getByRole('heading', { name: '用户管理', exact: true }),
        ).toBeVisible()
        const beforeReset = await (
            await fresh.request.get(url + '/api/edge-admin/v1/users')
        ).json()
        expect(beforeReset.items).toHaveLength(2)
        await page.getByRole('button', { name: '使用其他已有账号登录' }).click()
        await page.getByLabel('账号', { exact: true }).fill('browser-admin')
        await page.getByLabel('密码', { exact: true }).fill(adminPassword)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await expect(page.getByRole('alert')).toContainText('账号或密码错误')
        await page.getByRole('button', { name: '返回管理员重置' }).click()
        await expect(page.getByLabel('密码', { exact: true })).toHaveValue('')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Reset-browser-5826')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Reset-browser-5826')
        await page.getByRole('button', { name: '设置密码并登录' }).click()
        await expect(
            page.getByRole('heading', { name: '用户管理', exact: true }),
        ).toBeVisible()
        const users = await (
            await page.request.get(url + '/api/edge-admin/v1/users')
        ).json()
        expect(users.items.map((u: { id: string }) => u.id).sort()).toEqual(
            beforeReset.items.map((u: { id: string }) => u.id).sort(),
        )
        const info = await (
            await page.request.get(url + '/api/edge-admin/v1/system/info')
        ).json()
        expect(info.nodeId).toBe(originalInfo.nodeId)
        expect(info.httpsCertificate.sha256Fingerprint).toBe(
            originalInfo.httpsCertificate.sha256Fingerprint,
        )
        const audit = await (
            await page.request.get(url + '/api/edge-admin/v1/audit')
        ).json()
        expect(
            audit.items.filter(
                (a: { action: string }) =>
                    a.action === 'host.resetAdministrator',
            ),
        ).toHaveLength(1)
        expect(
            audit.items.filter(
                (a: { action: string }) => a.action === 'setup.complete',
            ),
        ).toHaveLength(2)
        expect(JSON.stringify(audit)).not.toContain('Reset-browser-5826')
        expect(await host.stop()).toBe(0)
        host = undefined
        host = await startOwnedHost(data, Number(new URL(url).port))
        await page.context().clearCookies()
        await login(page, url, 'browser-admin', 'Reset-browser-5826')
        await expect(page.getByRole('navigation')).toBeVisible()
        expect(
            (
                await (
                    await page.request.get(url + '/api/edge-admin/v1/setup')
                ).json()
            ).state,
        ).toBe('initialized')
    } finally {
        await otherContext.close()
        await freshContext.close()
        await host?.stop()
        await rm(data, { recursive: true, force: true })
    }
})

for (const state of ['uninitialized', 'resetPending'] as const) {
    owned(
        `closed ${state} UI fixture hides setup; reset still exposes ordinary login`,
        async ({ page, userHost }) => {
            // UI projection only: server expiry itself is covered using ManualTimeProvider
            // in SetupTests. This does not claim a 30-minute wall-clock browser wait.
            await page.route('**/api/edge-admin/v1/setup', async (route) => {
                expect(route.request().method()).toBe('GET')
                await route.fulfill({
                    json: {
                        state,
                        windowOpen: false,
                        windowClosesAt: null,
                        resetAccountName:
                            state === 'resetPending' ? 'browser-admin' : null,
                    },
                })
            })
            await page.goto(userHost + '/')
            await expect(page.getByRole('status')).toHaveText(
                '设置窗口已关闭，请在主机上重启服务后再试。',
            )
            await expect(page.locator('form')).toHaveCount(0)
            if (state === 'resetPending') {
                await page
                    .getByRole('button', { name: '使用其他已有账号登录' })
                    .click()
                await expect(
                    page.getByRole('button', { name: '登录', exact: true }),
                ).toBeVisible()
                await expect(
                    page.getByLabel('账号', { exact: true }),
                ).toBeEditable()
                await expect(
                    page.getByLabel('确认密码', { exact: true }),
                ).toHaveCount(0)
                await page
                    .getByRole('button', { name: '返回管理员重置' })
                    .click()
                await expect(page.locator('form')).toHaveCount(0)
            } else
                await expect(
                    page.getByRole('button', { name: '使用其他已有账号登录' }),
                ).toHaveCount(0)
        },
    )
}
