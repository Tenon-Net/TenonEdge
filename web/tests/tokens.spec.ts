import { test, expect, type Page } from '@playwright/test'
import { request as httpsRequest } from 'node:https'
import { startTrustedBrowser } from './support/trusted-browser'

// Token creation responses must not enter retained browser traces/screenshots.
test.use({ trace: 'off', screenshot: 'off', video: 'off' })
const password = 'Token-Browser-5938'
async function setup(page: Page, url: string) {
    await page.goto(url + '/tokens')
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByRole('button', { name: '创建管理员并登录' }).click()
    await expect(
        page.getByRole('heading', { name: 'API 令牌', exact: true }),
    ).toBeVisible()
}
async function create(page: Page, name: string, role = 'viewer') {
    await page.getByRole('button', { name: '创建令牌', exact: true }).click()
    await page.getByLabel('令牌名称', { exact: true }).fill(name)
    await page.getByLabel('绑定角色', { exact: true }).selectOption(role)
    await page
        .getByRole('button', { name: '创建令牌', exact: true })
        .last()
        .click()
    await expect(
        page.getByRole('textbox', { name: '令牌原文', exact: true }),
    ).toBeVisible()
    const secret = await page
        .getByRole('textbox', { name: '令牌原文', exact: true })
        .inputValue()
    expect(secret.startsWith('te1.')).toBe(true)
    return secret
}
function row(page: Page, name: string) {
    return page
        .getByRole('row')
        .filter({ has: page.getByRole('cell', { name, exact: true }) })
}
function bearer(
    url: string,
    ca: Buffer,
    secret: string,
    method = 'GET',
    path = '/connections',
): Promise<number> {
    return new Promise((resolve, reject) => {
        const request = httpsRequest(
            url + '/api/edge-admin/v1' + path,
            {
                ca,
                rejectUnauthorized: true,
                method,
                headers: { Authorization: 'Bearer ' + secret },
            },
            (response) => {
                response.resume()
                response.once('end', () => resolve(response.statusCode!))
            },
        )
        request.on('error', reject)
        request.end()
    })
}
test('trusted TLS browser creates once-only tokens, enforces role, revokes and rejects HTTP creation', async () => {
    const host = await startTrustedBrowser()
    try {
        const page = await host.context.newPage()
        await setup(page, host.url)
        const secret = await create(page, 'MES browser')
        const stored = await page.evaluate(
            (value) =>
                JSON.stringify({
                    local: { ...localStorage },
                    session: { ...sessionStorage },
                    cookies: document.cookie,
                    url: location.href,
                }).includes(value),
            secret,
        )
        expect(stored).toBe(false)
        expect(await bearer(host.url, host.ca, secret)).toBe(200)
        expect(await bearer(host.url, host.ca, secret, 'POST')).toBe(403)
        await page.getByRole('button', { name: '我已保存，关闭原文' }).click()
        await expect(
            page.getByRole('textbox', { name: '令牌原文', exact: true }),
        ).toHaveCount(0)
        await page.reload()
        await expect(
            page.getByRole('textbox', { name: '令牌原文', exact: true }),
        ).toHaveCount(0)
        await row(page, 'MES browser')
            .getByRole('button', { name: '吊销', exact: true })
            .click()
        await expect(
            page.getByRole('button', { name: '确认吊销', exact: true }),
        ).toBeDisabled()
        await page.getByLabel('我已核对令牌，确认吊销', { exact: true }).check()
        await page
            .getByRole('button', { name: '确认吊销', exact: true })
            .click()
        await expect(row(page, 'MES browser')).toContainText('已吊销')
        expect(await bearer(host.url, host.ca, secret)).toBe(401)
        await page.screenshot({
            path: '/tmp/tenonedge-tokens-browser-metadata.png',
            fullPage: true,
        })
        await page.goto(host.httpUrl + '/tokens')
        await page.getByLabel('账号', { exact: true }).fill('browser-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await expect(
            page.getByRole('button', { name: '创建令牌', exact: true }),
        ).toBeDisabled()
        await expect(
            page.getByText(/只能经 HTTPS 创建和使用令牌/),
        ).toBeVisible()
    } finally {
        await host.close()
    }
})
for (const action of ['disable', 'delete'] as const)
    for (const revoke of [false, true]) {
        test(`${action} creator requires explicit ${revoke ? 'revoke' : 'keep'} choice and preserves independent roles`, async () => {
            const host = await startTrustedBrowser()
            try {
                const page = await host.context.newPage()
                await setup(page, host.url)
                // Create a second administrator through the actual API; set its temporary password in its own session.
                const status = await page.evaluate(async (password) => {
                    const session = await (
                        await fetch('/api/edge-admin/v1/auth/session')
                    ).json()
                    return (
                        await fetch('/api/edge-admin/v1/users', {
                            method: 'POST',
                            headers: {
                                'X-CSRF-Token': session.csrfToken,
                                'Content-Type': 'application/json',
                            },
                            body: JSON.stringify({
                                accountName: 'token-owner',
                                role: 'administrator',
                                temporaryPassword: password,
                            }),
                        })
                    ).status
                }, password)
                expect(status).toBe(200)
                const ownerContext = await host.browser.newContext({
                    ignoreHTTPSErrors: false,
                })
                try {
                    const owner = await ownerContext.newPage()
                    await owner.goto(host.url + '/tokens')
                    await owner
                        .getByLabel('账号', { exact: true })
                        .fill('token-owner')
                    await owner
                        .getByLabel('密码', { exact: true })
                        .fill(password)
                    await owner
                        .getByRole('button', { name: '登录', exact: true })
                        .click()
                    await owner
                        .getByLabel('原密码', { exact: true })
                        .fill(password)
                    await owner
                        .getByLabel('新密码', { exact: true })
                        .fill('Token-Personal-6249')
                    await owner
                        .getByRole('button', {
                            name: '保存新密码',
                            exact: true,
                        })
                        .click()
                    const secret = await create(owner, 'owner-MES')
                    await owner
                        .getByRole('button', { name: '我已保存，关闭原文' })
                        .click()
                    await page
                        .getByRole('link', { name: '用户管理', exact: true })
                        .click()
                    await row(page, 'token-owner')
                        .getByRole('button', {
                            name: action === 'delete' ? '删除用户' : '编辑用户',
                            exact: true,
                        })
                        .click()
                    if (action === 'disable')
                        await page
                            .getByLabel('启用账号', { exact: true })
                            .uncheck()
                    await page
                        .getByRole('button', { name: '核对操作', exact: true })
                        .click()
                    await expect(
                        page.getByRole('region', {
                            name: '该账号创建的尚未吊销令牌',
                        }),
                    ).toContainText('owner-MES')
                    await page
                        .getByLabel('我已核对账号与操作，确认执行')
                        .check()
                    await expect(
                        page.getByRole('button', {
                            name: '确认执行',
                            exact: true,
                        }),
                    ).toBeDisabled()
                    await page
                        .getByLabel(
                            revoke
                                ? '一并吊销这些令牌'
                                : '保留这些令牌（继续按各自角色使用）',
                            { exact: true },
                        )
                        .check()
                    await page
                        .getByLabel('我已核对账号与操作，确认执行')
                        .check()
                    await page
                        .getByRole('button', { name: '确认执行', exact: true })
                        .click()
                    await expect(page.getByRole('status')).toHaveText(
                        '用户操作已完成。',
                    )
                    expect(await bearer(host.url, host.ca, secret)).toBe(
                        revoke ? 401 : 200,
                    )
                    await owner.reload()
                    await expect(
                        owner.getByRole('button', {
                            name: '登录',
                            exact: true,
                        }),
                    ).toBeVisible()
                } finally {
                    await ownerContext.close()
                }
            } finally {
                await host.close()
            }
        })
    }
test('lost creation response is never replayed and cannot expose a secret after rereading', async () => {
    const host = await startTrustedBrowser()
    try {
        const page = await host.context.newPage()
        await setup(page, host.url)
        let posts = 0
        await page.route('**/api/edge-admin/v1/tokens', async (route) => {
            if (route.request().method() !== 'POST') {
                await route.continue()
                return
            }
            posts++
            // Playwright's Node request context does not use Chromium's isolated NSS store.
            // Send exactly the intercepted request over separately verified TLS, then drop only its reply.
            const original = route.request()
            const headers = await original.allHeaders()
            const status = await new Promise<number>((resolve, reject) => {
                const forwarded = httpsRequest(
                    original.url(),
                    {
                        ca: host.ca,
                        rejectUnauthorized: true,
                        method: original.method(),
                        headers,
                    },
                    (response) => {
                        response.resume()
                        response.once('end', () =>
                            resolve(response.statusCode!),
                        )
                    },
                )
                forwarded.on('error', reject)
                forwarded.end(original.postDataBuffer())
            })
            expect(status).toBe(200)
            await route.abort('connectionclosed')
        })
        await page
            .getByRole('button', { name: '创建令牌', exact: true })
            .click()
        await page.getByLabel('令牌名称', { exact: true }).fill('response-lost')
        await page
            .getByRole('button', { name: '创建令牌', exact: true })
            .last()
            .click()
        await expect(page.getByText(/操作未确认成功，已停止重放/)).toBeVisible()
        await expect(
            page.getByRole('button', { name: '创建令牌', exact: true }),
        ).toBeDisabled()
        await page
            .getByRole('button', { name: '重新读取', exact: true })
            .click()
        await expect(row(page, 'response-lost')).toBeVisible()
        await expect(
            page.getByRole('textbox', { name: '令牌原文', exact: true }),
        ).toHaveCount(0)
        expect(posts).toBe(1)
    } finally {
        await host.close()
    }
})
