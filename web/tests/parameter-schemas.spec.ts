import { test, expect } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
test('declarations drive real Modbus/MQTT drafts; bad ranges and metadata failures preserve editing without writes', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-parameters-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        page.on('dialog', (dialog) => dialog.accept())
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('parameters-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Parameters-browser-9164!')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Parameters-browser-9164!')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        await page.goto('/connections')
        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page
            .getByLabel('连接 key', { exact: true })
            .fill('parameters-plc')
        await page.getByLabel('连接名称', { exact: true }).fill('声明 PLC')
        await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
        const port = page.getByLabel('端口', { exact: true })
        await expect(port).toHaveValue('502')
        await expect(port).toHaveAttribute('type', 'text')
        const save = page.getByRole('button', {
            name: '保存到草稿',
            exact: true,
        })
        let writes = 0
        page.on('request', (request) => {
            if (
                request.url().endsWith(root + '/connections') &&
                request.method() === 'POST'
            )
                writes++
        })
        await port.fill('65536')
        await save.click()
        await expect(
            page.getByText('数字不在声明范围内。', { exact: true }),
        ).toBeVisible()
        expect(writes).toBe(0)
        await port.fill('1502')
        await page
            .getByLabel('帧格式', { exact: true })
            .selectOption('rtuOverTcp')
        await page
            .getByLabel('RTU 静默间隔（毫秒）', { exact: true })
            .fill('25')
        await save.click()
        await expect(
            page.getByText('已保存到网关草稿，尚未应用。', { exact: true }),
        ).toBeVisible()
        expect(writes).toBe(1)
        const read = async () =>
            (await page.request.get(root + '/connections')).json()
        const first = await read()
        expect(first.connections[0].modbus).toMatchObject({
            port: 1502,
            format: 'rtuOverTcp',
            rtuSilenceMs: 25,
        })

        // Same scalar declaration is enforced when browser validation is bypassed.
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const invalid = await page.request.post(root + '/connections', {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: {
                expectedVersion: first.version,
                key: 'invalid',
                name: 'Invalid',
                kind: 'modbus',
                enabled: true,
                modbus: { ...first.connections[0].modbus, port: 65536 },
            },
        })
        expect(invalid.status()).toBe(400)
        expect((await read()).version).toBe(first.version)

        await page
            .getByRole('button', { name: '新建连接', exact: true })
            .click()
        await page
            .getByRole('combobox', { name: '协议类型', exact: true })
            .selectOption('mqtt')
        await page
            .getByLabel('连接 key', { exact: true })
            .fill('parameters-broker')
        await page.getByLabel('连接名称', { exact: true }).fill('声明 Broker')
        await page.getByLabel('主机或 IP', { exact: true }).fill('127.0.0.1')
        await expect(port).toHaveValue('1883')
        await page.getByLabel('启用 TLS', { exact: true }).check()
        await expect(port).toHaveValue('8883')
        await port.fill('28883')
        await page.getByLabel('启用 TLS', { exact: true }).uncheck()
        await expect(port).toHaveValue('28883')
        await page
            .getByLabel('保活 / PING 间隔（毫秒）', { exact: true })
            .fill('1.3e4')
        await page.getByLabel('总超时（毫秒）', { exact: true }).fill('1200')
        const metadata = '**/parameter-schemas/connection.mqtt'
        for (const status of [500, 404]) {
            await page.route(metadata, (route) =>
                route.fulfill({
                    status,
                    contentType: 'application/problem+json',
                    body: JSON.stringify({
                        status,
                        title: 'Injected metadata failure',
                    }),
                }),
            )
            await page
                .getByRole('button', { name: '重新读取参数声明', exact: true })
                .last()
                .click()
            await expect(page.getByText(/MQTT: 参数声明不可用/)).toBeVisible()
            await expect(save).toBeDisabled()
            await expect(port).toHaveValue('28883')
            expect((await read()).version).toBe(first.version)
            await page.unroute(metadata)
            await page
                .getByRole('button', { name: '重新读取参数声明', exact: true })
                .last()
                .click()
            await expect(save).toBeEnabled()
            await expect(port).toHaveValue('28883')
            await expect(
                page.getByLabel('总超时（毫秒）', { exact: true }),
            ).toHaveValue('1200')
        }
        await save.click()
        await expect(
            page.getByText('已保存到网关草稿，尚未应用。', { exact: true }),
        ).toBeVisible()
        const second = await read()
        expect(
            second.connections.find(
                (connection: { key: string }) =>
                    connection.key === 'parameters-broker',
            ).mqtt,
        ).toMatchObject({
            port: 28883,
            tls: false,
            timeoutMs: 1200,
            pingIntervalMs: 13000,
        })
        await page.reload()
        await page
            .getByRole('row')
            .filter({ hasText: 'parameters-broker' })
            .getByRole('button', { name: '编辑连接', exact: true })
            .click()
        await expect(port).toHaveValue('28883')
        await expect(
            page.getByLabel('总超时（毫秒）', { exact: true }),
        ).toHaveValue('1200')
        const runtime = await (
            await page.request.get(root + '/configuration/runtime')
        ).json()
        expect(runtime.effectiveRevision).toBeNull()
    } finally {
        try {
            await context.close()
        } finally {
            try {
                await host.stop()
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
