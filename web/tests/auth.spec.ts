import { test, expect } from '@playwright/test'
import { createServer } from 'node:http'
import { createServer as createTcpServer, type Socket } from 'node:net'

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

// Configuration scenarios reuse one real login in memory; authentication scenarios above remain isolated.
let editorCookies: Parameters<
    import('@playwright/test').BrowserContext['addCookies']
>[0] = []
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
    if (editorCookies.length) await page.context().addCookies(editorCookies)
    await page.goto('/prototypes/points')
    if (!editorCookies.length) {
        await page.getByLabel('账号', { exact: true }).fill('browser-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Changed-fixture-5938')
        await page.getByRole('button', { name: '登录', exact: true }).click()
    }
    await expect(
        page.getByRole('heading', { name: '点表交互原型' }),
    ).toBeVisible()
    editorCookies = await page.context().cookies()
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

test('canvas drag/connect, cycle rejection, properties, clipboard and history use own local format', async ({
    page,
}) => {
    await page.setViewportSize({ width: 1600, height: 1100 })
    await openPointEditor(page)
    await page.getByRole('link', { name: '画布交互原型' }).click()
    const errors: string[] = []
    page.on('pageerror', (error) => errors.push(error.message))
    const mutations: string[] = []
    page.on('request', (request) => {
        if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()))
            mutations.push(request.url())
    })
    await page
        .getByText('查看自有格式（定义与布局分离）', { exact: true })
        .click()
    const graph = async () =>
        JSON.parse(
            await page.getByTestId('graph-document').innerText(),
        ) as import('../src/features/canvas/model').GraphDocument
    const area = page.getByLabel('路线画布', { exact: true })
    const box = (await area.boundingBox())!
    async function drop(name: string, x: number, y: number) {
        await page
            .getByRole('button', { name, exact: true })
            .dragTo(area, { targetPosition: { x, y } })
    }
    await drop('Modbus 输入', 130, 100)
    await drop('字段映射', 420, 100)
    await drop('字段映射', 420, 280)
    await drop('MQTT 输出', 660, 280)
    await expect
        .poll(async () => (await graph()).definition.nodes.length)
        .toBe(4)
    await expect(page.locator('.react-flow__minimap-node')).toHaveCount(4)
    await expect
        .poll(async () =>
            Number(
                await page
                    .locator('.react-flow__minimap-node')
                    .first()
                    .getAttribute('width'),
            ),
        )
        .toBeGreaterThan(0)
    const ids = (await graph()).definition.nodes.map((n) => n.id)
    const node = (id: string) =>
        page.locator(`.react-flow__node[data-id="${id}"]`)
    async function connect(source: string, target: string) {
        await node(source)
            .locator('.react-flow__handle.source')
            .dragTo(node(target).locator('.react-flow__handle.target'))
    }
    await connect(ids[0], ids[1])
    await connect(ids[1], ids[2])
    await connect(ids[2], ids[3])
    await expect
        .poll(async () => (await graph()).definition.edges.length)
        .toBe(3)
    await connect(ids[2], ids[1])
    await expect(page.getByRole('alert')).toContainText('不允许自环或循环路线')
    expect((await graph()).definition.edges).toHaveLength(3)
    await node(ids[1]).click()
    await page.getByLabel('节点名称', { exact: true }).fill('本地映射')
    await page.getByRole('button', { name: '修改名称', exact: true }).click()
    expect((await graph()).definition.nodes[1].name).toBe('本地映射')
    const beforeMove = (await graph()).layout.positions[ids[1]]
    const nodeBox = (await node(ids[1]).boundingBox())!
    await page.mouse.move(nodeBox.x + 90, nodeBox.y + 20)
    await page.mouse.down()
    await page.mouse.move(nodeBox.x + 120, nodeBox.y + 65, { steps: 12 })
    await page.mouse.up()
    await expect
        .poll(async () => (await graph()).layout.positions[ids[1]].x)
        .not.toBe(beforeMove.x)
    await page.getByRole('button', { name: '撤销', exact: true }).click()
    expect((await graph()).layout.positions[ids[1]]).toEqual(beforeMove)
    await page.getByRole('button', { name: '重做', exact: true }).click()
    await node(ids[1]).click()
    await page.getByRole('button', { name: '复制所选', exact: true }).click()
    await page.getByRole('button', { name: '粘贴副本', exact: true }).click()
    expect((await graph()).definition.nodes).toHaveLength(5)
    await area.focus()
    await page.keyboard.press('Delete')
    expect((await graph()).definition.nodes).toHaveLength(4)
    await page.keyboard.press('Control+z')
    expect((await graph()).definition.nodes).toHaveLength(5)
    await page.keyboard.press('Control+y')
    expect((await graph()).definition.nodes).toHaveLength(4)
    const serialized = JSON.stringify(await graph())
    for (const privateField of [
        'selected',
        'dragging',
        'measured',
        'positionAbsolute',
    ])
        expect(serialized).not.toContain(`"${privateField}"`)
    // Box selection and group movement are real pointer gestures, one undo restores the group.
    const beforeGroup = (await graph()).layout.positions
    const currentBox = (await area.boundingBox())!
    await page.keyboard.down('Shift')
    await page.mouse.move(currentBox.x + 10, currentBox.y + 10)
    await page.mouse.down()
    await page.mouse.move(
        currentBox.x + currentBox.width - 10,
        currentBox.y + 410,
        { steps: 10 },
    )
    await page.mouse.up()
    await page.keyboard.up('Shift')
    await expect(page.locator('.react-flow__node.selected')).toHaveCount(4)
    const selectedBox = (await node(ids[0]).boundingBox())!
    await page.mouse.move(selectedBox.x + 90, selectedBox.y + 20)
    await page.mouse.down()
    await page.mouse.move(selectedBox.x + 110, selectedBox.y + 40, {
        steps: 10,
    })
    await page.mouse.up()
    for (const id of ids)
        expect((await graph()).layout.positions[id]).not.toEqual(
            beforeGroup[id],
        )
    await page.getByRole('button', { name: '撤销', exact: true }).click()
    expect((await graph()).layout.positions).toEqual(beforeGroup)
    const beforeView = await graph()
    await page.getByRole('button', { name: '放大', exact: true }).click()
    await page.getByRole('button', { name: '缩小', exact: true }).click()
    await page.getByRole('button', { name: '适应画布', exact: true }).click()
    expect(await graph()).toEqual(beforeView)
    expect(mutations).toEqual([])
    expect(errors).toEqual([])
    await page.screenshot({ path: 'test-results/canvas.png', fullPage: true })
    await page.getByRole('link', { name: '工作区', exact: true }).click()
    await page.getByRole('link', { name: '画布交互原型' }).click()
    await page
        .getByText('查看自有格式（定义与布局分离）', { exact: true })
        .click()
    expect((await graph()).definition.nodes).toHaveLength(0)
    expect(box.width).toBeGreaterThan(600)
})

test('canvas keeps 50-step UI history, text editing shortcuts and multi-node copy independent', async ({
    page,
}) => {
    test.setTimeout(60_000)
    await page.setViewportSize({ width: 1600, height: 1100 })
    await openPointEditor(page)
    await page.getByRole('link', { name: '画布交互原型' }).click()
    await page.getByRole('button', { name: '字段映射', exact: true }).click()
    await page
        .getByText('查看自有格式（定义与布局分离）', { exact: true })
        .click()
    const graph = async () =>
        JSON.parse(
            await page.getByTestId('graph-document').innerText(),
        ) as import('../src/features/canvas/model').GraphDocument
    for (let i = 0; i < 55; i++) {
        await page.getByLabel('节点名称', { exact: true }).fill(`映射${i}`)
        await page
            .getByRole('button', { name: '修改名称', exact: true })
            .click()
    }
    const area = page.getByLabel('路线画布', { exact: true })
    await area.focus()
    for (let i = 0; i < 50; i++) await page.keyboard.press('Control+z')
    expect((await graph()).definition.nodes[0].name).toBe('映射4')
    await expect(
        page.getByRole('button', { name: '撤销', exact: true }),
    ).toBeDisabled()
    for (let i = 0; i < 50; i++) await page.keyboard.press('Control+y')
    expect((await graph()).definition.nodes[0].name).toBe('映射54')
    await expect(
        page.getByRole('button', { name: '重做', exact: true }),
    ).toBeDisabled()
    await page.locator('.react-flow__node').click()
    const input = page.getByLabel('节点名称', { exact: true })
    await input.fill('未提交名称')
    await input.press('Control+z')
    expect((await graph()).definition.nodes[0].name).toBe('映射54')
    await input.press('Control+a')
    await input.press('Backspace')
    expect((await graph()).definition.nodes).toHaveLength(1)
    await page
        .getByRole('button', { name: 'Modbus 输入', exact: true })
        .dragTo(area, { targetPosition: { x: 60, y: 400 } })
    const nodes = page.locator('.react-flow__node')
    await nodes.nth(0).click()
    await nodes.nth(1).click({ modifiers: ['Shift'] })
    await expect(
        page.getByText('选择一个节点编辑属性。', { exact: true }),
    ).toBeVisible()
    await area.focus()
    await page.keyboard.press('Control+c')
    await page.keyboard.press('Control+v')
    expect((await graph()).definition.nodes).toHaveLength(4)
    await page.keyboard.press('Delete')
    expect((await graph()).definition.nodes).toHaveLength(2)
})

test('real connection drafts persist, conflicts and query failures retain edits, save never applies', async ({
    page,
}) => {
    test.setTimeout(60_000)
    await openPointEditor(page)
    await page.getByRole('link', { name: '连接草稿', exact: true }).click()
    const versionResponse = await page.request.get(
        '/api/edge-admin/v1/runtime/status',
    )
    // Runtime status may evolve diagnostics, but no configuration write is an apply request.
    const writes: string[] = []
    page.on('request', (request) => {
        if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()))
            writes.push(new URL(request.url()).pathname)
    })
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('browser-plc')
    await page.getByLabel('连接名称', { exact: true }).fill('浏览器 PLC')
    await page.getByLabel('主机或 IP', { exact: true }).fill('10.20.30.40')
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(page.getByRole('status')).toContainText(
        '已保存到网关草稿，尚未应用',
    )
    await page.reload()
    const row = page.getByRole('row').filter({ hasText: 'browser-plc' })
    await expect(row).toContainText('10.20.30.40:502')
    await row.getByRole('button', { name: '编辑连接', exact: true }).click()
    await expect(page.getByLabel('连接 key', { exact: true })).toBeDisabled()
    await expect(
        page.getByRole('combobox', { name: '协议类型', exact: true }),
    ).toBeDisabled()
    await page.getByLabel('连接名称', { exact: true }).fill('保留未保存编辑')
    // A real second management request changes the same global draft version.
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const list = await (
        await page.request.get('/api/edge-admin/v1/connections')
    ).json()
    const created = await page.request.post('/api/edge-admin/v1/connections', {
        headers: { 'X-CSRF-Token': session.csrfToken },
        data: {
            expectedVersion: list.version,
            key: 'browser-broker',
            name: '另一个编辑者的 Broker',
            kind: 'mqtt',
            enabled: true,
            mqtt: { host: '10.20.30.41' },
        },
    })
    expect(created.status()).toBe(200)
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByText(
            '草稿已变化或此操作受引用/唯一性约束。编辑内容已保留；请刷新列表核对，必要时明确重新载入此连接，不会自动覆盖他人的修改。',
        ),
    ).toBeVisible()
    await expect(page.getByLabel('连接名称', { exact: true })).toHaveValue(
        '保留未保存编辑',
    )
    await page.route('**/api/edge-admin/v1/connections', (route) =>
        route.request().method() === 'GET'
            ? route.fulfill({
                  status: 500,
                  contentType: 'application/problem+json',
                  body: '{"status":500}',
              })
            : route.continue(),
    )
    const queryFailure = page.waitForResponse(
        (response) =>
            response.url().endsWith('/connections') &&
            response.status() === 500,
    )
    await page
        .getByRole('button', { name: '刷新连接列表', exact: true })
        .click()
    await queryFailure
    await expect(page.getByLabel('连接名称', { exact: true })).toHaveValue(
        '保留未保存编辑',
    )
    await page.unroute('**/api/edge-admin/v1/connections')
    await page
        .getByRole('button', { name: '刷新连接列表', exact: true })
        .click()
    await expect(
        page.getByRole('row').filter({ hasText: 'browser-broker' }),
    ).toBeVisible()
    await expect(page.getByLabel('连接名称', { exact: true })).toHaveValue(
        '保留未保存编辑',
    )
    page.once('dialog', (dialog) => dialog.accept())
    await page
        .getByRole('button', { name: '重新载入此连接', exact: true })
        .click()
    await expect(page.getByLabel('连接名称', { exact: true })).toHaveValue(
        '浏览器 PLC',
    )
    await page.getByLabel('连接名称', { exact: true }).fill('更新后的 PLC')
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(row).toContainText('更新后的 PLC')
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('browser-http')
    await page.getByLabel('连接名称', { exact: true }).fill('HTTP 拉取')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('http')
    await page
        .getByLabel('HTTP 基础地址', { exact: true })
        .fill('https://example.test/api')
    await page.getByRole('button', { name: '添加请求头', exact: true }).click()
    await page.getByLabel('请求头 1 名称', { exact: true }).fill('Accept')
    await page
        .getByLabel('请求头 1 值', { exact: true })
        .fill('application/json')
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByRole('row').filter({ hasText: 'browser-http' }),
    ).toContainText('https://example.test/api/')
    await page
        .getByRole('combobox', { name: '协议筛选', exact: true })
        .selectOption('http')
    await expect(row).toHaveCount(0)
    page.once('dialog', (dialog) => dialog.accept())
    await page
        .getByRole('row')
        .filter({ hasText: 'browser-http' })
        .getByRole('button', { name: '删除连接', exact: true })
        .click()
    await expect(
        page.getByRole('row').filter({ hasText: 'browser-http' }),
    ).toHaveCount(0)
    expect(
        writes.every((path) =>
            /^\/api\/edge-admin\/v1\/connections(?:\/\d+)?$/.test(path),
        ),
    ).toBe(true)
    expect(versionResponse.status()).toBe(200)
    const afterStatus = await (
        await page.request.get('/api/edge-admin/v1/runtime/status')
    ).json()
    expect(afterStatus.coordinator.activeRevision).toBe(
        (await versionResponse.json()).coordinator.activeRevision,
    )
    await page
        .getByRole('combobox', { name: '协议筛选', exact: true })
        .selectOption('')
    await page.screenshot({
        path: 'test-results/connections-draft.png',
        fullPage: true,
    })
})

test('managed connection credentials save separately, never echo, and clear with explicit confirmation', async ({
    page,
}) => {
    await openPointEditor(page)
    await page.getByRole('link', { name: '连接草稿', exact: true }).click()
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('credential-broker')
    await page.getByLabel('连接名称', { exact: true }).fill('凭证测试 Broker')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('mqtt')
    await page.getByLabel('主机或 IP', { exact: true }).fill('10.20.30.42')
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    const row = page.getByRole('row').filter({ hasText: 'credential-broker' })
    await row.getByRole('button', { name: '编辑连接', exact: true }).click()
    const panel = page.getByRole('region', {
        name: '连接认证凭证',
        exact: true,
    })
    await expect(panel.getByText('未设置', { exact: true })).toBeVisible()
    const before = await (
        await page.request.get('/api/edge-admin/v1/connections')
    ).json()
    const connection = before.connections.find(
        (value: { key: string }) => value.key === 'credential-broker',
    )
    await panel.getByLabel('认证用户名', { exact: true }).fill('fixture-user')
    const secret = 'Browser-credential-fixture-1048'
    await panel.getByLabel('新密码或秘密', { exact: true }).fill(secret)
    page.once('dialog', (dialog) => dialog.accept())
    await panel
        .getByRole('button', { name: '立即保存凭证', exact: true })
        .click()
    await expect(panel.getByText('已设置', { exact: true })).toBeVisible()
    await expect(panel.getByLabel('新密码或秘密', { exact: true })).toHaveValue(
        '',
    )
    await expect(panel.getByLabel('认证用户名', { exact: true })).toHaveValue(
        '',
    )
    const metadata = await page.request.get(
        `/api/edge-admin/v1/connections/${connection.id}/mqtt-credential`,
    )
    expect(metadata.status()).toBe(200)
    expect(await metadata.text()).not.toContain(secret)
    expect(
        (
            await (
                await page.request.get('/api/edge-admin/v1/connections')
            ).json()
        ).version,
    ).toBe(before.version)
    expect(
        await page.evaluate(() =>
            JSON.stringify({
                local: { ...localStorage },
                session: { ...sessionStorage },
            }),
        ),
    ).not.toContain(secret)
    await page.reload()
    await row.getByRole('button', { name: '编辑连接', exact: true }).click()
    await expect(panel.getByText('已设置', { exact: true })).toBeVisible()
    await expect(panel.getByLabel('新密码或秘密', { exact: true })).toHaveValue(
        '',
    )
    page.once('dialog', (dialog) => dialog.accept())
    await panel.getByRole('button', { name: '清除凭证', exact: true }).click()
    await expect(panel.getByText('未设置', { exact: true })).toBeVisible()
    expect(
        (
            await page.request.get(
                `/api/edge-admin/v1/connections/${connection.id}/mqtt-credential`,
            )
        ).status(),
    ).toBe(404)
})

test('HTTP credential form writes each supported authentication kind without echoing secrets', async ({
    page,
}) => {
    await openPointEditor(page)
    await page.getByRole('link', { name: '连接草稿', exact: true }).click()
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page.getByLabel('连接 key', { exact: true }).fill('credential-http')
    await page.getByLabel('连接名称', { exact: true }).fill('凭证测试 HTTP')
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('http')
    await page
        .getByLabel('HTTP 基础地址', { exact: true })
        .fill('https://example.test/api/')
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await page
        .getByRole('row')
        .filter({ hasText: 'credential-http' })
        .getByRole('button', { name: '编辑连接', exact: true })
        .click()
    const panel = page.getByRole('region', {
        name: '连接认证凭证',
        exact: true,
    })
    for (const kind of ['basic', 'bearer', 'apiKeyHeader', 'apiKeyQuery']) {
        await panel
            .getByRole('combobox', { name: '新认证方式', exact: true })
            .selectOption(kind)
        if (kind === 'basic')
            await panel
                .getByLabel('认证用户名', { exact: true })
                .fill('fixture-user')
        if (kind.startsWith('apiKey'))
            await panel
                .getByLabel('API Key 参数名', { exact: true })
                .fill(kind === 'apiKeyHeader' ? 'X-Fixture-Key' : 'fixture_key')
        const secret = `fixture-${kind}-not-real`
        await panel.getByLabel('新密码或秘密', { exact: true }).fill(secret)
        const saved = page.waitForResponse(
            (response) =>
                response.url().endsWith('/http-credential') &&
                response.request().method() === 'PUT',
        )
        page.once('dialog', (dialog) => dialog.accept())
        await panel
            .getByRole('button', { name: '立即保存凭证', exact: true })
            .click()
        const response = await saved
        expect(response.status()).toBe(200)
        expect(response.request().postDataJSON().kind).toBe(kind)
        expect(await response.text()).not.toContain(secret)
        await expect(
            panel.getByLabel('新密码或秘密', { exact: true }),
        ).toHaveValue('')
        await expect(
            panel.getByRole('button', { name: '立即保存凭证', exact: true }),
        ).toBeEnabled()
    }
    await page.screenshot({
        path: 'test-results/connection-credential.png',
        fullPage: true,
    })
})

test('device drafts persist real references, preserve conflicted edits, and never apply', async ({
    page,
}) => {
    await openPointEditor(page)
    const fixtureSession = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const fixtureConnections = await (
        await page.request.get('/api/edge-admin/v1/connections')
    ).json()
    if (
        !fixtureConnections.connections.some(
            (c: { key: string }) => c.key === 'browser-plc',
        )
    ) {
        const created = await page.request.post(
            '/api/edge-admin/v1/connections',
            {
                headers: { 'X-CSRF-Token': fixtureSession.csrfToken },
                data: {
                    expectedVersion: fixtureConnections.version,
                    key: 'browser-plc',
                    name: '更新后的 PLC',
                    kind: 'modbus',
                    modbus: { host: '10.20.30.40' },
                },
            },
        )
        expect(created.status()).toBe(200)
    }
    await page.getByRole('link', { name: '设备草稿', exact: true }).click()
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page.getByLabel('设备 key', { exact: true }).fill('browser-meter')
    await page.getByLabel('设备名称', { exact: true }).fill('浏览器电表')
    await page.getByLabel('设备分组', { exact: true }).fill('一号车间')
    await page
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({ label: '更新后的 PLC (browser-plc)' })
    await page.getByLabel('UnitId', { exact: true }).fill('255')
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await expect(page.getByRole('status')).toHaveText(
        '设备草稿已保存；尚未应用。',
    )
    await page.reload()
    const meter = page.getByRole('button', { name: /浏览器电表 browser-meter/ })
    await meter.click()
    await expect(page.getByLabel('设备 key', { exact: true })).toBeDisabled()
    await expect(page.getByLabel('UnitId', { exact: true })).toHaveValue('255')
    await expect(page.getByLabel('设备分组', { exact: true })).toHaveValue(
        '一号车间',
    )
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const snapshot = await (
        await page.request.get('/api/edge-admin/v1/devices')
    ).json()
    const connections = await (
        await page.request.get('/api/edge-admin/v1/connections')
    ).json()
    const plc = connections.connections.find(
        (c: { key: string }) => c.key === 'browser-plc',
    )
    const rejectedDelete = await page.request.delete(
        '/api/edge-admin/v1/connections/' +
            plc.id +
            '?expectedVersion=' +
            snapshot.version,
        { headers: { 'X-CSRF-Token': session.csrfToken } },
    )
    expect(rejectedDelete.status()).toBe(409)
    expect((await rejectedDelete.json()).code).toBe(
        'connection.deviceReferenced',
    )
    await page.getByLabel('设备名称', { exact: true }).fill('保留设备编辑')
    const concurrent = await page.request.post('/api/edge-admin/v1/devices', {
        headers: { 'X-CSRF-Token': session.csrfToken },
        data: {
            expectedVersion: snapshot.version,
            key: 'other-simulator',
            name: '另一个编辑者',
            kind: 'simulator',
        },
    })
    expect(concurrent.status()).toBe(200)
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await expect(
        page.getByText(
            '草稿版本或引用冲突，输入已保留。刷新后确认重新载入，不会自动覆盖。',
        ),
    ).toBeVisible()
    await expect(page.getByLabel('设备名称', { exact: true })).toHaveValue(
        '保留设备编辑',
    )
    await page.route('**/api/edge-admin/v1/devices', (route) =>
        route.request().method() === 'GET'
            ? route.fulfill({
                  status: 500,
                  contentType: 'application/problem+json',
                  body: '{"status":500}',
              })
            : route.continue(),
    )
    const failed = page.waitForResponse(
        (response) =>
            response.url().endsWith('/devices') && response.status() === 500,
    )
    await page
        .getByRole('button', { name: '刷新设备与连接', exact: true })
        .click()
    await failed
    await expect(page.getByLabel('设备名称', { exact: true })).toHaveValue(
        '保留设备编辑',
    )
    await page.unroute('**/api/edge-admin/v1/devices')
    await page
        .getByRole('button', { name: '刷新设备与连接', exact: true })
        .click()
    await expect(
        page.getByRole('button', { name: '重新载入设备', exact: true }),
    ).toBeEnabled()
    page.once('dialog', (dialog) => dialog.accept())
    await page
        .getByRole('button', { name: '重新载入设备', exact: true })
        .click()
    await expect(page.getByLabel('设备名称', { exact: true })).toHaveValue(
        '浏览器电表',
    )
    await page.getByLabel('设备名称', { exact: true }).fill('已更新的电表')
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page
        .getByRole('button', { name: /已更新的电表 browser-meter/ })
        .click()
    await page.screenshot({
        path: 'test-results/device-draft.png',
        fullPage: true,
    })
    const runtime = await (
        await page.request.get('/api/edge-admin/v1/runtime/status')
    ).json()
    expect(runtime.coordinator.activeRevision).toBeNull()
    page.once('dialog', (dialog) => dialog.accept())
    await page.getByRole('button', { name: '删除设备', exact: true }).click()
    await expect(page.getByRole('status')).toHaveText(
        '设备草稿已删除；运行配置未改变。',
    )
    const final = await (
        await page.request.get('/api/edge-admin/v1/devices')
    ).json()
    expect(
        final.devices.some((d: { key: string }) => d.key === 'browser-meter'),
    ).toBe(false)
})

test('Modbus source point table persists normalized offsets and a bounded 10000-row real HTTP payload', async ({
    page,
}) => {
    await openPointEditor(page)
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const headers = { 'X-CSRF-Token': session.csrfToken }
    const list = await (
        await page.request.get('/api/edge-admin/v1/connections')
    ).json()
    const connectionResponse = await page.request.post(
        '/api/edge-admin/v1/connections',
        {
            headers,
            data: {
                expectedVersion: list.version,
                key: 'source-plc',
                name: '来源测试 PLC',
                kind: 'modbus',
                modbus: { host: '10.20.30.55' },
            },
        },
    )
    expect(connectionResponse.status()).toBe(200)
    const connection = await connectionResponse.json()
    const deviceResponse = await page.request.post(
        '/api/edge-admin/v1/devices',
        {
            headers,
            data: {
                expectedVersion: connection.version,
                key: 'source-meter',
                name: '来源测试电表',
                kind: 'modbus',
                connectionId: connection.connection.id,
                unitId: 1,
            },
        },
    )
    expect(deviceResponse.status()).toBe(200)
    const device = (await deviceResponse.json()).device
    const endpoint = '/api/edge-admin/v1/devices/' + device.id + '/inputs'
    await page.goto('/devices/' + device.id + '/inputs')
    await page.getByRole('button', { name: '新建采集组', exact: true }).click()
    await page.getByLabel('来源 key', { exact: true }).fill('fast')
    await page.getByLabel('来源名称', { exact: true }).fill('快速采集')
    await pasteIntoGrid(page, pointText(2))
    await page
        .getByRole('combobox', { name: '地址写法', exact: true })
        .selectOption('manual')
    await page
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    await expect(page.getByRole('status')).toHaveText(
        '来源与点表已保存到网关草稿，尚未应用。',
    )
    const saved = await (await page.request.get(endpoint)).json()
    expect(saved.inputs[0].modbus.points[0].address).toBe(0)
    expect(saved.inputs[0].modbus.points[1].address).toBe(1)
    await page.reload()
    await page.getByRole('button', { name: /快速采集 \(fast\)/ }).click()
    await expect(page.getByLabel('来源 key', { exact: true })).toBeDisabled()
    await expect(page.getByLabel('第 1 行 key', { exact: true })).toHaveValue(
        'point_0',
    )
    await page.getByRole('button', { name: '添加空行', exact: true }).click()
    await expect(
        page.getByText('3 行 · 0 行选中 · 1 行错误 · 0 行重叠提示'),
    ).toBeVisible()
    await page
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    await expect(
        page.getByText('点表或来源表单存在错误，请检查标红行及重复请求头。'),
    ).toBeVisible()
    page.once('dialog', (dialog) => dialog.accept())
    await page
        .getByRole('button', { name: '重新载入来源', exact: true })
        .click()
    // A second source uses the same point key: device scope, not just table scope.
    const duplicate = await page.request.post(endpoint, {
        headers,
        data: {
            expectedVersion: saved.version,
            key: 'slow',
            name: 'Slow',
            modbus: { periodMs: 1000, points: saved.inputs[0].modbus.points },
        },
    })
    expect(duplicate.status()).toBe(409)
    expect((await duplicate.json()).code).toBe('input.duplicateDeviceField')
    const bulk = {
        expectedVersion: saved.version,
        key: 'bulk',
        name: '万点采集组',
        modbus: {
            periodMs: 5000,
            points: Array.from({ length: 10000 }, (_, index) => ({
                key: 'bulk_' + index,
                name: 'n'.repeat(64),
                unit: 'u'.repeat(64),
                functionCode: 3,
                address: index,
                type: 'uint16',
                enabled: true,
            })),
        },
    }
    expect(JSON.stringify(bulk).length).toBeGreaterThan(1024 * 1024)
    const bulkResponse = await page.request.post(endpoint, {
        headers,
        data: bulk,
    })
    expect(bulkResponse.status()).toBe(200)
    // Only source writes opt into 16 MiB; their own ceiling and the ordinary 1 MiB limit remain effective.
    const tooLarge = await page.request.post(endpoint, {
        headers: { ...headers, 'Content-Type': 'application/json' },
        data: ' '.repeat(16 * 1024 * 1024 + 1),
    })
    expect(tooLarge.status()).toBe(413)
    const ordinaryTooLarge = await page.request.post(
        '/api/edge-admin/v1/connections',
        {
            headers: { ...headers, 'Content-Type': 'application/json' },
            data: ' '.repeat(1024 * 1024 + 1),
        },
    )
    expect(ordinaryTooLarge.status()).toBe(413)

    await page.getByLabel('来源名称', { exact: true }).fill('保留来源修改')
    await page
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    await expect(
        page.getByText(
            '草稿版本、字段 key 或引用冲突。输入已保留，请刷新后明确重新载入。',
        ),
    ).toBeVisible()
    await expect(page.getByLabel('来源名称', { exact: true })).toHaveValue(
        '保留来源修改',
    )
    await page
        .getByRole('button', { name: '刷新来源与设备', exact: true })
        .click()
    await expect(
        page.getByRole('button', { name: /万点采集组 \(bulk\)/ }),
    ).toBeEnabled()
    page.once('dialog', (dialog) => dialog.accept())
    await page.getByRole('button', { name: /万点采集组 \(bulk\)/ }).click()
    await expect(
        page.getByText('10000 行 · 0 行选中 · 0 行错误 · 0 行重叠提示'),
    ).toBeVisible()
    await page.screenshot({
        path: 'test-results/source-points.png',
        fullPage: true,
    })
    const reloaded = await (await page.request.get(endpoint)).json()
    expect(
        reloaded.inputs.find((i: { key: string }) => i.key === 'bulk').modbus
            .points,
    ).toHaveLength(10000)
    expect(
        (
            await (
                await page.request.get('/api/edge-admin/v1/runtime/status')
            ).json()
        ).coordinator.activeRevision,
    ).toBeNull()
    page.once('dialog', (dialog) => dialog.accept())
    await page.getByRole('button', { name: '删除来源', exact: true }).click()
    await expect(page.getByRole('status')).toHaveText('来源草稿已删除。')
})

test('MQTT, HTTP and simulator sources save real typed fields without captured samples', async ({
    page,
}) => {
    await openPointEditor(page)
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const headers = { 'X-CSRF-Token': session.csrfToken }
    const sampleMarker = 'transient-private-sample-9173'
    const leakedRequests: string[] = []
    page.on('request', (request) => {
        if (
            request.url().includes(sampleMarker) ||
            request.postData()?.includes(sampleMarker)
        )
            leakedRequests.push(request.url())
    })
    for (const kind of ['mqtt', 'http', 'simulator'] as const) {
        let version = (
            await (await page.request.get('/api/edge-admin/v1/devices')).json()
        ).version
        let connectionId: number | null = null
        if (kind !== 'simulator') {
            const result = await page.request.post(
                '/api/edge-admin/v1/connections',
                {
                    headers,
                    data: {
                        expectedVersion: version,
                        key: 'json-' + kind,
                        name: 'JSON ' + kind,
                        kind,
                        ...(kind === 'mqtt'
                            ? { mqtt: { host: '10.20.30.66' } }
                            : {
                                  http: {
                                      baseAddress: 'https://example.test/api/',
                                  },
                              }),
                    },
                },
            )
            expect(result.status()).toBe(200)
            const saved = await result.json()
            version = saved.version
            connectionId = saved.connection.id
        }
        const result = await page.request.post('/api/edge-admin/v1/devices', {
            headers,
            data: {
                expectedVersion: version,
                key: 'json-device-' + kind,
                name: 'JSON 设备 ' + kind,
                kind,
                connectionId,
            },
        })
        expect(result.status()).toBe(200)
        const device = (await result.json()).device
        const endpoint = '/api/edge-admin/v1/devices/' + device.id + '/inputs'
        await page.goto('/devices/' + device.id + '/inputs')
        await page
            .getByRole('button', { name: '新建数据来源', exact: true })
            .click()
        await page.getByLabel('来源 key', { exact: true }).fill('source')
        await page.getByLabel('来源名称', { exact: true }).fill('来源 ' + kind)
        if (kind === 'simulator') {
            await page
                .getByRole('combobox', { name: '模拟数据方式', exact: true })
                .selectOption('custom')
            await page
                .getByRole('textbox', {
                    name: '模拟输入固定 JSON',
                    exact: true,
                })
                .fill('{"count":9007199254740993,"healthy":true}')
            await page.getByLabel('模拟缺字段比例', { exact: true }).fill('0.1')
        } else {
            const sample = page.getByRole('textbox', {
                name: '临时 JSON 样本',
                exact: true,
            })
            await sample.fill(
                '{"values":[9007199254740993],"private":"' +
                    sampleMarker +
                    '","empty":null}',
            )
            await page
                .getByRole('button', { name: '解析样本字段', exact: true })
                .click()
            await expect(
                page.getByRole('cell', {
                    name: '9007199254740993',
                    exact: true,
                }),
            ).toBeVisible()
            await expect(
                page.getByLabel('选择样本字段 /empty', { exact: true }),
            ).toBeDisabled()
            await page
                .getByLabel('选择样本字段 /values/0', { exact: true })
                .check()
            await page.screenshot({
                path: 'test-results/sample-fields-' + kind + '.png',
                fullPage: true,
            })
            await page
                .getByRole('button', { name: '添加选中字段', exact: true })
                .click()
            await expect(sample).toHaveValue('')
            await expect(
                page.getByLabel('JSON 字段 1 path', { exact: true }),
            ).toHaveValue('/values/0')
            await expect(
                page.getByLabel('JSON 字段 1 type', { exact: true }),
            ).toHaveValue('number')
            await sample.fill('{"count":1,"count":2}')
            await page
                .getByRole('button', { name: '解析样本字段', exact: true })
                .click()
            await expect(
                page.getByText(
                    '样本包含同名对象成员，无法唯一定位；请先消除重复字段。',
                    { exact: true },
                ),
            ).toBeVisible()
            await sample.fill('{bad')
            await page
                .getByRole('button', { name: '解析样本字段', exact: true })
                .click()
            await expect(
                page.getByText('样本不是有效 JSON；没有修改已有字段。', {
                    exact: true,
                }),
            ).toBeVisible()
            await expect(
                page.getByLabel('JSON 字段 1 path', { exact: true }),
            ).toHaveValue('/values/0')
            await page
                .getByRole('button', { name: '清除临时样本', exact: true })
                .click()
            await page
                .getByLabel('JSON 字段 1 key', { exact: true })
                .fill('count')
            await page
                .getByRole('combobox', {
                    name: 'JSON 字段 1 type',
                    exact: true,
                })
                .selectOption('integer')
            await page
                .getByLabel('JSON 字段 1 name', { exact: true })
                .fill('计数')
            await page.getByLabel('JSON 字段 1 unit', { exact: true }).fill('n')
            if (kind === 'mqtt') {
                await page
                    .getByLabel('MQTT Topic', { exact: true })
                    .fill('plant/meter')
                await page.getByLabel('处理保留消息', { exact: true }).check()
                const rejected = page.waitForResponse(
                    (response) =>
                        response.url().endsWith('/inputs') &&
                        response.request().method() === 'POST',
                )
                await page
                    .getByRole('button', { name: '保存来源草稿', exact: true })
                    .click()
                expect((await rejected).status()).toBe(400)
                await expect(
                    page.getByLabel('JSON 字段 1 key', { exact: true }),
                ).toHaveValue('count')
                await page
                    .getByLabel('上游时间 JSON 定位（可选）', { exact: true })
                    .fill('/stamp')
                await page
                    .getByRole('combobox', {
                        name: '上游时间格式',
                        exact: true,
                    })
                    .selectOption('unixMilliseconds')
            } else {
                await page
                    .getByLabel('HTTP 路径与非秘密查询参数', { exact: true })
                    .fill('values?site=plant')
                await page
                    .getByRole('button', {
                        name: '添加来源请求头',
                        exact: true,
                    })
                    .click()
                await page
                    .getByLabel('来源请求头 1 名称', { exact: true })
                    .fill('X-Site')
                await page
                    .getByLabel('来源请求头 1 值', { exact: true })
                    .fill('plant')
            }
        }
        await page
            .getByRole('button', { name: '保存来源草稿', exact: true })
            .click()
        await expect(page.getByRole('status')).toHaveText(
            '来源与点表已保存到网关草稿，尚未应用。',
        )
        const saved = (await (await page.request.get(endpoint)).json())
            .inputs[0]
        expect(saved.kind).toBe(kind)
        expect(JSON.stringify(saved)).not.toContain(sampleMarker)
        if (kind === 'simulator') {
            expect(saved.simulator.sampleJson).toBe(
                '{"count":9007199254740993,"healthy":true}',
            )
            expect(saved.simulator.missingField).toBe(0.1)
        } else {
            expect(saved[kind].fields[0]).toMatchObject({
                key: 'count',
                path: '/values/0',
                type: 'integer',
                name: '计数',
                unit: 'n',
            })
            expect(saved.simulator).toBeNull()
            if (kind === 'mqtt')
                expect(saved.mqtt.upstreamTime).toEqual({
                    path: '/stamp',
                    format: 'unixMilliseconds',
                })
            else expect(saved.http.headers).toEqual({ 'X-Site': 'plant' })
        }
        await page.reload()
        await page
            .getByRole('button', {
                name: new RegExp('来源 ' + kind + ' \\(source\\)'),
            })
            .click()
        if (kind === 'simulator')
            await expect(
                page.getByRole('textbox', {
                    name: '模拟输入固定 JSON',
                    exact: true,
                }),
            ).toHaveValue('{"count":9007199254740993,"healthy":true}')
        else
            await expect(
                page.getByLabel('JSON 字段 1 key', { exact: true }),
            ).toHaveValue('count')
        if (kind !== 'simulator') {
            await expect(
                page.getByRole('textbox', {
                    name: '临时 JSON 样本',
                    exact: true,
                }),
            ).toHaveValue('')
            // Reloading a source also clears an unconsumed sample, not just appended candidates.
            await page
                .getByRole('textbox', { name: '临时 JSON 样本', exact: true })
                .fill(sampleMarker)
            await page
                .getByRole('button', { name: '重新载入来源', exact: true })
                .click()
            await expect(
                page.getByRole('textbox', {
                    name: '临时 JSON 样本',
                    exact: true,
                }),
            ).toHaveValue('')
        }
        expect(leakedRequests).toEqual([])
        expect(
            await page.evaluate(() => ({ ...localStorage, ...sessionStorage })),
        ).toEqual({})
        await page.screenshot({
            path: 'test-results/source-' + kind + '.png',
            fullPage: true,
        })
        expect(
            (
                await (
                    await page.request.get('/api/edge-admin/v1/runtime/status')
                ).json()
            ).coordinator.activeRevision,
        ).toBeNull()
    }
})

test('device copy atomically persists disabled copies with independent sources and keeps stale edits', async ({
    page,
}) => {
    await openPointEditor(page)
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const headers = { 'X-CSRF-Token': session.csrfToken }
    let version = (
        await (await page.request.get('/api/edge-admin/v1/devices')).json()
    ).version
    const connectionResponse = await page.request.post(
        '/api/edge-admin/v1/connections',
        {
            headers,
            data: {
                expectedVersion: version,
                key: 'copy-connection',
                name: 'Copy PLC',
                kind: 'modbus',
                modbus: { host: '10.20.30.77' },
            },
        },
    )
    expect(connectionResponse.status()).toBe(200)
    const connection = await connectionResponse.json()
    const deviceResponse = await page.request.post(
        '/api/edge-admin/v1/devices',
        {
            headers,
            data: {
                expectedVersion: connection.version,
                key: 'copy-original',
                name: '复制测试设备',
                kind: 'modbus',
                connectionId: connection.connection.id,
                unitId: 3,
                group: '复制测试',
            },
        },
    )
    expect(deviceResponse.status()).toBe(200)
    const device = await deviceResponse.json()
    const sourceResponse = await page.request.post(
        `/api/edge-admin/v1/devices/${device.device.id}/inputs`,
        {
            headers,
            data: {
                expectedVersion: device.version,
                key: 'fast',
                name: '快速采集',
                modbus: {
                    periodMs: 1000,
                    points: [
                        {
                            key: 'temperature',
                            address: 0,
                            functionCode: 3,
                            type: 'uint32',
                            byteOrder: 'ABCD',
                            enabled: false,
                        },
                    ],
                },
            },
        },
    )
    expect(sourceResponse.status()).toBe(200)
    const source = await sourceResponse.json()
    version = source.version
    await page.goto('/devices')
    await page
        .getByRole('button', { name: /复制测试设备 copy-original/ })
        .click()
    await page.getByLabel('设备名称', { exact: true }).fill('未保存名称')
    await expect(
        page.getByRole('button', { name: '复制设备到草稿', exact: true }),
    ).toBeDisabled()
    await page.getByLabel('设备名称', { exact: true }).fill('复制测试设备')
    await page
        .getByRole('combobox', { name: '复制份数', exact: true })
        .selectOption('2')
    // A different draft mutation makes the open copy form stale.
    const unrelated = await page.request.post('/api/edge-admin/v1/devices', {
        headers,
        data: {
            expectedVersion: version,
            key: 'copy-unrelated',
            name: 'Other',
            kind: 'simulator',
        },
    })
    expect(unrelated.status()).toBe(200)
    const unrelatedVersion = (await unrelated.json()).version
    const stale = page.waitForResponse(
        (r) => r.url().endsWith('/copy') && r.request().method() === 'POST',
    )
    await page
        .getByRole('button', { name: '复制设备到草稿', exact: true })
        .click()
    expect((await stale).status()).toBe(409)
    await expect(page.getByLabel('设备名称', { exact: true })).toHaveValue(
        '复制测试设备',
    )
    await expect(
        page.getByRole('combobox', { name: '复制份数', exact: true }),
    ).toHaveValue('2')
    await page
        .getByRole('button', { name: '刷新设备与连接', exact: true })
        .click()
    await expect(
        page.getByText('全局草稿版本：' + unrelatedVersion, { exact: true }),
    ).toBeVisible()
    await page
        .getByRole('button', { name: '重新载入设备', exact: true })
        .click()
    const copiedResponse = page.waitForResponse(
        (r) => r.url().endsWith('/copy') && r.request().method() === 'POST',
    )
    await page
        .getByRole('button', { name: '复制设备到草稿', exact: true })
        .click()
    const copied = await copiedResponse
    expect(copied.status()).toBe(200)
    const result = await copied.json()
    expect(result.devices).toHaveLength(2)
    await expect(page.getByRole('status')).toHaveText(
        '设备及来源已复制到草稿，副本默认停用；运行配置未改变。',
    )
    for (const copy of result.devices) {
        expect(copy.enabled).toBe(false)
        expect(copy.key).toMatch(/^copy-original_copy_[0-9]+$/)
        expect(copy.connectionId).toBe(device.device.connectionId)
        expect(copy.unitId).toBe(3)
        const inputs = await (
            await page.request.get(
                `/api/edge-admin/v1/devices/${copy.id}/inputs`,
            )
        ).json()
        expect(inputs.inputs).toHaveLength(1)
        expect(inputs.inputs[0].id).not.toBe(source.input.id)
        expect(inputs.inputs[0].modbus.points).toEqual(
            source.input.modbus.points,
        )
    }
    expect(result.devices[0].id).not.toBe(result.devices[1].id)
    await page.reload()
    await page
        .getByRole('button', { name: new RegExp(result.devices[0].key) })
        .click()
    await expect(page.getByLabel('草稿启用', { exact: true })).not.toBeChecked()
    await page.screenshot({
        path: 'test-results/device-copy.png',
        fullPage: true,
    })
    expect(
        (
            await (
                await page.request.get('/api/edge-admin/v1/runtime/status')
            ).json()
        ).coordinator.activeRevision,
    ).toBeNull()
})

test('device CSV downloads BOM templates, imports GBK, reports row errors and atomically merges real points', async ({
    page,
}) => {
    await openPointEditor(page)
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const headers = { 'X-CSRF-Token': session.csrfToken }
    const version = (
        await (await page.request.get('/api/edge-admin/v1/devices')).json()
    ).version
    const connectionResponse = await page.request.post(
        '/api/edge-admin/v1/connections',
        {
            headers,
            data: {
                expectedVersion: version,
                key: 'csv-plc',
                name: 'CSV PLC',
                kind: 'modbus',
                modbus: { host: '10.20.30.78' },
            },
        },
    )
    expect(connectionResponse.status()).toBe(200)
    const connection = await connectionResponse.json()
    const deviceResponse = await page.request.post(
        '/api/edge-admin/v1/devices',
        {
            headers,
            data: {
                expectedVersion: connection.version,
                key: 'csv-device',
                name: 'CSV 设备',
                kind: 'modbus',
                connectionId: connection.connection.id,
                unitId: 1,
                addressNotation: 'manual',
            },
        },
    )
    expect(deviceResponse.status()).toBe(200)
    const device = await deviceResponse.json()
    const inputUrl = `/api/edge-admin/v1/devices/${device.device.id}/inputs`
    const fastResponse = await page.request.post(inputUrl, {
        headers,
        data: {
            expectedVersion: device.version,
            key: 'fast',
            name: 'Fast',
            modbus: {
                periodMs: 1000,
                points: [
                    {
                        key: 'p',
                        name: '=1+1',
                        functionCode: 3,
                        address: 0,
                        type: 'uint16',
                    },
                    { key: 'coil', functionCode: 1, address: 0, type: 'bool' },
                ],
            },
        },
    })
    expect(fastResponse.status()).toBe(200)
    const fast = await fastResponse.json()
    const slowResponse = await page.request.post(inputUrl, {
        headers,
        data: {
            expectedVersion: fast.version,
            key: 'slow',
            name: 'Slow',
            modbus: { periodMs: 2000, points: [] },
        },
    })
    expect(slowResponse.status()).toBe(200)
    await page.goto(`/devices/${device.device.id}/inputs`)
    const panel = page.getByRole('region', {
        name: '设备点表 CSV',
        exact: true,
    })
    const file = panel.getByLabel('点表 CSV 文件', { exact: true })
    async function download(name: string) {
        const pending = page.waitForEvent('download')
        await panel.getByRole('button', { name, exact: true }).click()
        const result = await pending
        const { readFile } = await import('node:fs/promises')
        const bytes = await readFile((await result.path())!)
        expect([...bytes.subarray(0, 3)]).toEqual([239, 187, 191])
        return bytes
    }
    await download('下载 CSV 模板')
    const exported = await download('导出设备点表')
    expect(exported.toString('utf8')).toContain('"00001"')
    expect(exported.toString('utf8')).toContain('"40001"')
    expect(exported.toString('utf8')).toContain('"\'=1+1"')
    async function upload(bytes: Buffer, expectedStatus: number) {
        await file.setInputFiles({
            name: 'points.csv',
            mimeType: 'text/csv',
            buffer: bytes,
        })
        const pending = page.waitForResponse(
            (r) =>
                r.url().endsWith('/points/csv') &&
                r.request().method() === 'POST',
        )
        await panel
            .getByRole('button', { name: '导入点表到草稿', exact: true })
            .click()
        expect((await pending).status()).toBe(expectedStatus)
    }
    await upload(exported, 200)
    await expect(panel.getByRole('status')).toContainText(
        '新增 0，更新 0，跳过 2',
    )
    const header =
        'inputKey,key,name,functionCode,address,addressNotation,type,bit,byteOrder,unit,enabled\n'
    const current = await (await page.request.get(inputUrl)).json()
    const csvUrl = `/api/edge-admin/v1/devices/${device.device.id}/points/csv`
    const tooLarge = await page.request.post(csvUrl, {
        headers,
        data: {
            expectedVersion: current.version,
            mode: 'incremental',
            encoding: 'utf8',
            content: Buffer.alloc(5 * 1024 * 1024 + 1).toString('base64'),
        },
    })
    expect(tooLarge.status()).toBe(400)
    const overBody = await page.request.post(csvUrl, {
        headers: { ...headers, 'Content-Type': 'application/json' },
        data: JSON.stringify({ content: 'x'.repeat(8 * 1024 * 1024) }),
    })
    expect(overBody.status()).toBe(413)
    expect((await (await page.request.get(inputUrl)).json()).version).toBe(
        current.version,
    )

    await upload(
        Buffer.from(
            header +
                'slow,p,changed,3,40011,manual,uint16,,,V,false\nfast,new,bad,3,70000,offset,uint16,,,V,true\n',
        ),
        400,
    )
    await expect(panel.getByRole('alert')).toContainText('csv[3]')
    expect(
        await file.evaluate((node: HTMLInputElement) => node.files?.length),
    ).toBe(1)
    expect((await (await page.request.get(inputUrl)).json()).version).toBe(
        current.version,
    )
    const valid = Buffer.from(
        header +
            'slow,p,changed,3,40011,manual,uint16,,,V,false\nfast,new,new,3,7,offset,uint16,,,V,true\n',
    )
    await upload(valid, 200)
    await expect(panel.getByRole('status')).toContainText(
        '新增 1，更新 0，跳过 1',
    )
    await panel
        .getByRole('combobox', { name: 'CSV 导入模式', exact: true })
        .selectOption('overwrite')
    await upload(valid, 200)
    await expect(panel.getByRole('status')).toContainText(
        '新增 0，更新 2，跳过 0',
    )
    await panel
        .getByRole('combobox', { name: 'CSV 文件编码', exact: true })
        .selectOption('gbk')
    const gbk = Buffer.concat([
        Buffer.from(header + 'fast,gbk,'),
        Buffer.from('cec2b6c8', 'hex'),
        Buffer.from(',3,20,offset,uint16,,,V,true\n'),
    ])
    await upload(gbk, 200)
    await expect(panel.getByRole('status')).toContainText(
        '新增 1，更新 0，跳过 0',
    )
    const saved = await (await page.request.get(inputUrl)).json()
    const fastPoints = saved.inputs.find(
        (input: { key: string }) => input.key === 'fast',
    ).modbus.points
    const slowPoints = saved.inputs.find(
        (input: { key: string }) => input.key === 'slow',
    ).modbus.points
    expect(
        fastPoints.map((point: { key: string }) => point.key).sort(),
    ).toEqual(['coil', 'gbk', 'new'])
    expect(
        fastPoints.find((point: { key: string }) => point.key === 'gbk').name,
    ).toBe('温度')
    expect(slowPoints[0]).toMatchObject({
        key: 'p',
        address: 10,
        enabled: false,
    })
    await expect(file).toHaveValue('')
    await page.screenshot({
        path: 'test-results/device-csv.png',
        fullPage: true,
    })
    await page.reload()
    expect((await download('导出设备点表')).toString('utf8')).toContain('温度')
    expect(
        (
            await (
                await page.request.get('/api/edge-admin/v1/runtime/status')
            ).json()
        ).coordinator.activeRevision,
    ).toBeNull()
})

test('MQTT test uses unsaved parameters, reports real policy denial and does not save secrets or drafts', async ({
    page,
}) => {
    await openPointEditor(page)
    await page.getByRole('link', { name: '连接草稿', exact: true }).click()
    const before = await (
        await page.request.get('/api/edge-admin/v1/connections')
    ).json()
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('mqtt')
    // Key/name intentionally empty: testing must not require saving the draft.
    await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
    const panel = page.getByRole('region', {
        name: 'MQTT 真实连接测试',
        exact: true,
    })
    await panel
        .getByRole('combobox', { name: '测试认证' })
        .selectOption('provided')
    await panel
        .getByLabel('临时用户名', { exact: true })
        .fill('transient-browser-user')
    await panel
        .getByLabel('临时密码', { exact: true })
        .fill('Transient-fixture-8294')
    const response = page.waitForResponse((item) =>
        item.url().endsWith('/connections/test/mqtt'),
    )
    await panel
        .getByRole('button', { name: '测试 MQTT 连接', exact: true })
        .click()
    expect((await response).status()).toBe(200)
    await expect(panel.getByRole('status')).toContainText('被出站规则拦截')
    await expect(panel.getByRole('status')).toContainText('127.0.0.1:1883')
    const stages = panel.getByRole('table', { name: '连接测试阶段结果' })
    await expect(
        stages.getByRole('row').filter({ hasText: '地址解析' }),
    ).toContainText('成功')
    await expect(
        stages.getByRole('row').filter({ hasText: '建立 TCP 连接' }),
    ).toContainText('失败')
    await expect(
        stages.getByRole('row').filter({ hasText: 'TLS 握手' }),
    ).toContainText('未启用')
    await expect(
        stages.getByRole('row').filter({ hasText: '协议握手' }),
    ).toContainText('未到达')
    await expect(panel.getByLabel('临时密码', { exact: true })).toHaveValue('')
    expect(
        await (await page.request.get('/api/edge-admin/v1/connections')).json(),
    ).toEqual(before)
    expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
    expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
    await page.screenshot({
        path: 'test-results/mqtt-connection-test.png',
        fullPage: true,
    })
})

test('HTTP unsaved test performs one real GET with transient credentials, status and phases without reading a body or following redirects', async ({
    page,
}) => {
    const calls: {
        url: string | undefined
        authorization: string | undefined
    }[] = []
    let status = 204
    const peer = createServer((request, response) => {
        calls.push({
            url: request.url,
            authorization: request.headers.authorization,
        })
        response.writeHead(status, {
            Location: '/must-not-follow',
            'Content-Length': status === 204 ? '0' : '10000000',
        })
        response.flushHeaders()
        if (status === 204) response.end()
        // Other statuses deliberately never send the declared body.
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string')
        throw new Error('fixture-listener-address')
    try {
        await openPointEditor(page)
        await page.getByRole('link', { name: '连接草稿', exact: true }).click()
        const before = await (
            await page.request.get('/api/edge-admin/v1/connections')
        ).json()
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        const policyUrl = '/api/edge-admin/v1/system/outbound-policy'
        const policy = await (await page.request.get(policyUrl)).json()
        const allowed = await page.request.put(policyUrl, {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: {
                expectedVersion: policy.version,
                networks: policy.networks,
                localPorts: [...policy.localPorts, address.port],
            },
        })
        expect(allowed.status()).toBe(200)
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page
            .getByRole('combobox', { name: '协议类型', exact: true })
            .selectOption('http')
        await page
            .getByLabel('HTTP 基础地址', { exact: true })
            .fill(`http://127.0.0.1:${address.port}/base/`)
        const panel = page.getByRole('region', {
            name: 'HTTP 真实连接测试',
            exact: true,
        })
        await panel
            .getByRole('combobox', { name: '测试认证' })
            .selectOption('provided')
        await panel
            .getByLabel('临时密码/Token/API Key', { exact: true })
            .fill('fixture-browser-token')
        await panel
            .getByRole('button', { name: '测试 HTTP 连接', exact: true })
            .click()
        await expect(panel.getByRole('status')).toContainText('HTTP 204')
        expect(calls).toEqual([
            { url: '/base/', authorization: 'Bearer fixture-browser-token' },
        ])
        await expect(
            panel.getByLabel('临时密码/Token/API Key', { exact: true }),
        ).toHaveValue('')
        const stages = panel.getByRole('table', { name: '连接测试阶段结果' })
        await expect(
            stages.getByRole('row').filter({ hasText: 'HTTP 请求与响应头' }),
        ).toContainText('成功')
        await expect(
            stages
                .getByRole('row')
                .filter({ hasText: '认证（依据 HTTP 响应）' }),
        ).toContainText('未能确认')
        status = 302
        await panel
            .getByRole('combobox', { name: '测试认证' })
            .selectOption('none')
        await panel
            .getByRole('button', { name: '测试 HTTP 连接', exact: true })
            .click()
        await expect(panel.getByRole('status')).toContainText('HTTP 302')
        expect(calls).toHaveLength(2)
        expect(calls[1]).toEqual({ url: '/base/', authorization: undefined })
        expect(
            await (
                await page.request.get('/api/edge-admin/v1/connections')
            ).json(),
        ).toEqual(before)
        expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
        expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
        await page.screenshot({
            path: 'test-results/http-connection-test.png',
            fullPage: true,
        })
        await panel
            .getByRole('combobox', { name: '测试认证' })
            .selectOption('provided')
        await panel
            .getByLabel('临时密码/Token/API Key', { exact: true })
            .fill('discard-this-fixture-secret')
        page.once('dialog', (dialog) => dialog.accept())
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page
            .getByRole('combobox', { name: '协议类型', exact: true })
            .selectOption('http')
        await expect(
            page
                .getByRole('region', { name: 'HTTP 真实连接测试' })
                .getByRole('combobox', { name: '测试认证' }),
        ).toHaveValue('none')
        const restored = await page.request.put(policyUrl, {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: {
                expectedVersion: (await allowed.json()).version,
                networks: policy.networks,
                localPorts: policy.localPorts,
            },
        })
        expect(restored.status()).toBe(200)
    } finally {
        peer.closeAllConnections()
        await new Promise<void>((resolve, reject) =>
            peer.close((error) => (error ? reject(error) : resolve())),
        )
    }
})

test('Modbus unsaved TCP and RTU connection tests send no protocol bytes and preserve drafts', async ({
    page,
}) => {
    let accepted = 0
    let bytes = 0
    const sockets = new Set<Socket>()
    const peer = createTcpServer((socket) => {
        accepted++
        sockets.add(socket)
        socket.on('data', (chunk) => {
            bytes += chunk.length
        })
        socket.on('close', () => sockets.delete(socket))
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string')
        throw new Error('fixture-listener-address')
    let restore: (() => Promise<void>) | undefined
    try {
        await openPointEditor(page)
        await page.getByRole('link', { name: '连接草稿', exact: true }).click()
        const before = await (
            await page.request.get('/api/edge-admin/v1/connections')
        ).json()
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        const policyUrl = '/api/edge-admin/v1/system/outbound-policy'
        const policy = await (await page.request.get(policyUrl)).json()
        const allowed = await page.request.put(policyUrl, {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: {
                expectedVersion: policy.version,
                networks: policy.networks,
                localPorts: [...policy.localPorts, address.port],
            },
        })
        expect(allowed.status()).toBe(200)
        const allowedVersion = (await allowed.json()).version
        restore = async () => {
            const response = await page.request.put(policyUrl, {
                headers: { 'X-CSRF-Token': session.csrfToken },
                data: {
                    expectedVersion: allowedVersion,
                    networks: policy.networks,
                    localPorts: policy.localPorts,
                },
            })
            expect(response.status()).toBe(200)
        }
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
        await page
            .getByLabel('端口', { exact: true })
            .fill(String(address.port))
        const panel = page.getByRole('region', {
            name: 'Modbus 真实连接测试',
            exact: true,
        })
        for (const format of ['tcp', 'rtuOverTcp']) {
            await page
                .getByRole('combobox', { name: '帧格式', exact: true })
                .selectOption(format)
            const response = page.waitForResponse((item) =>
                item.url().endsWith('/connections/test/modbus'),
            )
            await panel
                .getByRole('button', { name: '测试 Modbus 连接', exact: true })
                .click()
            expect((await response).status()).toBe(200)
            await expect(panel.getByRole('status')).toContainText(
                'TCP 建连成功（尚未验证从站读取）',
            )
            await expect(
                panel
                    .getByRole('table')
                    .getByRole('row')
                    .filter({ hasText: '协议读取（本测试不执行）' }),
            ).toContainText('不执行')
        }
        await expect.poll(() => accepted).toBe(2)
        await expect.poll(() => sockets.size).toBe(0)
        expect(bytes).toBe(0)
        expect(
            await (
                await page.request.get('/api/edge-admin/v1/connections')
            ).json(),
        ).toEqual(before)
        await page.screenshot({
            path: 'test-results/modbus-connection-test.png',
            fullPage: true,
        })
        page.once('dialog', (dialog) => dialog.accept())
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await expect(
            page
                .getByRole('region', { name: 'Modbus 真实连接测试' })
                .getByRole('status'),
        ).toHaveCount(0)
    } finally {
        try {
            await restore?.()
        } finally {
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve, reject) =>
                peer.close((error) => (error ? reject(error) : resolve())),
            )
        }
    }
})

test('Modbus device preview reads real groups, preserves 64-bit text and adopts byte order into draft only', async ({
    page,
}) => {
    const sockets = new Set<Socket>()
    let connections = 0
    let requests = 0
    const peer = createTcpServer((socket) => {
        connections++
        sockets.add(socket)
        let pending = Buffer.alloc(0)
        socket.on('close', () => sockets.delete(socket))
        socket.on('error', () => {})
        socket.on('data', (chunk) => {
            pending = Buffer.concat([pending, chunk])
            while (pending.length >= 12) {
                const request = pending.subarray(0, 12)
                pending = pending.subarray(12)
                requests++
                const count = request.readUInt16BE(10)
                const address = request.readUInt16BE(8)
                if (request[7] !== 3 || count > 125)
                    throw new Error('unexpected-modbus-read')
                const reply = Buffer.alloc(9 + count * 2)
                request.copy(reply, 0, 0, 4)
                reply.writeUInt16BE(3 + count * 2, 4)
                reply[6] = request[6]
                reply[7] = 3
                reply[8] = count * 2
                for (let index = 0; index < count; index++)
                    reply.writeUInt16BE(0x1000 + address + index, 9 + index * 2)
                socket.write(reply)
            }
        })
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string')
        throw new Error('fixture-address')
    let restore: (() => Promise<void>) | undefined
    try {
        await openPointEditor(page)
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const policyUrl = '/api/edge-admin/v1/system/outbound-policy'
        const policy = await (await page.request.get(policyUrl)).json()
        const allowed = await page.request.put(policyUrl, {
            headers,
            data: {
                networks: policy.networks,
                expectedVersion: policy.version,
                localPorts: [...policy.localPorts, address.port],
            },
        })
        expect(allowed.status()).toBe(200)
        const allowedVersion = (await allowed.json()).version
        restore = async () => {
            const response = await page.request.put(policyUrl, {
                headers,
                data: {
                    expectedVersion: allowedVersion,
                    localPorts: policy.localPorts,
                    networks: policy.networks,
                },
            })
            expect(response.status()).toBe(200)
        }
        const version = (
            await (
                await page.request.get('/api/edge-admin/v1/connections')
            ).json()
        ).version
        const connectionResponse = await page.request.post(
            '/api/edge-admin/v1/connections',
            {
                headers,
                data: {
                    expectedVersion: version,
                    key: 'preview-plc',
                    name: '预览 PLC',
                    kind: 'modbus',
                    modbus: { host: '127.0.0.1', port: address.port },
                },
            },
        )
        expect(connectionResponse.status()).toBe(200)
        const connection = await connectionResponse.json()
        const deviceResponse = await page.request.post(
            '/api/edge-admin/v1/devices',
            {
                headers,
                data: {
                    expectedVersion: connection.version,
                    key: 'preview-device',
                    name: '真实读取设备',
                    kind: 'modbus',
                    connectionId: connection.connection.id,
                    unitId: 1,
                },
            },
        )
        expect(deviceResponse.status()).toBe(200)
        const device = await deviceResponse.json()
        const inputUrl = `/api/edge-admin/v1/devices/${device.device.id}/inputs`
        const firstResponse = await page.request.post(inputUrl, {
            headers,
            data: {
                expectedVersion: device.version,
                key: 'fast',
                name: '快速读取',
                modbus: {
                    periodMs: 1000,
                    points: [
                        {
                            key: 'large',
                            functionCode: 3,
                            address: 0,
                            type: 'uint64',
                            byteOrder: 'ABCD',
                        },
                    ],
                },
            },
        })
        expect(firstResponse.status()).toBe(200)
        const first = await firstResponse.json()
        const secondResponse = await page.request.post(inputUrl, {
            headers,
            data: {
                expectedVersion: first.version,
                key: 'slow',
                name: '慢速读取',
                modbus: {
                    periodMs: 2000,
                    points: [
                        {
                            key: 'other',
                            functionCode: 3,
                            address: 4,
                            type: 'uint16',
                        },
                    ],
                },
            },
        })
        expect(secondResponse.status()).toBe(200)
        const before = await (await page.request.get(inputUrl)).json()
        await page.goto(`/devices/${device.device.id}/inputs`)
        const whole = page.getByRole('region', {
            name: '整台设备真实读取',
            exact: true,
        })
        await whole
            .getByRole('button', { name: '读取一次', exact: true })
            .click()
        await expect(whole.getByRole('status')).toContainText('2 / 2')
        const exact = 0x1000100110021003n.toString()
        await expect(
            whole.getByRole('table', { name: 'fast 读取结果' }),
        ).toContainText(exact)
        await expect(whole).toContainText('超过 JavaScript 安全整数范围')
        expect(requests).toBe(2)
        expect(connections).toBe(1)
        expect(await (await page.request.get(inputUrl)).json()).toEqual(before)
        await page.getByRole('button', { name: /快速读取 \(fast\)/ }).click()
        const group = page.getByRole('region', {
            name: '当前采集组真实读取',
            exact: true,
        })
        await group
            .getByRole('button', { name: '读取一次', exact: true })
            .click()
        await expect(group.getByRole('status')).toContainText('1 / 1')
        await expect(group).toContainText('1000 1001 1002 1003')
        const adopt = group.getByRole('button', {
            name: 'large 采用 CDAB 并保存当前组草稿',
            exact: true,
        })
        await page.getByLabel('采集周期（毫秒）', { exact: true }).fill('2000')
        await expect(adopt).toBeDisabled()
        await page.getByLabel('采集周期（毫秒）', { exact: true }).fill('1000')
        await expect(adopt).toBeEnabled()
        await page.screenshot({
            path: 'test-results/modbus-read-preview.png',
            fullPage: true,
        })
        await group
            .getByRole('button', {
                name: 'large 采用 CDAB 并保存当前组草稿',
                exact: true,
            })
            .click()
        await expect(group).toHaveCount(0)
        const after = await (await page.request.get(inputUrl)).json()
        expect(
            after.inputs.find((input: { key: string }) => input.key === 'fast')
                .modbus.points[0].byteOrder,
        ).toBe('CDAB')
        expect(
            after.inputs.find((input: { key: string }) => input.key === 'slow'),
        ).toEqual(
            before.inputs.find(
                (input: { key: string }) => input.key === 'slow',
            ),
        )
        expect(requests).toBe(3)
        expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
        expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
    } finally {
        try {
            await restore?.()
        } finally {
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve, reject) =>
                peer.close((error) => (error ? reject(error) : resolve())),
            )
        }
    }
})

test('HTTP live sample preserves exact numbers, requires explicit inference and rejects redacted or truncated samples', async ({
    page,
}) => {
    const requests: string[] = []
    let payload = '{"value":9223372036854775807,"extra":42}'
    const peer = createServer((request, response) => {
        requests.push(request.url ?? '')
        response.writeHead(200, { 'Content-Type': 'application/json' })
        response.end(payload)
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string')
        throw new Error('fixture-address')
    let restore: (() => Promise<void>) | undefined
    try {
        await openPointEditor(page)
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const policyUrl = '/api/edge-admin/v1/system/outbound-policy'
        const policy = await (await page.request.get(policyUrl)).json()
        const allowed = await page.request.put(policyUrl, {
            headers,
            data: {
                ...policy,
                expectedVersion: policy.version,
                version: undefined,
                localPorts: [...policy.localPorts, address.port],
            },
        })
        expect(allowed.status()).toBe(200)
        const allowedVersion = (await allowed.json()).version
        restore = async () => {
            expect(
                (
                    await page.request.put(policyUrl, {
                        headers,
                        data: {
                            expectedVersion: allowedVersion,
                            networks: policy.networks,
                            localPorts: policy.localPorts,
                        },
                    })
                ).status(),
            ).toBe(200)
        }
        const version = (
            await (
                await page.request.get('/api/edge-admin/v1/connections')
            ).json()
        ).version
        const connectionResponse = await page.request.post(
            '/api/edge-admin/v1/connections',
            {
                headers,
                data: {
                    expectedVersion: version,
                    key: 'http-sample',
                    name: 'HTTP 采样连接',
                    kind: 'http',
                    http: { baseAddress: `http://127.0.0.1:${address.port}/` },
                },
            },
        )
        expect(connectionResponse.status()).toBe(200)
        const connection = await connectionResponse.json()
        const deviceResponse = await page.request.post(
            '/api/edge-admin/v1/devices',
            {
                headers,
                data: {
                    expectedVersion: connection.version,
                    key: 'http-sample',
                    name: 'HTTP 采样设备',
                    kind: 'http',
                    connectionId: connection.connection.id,
                },
            },
        )
        expect(deviceResponse.status()).toBe(200)
        const device = await deviceResponse.json()
        const inputUrl = `/api/edge-admin/v1/devices/${device.device.id}/inputs`
        const inputResponse = await page.request.post(inputUrl, {
            headers,
            data: {
                expectedVersion: device.version,
                key: 'values',
                name: 'HTTP 采样来源',
                http: {
                    path: 'values',
                    periodMs: 5000,
                    fields: [{ key: 'value', path: '/value', type: 'integer' }],
                },
            },
        })
        expect(inputResponse.status()).toBe(200)
        const before = await (await page.request.get(inputUrl)).json()
        await page.goto(`/devices/${device.device.id}/inputs`)
        const whole = page.getByRole('region', {
            name: '整台 HTTP 设备实时采样',
            exact: true,
        })
        await whole
            .getByRole('button', { name: '采样一次', exact: true })
            .click()
        await expect(whole.getByRole('status')).toContainText('1 / 1')
        await expect(whole).toContainText('9223372036854775807')
        await expect(whole).toContainText('超过 JavaScript 安全整数范围')
        await page
            .getByRole('button', { name: /HTTP 采样来源 \(values\)/ })
            .click()
        const panel = page.getByRole('region', {
            name: '当前 HTTP 来源实时采样',
            exact: true,
        })
        await page
            .getByLabel('HTTP 路径与非秘密查询参数', { exact: true })
            .fill('unsaved')
        await panel
            .getByRole('button', { name: '采样一次', exact: true })
            .click()
        await expect(panel.getByRole('status')).toContainText('1 / 1')
        expect(requests).toEqual(['/values', '/unsaved'])
        expect(await (await page.request.get(inputUrl)).json()).toEqual(before)
        const use = panel.getByRole('button', {
            name: '用作字段推断样本',
            exact: true,
        })
        await page
            .getByLabel('HTTP 路径与非秘密查询参数', { exact: true })
            .fill('changed')
        await expect(use).toBeDisabled()
        await page
            .getByLabel('HTTP 路径与非秘密查询参数', { exact: true })
            .fill('unsaved')
        await use.click()
        await expect(
            page.getByRole('textbox', { name: '临时 JSON 样本', exact: true }),
        ).toHaveValue(payload)
        await page
            .getByRole('button', { name: '解析样本字段', exact: true })
            .click()
        await page.getByLabel('选择样本字段 /extra', { exact: true }).check()
        await page
            .getByRole('button', { name: '添加选中字段', exact: true })
            .click()
        expect(await (await page.request.get(inputUrl)).json()).toEqual(before)
        await page
            .getByRole('button', { name: '保存来源草稿', exact: true })
            .click()
        await expect(panel).toHaveCount(0)
        const after = await (await page.request.get(inputUrl)).json()
        expect(after.inputs[0].http.path).toBe('unsaved')
        expect(after.inputs[0].http.fields).toContainEqual(
            expect.objectContaining({ path: '/extra', type: 'number' }),
        )
        await page
            .getByRole('button', { name: /HTTP 采样来源 \(values\)/ })
            .click()
        for (const body of [
            '{"password":"fixture-private","value":42}',
            JSON.stringify({ value: '中'.repeat(40000) }),
        ]) {
            payload = body
            await panel
                .getByRole('button', { name: '采样一次', exact: true })
                .click()
            await expect(panel.getByRole('status')).toContainText('1 / 1')
            await expect(use).toBeDisabled()
            await expect(panel).toContainText('结果已脱敏或截断')
            await expect(panel).not.toContainText('fixture-private')
        }
        expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
        expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
    } finally {
        try {
            await restore?.()
        } finally {
            peer.closeAllConnections()
            await new Promise<void>((resolve, reject) =>
                peer.close((error) => (error ? reject(error) : resolve())),
            )
        }
    }
})

test('MQTT capture marks retained data, preserves exact values, adopts samples explicitly and cancels or times out cleanly', async ({
    page,
}) => {
    test.setTimeout(65000)
    const requests: string[] = []
    const sockets = new Set<Socket>()
    let payload: string | null = '{"value":9223372036854775807,"extra":42}'
    const send = (socket: Socket, header: number, body: Buffer) => {
        let length = body.length
        const prefix = [header]
        do {
            const digit = length % 128
            length = Math.floor(length / 128)
            prefix.push(digit | (length > 0 ? 128 : 0))
        } while (length > 0)
        socket.write(Buffer.concat([Buffer.from(prefix), body]))
    }
    const peer = createTcpServer((socket) => {
        sockets.add(socket)
        socket.on('close', () => sockets.delete(socket))
        let pending = Buffer.alloc(0)
        socket.on('data', (chunk) => {
            pending = Buffer.concat([pending, chunk])
            while (pending.length >= 2) {
                let count = 1,
                    length = 0,
                    multiplier = 1,
                    complete = false
                for (; count < Math.min(pending.length, 5); count++) {
                    const digit = pending[count]
                    length += (digit & 127) * multiplier
                    multiplier *= 128
                    if (!(digit & 128)) {
                        count++
                        complete = true
                        break
                    }
                }
                if (!complete || pending.length < count + length) return
                const kind = pending[0] >> 4
                const body = pending.subarray(count, count + length)
                pending = pending.subarray(count + length)
                if (kind === 1) {
                    expect(body[7] & 2).toBe(2)
                    const id = body
                        .subarray(12, 12 + body.readUInt16BE(10))
                        .toString()
                    expect(id).toMatch(/^te-sample-/)
                    socket.write(Buffer.from([0x20, 2, 0, 0]))
                } else if (kind === 8) {
                    const topic = body
                        .subarray(4, 4 + body.readUInt16BE(2))
                        .toString()
                    requests.push(topic)
                    socket.write(Buffer.from([0x90, 3, body[0], body[1], 0]))
                    if (payload != null) {
                        const topicBytes = Buffer.from(topic)
                        const prefix = Buffer.alloc(2)
                        prefix.writeUInt16BE(topicBytes.length)
                        send(
                            socket,
                            0x31,
                            Buffer.concat([
                                prefix,
                                topicBytes,
                                Buffer.from(payload),
                            ]),
                        )
                    }
                } else if (kind === 12) socket.write(Buffer.from([0xd0, 0]))
            }
        })
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string')
        throw new Error('fixture-address')
    let restore: (() => Promise<void>) | undefined
    try {
        await openPointEditor(page)
        const session = await (
            await page.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const policyUrl = '/api/edge-admin/v1/system/outbound-policy'
        const policy = await (await page.request.get(policyUrl)).json()
        const allowed = await page.request.put(policyUrl, {
            headers,
            data: {
                ...policy,
                expectedVersion: policy.version,
                version: undefined,
                localPorts: [...policy.localPorts, address.port],
            },
        })
        expect(allowed.status()).toBe(200)
        const allowedVersion = (await allowed.json()).version
        restore = async () => {
            expect(
                (
                    await page.request.put(policyUrl, {
                        headers,
                        data: {
                            expectedVersion: allowedVersion,
                            networks: policy.networks,
                            localPorts: policy.localPorts,
                        },
                    })
                ).status(),
            ).toBe(200)
        }
        const version = (
            await (
                await page.request.get('/api/edge-admin/v1/connections')
            ).json()
        ).version
        const connectionResponse = await page.request.post(
            '/api/edge-admin/v1/connections',
            {
                headers,
                data: {
                    expectedVersion: version,
                    key: 'mqtt-sample',
                    name: 'MQTT 抓取连接',
                    kind: 'mqtt',
                    mqtt: {
                        host: '127.0.0.1',
                        port: address.port,
                        clientId: 'must-not-use-production-id',
                    },
                },
            },
        )
        expect(connectionResponse.status()).toBe(200)
        const connection = await connectionResponse.json()
        const deviceResponse = await page.request.post(
            '/api/edge-admin/v1/devices',
            {
                headers,
                data: {
                    expectedVersion: connection.version,
                    key: 'mqtt-sample',
                    name: 'MQTT 抓取设备',
                    kind: 'mqtt',
                    connectionId: connection.connection.id,
                },
            },
        )
        expect(deviceResponse.status()).toBe(200)
        const device = await deviceResponse.json()
        const inputUrl = `/api/edge-admin/v1/devices/${device.device.id}/inputs`
        const inputResponse = await page.request.post(inputUrl, {
            headers,
            data: {
                expectedVersion: device.version,
                key: 'values',
                name: 'MQTT 抓取来源',
                mqtt: {
                    topicFilter: 'values',
                    qos: 0,
                    fields: [{ key: 'value', path: '/value', type: 'integer' }],
                },
            },
        })
        expect(inputResponse.status()).toBe(200)
        const before = await (await page.request.get(inputUrl)).json()
        await page.goto(`/devices/${device.device.id}/inputs`)
        await page
            .getByRole('button', { name: /MQTT 抓取来源 \(values\)/ })
            .click()
        const panel = page.getByRole('region', {
            name: '当前 MQTT 来源实时抓取',
            exact: true,
        })
        await page.getByLabel('MQTT Topic', { exact: true }).fill('unsaved')
        await panel
            .getByRole('button', { name: '抓取一条消息', exact: true })
            .click()
        await expect(panel.getByRole('status')).toContainText('success')
        expect(requests).toEqual(['unsaved'])
        await expect(panel).toContainText('保留消息：')
        await expect(panel).toContainText('9223372036854775807')
        await expect(panel).toContainText('超过 JavaScript 安全整数范围')
        expect(await (await page.request.get(inputUrl)).json()).toEqual(before)
        const use = panel.getByRole('button', {
            name: '用作字段推断样本',
            exact: true,
        })
        await page.getByLabel('MQTT Topic', { exact: true }).fill('changed')
        await expect(use).toBeDisabled()
        await page.getByLabel('MQTT Topic', { exact: true }).fill('unsaved')
        await use.click()
        await expect(
            page.getByRole('textbox', { name: '临时 JSON 样本', exact: true }),
        ).toHaveValue(payload)
        await page
            .getByRole('button', { name: '解析样本字段', exact: true })
            .click()
        await page.getByLabel('选择样本字段 /extra', { exact: true }).check()
        await page
            .getByRole('button', { name: '添加选中字段', exact: true })
            .click()
        expect(await (await page.request.get(inputUrl)).json()).toEqual(before)
        await page
            .getByRole('button', { name: '保存来源草稿', exact: true })
            .click()
        await expect(panel).toHaveCount(0)
        const after = await (await page.request.get(inputUrl)).json()
        expect(after.inputs[0].mqtt.topicFilter).toBe('unsaved')
        expect(after.inputs[0].mqtt.fields).toContainEqual(
            expect.objectContaining({ path: '/extra', type: 'number' }),
        )
        await page
            .getByRole('button', { name: /MQTT 抓取来源 \(values\)/ })
            .click()
        for (const body of [
            '{"password":"fixture-private","value":42}',
            JSON.stringify({ value: '中'.repeat(40000) }),
        ]) {
            payload = body
            await panel
                .getByRole('button', { name: '抓取一条消息', exact: true })
                .click()
            await expect(panel.getByRole('status')).toBeVisible()
            await expect(use).toBeDisabled()
            await expect(panel).toContainText('结果已脱敏或截断')
            await expect(panel).not.toContainText('fixture-private')
        }
        payload = null
        await panel
            .getByRole('button', { name: '抓取一条消息', exact: true })
            .click()
        await expect.poll(() => sockets.size).toBe(1)
        await panel
            .getByRole('button', { name: '取消抓取', exact: true })
            .click()
        await expect.poll(() => sockets.size).toBe(0)
        await panel
            .getByRole('button', { name: '抓取一条消息', exact: true })
            .click()
        await expect(panel.getByRole('status')).toContainText('timeout', {
            timeout: 35000,
        })
        await expect(panel).toContainText('采样超时：整次最多 30 秒')
        await expect.poll(() => sockets.size).toBe(0)
        expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
        expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
    } finally {
        try {
            await restore?.()
        } finally {
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve, reject) =>
                peer.close((error) => (error ? reject(error) : resolve())),
            )
        }
    }
})

test('route drafts persist exact configuration, preserve conflicted edits and never apply', async ({
    page,
}) => {
    await page.setViewportSize({ width: 1700, height: 1100 })
    await openPointEditor(page)
    const runtimeBefore = await (
        await page.request.get('/api/edge-admin/v1/runtime/status')
    ).json()
    await page.getByRole('link', { name: '路线草稿', exact: true }).click()
    await page.getByRole('button', { name: '新建路线', exact: true }).click()
    await page
        .getByLabel('路线标识', { exact: true })
        .fill('browser-route-exact')
    await page.getByLabel('路线名称', { exact: true }).fill('浏览器精度路线')
    await page.getByRole('button', { name: 'HTTP 输出', exact: true }).click()
    const config =
        '{"connectionId":null,"payload":{"mode":"custom","fields":[{"target":"large","constant":9223372036854775807},{"target":"decimal","constant":0.1234567890123456789012345678}]}}'
    const configInput = page.getByLabel('节点配置 JSON（临时编辑入口）', {
        exact: true,
    })
    await configInput.fill(config)
    await expect(
        page.getByRole('button', { name: '保存路线草稿', exact: true }),
    ).toBeDisabled()
    await page
        .getByRole('button', { name: '确认节点配置', exact: true })
        .click()
    const created = page.waitForResponse(
        (response) =>
            response.url().endsWith('/api/edge-admin/v1/routes') &&
            response.request().method() === 'POST',
    )
    await page
        .getByRole('button', { name: '保存路线草稿', exact: true })
        .click()
    const response = await created
    expect(response.status()).toBe(200)
    const raw = await response.text()
    expect(raw).toContain('9223372036854775807')
    expect(raw).toContain('0.1234567890123456789012345678')
    let saved = JSON.parse(raw)
    await expect(
        page.getByText('路线草稿已保存，未应用。', { exact: true }),
    ).toBeVisible()
    await page.reload()
    await page
        .getByRole('button', { name: '打开 浏览器精度路线', exact: true })
        .click()
    await page.locator('.react-flow__node').first().click()
    await expect(configInput).toHaveValue(/"constant":9223372036854775807/)
    await expect(configInput).toHaveValue(
        /"constant":0\.1234567890123456789012345678/,
    )
    const normalizedConfig = await configInput.inputValue()
    // An invalid unconfirmed edit cannot accidentally save an older configuration.
    await configInput.fill('{')
    await page
        .getByRole('button', { name: '确认节点配置', exact: true })
        .click()
    await expect(
        page.getByRole('button', { name: '保存路线草稿', exact: true }),
    ).toBeDisabled()
    await expect(configInput).toHaveValue('{')
    await page
        .getByRole('button', { name: '放弃节点配置修改', exact: true })
        .click()
    await expect(configInput).toHaveValue(normalizedConfig)
    await page
        .getByLabel('路线说明', { exact: true })
        .fill('read/write precision')
    const updated = page.waitForResponse(
        (response) =>
            response.request().method() === 'PUT' &&
            response.url().endsWith(`/routes/${saved.route.id}`),
    )
    await page
        .getByRole('button', { name: '保存路线草稿', exact: true })
        .click()
    const updateResponse = await updated
    expect(updateResponse.status()).toBe(200)
    expect(updateResponse.request().postData()).toContain(
        '"constant":9223372036854775807',
    )
    expect(updateResponse.request().postData()).toContain(
        '"constant":0.1234567890123456789012345678',
    )
    saved = JSON.parse(await updateResponse.text())
    await expect(
        page.getByText('路线草稿已保存，未应用。', { exact: true }),
    ).toBeVisible()
    // A second writer changes the global version. No UI overwrite/retry is allowed.
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const external = await page.request.post(
        `/api/edge-admin/v1/routes/${saved.route.id}/copy`,
        {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: {
                expectedVersion: saved.version,
                key: 'browser-route-external',
                name: '外部副本',
            },
        },
    )
    expect(external.status()).toBe(200)
    await page.getByLabel('路线名称', { exact: true }).fill('保留冲突编辑')
    const conflict = page.waitForResponse(
        (response) =>
            response.request().method() === 'PUT' &&
            response.url().endsWith(`/routes/${saved.route.id}`),
    )
    await page
        .getByRole('button', { name: '保存路线草稿', exact: true })
        .click()
    expect((await conflict).status()).toBe(409)
    await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
        '保留冲突编辑',
    )
    await page.route('**/api/edge-admin/v1/routes', (route) =>
        route.fulfill({
            status: 500,
            json: { title: 'fixture query failure' },
        }),
    )
    await page.getByRole('button', { name: '重新查询', exact: true }).click()
    await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
        '保留冲突编辑',
    )
    await page.unroute('**/api/edge-admin/v1/routes')
    await page.getByRole('button', { name: '重新查询', exact: true }).click()
    page.once('dialog', (dialog) => dialog.accept())
    await page
        .getByRole('button', { name: '打开 浏览器精度路线', exact: true })
        .click()
    await expect(page.getByLabel('路线名称', { exact: true })).toHaveValue(
        '浏览器精度路线',
    )
    await page
        .getByLabel('副本标识', { exact: true })
        .fill('browser-route-copy')
    await page.getByLabel('副本名称', { exact: true }).fill('页面副本')
    await page
        .getByRole('button', { name: '复制已保存路线', exact: true })
        .click()
    await expect(
        page.getByText('已创建停用的独立副本，未应用。', { exact: true }),
    ).toBeVisible()
    await expect(
        page.getByLabel('参与静态编译（草稿启用）', { exact: true }),
    ).not.toBeChecked()
    await page
        .getByRole('button', { name: '静态编译已保存的启用路线', exact: true })
        .click()
    await expect(page.getByText(/静态编译通过：版本/)).toBeVisible()
    page.once('dialog', (dialog) => dialog.accept())
    await page
        .getByRole('button', { name: '删除路线草稿', exact: true })
        .click()
    await expect(
        page.getByText('路线草稿已删除；旧运行配置及投递未撤回。', {
            exact: true,
        }),
    ).toBeVisible()
    const runtimeAfter = await (
        await page.request.get('/api/edge-admin/v1/runtime/status')
    ).json()
    expect(runtimeAfter.coordinator.activeRevision).toBe(
        runtimeBefore.coordinator.activeRevision,
    )
})
