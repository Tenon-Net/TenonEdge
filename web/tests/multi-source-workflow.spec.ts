import { expect, test, type BrowserContext } from '@playwright/test'
import { createServer as createHttpServer } from 'node:http'
import { createServer as createTcpServer, type Socket } from 'node:net'
import { mkdtemp, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { startOwnedHost } from './support/owned-host'
import { applyRoute } from './support/workflow-ui'

// Functional §5.7: ten devices selected in ONE input node, not ten unrelated routes.
// Device/connection drafts are API fixtures; selection, mapping, saving and applying use the real UI.
// Real TCP/HTTP software peers are not a physical-device compatibility claim.
test('one input selects ten Modbus sources, maps all device identities through one stage and rejects an incompatible source', async ({
    browser,
}, testInfo) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-ten-sources-'))
    const sockets = new Set<Socket>()
    const readUnits: number[] = []
    const protocolErrors: string[] = []
    const received: {
        deviceKey: string
        values: { doubled: number }
        messageId: string
    }[] = []
    let modbusSockets = 0
    let maxModbusSockets = 0
    const track = (socket: Socket) => {
        sockets.add(socket)
        socket.on('error', () => {})
        socket.on('close', () => sockets.delete(socket))
    }
    const modbus = createTcpServer((socket) => {
        track(socket)
        maxModbusSockets = Math.max(maxModbusSockets, ++modbusSockets)
        socket.on('close', () => --modbusSockets)
        let buffered = Buffer.alloc(0)
        socket.on('data', (data) => {
            buffered = Buffer.concat([buffered, data])
            if (buffered.length > 4096) {
                protocolErrors.push('request buffer exceeded')
                socket.destroy()
                return
            }
            while (buffered.length >= 12) {
                const request = buffered.subarray(0, 12)
                buffered = buffered.subarray(12)
                const unit = request[6]
                if (
                    request.readUInt16BE(2) !== 0 ||
                    request.readUInt16BE(4) !== 6 ||
                    request[7] !== 3 ||
                    request.readUInt16BE(8) !== 0 ||
                    request.readUInt16BE(10) !== 1 ||
                    unit < 1 ||
                    unit > 11
                ) {
                    protocolErrors.push('unexpected Modbus request')
                    socket.destroy()
                    return
                }
                readUnits.push(unit)
                const reply = Buffer.alloc(11)
                request.copy(reply, 0, 0, 4)
                reply.writeUInt16BE(5, 4)
                reply[6] = unit
                reply[7] = 3
                reply[8] = 2
                reply.writeUInt16BE(unit * 10, 9)
                socket.write(reply)
            }
        })
    })
    const receiver = createHttpServer(async (request, response) => {
        const parts: Buffer[] = []
        let length = 0
        for await (const part of request) {
            length += part.length
            if (length > 64 * 1024) {
                protocolErrors.push('unexpected output size')
                response.writeHead(413).end()
                return
            }
            parts.push(Buffer.from(part))
        }
        try {
            const record = JSON.parse(Buffer.concat(parts).toString('utf8'))
            if (
                request.method !== 'POST' ||
                request.url !== '/capture' ||
                record.messageId !== request.headers['idempotency-key']
            )
                protocolErrors.push('unexpected HTTP request')
            received.push(record)
            response.writeHead(204).end()
        } catch {
            protocolErrors.push('invalid output JSON')
            response.writeHead(400).end()
        }
    })
    receiver.on('connection', track)
    const listen = (server: typeof modbus | typeof receiver) =>
        new Promise<number>((resolve, reject) => {
            server.once('error', reject)
            server.listen(0, '127.0.0.1', () => {
                const address = server.address()
                if (!address || typeof address === 'string')
                    return reject(new Error('listener-address'))
                resolve(address.port)
            })
        })
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    try {
        const modbusPort = await listen(modbus)
        const httpPort = await listen(receiver)
        const address = new URL(
            process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
        ).hostname
        host = await startOwnedHost(
            data,
            0,
            [
                `--TenonEdge:Outbound:LocalPorts:0=${modbusPort}`,
                `--TenonEdge:Outbound:LocalPorts:1=${httpPort}`,
            ],
            address,
        )
        context = await browser.newContext({
            baseURL: host.url,
            viewport: { width: 1800, height: 1200 },
        })
        const page = await context.newPage()
        page.setDefaultTimeout(15_000)
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('ten-source-admin')
        for (const label of ['密码', '确认密码'])
            await page
                .getByLabel(label, { exact: true })
                .fill('Ten-source-fixture-7438!')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const root = '/api/edge-admin/v1'
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const write = async (path: string, value: object) => {
            const response = await page.request.post(root + path, {
                headers,
                data: value,
            })
            expect(response.status(), await response.text()).toBe(200)
            return response.json()
        }
        const first = await (
            await page.request.get(root + '/configuration/draft')
        ).json()
        const connection = await write('/connections', {
            expectedVersion: first.version,
            key: 'ten-modbus',
            name: 'Ten Modbus devices',
            kind: 'modbus',
            modbus: { host: '127.0.0.1', port: modbusPort },
        })
        const target = await write('/connections', {
            expectedVersion: connection.version,
            key: 'ten-http',
            name: 'Ten receiver',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${httpPort}/` },
        })
        let version = target.version
        const expectedKeys = Array.from(
            { length: 10 },
            (_, index) => `meter-${index + 1}`,
        ).sort()
        for (let unit = 1; unit <= 11; unit++) {
            const device = await write('/devices', {
                expectedVersion: version,
                key: `meter-${unit}`,
                name: `Meter ${unit}`,
                kind: 'modbus',
                connectionId: connection.connection.id,
                unitId: unit,
            })
            const source = await write(`/devices/${device.device.id}/inputs`, {
                expectedVersion: device.version,
                key: 'sample',
                name: 'Sample',
                modbus: {
                    periodMs: 1000,
                    points: [
                        {
                            key: unit === 11 ? 'incompatible' : 'value',
                            functionCode: 3,
                            address: 0,
                            type: 'uint16',
                            enabled: true,
                        },
                    ],
                },
            })
            version = source.version
        }
        expect(readUnits).toEqual([])
        await page.goto('/routes')
        await page
            .getByRole('button', { name: '新建路线', exact: true })
            .click()
        await page
            .getByLabel('路线标识', { exact: true })
            .fill('ten-source-route')
        await page
            .getByLabel('路线名称', { exact: true })
            .fill('Ten source mapping')
        await page
            .getByLabel('参与静态编译（草稿启用）', { exact: true })
            .check()
        const canvas = page.getByLabel('路线画布', { exact: true })
        await canvas.scrollIntoViewIfNeeded()
        const nodes = page.locator('.react-flow__node')
        const add = async (name: string, x: number) => {
            const before = await nodes.count()
            await page
                .getByRole('button', { name, exact: true })
                .dragTo(canvas, { targetPosition: { x, y: 150 } })
            await expect(nodes).toHaveCount(before + 1)
        }
        await add('Modbus 输入', 120)
        for (let unit = 1; unit <= 10; unit++)
            await page
                .getByLabel(`Meter ${unit} / Sample (sample)`, { exact: true })
                .check()
        await expect(
            page.getByText('已选 10 个引用（含不在当前页的引用）', {
                exact: true,
            }),
        ).toBeVisible()
        await add('字段映射', 400)
        const mapping = page.getByRole('form', {
            name: '字段映射规则表',
            exact: true,
        })
        await mapping
            .getByRole('button', { name: '添加映射规则', exact: true })
            .click()
        await mapping.getByLabel('目标字段 1', { exact: true }).fill('doubled')
        await mapping
            .getByLabel('目标类型 1', { exact: true })
            .selectOption('integer')
        await mapping
            .getByLabel('来源字段 / Pointer / 固定值 1', { exact: true })
            .fill('value')
        await mapping.getByLabel('比例 1', { exact: true }).fill('2')
        await mapping
            .getByRole('button', { name: '确认映射规则', exact: true })
            .click()
        await add('HTTP 输出', 680)
        await page
            .getByLabel('Ten receiver (ten-http)', { exact: true })
            .check()
        const output = page.getByRole('form', { name: '输出参数', exact: true })
        await output
            .getByLabel('输出相对路径', { exact: true })
            .fill('/capture')
        await output
            .getByRole('button', { name: '确认输出参数', exact: true })
            .click()
        await expect(nodes).toHaveCount(3)
        for (const index of [0, 1])
            await nodes
                .nth(index)
                .locator('.react-flow__handle.source')
                .dragTo(
                    nodes.nth(index + 1).locator('.react-flow__handle.target'),
                )
        await expect(page.locator('.react-flow__edge')).toHaveCount(2)
        const save = () =>
            page
                .getByRole('button', { name: '保存路线草稿', exact: true })
                .click()
        await save()
        await expect(
            page.getByText('路线草稿已保存，未应用。', { exact: true }),
        ).toBeVisible()
        expect(received).toEqual([])
        await applyRoute(page)
        await expect
            .poll(() =>
                [...new Set(received.map((item) => item.deviceKey))].sort(),
            )
            .toEqual(expectedKeys)
        const verify = () => {
            for (const item of received)
                expect(item.values).toEqual({
                    doubled: Number(item.deviceKey.split('-')[1]) * 20,
                })
            expect([...new Set(readUnits)].sort((a, b) => a - b)).toEqual(
                Array.from({ length: 10 }, (_, i) => i + 1),
            )
            expect(maxModbusSockets).toBe(1)
            expect(protocolErrors).toEqual([])
        }
        verify()
        const active = await (
            await page.request.get(root + '/runtime/status')
        ).json()
        expect(active.configuration.effectiveRevision).toBe(1)
        expect(
            active.runtime.units.filter(
                (unit: { id: { kind: string } }) =>
                    unit.id.kind === 'input.modbus',
            ),
        ).toHaveLength(10)
        await page.goto('/routes')
        await page
            .getByRole('button', {
                name: '打开 Ten source mapping',
                exact: true,
            })
            .click()
        await page
            .locator('.react-flow__node')
            .filter({ hasText: 'Modbus 输入' })
            .click()
        for (let unit = 1; unit <= 10; unit++)
            await expect(
                page.getByLabel(`Meter ${unit} / Sample (sample)`, {
                    exact: true,
                }),
            ).toBeChecked()
        await page
            .getByLabel('Meter 11 / Sample (sample)', { exact: true })
            .check()
        await save()
        await expect(
            page.getByText('路线草稿已保存，未应用。', { exact: true }),
        ).toBeVisible()
        await page
            .getByRole('button', {
                name: '静态编译已保存的启用路线',
                exact: true,
            })
            .click()
        await expect(
            page.getByText(/source.schemaMismatch/).first(),
        ).toBeVisible()
        await page.goto('/configuration')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await expect(
            page.getByText(/source.schemaMismatch/).first(),
        ).toBeVisible()
        await expect(
            page.getByLabel('我已检查变更及影响，确认应用这份草稿', {
                exact: true,
            }),
        ).toBeDisabled()
        await expect(
            page.getByRole('button', { name: '确认应用配置', exact: true }),
        ).toBeDisabled()
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .configuration.effectiveRevision,
        ).toBe(1)
        const before = new Map(
            expectedKeys.map((key) => [
                key,
                received.filter((item) => item.deviceKey === key).length,
            ]),
        )
        await expect
            .poll(() =>
                expectedKeys.every(
                    (key) =>
                        received.filter((item) => item.deviceKey === key)
                            .length > before.get(key)!,
                ),
            )
            .toBe(true)
        verify()
        await testInfo.attach('ten-source-evidence', {
            body: Buffer.from(
                JSON.stringify({
                    expectedKeys,
                    received: received.length,
                    readUnits: [...new Set(readUnits)],
                    maxModbusSockets,
                    browser: await page.evaluate(() => navigator.userAgent),
                }),
            ),
            contentType: 'application/json',
        })
    } finally {
        try {
            await context?.close()
        } finally {
            try {
                if (host) expect(await host.stop()).toBe(0)
            } finally {
                for (const socket of sockets) socket.destroy()
                await Promise.all(
                    [modbus, receiver].map(
                        (server) =>
                            new Promise<void>((resolve) =>
                                server.close(() => resolve()),
                            ),
                    ),
                )
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
