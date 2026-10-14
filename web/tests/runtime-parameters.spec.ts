import { test, expect, type Page, type Locator } from '@playwright/test'
import { createServer, type ServerResponse } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const endpoint = root + '/system/runtime-parameters'
const password = 'Runtime-parameters-7831!'
function panel(page: Page) {
    return page.getByRole('region', {
        name: '运行参数：资源保护与审计',
        exact: true,
    })
}
async function setup(page: Page) {
    await page.goto('/system')
    await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        panel(page).getByRole('button', { name: '编辑运行参数', exact: true }),
    ).toBeEnabled()
}
async function edit(
    view: Locator,
    days: string,
    global: string,
    session: string,
) {
    await view
        .getByRole('button', { name: '编辑运行参数', exact: true })
        .click()
    await view.getByLabel('审计保留期（天）', { exact: true }).fill(days)
    await view.getByLabel('预览全局并发上限', { exact: true }).fill(global)
    await view.getByLabel('每个会话预览并发上限', { exact: true }).fill(session)
}
async function save(view: Locator) {
    await view.getByRole('checkbox').check()
    await view
        .getByRole('button', { name: '确认保存运行参数', exact: true })
        .click()
}
async function saved(view: Locator) {
    await expect(
        view.getByText('运行参数已保存；变更已记审计，无变化不重复写入。', {
            exact: true,
        }),
    ).toBeVisible()
    await expect(
        view.getByRole('button', { name: '编辑运行参数', exact: true }),
    ).toBeEnabled()
}

test('runtime parameter confirmation changes real preview admission, persists across Host restart and respects read-only roles', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    let held: ServerResponse | undefined
    let received!: () => void
    const arrived = new Promise<void>((resolve) => {
        received = resolve
    })
    const server = createServer((_request, response) => {
        held = response
        received()
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('listener')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-parameters-browser-'))
    let host = await startOwnedHost(data, 0, [
        `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
    ])
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        let view = panel(page)
        const originalDraft = await (
            await page.request.get(root + '/devices')
        ).json()
        await edit(view, '365', '1', '1')
        await view.getByLabel('每个输入通道条数上限', { exact: true }).fill('7')
        await view
            .getByLabel('全局在途字节预算', { exact: true })
            .fill('33554432')
        await view
            .getByLabel('调试缓存每对象字节上限', { exact: true })
            .fill('67584')
        await view
            .getByLabel('调试缓存全局字节上限', { exact: true })
            .fill('135168')
        await view
            .getByLabel('调试缓存每对象条数上限', { exact: true })
            .fill('2')
        await expect(
            view.getByRole('button', { name: '确认保存运行参数', exact: true }),
        ).toBeDisabled()
        await expect(
            view.getByText(/调高资源上限前必须在本机实测/),
        ).toBeVisible()
        await save(view)
        await saved(view)
        await expect(
            view.locator('[data-parameter="inFlightBytes"]'),
        ).toHaveText('33554432')
        await expect(
            view.locator('[data-parameter="debugObjectBytes"]'),
        ).toHaveText('67584')
        await expect(
            view.locator('[data-parameter="debugGlobalBytes"]'),
        ).toHaveText('135168')
        await expect(
            view.locator('[data-parameter="debugObjectRecords"]'),
        ).toHaveText('2')
        await expect(
            view.locator('[data-parameter="inputChannelCapacity"]'),
        ).toHaveText('7')
        const runtimeBudget = await (
            await page.request.get(root + '/runtime/status')
        ).json()
        expect(runtimeBudget.inFlight.capacityBytes).toBe(33554432)
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const pending = page.request.post(root + '/connections/test/http', {
            headers,
            data: {
                connection: {
                    baseAddress: `http://127.0.0.1:${address.port}/`,
                    timeoutMs: 30000,
                },
            },
        })
        await arrived
        const busy = await page.request.post(
            root + '/connections/inspect-outbound',
            { headers, data: { host: '203.0.113.1', port: 443 } },
        )
        expect(busy.status()).toBe(429)
        // A real in-progress HTTP diagnostic still owns the lease; saving a higher
        // limit does not replace the budget or cancel that request.
        await edit(view, '730', '6', '3')
        await save(view)
        await saved(view)
        expect(held?.writableEnded).toBe(false)
        const admitted = await page.request.post(
            root + '/connections/inspect-outbound',
            { headers, data: { host: '203.0.113.1', port: 443 } },
        )
        expect(admitted.status()).toBe(200)
        held!.end('ok')
        expect((await pending).status()).toBe(200)
        const current = await (await page.request.get(endpoint)).json()
        // Native Chromium fetch, not APIRequestContext, exercises Kestrel's body limit.
        const boundary = await page.evaluate(
            async ({ endpoint, current, csrf }) => {
                const body = JSON.stringify({
                    values: current.values,
                    expectedVersion: current.version,
                    confirmed: true,
                })
                const results = []
                for (const size of [4096, 4097]) {
                    const response = await fetch(endpoint, {
                        method: 'PUT',
                        headers: {
                            'Content-Type': 'application/json',
                            'X-CSRF-Token': csrf,
                        },
                        body: body.padEnd(size, ' '),
                    })
                    results.push({
                        status: response.status,
                        body: await response.json(),
                    })
                }
                return results
            },
            { endpoint, current, csrf: session.csrfToken },
        )
        expect(boundary.map((r) => r.status)).toEqual([200, 413])
        expect(boundary[1].body.status).toBe(413)
        expect((await (await page.request.get(endpoint)).json()).version).toBe(
            current.version,
        )
        expect(
            await (await page.request.get(root + '/devices')).json(),
        ).toEqual(originalDraft)
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .coordinator.activeRevision,
        ).toBeNull()
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system')
        await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        view = panel(page)
        await expect(
            view.locator('[data-parameter="auditRetentionDays"]'),
        ).toHaveText('730')
        await expect(
            view.locator('[data-parameter="previewGlobalLimit"]'),
        ).toHaveText('6')
        await expect(
            view.locator('[data-parameter="inputChannelCapacity"]'),
        ).toHaveText('7')
        expect(await (await page.request.get(endpoint)).json()).toEqual(current)
        const audit = await (
            await page.request.get(
                root + '/audit?action=system.runtimeParametersChanged',
            )
        ).json()
        expect(audit.items).toHaveLength(2)
        expect(
            JSON.parse(audit.items[0].detail).current.Values.AuditRetentionDays,
        ).toBe(730)
        const own = await (
            await page.request.get(root + '/auth/session')
        ).json()
        for (const role of ['viewer', 'configurator']) {
            expect(
                (
                    await page.request.post(root + '/users', {
                        headers: { 'X-CSRF-Token': own.csrfToken },
                        data: {
                            accountName: role,
                            role,
                            temporaryPassword: password,
                            isEnabled: true,
                        },
                    })
                ).status(),
            ).toBe(200)
            const other = await browser.newContext({ baseURL: host.url })
            try {
                const user = await other.newPage()
                await user.goto('/system')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user.getByLabel('密码', { exact: true }).fill(password)
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user.getByLabel('原密码', { exact: true }).fill(password)
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Changed-parameters-5613!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    panel(user).locator(
                        '[data-parameter="auditRetentionDays"]',
                    ),
                ).toHaveText('730')
                await expect(
                    panel(user).getByRole('button', {
                        name: '编辑运行参数',
                        exact: true,
                    }),
                ).toHaveCount(0)
                const auth = await (
                    await user.request.get(root + '/auth/session')
                ).json()
                expect(
                    (
                        await user.request.put(endpoint, {
                            headers: { 'X-CSRF-Token': auth.csrfToken },
                            data: {
                                values: current.values,
                                expectedVersion: current.version,
                                confirmed: true,
                            },
                        })
                    ).status(),
                ).toBe(403)
            } finally {
                await other.close()
            }
        }
    } finally {
        held?.end()
        server.closeAllConnections()
        await new Promise<void>((resolve) => server.close(() => resolve()))
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('parameter editor retains validation input, freezes version and never replays conflicts or lost replies', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-parameters-errors-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        const view = panel(page)
        let writes = 0
        page.on('request', (r) => {
            if (r.method() === 'PUT' && r.url().endsWith('/runtime-parameters'))
                writes++
        })
        await edit(view, '179', '4', '2')
        await save(view)
        await expect(view.getByText(/请填写范围内的整数/)).toBeVisible()
        expect(writes).toBe(0)
        await expect(
            view.getByLabel('审计保留期（天）', { exact: true }),
        ).toHaveValue('179')
        await view.getByLabel('审计保留期（天）', { exact: true }).fill('365')
        page.once('dialog', (dialog) => dialog.dismiss())
        await page.getByRole('link', { name: '总览', exact: true }).click()
        await expect(page).toHaveURL(/\/system$/)
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const before = await (await page.request.get(endpoint)).json()
        expect(
            (
                await page.request.put(endpoint, {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: {
                        values: { ...before.values, auditRetentionDays: 730 },
                        expectedVersion: null,
                        confirmed: true,
                    },
                })
            ).status(),
        ).toBe(200)
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(1)
        await expect(view.getByRole('form')).toHaveCount(0)
        await expect(
            view.getByRole('button', { name: '编辑运行参数', exact: true }),
        ).toBeDisabled()
        await view
            .getByRole('button', { name: '重新读取运行参数', exact: true })
            .click()
        await edit(view, '1000', '8', '4')
        await page.route('**/system/runtime-parameters', async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await save(view)
        await expect(view.getByText(/请求失败或结果尚未确认/)).toBeVisible()
        expect(writes).toBe(2)
        await page.unroute('**/system/runtime-parameters')
        await page.reload()
        await expect(
            panel(page).locator('[data-parameter="auditRetentionDays"]'),
        ).toHaveText('1000')
        expect(writes).toBe(2)
        const audits = await (
            await page.request.get(
                root + '/audit?action=system.runtimeParametersChanged',
            )
        ).json()
        expect(audits.items).toHaveLength(2)
        await page.route('**/system/runtime-parameters', (route) =>
            route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503,"detail":"fixture"}',
            }),
        )
        await panel(page)
            .getByRole('button', { name: '重新读取运行参数', exact: true })
            .click()
        await expect(panel(page).getByText(/参数读取失败/)).toBeVisible()
        await expect(panel(page).locator('[data-parameter]')).toHaveCount(0)
        await expect(
            panel(page).getByRole('button', {
                name: '编辑运行参数',
                exact: true,
            }),
        ).toBeDisabled()
        expect(writes).toBe(2)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('plan scale limits affect new admission without stopping live inputs or invalidating the effective snapshot on restart', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    let received = 0
    const server = createServer((request, response) => {
        request.resume()
        request.on('end', () => {
            received++
            response.writeHead(204).end()
        })
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('listener')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-plan-limits-browser-'))
    const args = [`--TenonEdge:Outbound:LocalPorts:0=${address.port}`]
    let host = await startOwnedHost(data, 0, args)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        // API fixture only: this test exercises the system form and real runtime,
        // not the separate T3-11 all-UI configuration acceptance.
        async function post(path: string, data: unknown) {
            const response = await page.request.post(root + path, {
                headers,
                data,
            })
            expect(response.status()).toBe(200)
            return response.json()
        }
        let version = '0'
        const sourceIds: number[] = []
        for (let i = 0; i < 2; i++) {
            const device = await post('/devices', {
                expectedVersion: version,
                key: `meter${i}`,
                name: `Meter ${i}`,
                kind: 'simulator',
            })
            const input = await post(`/devices/${device.device.id}/inputs`, {
                expectedVersion: device.version,
                key: 'sample',
                name: 'Sample',
                simulator: { periodMs: 1000, sampleJson: '{"value":42}' },
            })
            version = input.version
            sourceIds.push(input.input.id)
        }
        const connection = await post('/connections', {
            expectedVersion: version,
            key: 'http',
            name: 'HTTP',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${address.port}/` },
        })
        const route = await post('/routes', {
            expectedVersion: connection.version,
            key: 'route',
            name: 'Route',
            enabled: true,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'in',
                        type: 'input.simulator',
                        version: 1,
                        name: 'Input',
                        config: { sourceIds },
                    },
                    {
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: { connectionId: connection.connection.id },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: { nodes: {} },
            },
        })
        version = route.version
        const review = await post('/configuration/review', {
            expectedVersion: version,
        })
        expect(review.preflightPassed).toBe(true)
        const apply = await page.request.post(root + '/configuration/apply', {
            headers,
            data: {
                expectedVersion: version,
                expectedEffectiveRevision: review.effectiveRevision,
                expectedPlanHash: review.planHash,
                confirmed: true,
            },
        })
        expect(apply.status()).toBe(202)
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(root + '/runtime/status')
                        ).json()
                    ).runtime.activeRevision,
            )
            .toBe(1)
        await expect.poll(() => received).toBeGreaterThanOrEqual(2)
        const original = await (
            await page.request.get(root + '/configuration/versions/1')
        ).json()
        let view = panel(page)
        await edit(view, '365', '4', '2')
        for (const label of [
            '新计划数据来源上限',
            '新计划连接上限',
            '新计划 Modbus 连接上限',
            '新计划每来源路径上限',
            '新计划总路径上限',
        ]) {
            await view.getByLabel(label, { exact: true }).fill('1')
        }
        await expect(view.getByText(/降低不停止现有计划/)).toBeVisible()
        await save(view)
        await saved(view)
        await expect(view.locator('[data-parameter="planSources"]')).toHaveText(
            '1',
        )
        const blocked = await post('/routes/compile', {
            expectedVersion: version,
        })
        expect(blocked.compiled).toBe(false)
        expect(blocked.issues[0].code).toBe('sources.totalLimit')
        expect(
            (await post('/configuration/review', { expectedVersion: version }))
                .preflightPassed,
        ).toBe(false)
        expect(
            await (
                await page.request.get(root + '/configuration/versions/1')
            ).json(),
        ).toEqual(original)
        const current = received
        await context.close()
        await expect.poll(() => received).toBeGreaterThan(current)
        expect(await host.stop()).toBe(0)
        const stopped = received
        host = await startOwnedHost(data, 0, args)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system')
        await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        view = panel(page)
        await expect(view.locator('[data-parameter="planSources"]')).toHaveText(
            '1',
        )
        await expect.poll(() => received).toBeGreaterThan(stopped)
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .runtime.activeRevision,
        ).toBe(1)
        expect(
            await (
                await page.request.get(root + '/configuration/versions/')
            ).json(),
        ).toHaveLength(1)
        await edit(view, '365', '4', '2')
        await view.getByLabel('新计划数据来源上限', { exact: true }).fill('2')
        await view.getByLabel('新计划总路径上限', { exact: true }).fill('2')
        await save(view)
        await saved(view)
        const own = await (
            await page.request.get(root + '/auth/session')
        ).json()
        headers['X-CSRF-Token'] = own.csrfToken
        expect(
            (await post('/routes/compile', { expectedVersion: version }))
                .compiled,
        ).toBe(true)
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .runtime.activeRevision,
        ).toBe(1)
    } finally {
        await context.close()
        await host.stop()
        server.closeAllConnections()
        await new Promise<void>((resolve) => server.close(() => resolve()))
        await rm(data, { recursive: true, force: true })
    }
})
