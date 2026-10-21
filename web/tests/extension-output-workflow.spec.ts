import { expect, test, type Page } from '@playwright/test'
import { createServer, type Socket } from 'node:net'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'
import type { OutputModule } from '../src/features/canvas/output-module-model'

const password = 'Wire-workflow-8251!'
const root = '/api/edge-admin/v1'

async function createInput(page: Page) {
    await page.goto('/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page.getByLabel('设备 key', { exact: true }).fill('sensor')
    await page.getByLabel('设备名称', { exact: true }).fill('Sensor')
    await page
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption('module')
    await page
        .getByRole('combobox', { name: '输入模块类型', exact: true })
        .selectOption('input.testscalar')
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page.getByRole('button', { name: /Sensor/ }).click()
    await page
        .getByRole('link', { name: '数据来源与点位', exact: true })
        .click()
    await page
        .getByRole('button', { name: '新建数据来源', exact: true })
        .click()
    await page.getByLabel('来源 key', { exact: true }).fill('values')
    await page.getByLabel('来源名称', { exact: true }).fill('Values')
    await page
        .getByRole('combobox', { name: '输入模块版本', exact: true })
        .selectOption('3')
    await page.getByLabel('初值', { exact: true }).fill('9007199254740993')
    const saved = page.waitForResponse(
        (r) => r.request().method() === 'POST' && r.url().endsWith('/inputs'),
    )
    await page
        .getByRole('button', { name: '保存来源草稿', exact: true })
        .click()
    expect((await saved).status()).toBe(200)
    expect(await (await saved).text()).toContain('9007199254740993')
    return (await (await saved).json()).input.id
}

test('extension output: UI-created connection, source and route use discovered transport and frozen TCP replay', async ({
    browser,
}, testInfo) => {
    test.setTimeout(180_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-wire-web-'))
    const sockets = new Set<Socket>()
    const frames: { address: string; body: string }[] = []
    const faults: string[] = []
    let acknowledge = false
    let acceptedConnections = 0
    const peer = createServer((socket) => {
        acceptedConnections++
        sockets.add(socket)
        socket.on('error', () => {})
        socket.on('close', () => sockets.delete(socket))
        let pending = Buffer.alloc(0)
        socket.on('data', (chunk) => {
            pending = Buffer.concat([pending, chunk])
            if (pending.length > 270_344) {
                faults.push('frame budget')
                socket.destroy()
                return
            }
            while (pending.length >= 8) {
                const addressBytes = pending.readInt32BE(0),
                    bodyBytes = pending.readInt32BE(4)
                if (
                    addressBytes < 0 ||
                    addressBytes > 4096 ||
                    bodyBytes < 1 ||
                    bodyBytes > 262144
                ) {
                    faults.push('invalid frame')
                    socket.destroy()
                    return
                }
                const size = 8 + addressBytes + bodyBytes
                if (pending.length < size) return
                frames.push({
                    address: pending
                        .subarray(8, 8 + addressBytes)
                        .toString('utf8'),
                    body: pending
                        .subarray(8 + addressBytes, size)
                        .toString('utf8'),
                })
                pending = pending.subarray(size)
                socket.write(Buffer.from([acknowledge ? 6 : 21]))
            }
        })
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const endpoint = peer.address()
    if (!endpoint || typeof endpoint === 'string') throw new Error('wire port')
    const address = new URL(
        process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
    ).hostname
    const args = [`--TenonEdge:Outbound:LocalPorts:0=${endpoint.port}`]
    let host = await startOwnedHost(data, 0, args, address, true)
    const context = await browser.newContext({
        baseURL: host.url,
        viewport: { width: 1800, height: 1200 },
    })
    const external: string[] = []
    await context.route('**/*', (route) => {
        if (new URL(route.request().url()).origin === host.url)
            return route.continue()
        external.push(route.request().url())
        return route.abort()
    })
    const page = await context.newPage()
    page.setDefaultTimeout(15_000)
    page.on('dialog', (dialog) => dialog.accept())
    try {
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('wire-admin')
        for (const name of ['密码', '确认密码'])
            await page.getByLabel(name, { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()

        // All configuration writes originate in visible controls, never API/JSON seeding.
        await page.goto('/connections')
        await page
            .getByRole('button', { name: '新建扩展连接', exact: true })
            .click()
        const connection = page.getByRole('form', {
            name: '扩展连接参数',
            exact: true,
        })
        await connection
            .getByLabel('扩展类型与版本', { exact: true })
            .selectOption('wire@1')
        await connection.getByLabel('连接 key', { exact: true }).fill('wire')
        await connection
            .getByLabel('连接名称', { exact: true })
            .fill('Wire sink')
        await connection.getByLabel('Host', { exact: true }).fill('127.0.0.1')
        await connection
            .getByLabel('Port', { exact: true })
            .fill(String(endpoint.port))
        await connection
            .getByRole('button', { name: '保存到草稿', exact: true })
            .click()
        await expect(
            page.getByRole('row').filter({ hasText: 'Wire sink' }),
        ).toBeVisible()
        const sourceId = await createInput(page)
        await page.goto('/routes')
        await page
            .getByRole('button', { name: '新建路线', exact: true })
            .click()
        await page.getByLabel('路线标识', { exact: true }).fill('wire-route')
        await page.getByLabel('路线名称', { exact: true }).fill('Wire route')
        await page
            .getByLabel('参与静态编译（草稿启用）', { exact: true })
            .check()
        const canvas = page.getByLabel('路线画布', { exact: true })
        await canvas.scrollIntoViewIfNeeded()
        await page
            .getByRole('button', { name: '测试标量输入 (v3)', exact: true })
            .dragTo(canvas, { targetPosition: { x: 120, y: 150 } })
        await page
            .getByLabel('Sensor / Values (values)', { exact: true })
            .check()
        await page
            .getByRole('button', {
                name: 'output.wiremodule (v1)',
                exact: true,
            })
            .click()
        await expect(page.locator('.react-flow__node')).toHaveCount(2)
        const output = page.getByRole('form', {
            name: '扩展输出参数',
            exact: true,
        })
        await expect(output).toBeVisible()
        await output
            .getByLabel('Connection', { exact: true })
            .selectOption({ label: 'Wire sink (wire)' })
        await expect(
            output.getByRole('combobox', { name: '模块投递目标', exact: true }),
        ).toHaveValue('')
        await output
            .getByRole('combobox', { name: '模块投递目标', exact: true })
            .selectOption({ label: 'Wire sink (wire)' })
        await output
            .getByLabel('Address', { exact: true })
            .fill('events/{deviceKey}')
        const target = output.getByRole('combobox', {
            name: '模块投递目标',
            exact: true,
        })
        const selectedTarget = await target.inputValue()
        const accept = output.getByRole('button', {
            name: '确认节点配置',
            exact: true,
        })
        const refresh = page.getByRole('button', {
            name: '重新读取输出模块',
            exact: true,
        })
        async function reloadModules() {
            const response = page.waitForResponse(
                (r) => new URL(r.url()).pathname === root + '/output-modules',
            )
            await refresh.click()
            expect(await (await response).finished()).toBeNull()
            await expect(refresh).toBeEnabled()
        }
        // Fault injection only changes read-only capability discovery. Config writes and TCP stay real.
        for (const fault of [
            'error',
            'missing',
            'unavailable',
            'invalid',
        ] as const) {
            const pattern = '**/output-modules'
            await page.route(pattern, async (intercept) => {
                if (fault === 'error')
                    return intercept.fulfill({
                        status: 500,
                        contentType: 'application/problem+json',
                        body: '{}',
                    })
                const response = await intercept.fetch()
                const modules: OutputModule[] = await response.json()
                await intercept.fulfill({
                    response,
                    json:
                        fault === 'missing'
                            ? modules.filter(
                                  (m) => m.kind !== 'output.wiremodule',
                              )
                            : modules.map((m) =>
                                  m.kind === 'output.wiremodule'
                                      ? {
                                            ...m,
                                            targetConnectionKinds:
                                                fault === 'invalid'
                                                    ? ['mqtt']
                                                    : [],
                                        }
                                      : m,
                              ),
                })
            })
            await reloadModules()
            await expect(accept).toBeDisabled()
            await expect(
                output.getByLabel('Address', { exact: true }),
            ).toHaveValue('events/{deviceKey}')
            await expect(
                output.getByLabel('Connection', { exact: true }),
            ).toHaveValue(selectedTarget)
            await expect(target).toHaveValue(selectedTarget)
            expect(frames).toHaveLength(0)
            await page.unroute(pattern)
            await reloadModules()
            await expect(accept).toBeEnabled()
            await expect(target).toHaveValue(selectedTarget)
        }
        await accept.click()
        const nodes = page.locator('.react-flow__node')
        await expect(nodes).toHaveCount(2)
        await nodes
            .nth(0)
            .locator('.react-flow__handle.source')
            .dragTo(nodes.nth(1).locator('.react-flow__handle.target'))
        await expect(page.locator('.react-flow__edge')).toHaveCount(1)
        // The only hand-written JSON here is a transient sample, never configuration.
        const preview = page.getByRole('region', {
            name: '路线样本预览',
            exact: true,
        })
        await preview
            .getByLabel('预览数据来源', { exact: true })
            .selectOption(`input.testscalar:${sourceId}`)
        await preview
            .getByLabel('手工 JSON 样本（不保存）', { exact: true })
            .fill('{"value":9007199254740993}')
        const previewReply = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/routes/preview'),
        )
        await preview
            .getByRole('button', { name: '预览当前图（不发送）', exact: true })
            .click()
        const reply = await previewReply
        expect(reply.status()).toBe(200)
        const result = (await reply.json()).result
        expect(result.totalPaths).toBe(1)
        expect(result.succeededPaths).toBe(1)
        expect(result.paths[0].output.kind).toBe('output.wire')
        expect(result.paths[0].output.address).toBe('events/sensor')
        expect(result.paths[0].output.body).toContain(
            '"value":9007199254740993',
        )
        expect(result.paths[0].output.testSendId).toBeNull()
        await expect(preview).toContainText('output.wire · events/sensor')
        await expect(
            preview.getByLabel('将要发送的正文（仅预览）', { exact: true }),
        ).toHaveText(result.paths[0].output.body)
        await expect(
            preview.getByRole('region', {
                name: '发送预览测试报文',
                exact: true,
            }),
        ).toHaveCount(0)
        expect(acceptedConnections).toBe(0)
        expect(frames).toHaveLength(0)
        await page
            .getByRole('button', { name: '保存路线草稿', exact: true })
            .click()
        await expect(
            page.getByText('路线草稿已保存，未应用。', { exact: true }),
        ).toBeVisible()
        expect(frames).toHaveLength(0)
        await page.goto('/configuration')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await page
            .getByLabel('我已检查变更及影响，确认应用这份草稿', { exact: true })
            .check()
        await page
            .getByRole('button', { name: '确认应用配置', exact: true })
            .click()
        await expect(
            page
                .getByText('配置已提交并激活。后续运行健康情况仍须查看诊断。', {
                    exact: true,
                })
                .first(),
        ).toBeVisible()
        await expect.poll(() => frames.length).toBe(1)
        expect(frames[0].address).toBe('events/sensor')
        expect(frames[0].body).toContain('"value":9007199254740993')
        const before = frames[0]
        const url = host.url
        expect(await host.stop()).toBe(0)
        acknowledge = true
        host = await startOwnedHost(
            data,
            Number(new URL(url).port),
            args,
            address,
            true,
        )
        await expect.poll(() => frames.length).toBeGreaterThanOrEqual(2)
        expect(frames[1]).toEqual(before)
        await page.goto('/runtime/outputs')
        const diagnostic = page
            .getByRole('row')
            .filter({ hasText: 'Wire sink' })
        await expect(diagnostic).toBeVisible()
        await expect(
            diagnostic.getByRole('cell', { name: '正常', exact: true }),
        ).toBeVisible()
        await expect(
            diagnostic.getByRole('cell', { name: '0', exact: true }),
        ).toHaveCount(3)
        const draft = await (
            await page.request.get(root + '/configuration/draft')
        ).json()
        expect(draft.effectiveRevision).toBe(1)
        expect(external).toEqual([])
        expect(faults).toEqual([])
    } catch (error) {
        await testInfo.attach('page-at-failure', {
            body: await page.locator('body').ariaSnapshot(),
            contentType: 'text/plain',
        })
        await testInfo.attach('screen-at-failure', {
            body: await page.screenshot({ fullPage: true }),
            contentType: 'image/png',
        })
        throw error
    } finally {
        await context.close()
        await host.stop()
        for (const socket of sockets) socket.destroy()
        await new Promise<void>((resolve) => peer.close(() => resolve()))
        await rm(data, { recursive: true, force: true })
    }
})
