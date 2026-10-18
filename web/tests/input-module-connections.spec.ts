import { test, expect } from '@playwright/test'
import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'
import type { Diagnostics } from '../src/features/diagnostics/api'

const root = '/api/edge-admin/v1'
test('input HTTP reference: page selection, read failure/conflict, exact real forwarding and frozen restart', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-input-reference-web-'))
    const bodies: string[] = []
    const reads: string[] = []
    const peer = createServer((request, response) => {
        if (request.method === 'GET') {
            reads.push(request.url ?? '')
            response
                .writeHead(200, { 'Content-Type': 'application/json' })
                .end('{"value":9007199254740993}')
            return
        }
        const chunks: Buffer[] = []
        request.on('data', (chunk) => chunks.push(chunk))
        request.on('end', () => {
            bodies.push(Buffer.concat(chunks).toString('utf8'))
            response.writeHead(204).end()
        })
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('peer')
    const httpAddress = new URL(
        process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
    ).hostname
    let host = await startOwnedHost(data, 0, [], httpAddress, true)
    const context = await browser.newContext({
        baseURL: host.url,
        viewport: { width: 1700, height: 1100 },
    })
    try {
        const page = await context.newPage()
        page.setDefaultTimeout(10_000)
        page.on('dialog', (dialog) => dialog.accept())
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('reference-admin')
        for (const name of ['密码', '确认密码'])
            await page
                .getByLabel(name, { exact: true })
                .fill('Reference-browser-7183!')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        async function post(path: string, body: unknown) {
            const response = await page.request.post(root + path, {
                headers,
                data: body,
            })
            expect(response.status(), await response.text()).toBe(200)
            return response.json()
        }
        // Connection, outbound authorization and graph are API fixtures. The module
        // device/source selection, editing, preview and application use real pages.
        const policy = await (
            await page.request.get(root + '/system/outbound-policy')
        ).json()
        expect(
            (
                await page.request.put(root + '/system/outbound-policy', {
                    headers,
                    data: {
                        expectedVersion: policy.version,
                        networks: policy.networks,
                        localPorts: [...policy.localPorts, address.port],
                    },
                })
            ).status(),
        ).toBe(200)
        const connection = await post('/connections', {
            expectedVersion: '0',
            key: 'shared',
            name: 'Shared HTTP',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${address.port}/` },
        })
        await page.goto('/devices')
        await page
            .getByRole('button', { name: '新建设备', exact: true })
            .click()
        await page.getByLabel('设备 key', { exact: true }).fill('meter')
        await page
            .getByLabel('设备名称', { exact: true })
            .fill('Reference meter')
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('module')
        await page
            .getByRole('combobox', { name: '输入模块类型', exact: true })
            .selectOption('input.testreference')
        const deviceSaved = page.waitForResponse(
            (r) =>
                r.request().method() === 'POST' && r.url().endsWith('/devices'),
        )
        await page
            .getByRole('button', { name: '保存设备草稿', exact: true })
            .click()
        expect((await deviceSaved).status()).toBe(200)
        const device = (await (await deviceSaved).json()).device
        await page.getByRole('button', { name: /Reference meter/ }).click()
        await page
            .getByRole('link', { name: '数据来源与点位', exact: true })
            .click()
        await page
            .getByRole('button', { name: '新建数据来源', exact: true })
            .click()
        await page.getByLabel('来源 key', { exact: true }).fill('readings')
        const name = page.getByLabel('来源名称', { exact: true })
        await name.fill('Unsaved readings')
        const save = page.getByRole('button', {
            name: '保存来源草稿',
            exact: true,
        })
        const editor = page.getByRole('region', {
            name: '扩展输入参数',
            exact: true,
        })
        await editor
            .getByRole('combobox', { name: '输入模块版本', exact: true })
            .selectOption('1')
        const reference = editor.getByRole('combobox', {
            name: '连接',
            exact: true,
        })
        await expect(save).toBeDisabled()
        await reference.selectOption(String(connection.connection.id))
        await expect(save).toBeEnabled()
        const refresh = editor.getByRole('button', {
            name: '重新读取模块连接',
            exact: true,
        })
        const pattern = '**/api/edge-admin/v1/connections'
        // Only inject read responses. Writes and all sampling/forwarding are real.
        await page.route(pattern, (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{}',
            }),
        )
        const failedRead = page.waitForResponse(
            (r) => r.url().endsWith('/connections') && r.status() === 500,
        )
        await refresh.click()
        await failedRead
        await expect(editor.getByRole('alert')).toContainText('连接目录')
        await expect(save).toBeDisabled()
        await expect(reference).toBeDisabled()
        await expect(reference).toHaveValue(String(connection.connection.id))
        await expect(name).toHaveValue('Unsaved readings')
        await page.unroute(pattern)
        await page.route(pattern, async (route) => {
            const response = await route.fetch()
            const json = await response.json()
            await route.fulfill({
                response,
                json: { ...json, version: '999999' },
            })
        })
        const conflictingRead = page.waitForResponse(
            (r) => r.url().endsWith('/connections') && r.status() === 200,
        )
        await refresh.click()
        expect((await (await conflictingRead).json()).version).toBe('999999')
        await expect(reference).toBeDisabled()
        await expect(save).toBeDisabled()
        await expect(name).toHaveValue('Unsaved readings')
        await page.unroute(pattern)
        await refresh.click()
        await expect(reference).toBeEnabled()
        await expect(reference).toHaveValue(String(connection.connection.id))
        await expect(name).toHaveValue('Unsaved readings')
        const saved = page.waitForResponse(
            (r) =>
                r.request().method() === 'POST' &&
                r.url().endsWith(`/devices/${device.id}/inputs`),
        )
        await save.click()
        const response = await saved
        expect(response.status(), await response.text()).toBe(200)
        expect(response.request().postDataJSON().module).toEqual({
            kind: 'input.testreference',
            version: 1,
            parameters: {},
            connections: { connection: connection.connection.id },
        })
        const source = await response.json()
        await page
            .getByRole('button', {
                name: 'Unsaved readings (readings) · 扩展输入',
                exact: true,
            })
            .click()
        await expect(reference).toHaveValue(String(connection.connection.id))
        expect(reads).toHaveLength(0)
        expect(bodies).toHaveLength(0)
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'reference',
            name: 'Reference route',
            enabled: true,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'in',
                        type: 'input.testreference',
                        version: 1,
                        name: 'Input',
                        config: { sourceIds: [source.input.id] },
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
                layout: {
                    nodes: { in: { x: 100, y: 200 }, out: { x: 600, y: 200 } },
                },
            },
        })
        await page.goto('/routes')
        await page
            .getByRole('button', { name: '打开 Reference route', exact: true })
            .click()
        await page
            .getByLabel('预览数据来源', { exact: true })
            .selectOption('input.testreference:' + source.input.id)
        const preview = page.getByRole('region', {
            name: '路线样本预览',
            exact: true,
        })
        await preview
            .getByLabel('手工 JSON 样本（不保存）', { exact: true })
            .fill('{"value":9007199254740993}')
        await preview
            .getByRole('button', { name: '预览当前图（不发送）', exact: true })
            .click()
        await expect(preview).toContainText('9007199254740993')
        expect(reads).toHaveLength(0)
        expect(bodies).toHaveLength(0)
        await page.goto('/configuration')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        const review = page.getByRole('region', { name: '应用前确认' })
        await review.getByLabel('我已检查变更及影响，确认应用这份草稿').check()
        await review
            .getByRole('button', { name: '确认应用配置', exact: true })
            .click()
        await expect
            .poll(() =>
                bodies.some((body) =>
                    body.includes('"value":9007199254740993'),
                ),
            )
            .toBe(true)
        await expect
            .poll(async () => {
                const status: Diagnostics = await (
                    await page.request.get(root + '/runtime/status')
                ).json()
                return (
                    Number(status.queue.committedRecords) >= 1 &&
                    status.queue.deletedRecords ===
                        status.queue.committedRecords
                )
            })
            .toBe(true)
        expect(reads.length).toBeGreaterThan(0)
        expect(reads.every((path) => path === '/module')).toBe(true)
        // Deliberately alter only the draft. Restart must use the frozen connection.
        const changed = await page.request.put(
            `${root}/connections/${connection.connection.id}`,
            {
                headers,
                data: {
                    expectedVersion: route.version,
                    key: 'shared',
                    name: 'Unapplied',
                    kind: 'http',
                    http: { baseAddress: 'http://unapplied.invalid/' },
                },
            },
        )
        expect(changed.status(), await changed.text()).toBe(200)
        const storageState = await context.storageState()
        await context.close()
        expect(await host.stop()).toBe(0)
        const beforeRestart = bodies.length
        const readsBeforeRestart = reads.length
        host = await startOwnedHost(data, 0, [], httpAddress, true)
        const restarted = await browser.newContext({
            baseURL: host.url,
            storageState,
        })
        try {
            await expect
                .poll(async () => {
                    const status = await (
                        await restarted.request.get(root + '/runtime/status')
                    ).json()
                    // Dynamic listeners may not be bound at the first restored poll;
                    // the original outbound guard refuses it until the next cycle.
                    return bodies.length > beforeRestart
                        ? 'forwarded'
                        : { reads, bodies, status }
                })
                .toBe('forwarded')
        } finally {
            await restarted.close()
        }
        expect(reads.length).toBeGreaterThan(readsBeforeRestart)
        expect(reads.every((path) => path === '/module')).toBe(true)
        expect(
            bodies.every((body) => body.includes('"value":9007199254740993')),
        ).toBe(true)
    } finally {
        await context.close().catch(() => {})
        await host.stop()
        await new Promise<void>((resolve, reject) =>
            peer.close((error) => (error ? reject(error) : resolve())),
        )
        await rm(data, { recursive: true, force: true })
    }
})
