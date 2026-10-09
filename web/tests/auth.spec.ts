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
