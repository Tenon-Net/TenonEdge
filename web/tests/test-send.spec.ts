import { test, expect, type Locator, type Browser } from '@playwright/test'
import { createServer } from 'node:http'
import { createHash, randomUUID } from 'node:crypto'
import { execFile, spawn } from 'node:child_process'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { promisify } from 'node:util'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const run = promisify(execFile)
const password = 'Send-browser-7419!'
const acknowledge =
    '我已核对真实目标、账号及完整原报文，了解对方可能将其当作真实数据保存'

// API creates only the configuration fixture. Every preview/prepare/confirm below
// is a real browser action against the product Host, not a mocked management API.
async function fixture(
    browser: Browser,
    port: number,
    protocol: 'http' | 'mqtt' = 'http',
) {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-test-send-browser-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    const close = async () => {
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
    try {
        const page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('send-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
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
                        localPorts: [...policy.localPorts, port],
                    },
                })
            ).status(),
        ).toBe(200)
        const connection = await post('/connections', {
            expectedVersion: '0',
            key: 'test-target',
            name: 'Test target',
            kind: protocol,
            ...(protocol === 'http'
                ? {
                      http: {
                          baseAddress: `http://127.0.0.1:${port}/`,
                          timeoutMs: 30000,
                      },
                  }
                : { mqtt: { host: '127.0.0.1', port } }),
        })
        const device = await post('/devices', {
            expectedVersion: connection.version,
            key: 'sample',
            name: 'Sample',
            kind: 'simulator',
        })
        const source = await post(`/devices/${device.device.id}/inputs`, {
            expectedVersion: device.version,
            key: 'values',
            name: 'Values',
            simulator: {
                sampleJson:
                    protocol === 'http' ? '{"value":"x"}' : '{"value":1}',
            },
        })
        const nodes = protocol === 'http' ? [1] : [0, 1]
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'test-send',
            name: 'Test send',
            enabled: false,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'in',
                        type: 'input.simulator',
                        version: 1,
                        name: 'Input',
                        config: { sourceIds: [source.input.id] },
                    },
                    ...nodes.map((qos) => ({
                        id: 'out' + qos,
                        type: 'output.' + protocol,
                        version: 1,
                        name: 'Output ' + qos,
                        config: {
                            connectionId: connection.connection.id,
                            ...(protocol === 'http'
                                ? { path: 'events' }
                                : { topic: 'test-send/' + qos, qos }),
                            payload: {
                                mode: 'custom',
                                fields: [
                                    { target: 'first', sourceField: 'value' },
                                    { target: 'second', sourceField: 'value' },
                                ],
                            },
                        },
                    })),
                ],
                edges: nodes.map((qos) => ({
                    source: 'in',
                    target: 'out' + qos,
                })),
                layout: { nodes: {} },
            },
        })
        async function open() {
            await page.goto('/routes')
            await page
                .getByRole('button', { name: '打开 Test send', exact: true })
                .click()
            await page
                .getByLabel('预览数据来源', { exact: true })
                .selectOption('input.simulator:' + source.input.id)
        }
        await open()
        const panel = page.getByRole('region', {
            name: '路线样本预览',
            exact: true,
        })
        async function preview(
            sample = protocol === 'http'
                ? '{"value":"9223372036854775807"}'
                : '{"value":9223372036854775807}',
        ) {
            await panel
                .getByLabel('手工 JSON 样本（不保存）', { exact: true })
                .fill(sample)
            await panel
                .getByRole('button', {
                    name: '预览当前图（不发送）',
                    exact: true,
                })
                .click()
            await expect(
                panel.getByRole('region', {
                    name: '发送预览测试报文',
                    exact: true,
                }),
            ).toHaveCount(nodes.length)
            return panel.getByRole('region', {
                name: '发送预览测试报文',
                exact: true,
            })
        }
        async function unchanged() {
            expect(
                (
                    await (
                        await page.request.get(root + '/configuration/draft')
                    ).json()
                ).version,
            ).toBe(route.version)
            const runtime = await (
                await page.request.get(root + '/configuration/runtime')
            ).json()
            expect(runtime.effectiveRevision).toBeNull()
            const audits = await (
                await page.request.get(root + '/audit?limit=100')
            ).json()
            return audits
        }
        return {
            page,
            panel,
            context,
            host,
            post,
            preview,
            open,
            unchanged,
            close,
        }
    } catch (error) {
        await close()
        throw error
    }
}
async function review(panel: Locator) {
    await panel
        .getByRole('button', { name: '核对测试发送', exact: true })
        .click()
    await expect(
        panel.getByRole('button', { name: '确认发送一次', exact: true }),
    ).toBeDisabled()
    await expect(panel).toContainText('完整原报文 SHA-256：')
}
async function confirm(panel: Locator) {
    await panel.getByLabel(acknowledge, { exact: true }).check()
    await panel
        .getByRole('button', { name: '确认发送一次', exact: true })
        .click()
}

async function httpPeer() {
    let mode: 'safe' | 'large' | 'secret' | 'wait' = 'safe'
    const requests: { body: string; id: string | string[] | undefined }[] = []
    const server = createServer(async (req, res) => {
        const chunks: Buffer[] = []
        for await (const chunk of req) chunks.push(Buffer.from(chunk))
        requests.push({
            body: Buffer.concat(chunks).toString('utf8'),
            id: req.headers['idempotency-key'],
        })
        if (mode === 'wait') return
        const text =
            mode === 'large'
                ? 'x'.repeat(65537)
                : mode === 'secret'
                  ? '{"password":"fixture-sensitive"}'
                  : '{"value":9223372036854775807}'
        res.writeHead(422, {
            'Content-Type': 'application/json',
            'Content-Length': Buffer.byteLength(text),
        })
        res.end(text)
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('listener')
    return {
        port: address.port,
        requests,
        mode: (next: typeof mode) => {
            mode = next
        },
        close: async () => {
            server.closeAllConnections()
            await new Promise<void>((resolve, reject) =>
                server.close((error) => (error ? reject(error) : resolve())),
            )
        },
    }
}

test('HTTP preview confirmation sends exact full bytes once and safely hides oversized or sensitive responses', async ({
    browser,
}) => {
    test.setTimeout(90000)
    const peer = await httpPeer()
    try {
        const f = await fixture(browser, peer.port)
        try {
            let sends = 0
            f.page.on('request', (req) => {
                if (req.url().endsWith('/routes/test-send/confirm')) sends++
            })
            for (const mode of ['safe', 'large', 'secret'] as const) {
                peer.mode(mode)
                const value = mode === 'large' ? 'x'.repeat(50000) : null
                const panel = await f.preview(
                    value ? JSON.stringify({ value }) : undefined,
                )
                const expected = value
                    ? JSON.stringify({ first: value, second: value })
                    : '{"first":"9223372036854775807","second":"9223372036854775807"}'
                const idText = await f.panel
                    .getByText(/^本次预览临时编号：/)
                    .textContent()
                const id = idText?.match(/[0-9a-f-]{36}/)?.[0]
                await review(panel)
                await expect(panel).toContainText(
                    createHash('sha256').update(expected).digest('hex'),
                )
                await expect(panel).toContainText(
                    `完整原报文：${Buffer.byteLength(expected)} 字节`,
                )
                if (value)
                    await expect(panel).toContainText(
                        '实际发送服务端保存的完整原报文',
                    )
                expect(peer.requests).toHaveLength(sends)
                // A canceled review and focus changes never publish.
                await panel
                    .getByRole('button', { name: '放弃本次核对' })
                    .click()
                await review(panel)
                await f.page.bringToFront()
                expect(peer.requests).toHaveLength(sends)
                await confirm(panel)
                await expect(panel).toContainText('HTTP 状态码：422')
                expect(peer.requests).toHaveLength(sends)
                expect(peer.requests.at(-1)).toEqual({ body: expected, id })
                if (mode === 'safe')
                    await expect(panel.getByLabel('安全响应正文')).toHaveText(
                        '{"value":9223372036854775807}',
                    )
                else {
                    await expect(panel.getByLabel('安全响应正文')).toHaveCount(
                        0,
                    )
                    await expect(panel).toContainText(
                        mode === 'large'
                            ? '已达读取上限，正文隐藏'
                            : '响应含敏感内容或凭证回显，正文隐藏',
                    )
                }
                await expect(
                    panel.getByRole('button', { name: '确认发送一次' }),
                ).toHaveCount(0)
                await expect(
                    panel.getByRole('button', { name: '核对测试发送' }),
                ).toHaveCount(0)
            }
            expect(sends).toBe(3)
            await expect(f.page.locator('body')).not.toContainText(
                'fixture-sensitive',
            )
            await f.unchanged()
            await f.page.screenshot({
                path: '/tmp/tenonedge-test-send-http.png',
                fullPage: true,
            })
            // All browser requests above are tests; no plan was applied and no queue records exist.
        } finally {
            await f.close()
        }
    } finally {
        await peer.close()
    }
})

test('changed samples, canceled waits, lost replies and navigation never replay a test confirmation', async ({
    browser,
}) => {
    test.setTimeout(90000)
    const peer = await httpPeer()
    try {
        const f = await fixture(browser, peer.port)
        try {
            let sends = 0
            f.page.on('request', (req) => {
                if (req.url().endsWith('/routes/test-send/confirm')) sends++
            })
            let panel = await f.preview()
            await review(panel)
            await f.panel
                .getByLabel('手工 JSON 样本（不保存）')
                .fill('{"value":"2"}')
            await expect(panel).toHaveCount(0)
            expect(sends).toBe(0)
            panel = await f.preview()
            await review(panel)
            peer.mode('wait')
            await confirm(panel)
            await expect.poll(() => peer.requests.length).toBe(1)
            await panel
                .getByRole('button', { name: '取消等待', exact: true })
                .click()
            await expect(panel).toContainText('发送结果未知，对方可能已经收到')
            await expect(panel.getByRole('button')).toHaveCount(0)
            peer.mode('safe')
            panel = await f.preview()
            await review(panel)
            await f.page.route('**/routes/test-send/confirm', async (route) => {
                const response = await route.fetch()
                expect(response.status()).toBe(200) // The actual target received it; only the browser reply is lost.
                await route.abort('failed')
            })
            await confirm(panel)
            await expect(panel).toContainText('发送结果未知，对方可能已经收到')
            await expect(panel.getByRole('button')).toHaveCount(0)
            expect(peer.requests).toHaveLength(2)
            expect(sends).toBe(2)
            await f.page.unroute('**/routes/test-send/confirm')
            await f.page.reload()
            await expect(
                f.page.getByRole('region', {
                    name: '发送预览测试报文',
                    exact: true,
                }),
            ).toHaveCount(0)
            await f.open()
            panel = await f.preview()
            await review(panel)
            peer.mode('wait')
            await confirm(panel)
            await expect.poll(() => peer.requests.length).toBe(3)
            await f.page.goto('/account')
            await expect(
                f.page.getByRole('region', {
                    name: '发送预览测试报文',
                    exact: true,
                }),
            ).toHaveCount(0)
            await f.open()
            expect(sends).toBe(3)
            expect(peer.requests).toHaveLength(3)
            await f.unchanged()
        } finally {
            await f.close()
        }
    } finally {
        await peer.close()
    }
})

// Only this test-owned container is removed; no broad Docker cleanup or daemon/socket access by the product.
async function broker() {
    const files = await mkdtemp(join(tmpdir(), 'tenonedge-test-send-broker-'))
    const name = 'tenonedge-browser-send-' + randomUUID()
    const docker = (args: string[]) => run('docker', args, { timeout: 30000 })
    try {
        await writeFile(
            join(files, 'mosquitto.conf'),
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
            `${files}:/probe:ro`,
            'eclipse-mosquitto:2.1.2-alpine',
            // Keep the startup window deterministic: docker run returning is
            // not evidence that the broker can accept MQTT connections yet.
            'sh',
            '-c',
            'sleep 1; exec mosquitto -c /probe/mosquitto.conf',
        ])
        const mapping = await docker(['port', name, '1883/tcp'])
        const port = Number(mapping.stdout.trim().split(':').at(-1))
        await expect
            .poll(
                async () => {
                    try {
                        // A real PUBACK on a separate fixture-only topic proves
                        // protocol readiness; a host TCP proxy accepting is insufficient.
                        await docker([
                            'exec',
                            name,
                            'timeout',
                            '2',
                            'mosquitto_pub',
                            '-h',
                            '127.0.0.1',
                            '-t',
                            'fixture/readiness',
                            '-n',
                            '-q',
                            '1',
                        ])
                        return 'ready'
                    } catch (error) {
                        return error instanceof Error
                            ? error.message
                            : String(error)
                    }
                },
                { timeout: 10000 },
            )
            .toBe('ready')
        const subscriber = spawn(
            'docker',
            [
                'exec',
                '-t',
                name,
                'mosquitto_sub',
                '-h',
                '127.0.0.1',
                '-t',
                'test-send/#',
                '-q',
                '1',
                '-d',
            ],
            { stdio: ['ignore', 'pipe', 'pipe'] },
        )
        let output = ''
        subscriber.stdout.on('data', (chunk) => {
            output += chunk.toString()
        })
        subscriber.stderr.on('data', (chunk) => {
            output += chunk.toString()
        })
        const exited = new Promise((resolve) =>
            subscriber.once('close', resolve),
        )
        await expect.poll(() => output, { timeout: 10000 }).toContain('SUBACK')
        return {
            port,
            output: () => output,
            close: async () => {
                try {
                    await docker(['rm', '-f', name])
                    await exited
                } finally {
                    subscriber.kill('SIGTERM')
                    await rm(files, { recursive: true, force: true })
                }
            },
        }
    } catch (error) {
        try {
            await docker(['rm', '-f', name])
        } finally {
            await rm(files, { recursive: true, force: true })
        }
        throw error
    }
}

test('configurator browser sends original MQTT QoS 0 and 1 payloads to real Mosquitto and viewer has no entry', async ({
    browser,
}) => {
    test.setTimeout(120000)
    const peer = await broker()
    try {
        const f = await fixture(browser, peer.port, 'mqtt')
        try {
            await f.post('/users', {
                accountName: 'send-config',
                displayName: 'Configurator',
                role: 'configurator',
                temporaryPassword: password,
                isEnabled: true,
            })
            await f.post('/users', {
                accountName: 'send-viewer',
                displayName: 'Viewer',
                role: 'viewer',
                temporaryPassword: password,
                isEnabled: true,
            })
            for (const name of ['send-config', 'send-viewer']) {
                await f.page
                    .getByRole('button', { name: '退出登录', exact: true })
                    .click()
                await f.page.getByLabel('账号', { exact: true }).fill(name)
                await f.page.getByLabel('密码', { exact: true }).fill(password)
                await f.page
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await f.page
                    .getByLabel('原密码', { exact: true })
                    .fill(password)
                await f.page
                    .getByLabel('新密码', { exact: true })
                    .fill('Own-send-8520!')
                await f.page
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(f.page.getByRole('navigation')).toBeVisible()
                await f.page.goto('/routes')
                await f.page
                    .getByRole('button', {
                        name: '打开 Test send',
                        exact: true,
                    })
                    .click()
                if (name === 'send-viewer') {
                    await expect(
                        f.panel.getByRole('button', {
                            name: '预览当前图（不发送）',
                        }),
                    ).toBeDisabled()
                    await expect(
                        f.page.getByRole('region', {
                            name: '发送预览测试报文',
                            exact: true,
                        }),
                    ).toHaveCount(0)
                    continue
                }
                await f.page
                    .getByLabel('预览数据来源', { exact: true })
                    .selectOption({ index: 1 })
                const panels = await f.preview()
                expect(peer.output()).not.toContain('{"first":')
                for (const qos of [0, 1]) {
                    const panel = panels.nth(qos)
                    await review(panel)
                    await expect(panel).toContainText(`test-send/${qos}`)
                    await confirm(panel)
                    await expect(panel).toContainText(
                        qos === 0
                            ? 'QoS 0 已写出，没有 Broker 发布确认'
                            : '已收到 Broker PUBACK（QoS 1）',
                    )
                    await expect
                        .poll(
                            () =>
                                peer
                                    .output()
                                    .split('\n')
                                    .filter(
                                        (line) =>
                                            line.trim() ===
                                            '{"first":9223372036854775807,"second":9223372036854775807}',
                                    ).length,
                        )
                        .toBe(qos + 1)
                }
                await f.page.screenshot({
                    path: '/tmp/tenonedge-test-send-mqtt.png',
                    fullPage: true,
                })
            }
        } finally {
            await f.close()
        }
    } finally {
        await peer.close()
    }
})
