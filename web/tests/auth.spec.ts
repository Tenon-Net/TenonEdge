import { test, expect } from '@playwright/test'

test('real setup, session, CSRF password change, logout and offline assets', async ({
    page,
    context,
    baseURL,
}) => {
    const external: string[] = []
    const writes: string[] = []
    page.on('request', (request) => {
        if (new URL(request.url()).origin !== new URL(baseURL!).origin)
            external.push(request.url())
        if (request.method() === 'POST')
            writes.push(new URL(request.url()).pathname)
    })
    await page.route('**/*', (route) =>
        new URL(route.request().url()).origin === new URL(baseURL!).origin
            ? route.continue()
            : route.abort(),
    )
    await page.goto('/account')
    if (new URL(baseURL!).hostname !== '127.0.0.1')
        expect(await page.evaluate(() => window.isSecureContext)).toBe(false)
    console.log(
        'Browser:',
        await page.evaluate(() => navigator.userAgent),
        'origin:',
        baseURL,
    )
    await expect(page.getByText('首次设置', { exact: true })).toBeVisible()
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page.getByLabel('密码', { exact: true }).fill('Browser-fixture-4827')
    await page.getByLabel('网关名称（可选）').fill('浏览器测试网关')
    await page.getByRole('button', { name: '创建管理员并登录' }).click()
    await expect(page.getByRole('navigation')).toBeVisible()
    await expect(page.getByRole('heading', { name: '修改密码' })).toBeVisible()
    const cookies = await context.cookies()
    expect(cookies.some((cookie) => cookie.httpOnly)).toBe(true)
    await expect
        .poll(() => page.evaluate(() => document.cookie))
        .not.toContain('Session')
    await page.reload()
    await expect(page.getByRole('heading', { name: '修改密码' })).toBeVisible()
    await page
        .getByLabel('原密码', { exact: true })
        .fill('Browser-fixture-4827')
    await page
        .getByLabel('新密码', { exact: true })
        .fill('Changed-fixture-5938')
    const passwordResponse = page.waitForResponse(
        (response) =>
            response.url().endsWith('/auth/password') &&
            response.request().method() === 'POST',
    )
    await page.getByRole('button', { name: '保存新密码' }).click()
    expect((await passwordResponse).status()).toBe(200)
    await page.getByRole('button', { name: '退出登录' }).click()
    await expect(
        page.getByRole('button', { name: '登录', exact: true }),
    ).toBeVisible()
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page.getByLabel('密码', { exact: true }).fill('Changed-fixture-5938')
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await expect(page.getByRole('navigation')).toBeVisible()
    await page.getByRole('link', { name: '工作区', exact: true }).click()
    await expect(
        page.getByText('已连接到真实网关', { exact: true }),
    ).toBeVisible()
    await page.screenshot({
        path: 'test-results/workspace.png',
        fullPage: true,
    })
    const missing = await context.request.get(
        '/api/edge-admin/v1/does-not-exist',
        { headers: { Accept: 'text/html' } },
    )
    expect(missing.status()).toBe(404)
    expect(missing.headers()['content-type']).toContain(
        'application/problem+json',
    )
    expect(external).toEqual([])
    expect(writes).toEqual([
        '/api/edge-admin/v1/setup',
        '/api/edge-admin/v1/auth/password',
        '/api/edge-admin/v1/auth/logout',
        '/api/edge-admin/v1/auth/login',
    ])
    expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
    expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
})

test('failed login is not replayed and a transient error preserves entered values', async ({
    page,
}) => {
    // Keep this scenario runnable on its own, without relying on the preceding UI setup test.
    const setup = await page.request.get('/api/edge-admin/v1/setup')
    if ((await setup.json()).state === 'uninitialized') {
        const completed = await page.request.post('/api/edge-admin/v1/setup', {
            data: {
                accountName: 'browser-admin',
                password: 'Changed-fixture-5938',
                gatewayName: null,
            },
        })
        expect(completed.status()).toBe(200)
        await page.context().clearCookies()
    }
    await page.goto('/')
    await expect(
        page.getByRole('button', { name: '登录', exact: true }),
    ).toBeVisible()
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page
        .getByLabel('密码', { exact: true })
        .fill('not-the-fixture-password')
    let attempts = 0
    await page.route('**/auth/login', async (route) => {
        attempts++
        await route.fulfill({
            status: 500,
            contentType: 'application/problem+json',
            body: JSON.stringify({ status: 500 }),
        })
    })
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('请求失败')
    await expect(page.getByLabel('账号', { exact: true })).toHaveValue(
        'browser-admin',
    )
    await expect(page.getByLabel('密码', { exact: true })).toHaveValue(
        'not-the-fixture-password',
    )
    await page.getByLabel('账号', { exact: true }).focus()
    await page.waitForTimeout(1500)
    expect(attempts).toBe(1)
    await page.unroute('**/auth/login')
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('账号或密码错误')
})
