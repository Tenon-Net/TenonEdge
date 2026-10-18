import { test, expect } from '@playwright/test'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { randomUUID } from 'node:crypto'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { startWorkflowGateway } from './support/gateway-workflow'

const root = '/api/edge-admin/v1'
const run = promisify(execFile)
async function startBroker() {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-module-broker-'))
    const name = 'tenonedge-module-' + randomUUID()
    const docker = (args: string[]) => run('docker', args, { timeout: 30_000 })
    const close = async () => {
        try {
            await docker(['rm', '-f', name])
        } finally {
            await rm(data, { recursive: true, force: true })
        }
    }
    const publish = (topic: string, payload: string) =>
        docker([
            'exec',
            name,
            'timeout',
            '3',
            'mosquitto_pub',
            '-h',
            '127.0.0.1',
            '-t',
            topic,
            '-m',
            payload,
            '-q',
            '1',
        ])
    try {
        // Isolated test-only broker; anonymous access is not a deployment template.
        await writeFile(
            join(data, 'mosquitto.conf'),
            'listener 1883\nallow_anonymous true\n',
        )
        await docker([
            'run',
            '-d',
            '--name',
            name,
            '-p',
            '127.0.0.1::1883',
            '-v',
            `${data}:/probe:ro`,
            'eclipse-mosquitto:2.1.2-alpine',
            'mosquitto',
            '-c',
            '/probe/mosquitto.conf',
        ])
        const mapping = await docker(['port', name, '1883/tcp'])
        const port = Number(mapping.stdout.trim().split(':').at(-1))
        await expect
            .poll(
                async () => {
                    try {
                        await publish('fixture/readiness', '{}')
                        return true
                    } catch {
                        return false
                    }
                },
                { timeout: 10_000 },
            )
            .toBe(true)
        return { port, publish, close }
    } catch (error) {
        await close()
        throw error
    }
}

for (const protocol of ['mqtt', 'modbus'] as const) {
    test(`input ${protocol} module: real connection form, preview, original protocol delivery and restart`, async ({
        browser,
    }) => {
        test.setTimeout(120_000)
        const broker = protocol === 'mqtt' ? await startBroker() : undefined
        let gateway:
            Awaited<ReturnType<typeof startWorkflowGateway>> | undefined
        try {
            gateway = await startWorkflowGateway(true)
            const peer = gateway
            const context = await browser.newContext({
                baseURL: peer.url,
                viewport: { width: 1700, height: 1100 },
            })
            try {
                const page = await context.newPage()
                await page.goto('/')
                await page
                    .getByLabel('账号', { exact: true })
                    .fill('protocol-admin')
                for (const label of ['密码', '确认密码'])
                    await page
                        .getByLabel(label, { exact: true })
                        .fill('Module-browser-7183!')
                await page
                    .getByRole('button', {
                        name: '创建管理员并登录',
                        exact: true,
                    })
                    .click()
                await expect(
                    page.getByRole('heading', { name: '总览', exact: true }),
                ).toBeVisible()
                const session = await (
                    await page.request.get(root + '/auth/session')
                ).json()
                const headers = { 'X-CSRF-Token': session.csrfToken }
                async function post(path: string, data: unknown) {
                    const response = await page.request.post(root + path, {
                        headers,
                        data,
                    })
                    expect(response.status(), await response.text()).toBe(200)
                    return response.json()
                }
                if (broker) {
                    const policy = await (
                        await page.request.get(root + '/system/outbound-policy')
                    ).json()
                    const saved = await page.request.put(
                        root + '/system/outbound-policy',
                        {
                            headers,
                            data: {
                                expectedVersion: policy.version,
                                networks: policy.networks,
                                localPorts: [...policy.localPorts, broker.port],
                            },
                        },
                    )
                    expect(saved.status(), await saved.text()).toBe(200)
                }
                // Connections and graph are API fixtures; module discovery, reference/parameter
                // editing, manual preview and explicit application are real product pages.
                const connection = await post('/connections', {
                    expectedVersion: '0',
                    key: 'source',
                    name: 'Protocol source',
                    kind: protocol,
                    [protocol]: {
                        host: '127.0.0.1',
                        port: broker?.port ?? peer.modbusPort,
                    },
                })
                const output = await post('/connections', {
                    expectedVersion: connection.version,
                    key: 'sink',
                    name: 'HTTP sink',
                    kind: 'http',
                    http: { baseAddress: `http://127.0.0.1:${peer.httpPort}/` },
                })
                await page.goto('/devices')
                await page
                    .getByRole('button', { name: '新建设备', exact: true })
                    .click()
                await page.getByLabel('设备 key', { exact: true }).fill('meter')
                await page
                    .getByLabel('设备名称', { exact: true })
                    .fill('Protocol meter')
                await page
                    .getByRole('combobox', { name: '设备类型', exact: true })
                    .selectOption('module')
                await page
                    .getByRole('combobox', {
                        name: '输入模块类型',
                        exact: true,
                    })
                    .selectOption('input.test' + protocol)
                const deviceSaved = page.waitForResponse(
                    (r) =>
                        r.request().method() === 'POST' &&
                        r.url().endsWith('/devices'),
                )
                await page
                    .getByRole('button', { name: '保存设备草稿', exact: true })
                    .click()
                expect((await deviceSaved).status()).toBe(200)
                const device = (await (await deviceSaved).json()).device
                await page
                    .getByRole('button', { name: /Protocol meter/ })
                    .click()
                await page
                    .getByRole('link', { name: '数据来源与点位', exact: true })
                    .click()
                await page
                    .getByRole('button', { name: '新建数据来源', exact: true })
                    .click()
                await page
                    .getByLabel('来源 key', { exact: true })
                    .fill('readings')
                await page
                    .getByLabel('来源名称', { exact: true })
                    .fill('Protocol readings')
                const editor = page.getByRole('region', {
                    name: '扩展输入参数',
                    exact: true,
                })
                await editor
                    .getByRole('combobox', {
                        name: '输入模块版本',
                        exact: true,
                    })
                    .selectOption('1')
                const reference = editor.getByRole('combobox', {
                    name: '连接',
                    exact: true,
                })
                const save = page.getByRole('button', {
                    name: '保存来源草稿',
                    exact: true,
                })
                await expect(save).toBeDisabled()
                await expect(
                    reference.locator('option', { hasText: 'HTTP sink' }),
                ).toHaveCount(0)
                await reference.selectOption(String(connection.connection.id))
                await editor
                    .getByLabel(protocol === 'mqtt' ? '主题' : '从站', {
                        exact: true,
                    })
                    .fill(protocol === 'mqtt' ? 'module/readings' : '1')
                const saved = page.waitForResponse(
                    (r) =>
                        r.request().method() === 'POST' &&
                        r.url().endsWith(`/devices/${device.id}/inputs`),
                )
                await save.click()
                const response = await saved
                expect(response.status(), await response.text()).toBe(200)
                const source = await response.json()
                expect(
                    response.request().postDataJSON().module.connections,
                ).toEqual({ connection: connection.connection.id })
                await page
                    .getByRole('button', {
                        name: 'Protocol readings (readings) · 扩展输入',
                        exact: true,
                    })
                    .click()
                await expect(reference).toHaveValue(
                    String(connection.connection.id),
                )
                expect(peer.reads).toHaveLength(0)
                expect(peer.deliveries).toHaveLength(0)
                await post('/routes', {
                    expectedVersion: source.version,
                    key: 'relay',
                    name: 'Protocol route',
                    enabled: true,
                    graph: {
                        schemaVersion: 1,
                        nodes: [
                            {
                                id: 'in',
                                type: 'input.test' + protocol,
                                version: 1,
                                name: 'Input',
                                config: { sourceIds: [source.input.id] },
                            },
                            {
                                id: 'out',
                                type: 'output.http',
                                version: 1,
                                name: 'Output',
                                config: { connectionId: output.connection.id },
                            },
                        ],
                        edges: [{ source: 'in', target: 'out' }],
                        layout: {
                            nodes: {
                                in: { x: 100, y: 200 },
                                out: { x: 600, y: 200 },
                            },
                        },
                    },
                })
                await page.goto('/routes')
                await page
                    .getByRole('button', {
                        name: '打开 Protocol route',
                        exact: true,
                    })
                    .click()
                await page
                    .getByLabel('预览数据来源', { exact: true })
                    .selectOption(
                        'input.test' + protocol + ':' + source.input.id,
                    )
                const preview = page.getByRole('region', {
                    name: '路线样本预览',
                    exact: true,
                })
                await preview
                    .getByLabel('手工 JSON 样本（不保存）', { exact: true })
                    .fill('{"value":9007199254740993}')
                await preview
                    .getByRole('button', {
                        name: '预览当前图（不发送）',
                        exact: true,
                    })
                    .click()
                await expect(preview).toContainText('9007199254740993')
                expect(peer.reads).toHaveLength(0)
                expect(peer.deliveries).toHaveLength(0)
                await page.goto('/configuration')
                await page
                    .getByRole('button', {
                        name: '查看应用变更清单',
                        exact: true,
                    })
                    .click()
                const review = page.getByRole('region', { name: '应用前确认' })
                await review
                    .getByLabel('我已检查变更及影响，确认应用这份草稿')
                    .check()
                await review
                    .getByRole('button', { name: '确认应用配置', exact: true })
                    .click()
                for (let phase = 0; phase < 2; phase++) {
                    const before = peer.deliveries.length
                    if (broker) {
                        await expect
                            .poll(
                                async () => {
                                    const status = await (
                                        await page.request.get(
                                            root + '/runtime/status',
                                        )
                                    ).json()
                                    return status.persistentCounters[
                                        `connection/${connection.connection.id}/mqtt/subscribed`
                                    ]
                                },
                                { timeout: 20_000 },
                            )
                            .toBe(phase + 1)
                        await broker.publish(
                            'module/readings',
                            '{"value":9007199254740993}',
                        )
                    }
                    await expect
                        .poll(() => peer.deliveries.length, { timeout: 20_000 })
                        .toBeGreaterThan(before)
                    expect(peer.deliveries.at(-1)?.body).toContain(
                        protocol === 'mqtt'
                            ? '"value":9007199254740993'
                            : '"value":32',
                    )
                    expect(peer.deliveries.at(-1)?.body).toContain(
                        '"deviceKey":"meter"',
                    )
                    expect(peer.protocolErrors).toEqual([])
                    const devices = await (
                        await page.request.get(root + '/runtime/devices')
                    ).json()
                    const meter = devices.devices.find(
                        (device: { key: string }) => device.key === 'meter',
                    )
                    expect(meter.sources[0].kind).toBe('input.test' + protocol)
                    expect(meter.sources[0].periodMs).toBe(
                        protocol === 'mqtt' ? null : 1000,
                    )
                    if (protocol === 'mqtt') expect(meter.health).toBe('push')
                    if (phase === 0) await peer.restart()
                }
            } finally {
                await context.close()
            }
        } finally {
            try {
                await gateway?.close()
            } finally {
                await broker?.close()
            }
        }
    })
}
