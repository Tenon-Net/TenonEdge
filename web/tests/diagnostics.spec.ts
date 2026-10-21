import { test, expect, type BrowserContext } from '@playwright/test'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

test('real diagnostics show runtime, trends and persistent events; query errors preserve filters and navigation stops polling', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-diagnostics-'))
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    try {
        const seed = join(data, 'seed.json')
        // Seeded software input: not a no-JSON configuration or physical-device acceptance.
        await writeFile(
            seed,
            JSON.stringify({
                schemaVersion: 1,
                units: [
                    {
                        kind: 'input.simulator',
                        key: 'diagnostic-meter/sample',
                        configuration: { periodMs: 1000, seed: 42 },
                    },
                ],
            }),
        )
        const args = [`--TenonEdge:Runtime:SeedSnapshot=${seed}`]
        host = await startOwnedHost(data, 0, args)
        const url = host.url
        console.log('DIAGNOSTICS OWNED HOST', url, data)
        context = await browser.newContext({ baseURL: url })
        context.setDefaultTimeout(15_000)
        const page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('diagnostic-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Diagnostic-test-7419')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Diagnostic-test-7419')
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        await expect(
            page.getByRole('region', { name: '运行状态', exact: true }),
        ).toContainText('r1')
        await expect(
            page.getByRole('region', { name: '开始使用' }),
        ).toHaveCount(0)
        const requests: string[] = []
        page.on('request', (request) =>
            requests.push(
                request.method() + ' ' + new URL(request.url()).pathname,
            ),
        )
        const durable = await (
            await page.request.get('/api/edge-admin/v1/runtime/status')
        ).json()
        const historyVersions = await (
            await page.request.get('/api/edge-admin/v1/configuration/versions')
        ).json()
        expect(durable.configuration.effectiveRevision).toBe(1)
        expect(durable.configuration.appliedBy).toBe('系统')
        expect(Date.parse(durable.configuration.appliedAt)).toBe(
            historyVersions[0].activatedAt,
        )

        const state = page.getByRole('region', {
            name: '运行状态',
            exact: true,
        })
        await state.getByText('配置与网关信息', { exact: true }).click()
        await expect(state.getByText('系统', { exact: true })).toBeVisible()
        await expect(
            state.getByText('持久有效版本', { exact: true }),
        ).toBeVisible()
        await expect(
            page.getByRole('img', { name: '最近一小时趋势' }),
        ).toBeVisible({ timeout: 25_000 })
        const overviewEvents = page.getByRole('region', {
            name: '最近异常',
            exact: true,
        })
        await expect(overviewEvents).toContainText('setup.windowOpen')
        await page.route('**/runtime/status', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await state.getByRole('button', { name: '刷新看板' }).click()
        await expect(state.getByRole('alert')).toContainText('请求失败')
        await expect(state).toContainText('当前运行状态与有效版本未知')
        await expect(state.getByText('系统', { exact: true })).toHaveCount(0)
        await expect(overviewEvents).toContainText('setup.windowOpen')
        await expect(
            page.getByRole('img', { name: '最近一小时趋势' }),
        ).toBeVisible()
        await page.unroute('**/runtime/status')
        await state.getByRole('button', { name: '刷新看板' }).click()
        await expect(state.getByRole('alert')).toHaveCount(0)
        await expect(state.getByText('系统', { exact: true })).toBeVisible()
        expect(
            requests.filter((value) =>
                /\/configuration\/(versions|runtime)/.test(value),
            ),
        ).toEqual([])
        await page.screenshot({
            path: 'test-results/overview.png',
            fullPage: true,
        })
        await page
            .getByRole('navigation')
            .getByRole('link', { name: '运行诊断' })
            .click()
        await expect(
            page.getByRole('region', { name: '运行单元', exact: true }),
        ).toContainText('input.simulator/diagnostic-meter/sample')
        await expect(
            page.getByRole('img', { name: '最近一小时趋势' }),
        ).toBeVisible({ timeout: 25_000 })
        await page.getByText('查看精确采样数据', { exact: true }).click()
        const points = await (
            await page.request.get('/api/edge-admin/v1/runtime/trends')
        ).json()
        expect(points.length).toBeGreaterThan(0)
        expect(
            points.some((point: { received: number }) => point.received > 0),
        ).toBe(true)
        const events = page.getByRole('region', {
            name: '运行事件',
            exact: true,
        })
        const storedEvents = await (
            await page.request.get('/api/edge-admin/v1/runtime/events?limit=50')
        ).json()
        expect(storedEvents.items.length).toBeGreaterThan(0)
        const retainedEvent = storedEvents.items[0]
        await events
            .getByLabel('类型代码', { exact: true })
            .fill(retainedEvent.type)
        await events.getByRole('button', { name: '查询事件' }).click()
        await expect(
            events
                .getByRole('cell', { name: retainedEvent.type, exact: true })
                .first(),
        ).toBeVisible()
        await events
            .getByLabel('类型代码', { exact: true })
            .fill('unsubmitted-filter')
        await page.route('**/runtime/events?**', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await events.getByRole('button', { name: '刷新本区域' }).click()
        await expect(events.getByRole('alert')).toContainText('请求失败')
        await expect(
            events.getByLabel('类型代码', { exact: true }),
        ).toHaveValue('unsubmitted-filter')
        await expect(
            events
                .getByRole('cell', { name: retainedEvent.type, exact: true })
                .first(),
        ).toBeVisible()
        await expect(
            page.getByRole('heading', { name: '运行诊断', exact: true }),
        ).toBeVisible()
        await page.unroute('**/runtime/events?**')
        await events.getByRole('button', { name: '刷新本区域' }).click()
        await expect(events.getByRole('alert')).toHaveCount(0)
        await page.screenshot({
            path: 'test-results/diagnostics.png',
            fullPage: true,
        })
        await page.getByRole('link', { name: '账号安全' }).click()
        await expect(
            page.getByRole('heading', { name: '修改密码', exact: true }),
        ).toBeVisible()
        const reads = () =>
            requests.filter((value) =>
                /\/runtime\/(status|trends|events)$/.test(value),
            ).length
        const count = reads()
        await page.waitForTimeout(10_500)
        expect(reads()).toBe(count)
        expect(
            requests.filter((value) => /^(POST|PUT|DELETE) /.test(value)),
        ).toEqual([])
        await context.close()
        context = undefined
        expect(await host.stop()).toBe(0)
        host = undefined
        // Change fixture role only with its owned Host stopped; no product authorization bypass API.
        const database = new DatabaseSync(join(data, 'config.db'))
        try {
            database.exec(
                "UPDATE user_account SET role = 'Viewer' WHERE account_name = 'diagnostic-admin'",
            )
            // Persisted paging fixture, not claimed as 55 real runtime failures.
            const now = Date.now()
            for (let index = 1; index <= 55; index++)
                database
                    .prepare(
                        `
                INSERT INTO runtime_event(id, first_occurred_at, last_occurred_at, level, type, target, message, occurrence_count)
                SELECT MAX(id) + 1, ?, ?, 'warning', 'diagnostic.pagination', 'paging-fixture', ?, 2 FROM runtime_event
            `,
                    )
                    .run(now, now, `pagination-row-${index}`)
        } finally {
            database.close()
        }
        host = await startOwnedHost(data, Number(new URL(url).port), args)
        context = await browser.newContext({ baseURL: url })
        context.setDefaultTimeout(15_000)
        const viewer = await context.newPage()
        await viewer.goto('/diagnostics')
        await viewer
            .getByLabel('账号', { exact: true })
            .fill('diagnostic-admin')
        await viewer
            .getByLabel('密码', { exact: true })
            .fill('Diagnostic-test-7419')
        await viewer.getByRole('button', { name: '登录', exact: true }).click()
        await expect(
            viewer.getByRole('heading', { name: '运行诊断', exact: true }),
        ).toBeVisible()
        const session = await (
            await viewer.request.get('/api/edge-admin/v1/auth/session')
        ).json()
        expect(session.role).toBe('viewer')
        expect(
            await (
                await viewer.request.get('/api/edge-admin/v1/runtime/trends')
            ).json(),
        ).toEqual([])
        const history = await (
            await viewer.request.get(
                '/api/edge-admin/v1/runtime/events?limit=50&type=' +
                    encodeURIComponent(retainedEvent.type),
            )
        ).json()
        expect(
            history.items.some(
                (item: { id: number }) =>
                    String(item.id) === String(retainedEvent.id),
            ),
        ).toBe(true)
        const viewerEvents = viewer.getByRole('region', {
            name: '运行事件',
            exact: true,
        })
        await viewerEvents
            .getByLabel('类型代码', { exact: true })
            .fill('diagnostic.pagination')
        await viewerEvents
            .getByLabel('对象', { exact: true })
            .fill('paging-fixture')
        await viewerEvents
            .getByLabel('级别', { exact: true })
            .selectOption('warning')
        await viewerEvents.getByRole('button', { name: '查询事件' }).click()
        await expect(
            viewerEvents.getByRole('cell', { name: /^pagination-row-/ }),
        ).toHaveCount(50)
        await expect(
            viewerEvents.getByRole('cell', {
                name: 'pagination-row-55',
                exact: true,
            }),
        ).toBeVisible()
        await viewerEvents.getByRole('button', { name: '下一页' }).click()
        await expect(
            viewerEvents.getByRole('cell', { name: /^pagination-row-/ }),
        ).toHaveCount(5)
        await expect(
            viewerEvents.getByRole('cell', {
                name: 'pagination-row-1',
                exact: true,
            }),
        ).toBeVisible()
        await expect(
            viewerEvents.getByRole('button', { name: '下一页' }),
        ).toBeDisabled()
        await viewerEvents.getByRole('button', { name: '上一页' }).click()
        await expect(
            viewerEvents.getByRole('cell', { name: /^pagination-row-/ }),
        ).toHaveCount(50)
        await viewer.reload()
        await expect(
            viewer.getByRole('region', { name: '运行单元', exact: true }),
        ).toContainText('diagnostic-meter/sample')
        await viewer.goto('/')
        const restarted = await (
            await viewer.request.get('/api/edge-admin/v1/runtime/status')
        ).json()
        expect(restarted.configuration).toEqual(durable.configuration)
        await viewer.getByText('配置与网关信息', { exact: true }).click()
        await expect(
            viewer.getByRole('region', { name: '运行状态', exact: true }),
        ).toContainText('系统')
        await expect(
            viewer.getByRole('region', { name: '最近一小时趋势', exact: true }),
        ).toBeVisible()
        await expect(
            viewer.getByRole('region', { name: '最近异常', exact: true }),
        ).toContainText('pagination-row-55')
    } finally {
        try {
            await context?.close()
        } finally {
            try {
                if (host) expect(await host.stop()).toBe(0)
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
