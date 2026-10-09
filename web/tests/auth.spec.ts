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

async function openPointEditor(page: import('@playwright/test').Page) {
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
    await page.goto('/prototypes/points')
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page.getByLabel('密码', { exact: true }).fill('Changed-fixture-5938')
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await expect(
        page.getByRole('heading', { name: '点表交互原型' }),
    ).toBeVisible()
}
function pointText(count: number, badIndex = -1, start = 0) {
    return Array.from({ length: count }, (_, offset) => {
        const index = offset + start
        return [
            index === badIndex ? 'bad key' : `point_${index}`,
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
}
async function pasteIntoGrid(
    page: import('@playwright/test').Page,
    text: string,
) {
    // Software injection into the ordinary paste event path, not an OS/Excel clipboard claim.
    const propagated = await page
        .getByTestId('point-grid')
        .evaluate((element, value) => {
            const data = new DataTransfer()
            data.setData('text/plain', value)
            return element.dispatchEvent(
                new ClipboardEvent('paste', {
                    bubbles: true,
                    cancelable: true,
                    clipboardData: data,
                }),
            )
        }, text)
    expect(propagated).toBe(false)
}

test('1000-row paste, row errors, overwrite, bulk edit and query failure isolation', async ({
    page,
    baseURL,
}) => {
    await page.clock.install()
    await openPointEditor(page)
    if (new URL(baseURL!).hostname !== '127.0.0.1')
        expect(await page.evaluate(() => isSecureContext)).toBe(false)
    const writes: string[] = []
    page.on('request', (request) => {
        if (request.method() !== 'GET') writes.push(request.url())
    })
    await pasteIntoGrid(page, pointText(1000, 900))
    await expect(page.getByTestId('point-count')).toContainText(
        '1000 行 · 0 行选中 · 1 行错误',
    )
    await page.getByText('定位错误（前 20 行）', { exact: true }).click()
    await page.getByRole('button', { name: /^定位第 901 行/ }).click()
    const key = page.getByLabel('第 901 行 key', { exact: true })
    await expect(key).toBeVisible()
    await expect(key).toBeFocused()
    await expect(page.locator('[data-point-index="900"]')).toHaveAttribute(
        'data-invalid',
        'true',
    )
    await key.fill('point_900')
    await expect(page.getByTestId('point-count')).toContainText('0 行错误')
    await page.getByLabel('选择第 901 行', { exact: true }).check()
    await page.getByLabel('批量值', { exact: true }).fill('℃')
    await page.getByRole('button', { name: '修改所选行', exact: true }).click()
    await expect(
        page.getByLabel('第 901 行 单位', { exact: true }),
    ).toHaveValue('℃')
    await expect(
        page.getByLabel('第 902 行 单位', { exact: true }),
    ).toHaveValue('')
    await page
        .getByRole('combobox', { name: '粘贴方式', exact: true })
        .selectOption('replace')
    await pasteIntoGrid(
        page,
        [
            'replacement',
            '覆盖点',
            '3',
            '900',
            'uint16',
            '',
            '',
            'kPa',
            'true',
        ].join('\t'),
    )
    await expect(key).toHaveValue('replacement')
    await expect(page.getByTestId('point-count')).toContainText(
        '1000 行 · 1 行选中',
    )
    await page
        .getByRole('combobox', { name: '地址写法', exact: true })
        .selectOption('manual')
    await expect(
        page.getByLabel('第 901 行 地址', { exact: true }),
    ).toHaveValue('40901')
    await expect(page.getByTestId('address-900')).toHaveText('900 / 40901')
    await page.route('**/auth/session', (route) =>
        route.fulfill({
            status: 500,
            contentType: 'application/problem+json',
            body: '{"status":500}',
        }),
    )
    await page.clock.fastForward(61000)
    await expect(page.getByRole('alert')).toContainText('请求失败')
    await expect(key).toHaveValue('replacement')
    await expect(
        page.getByLabel('第 901 行 单位', { exact: true }),
    ).toHaveValue('kPa')
    expect(writes).toEqual([])
    await page.screenshot({
        path: 'test-results/points-1000.png',
        fullPage: true,
    })
    await expect(page.getByText(/^会话状态上次成功刷新：/)).toBeVisible()
    await page.unroute('**/auth/session')
    const refreshed = page.waitForResponse(
        (response) =>
            response.url().endsWith('/auth/session') &&
            response.status() === 200,
    )
    await page.getByRole('button', { name: '重新查询', exact: true }).click()
    await refreshed
    await expect(page.getByRole('alert')).toHaveCount(0)
    await expect(key).toHaveValue('replacement')
    await expect(
        page.getByLabel('第 901 行 单位', { exact: true }),
    ).toHaveValue('kPa')
    expect(writes).toEqual([])
})

test('10000-row virtualization reaches the last editable row without a 10000-row DOM', async ({
    page,
    context,
}) => {
    await openPointEditor(page)
    const cdp = await context.newCDPSession(page)
    const before = await cdp.send('Runtime.getHeapUsage')
    await pasteIntoGrid(page, pointText(1000))
    await expect(page.getByTestId('point-count')).toContainText(
        '1000 行 · 0 行选中 · 0 行错误',
    )
    const thousand = {
        heap: await cdp.send('Runtime.getHeapUsage'),
        timings: await page.getByTestId('point-timing').textContent(),
    }
    await pasteIntoGrid(page, pointText(9000, -1, 1000))
    await expect(page.getByTestId('point-count')).toContainText(
        '10000 行 · 0 行选中 · 0 行错误',
    )
    expect(await page.locator('[data-point-index]').count()).toBeLessThan(40)
    await page.getByTestId('point-grid').evaluate((element) => {
        element.scrollTop = element.scrollHeight
    })
    const last = page.getByLabel('第 10000 行 key', { exact: true })
    await expect(last).toHaveValue('point_9999')
    await last.fill('edited_last')
    await expect(last).toHaveValue('edited_last')
    const after = await cdp.send('Runtime.getHeapUsage')
    console.log(
        '10000-row browser observations:',
        JSON.stringify({
            before,
            thousand,
            after,
            timings: await page.getByTestId('point-timing').textContent(),
            renderedRows: await page.locator('[data-point-index]').count(),
        }),
    )
    await page.getByRole('button', { name: '添加空行' }).click()
    await expect(page.getByRole('alert')).toContainText('最多 10,000 行')
    await expect(page.getByTestId('point-count')).toContainText('10000 行')
    await page.screenshot({
        path: 'test-results/points-10000.png',
        fullPage: true,
    })
})
