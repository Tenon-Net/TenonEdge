import { expect, test, type Locator, type Page } from '@playwright/test'
import { createServer, type Socket } from 'node:net'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Authwire-workflow-8437!'
const button = (scope: Page | Locator, name: string) =>
    scope.getByRole('button', { name, exact: true })
const label = (scope: Page | Locator, name: string) =>
    scope.getByLabel(name, { exact: true })
const editor = (page: Page) =>
    page.getByRole('form', { name: '扩展连接参数', exact: true })
const credential = (page: Page) =>
    page.getByRole('region', { name: '扩展连接独立凭证', exact: true })

async function peer() {
    const sockets = new Set<Socket>()
    const frames: { secret: string; address: string; body: string }[] = []
    const faults: string[] = []
    let acknowledge = false
    let connections = 0
    const server = createServer((socket) => {
        connections++
        sockets.add(socket)
        socket.on('error', () => {})
        socket.on('close', () => sockets.delete(socket))
        let buffer = Buffer.alloc(0)
        socket.on('data', (chunk) => {
            buffer = Buffer.concat([buffer, chunk])
            const reject = () => {
                faults.push('invalid or oversized frame')
                socket.destroy()
            }
            if (buffer.length > 400_000) return reject()
            while (buffer.length >= 4) {
                const secretBytes = buffer.readInt32BE(0)
                if (secretBytes < 1 || secretBytes > 131072) return reject()
                const offset = 4 + secretBytes
                if (buffer.length < offset + 8) return
                const addressBytes = buffer.readInt32BE(offset)
                const bodyBytes = buffer.readInt32BE(offset + 4)
                if (
                    addressBytes < 0 ||
                    addressBytes > 4096 ||
                    bodyBytes < 1 ||
                    bodyBytes > 262144
                )
                    return reject()
                const size = offset + 8 + addressBytes + bodyBytes
                if (buffer.length < size) return
                frames.push({
                    secret: buffer.subarray(4, offset).toString('utf8'),
                    address: buffer
                        .subarray(offset + 8, offset + 8 + addressBytes)
                        .toString('utf8'),
                    body: buffer
                        .subarray(offset + 8 + addressBytes, size)
                        .toString('utf8'),
                })
                buffer = buffer.subarray(size)
                socket.write(Buffer.from([acknowledge ? 6 : 21]))
            }
        })
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const endpoint = server.address()
    if (!endpoint || typeof endpoint === 'string') throw new Error('peer port')
    return {
        port: endpoint.port,
        frames,
        faults,
        connections: () => connections,
        acknowledge: () => {
            acknowledge = true
        },
        suspend: () => {
            acknowledge = false
        },
        close: async () => {
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve) => server.close(() => resolve()))
        },
    }
}

async function writeCredential(page: Page, secret: string, rotating = false) {
    const panel = credential(page)
    await label(panel, '公开账户标识（非秘密）').fill('wire-owner')
    await label(panel, '认证秘密').fill(secret)
    const review = page.waitForRequest((request) =>
        request.url().endsWith('/module-credential/review'),
    )
    await button(panel, '核对凭证变更（不保存）').click()
    expect((await review).postData()).not.toContain(secret)
    await expect(
        panel.getByRole('region', { name: '确认凭证身份与积存', exact: true }),
    ).toBeVisible()
    if (rotating) {
        const choice = panel.getByRole('combobox')
        await expect(choice).toHaveValue('')
        await choice.selectOption('same')
    }
    await label(
        panel,
        '我已核对原新身份和积存，了解凭证立即生效及隔离规则',
    ).check()
    const saved = page.waitForResponse(
        (response) =>
            response.request().method() === 'PUT' &&
            response.url().endsWith('/module-credential'),
    )
    await button(panel, '立即保存凭证').click()
    expect((await saved).status()).toBe(200)
    expect(await (await saved).text()).not.toContain(secret)
    await expect(label(panel, '认证秘密')).toHaveValue('')
}

test('authenticated extension output: UI configuration, rotation, both confirmed migrations and frozen restart replay', async ({
    browser,
}, testInfo) => {
    test.setTimeout(180_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-authwire-output-'))
    const old = await peer()
    const next = await peer()
    next.acknowledge()
    const address = new URL(
        process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
    ).hostname
    const args = [
        `--TenonEdge:Outbound:LocalPorts:0=${old.port}`,
        `--TenonEdge:Outbound:LocalPorts:1=${next.port}`,
    ]
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
    async function openConnection() {
        await page.goto('/connections')
        await page
            .getByRole('row')
            .filter({ hasText: 'Auth sink' })
            .getByRole('button', { name: /编辑连接/ })
            .click()
        await expect(editor(page)).toBeVisible()
    }
    async function control(id: number, paused: boolean) {
        await page.goto('/runtime/outputs')
        await button(page, `查看目标 ${id}`).click()
        const controls = page.getByRole('region', {
            name: '目标发送操作',
            exact: true,
        })
        await button(controls, paused ? '手动暂停目标' : '恢复目标').click()
        await label(controls, '我已核对目标并确认此发送状态变更').check()
        await button(controls, '确认执行').click()
        const detail = page.getByRole('region', {
            name: '目标详情',
            exact: true,
        })
        await expect(
            detail.getByRole('heading', {
                name: paused ? / · 手动暂停$/ : / · 正常$/,
            }),
        ).toBeVisible()
    }
    try {
        await page.goto('/')
        await label(page, '账号').fill('authwire-admin')
        for (const name of ['密码', '确认密码'])
            await label(page, name).fill(password)
        await button(page, '创建管理员并登录').click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()

        // Every configuration and credential write below comes from real controls, not API/JSON fixtures.
        await page.goto('/connections')
        await button(page, '新建扩展连接').click()
        await label(editor(page), '扩展类型与版本').selectOption('authwire@1')
        await label(editor(page), '连接 key').fill('authenticated')
        await label(editor(page), '连接名称').fill('Auth sink')
        await label(editor(page), '主机').fill('127.0.0.1')
        await label(editor(page), '端口').fill(String(old.port))
        const created = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/connections'),
        )
        await button(editor(page), '保存到草稿').click()
        expect((await created).status()).toBe(200)
        const id: number = (await (await created).json()).connection.id
        await openConnection()
        await writeCredential(page, 'fixture-authwire-web-first')

        await page.goto('/devices')
        await button(page, '新建设备').click()
        await label(page, '设备 key').fill('sensor')
        await label(page, '设备名称').fill('Sensor')
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('module')
        await page
            .getByRole('combobox', { name: '输入模块类型', exact: true })
            .selectOption('input.testscalar')
        await button(page, '保存设备草稿').click()
        await page.getByRole('button', { name: /Sensor/ }).click()
        await page
            .getByRole('link', { name: '数据来源与点位', exact: true })
            .click()
        await button(page, '新建数据来源').click()
        await label(page, '来源 key').fill('values')
        await label(page, '来源名称').fill('Values')
        await page
            .getByRole('combobox', { name: '输入模块版本', exact: true })
            .selectOption('3')
        await label(page, '初值').fill('9007199254740993')
        const inputSaved = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/inputs'),
        )
        await button(page, '保存来源草稿').click()
        expect((await inputSaved).status()).toBe(200)
        await page.goto('/routes')
        await button(page, '新建路线').click()
        await label(page, '路线标识').fill('authenticated-route')
        await label(page, '路线名称').fill('Authenticated route')
        await label(page, '参与静态编译（草稿启用）').check()
        const canvas = label(page, '路线画布')
        await canvas.scrollIntoViewIfNeeded()
        await button(page, '测试标量输入 (v3)').dragTo(canvas, {
            targetPosition: { x: 120, y: 150 },
        })
        await label(page, 'Sensor / Values (values)').check()
        await button(page, 'output.authwiremodule (v1)').click()
        const output = page.getByRole('form', {
            name: '扩展输出参数',
            exact: true,
        })
        await label(output, 'Connection').selectOption({
            label: 'Auth sink (authenticated)',
        })
        await output
            .getByRole('combobox', { name: '模块投递目标', exact: true })
            .selectOption({
                label: 'Auth sink (authenticated)',
            })
        await label(output, 'Address').fill('events/{deviceKey}')
        await button(output, '确认节点配置').click()
        const nodes = page.locator('.react-flow__node')
        await expect(nodes).toHaveCount(2)
        await nodes
            .nth(0)
            .locator('.react-flow__handle.source')
            .dragTo(nodes.nth(1).locator('.react-flow__handle.target'))
        await expect(page.locator('.react-flow__edge')).toHaveCount(1)
        await button(page, '保存路线草稿').click()
        await expect(
            page.getByText('路线草稿已保存，未应用。', { exact: true }),
        ).toBeVisible()
        expect(old.connections()).toBe(0)
        await page.goto('/configuration')
        await button(page, '查看应用变更清单').click()
        await label(page, '我已检查变更及影响，确认应用这份草稿').check()
        await button(page, '确认应用配置').click()
        await expect(
            page
                .getByText('配置已提交并激活。后续运行健康情况仍须查看诊断。', {
                    exact: true,
                })
                .first(),
        ).toBeVisible()
        await expect.poll(() => old.frames.length).toBe(1)
        const frozen = old.frames[0]
        expect(frozen.secret).toBe('fixture-authwire-web-first')
        expect(frozen.address).toBe('events/sensor')
        expect(frozen.body).toContain('"value":9007199254740993')
        await openConnection()
        await writeCredential(page, 'fixture-authwire-web-next', true)
        await expect.poll(() => old.frames.length).toBe(2)
        expect(old.frames[1]).toEqual({
            ...frozen,
            secret: 'fixture-authwire-web-next',
        })
        await control(id, true)

        await openConnection()
        await label(editor(page), '端口').fill(String(next.port))
        await button(editor(page), '保存到草稿').click()
        await expect(editor(page)).toHaveCount(0)
        await openConnection()
        await writeCredential(page, 'fixture-authwire-web-new-address', true)
        expect(next.connections()).toBe(0)
        await page.goto('/configuration')
        await button(page, '查看应用变更清单').click()
        await expect(button(page, '确认应用配置')).toBeDisabled()
        await page
            .getByRole('link', { name: '核对同连接换地址迁移', exact: true })
            .click()
        await button(page, '读取换址变更清单').click()
        await label(page, `迁移连接 ${id} 的旧记录`).check()
        const prepared = page.waitForResponse((response) =>
            response.url().endsWith('/configuration/migrations/prepare'),
        )
        await button(page, '准备所选连接的换址应用').click()
        expect((await prepared).status()).toBe(200)
        const intentId: string = (await (await prepared).json()).targets[0]
            .intentId
        const review = page.getByRole('region', {
            name: '换址应用最终核对',
            exact: true,
        })
        await expect(review).toContainText(`authwire://127.0.0.1:${old.port}`)
        await expect(review).toContainText(`authwire://127.0.0.1:${next.port}`)
        await expect(button(page, '确认换址并应用配置')).toBeDisabled()
        await label(
            page,
            '我已核对全部配置差异、迁移连接和配额，确认应用',
        ).check()
        await label(
            page,
            '我确认每个所选连接的新旧接收方属于同一数据所有者',
        ).check()
        await button(page, '确认换址并应用配置').click()
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                root +
                                    `/configuration/migrations/intents/${intentId}`,
                            )
                        ).json()
                    ).status,
                { timeout: 20_000 },
            )
            .toBe('completed')
        expect(next.connections()).toBe(0)
        expect(old.frames).toHaveLength(2)

        const url = new URL(host.url)
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data, Number(url.port), args, address, true)
        expect(next.connections()).toBe(0)
        const intent = await (
            await page.request.get(
                root + `/configuration/migrations/intents/${intentId}`,
            )
        ).json()
        expect(intent.status).toBe('completed')
        expect(intent.affectedRecords).toBe(1)
        await control(id, false)
        await expect.poll(() => next.frames.length).toBeGreaterThanOrEqual(1)
        expect(next.frames[0]).toEqual({
            ...frozen,
            secret: 'fixture-authwire-web-new-address',
        })
        const diagnostic = page
            .getByRole('row')
            .filter({ hasText: 'Auth sink' })
        await expect(
            diagnostic.getByRole('cell', { name: '0', exact: true }),
        ).toHaveCount(3)
        expect(old.frames).toHaveLength(2)
        const draft = await (
            await page.request.get(root + '/configuration/draft')
        ).json()
        expect(draft.effectiveRevision).toBe(2)

        // Form B uses another active connection with no output route, not a fabricated queue target.
        await page.goto('/connections')
        await button(page, '新建扩展连接').click()
        await label(editor(page), '扩展类型与版本').selectOption('authwire@1')
        await label(editor(page), '连接 key').fill('migration-target')
        await label(editor(page), '连接名称').fill('Migration target')
        await label(editor(page), '主机').fill('127.0.0.1')
        await label(editor(page), '端口').fill(String(old.port))
        const targetCreated = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/connections'),
        )
        await button(editor(page), '保存到草稿').click()
        expect((await targetCreated).status()).toBe(200)
        const targetId: number = (await (await targetCreated).json()).connection
            .id
        await page
            .getByRole('row')
            .filter({ hasText: 'Migration target' })
            .getByRole('button', { name: /编辑连接/ })
            .click()
        await writeCredential(page, 'fixture-authwire-web-target')
        await page.goto('/configuration')
        await button(page, '查看应用变更清单').click()
        await label(page, '我已检查变更及影响，确认应用这份草稿').check()
        await button(page, '确认应用配置').click()
        await expect(
            page
                .getByText('配置已提交并激活。后续运行健康情况仍须查看诊断。', {
                    exact: true,
                })
                .first(),
        ).toBeVisible()
        await control(targetId, true)
        const previousFrames = next.frames.length
        next.suspend()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data, Number(url.port), args, address, true)
        await expect
            .poll(() => next.frames.length)
            .toBeGreaterThan(previousFrames)
        const selected = next.frames[previousFrames]
        const deliveryId: string = JSON.parse(selected.body).messageId
        await page.goto('/runtime/outputs')
        await button(page, `查看目标 ${id}`).click()
        await page
            .getByRole('link', {
                name: '选择记录并迁移到另一连接',
                exact: true,
            })
            .click()
        await label(page, `选择迁移记录 ${deliveryId}`).check()
        await label(page, '请选择目标连接').selectOption(String(targetId))
        await button(page, '准备迁移所选记录').click()
        const selectionReview = page.getByRole('region', {
            name: '核对冻结迁移清单',
            exact: true,
        })
        await expect(selectionReview).toContainText(
            `authwire://127.0.0.1:${next.port}`,
        )
        await expect(selectionReview).toContainText(
            `authwire://127.0.0.1:${old.port}`,
        )
        await expect(button(page, '确认迁移冻结清单')).toBeDisabled()
        await label(page, '我确认新接收方与原接收方属于同一数据所有者').check()
        const migrated = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                /\/deliveries\/migrations\/[^/]+\/confirm$/.test(
                    response.url(),
                ),
        )
        await button(page, '确认迁移冻结清单').click()
        expect((await migrated).status()).toBe(200)
        expect((await (await migrated).json()).migratedRecords).toBe(1)
        expect(old.frames).toHaveLength(2)
        old.acknowledge()
        await control(targetId, false)
        await expect.poll(() => old.frames.length).toBe(3)
        expect(old.frames[2]).toEqual({
            ...selected,
            secret: 'fixture-authwire-web-target',
        })
        const exported = await (
            await page.request.get(root + '/configuration/draft')
        ).text()
        expect(exported).not.toContain('fixture-authwire-web-')
        expect(old.faults).toEqual([])
        expect(next.faults).toEqual([])
        expect(external).toEqual([])
    } catch (error) {
        await testInfo.attach('page-at-failure', {
            body: await page.locator('body').ariaSnapshot(),
            contentType: 'text/plain',
        })
        throw error
    } finally {
        await context.close()
        await host.stop()
        await old.close()
        await next.close()
        await rm(data, { recursive: true, force: true })
    }
})
