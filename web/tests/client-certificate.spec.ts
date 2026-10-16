import {
    expect,
    test,
    type Browser,
    type Locator,
    type Page,
} from '@playwright/test'
import { createServer as createHttpsServer } from 'node:https'
import { createServer as createTlsServer } from 'node:tls'
import type { Socket } from 'node:net'
import { X509Certificate } from 'node:crypto'
import { execFile } from 'node:child_process'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { promisify } from 'node:util'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const run = promisify(execFile)
const password = 'Client-cert-browser-7419!'
const httpAddress = new URL(
    process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1:18082',
).hostname
const credentialPanel = (page: Page) =>
    page.getByRole('region', { name: '连接认证凭证', exact: true })
const reviewPanel = (page: Page) =>
    page.getByRole('region', { name: '确认客户端证书身份', exact: true })

async function fixture(browser: Browser, mqtt = false) {
    const data = await mkdtemp(
        join(tmpdir(), 'tenonedge-browser-client-certificate-'),
    )
    const openssl = (args: string[]) => run('openssl', args, { cwd: data })
    await openssl([
        'req',
        '-x509',
        '-newkey',
        'ec',
        '-pkeyopt',
        'ec_paramgen_curve:prime256v1',
        '-nodes',
        '-sha256',
        '-days',
        '2',
        '-subj',
        '/CN=Browser Client Root',
        '-keyout',
        'root.key',
        '-out',
        'root.pem',
        '-addext',
        'basicConstraints=critical,CA:TRUE',
        '-addext',
        'keyUsage=critical,keyCertSign,cRLSign',
    ])
    async function leaf(name: string, client = false) {
        await openssl([
            'req',
            '-new',
            '-newkey',
            'ec',
            '-pkeyopt',
            'ec_paramgen_curve:prime256v1',
            '-nodes',
            '-subj',
            `/CN=${client ? name : 'localhost'}`,
            '-keyout',
            `${name}.key`,
            '-out',
            `${name}.csr`,
            '-addext',
            `extendedKeyUsage=${client ? 'clientAuth' : 'serverAuth'}`,
            '-addext',
            'basicConstraints=critical,CA:FALSE',
            '-addext',
            'subjectAltName=DNS:localhost',
        ])
        await openssl([
            'x509',
            '-req',
            '-in',
            `${name}.csr`,
            '-CA',
            'root.pem',
            '-CAkey',
            'root.key',
            '-CAcreateserial',
            '-days',
            '1',
            '-copy_extensions',
            'copy',
            '-out',
            `${name}.pem`,
        ])
        return {
            key: await readFile(join(data, `${name}.key`)),
            cert: await readFile(join(data, `${name}.pem`)),
        }
    }
    const serverMaterial = await leaf('server')
    const clientMaterial = await leaf('browser-client', true)
    await openssl([
        'pkcs12',
        '-export',
        '-in',
        'browser-client.pem',
        '-inkey',
        'browser-client.key',
        '-certfile',
        'root.pem',
        '-passout',
        'pass:fixture-pfx-password',
        '-out',
        'client.pfx',
    ])
    const pfx = await readFile(join(data, 'client.pfx'))
    const ca = await readFile(join(data, 'root.pem'))
    const options = {
        ...serverMaterial,
        ca,
        requestCert: true,
        rejectUnauthorized: true,
    }
    let statusCode = 200
    const received: {
        subject: string | string[] | undefined
        passwordSent: boolean
    }[] = []
    // Actual TLS + minimal MQTT 3.1.1 CONNACK peer. Broker behavior is tested in backend Mosquitto tests.
    const server = mqtt
        ? createTlsServer(options, (socket) => {
              socket.on('error', () => {})
              let buffer = Buffer.alloc(0)
              socket.on('data', (chunk) => {
                  buffer = Buffer.concat([buffer, chunk])
                  if (buffer[0] !== 0x10) return
                  let length = 0,
                      multiplier = 1,
                      i = 1
                  let complete = false
                  while (i < buffer.length && i <= 4) {
                      const value = buffer[i++]
                      length += (value & 127) * multiplier
                      multiplier *= 128
                      if (!(value & 128)) {
                          complete = true
                          break
                      }
                  }
                  if (!complete || buffer.length < i + length) return
                  received.push({
                      subject: socket.getPeerCertificate().subject?.CN,
                      passwordSent: (buffer[i + 7] & 0xc0) !== 0,
                  })
                  socket.write(Buffer.from([0x20, 2, 0, 0]))
                  buffer = Buffer.alloc(0)
              })
          })
        : createHttpsServer(options, (request, response) => {
              received.push({
                  subject:
                      'getPeerCertificate' in request.socket
                          ? (
                                request.socket as import('node:tls').TLSSocket
                            ).getPeerCertificate().subject?.CN
                          : undefined,
                  passwordSent: !!request.headers.authorization,
              })
              response.writeHead(statusCode, { 'content-length': 0 })
              response.end()
          })
    server.on('tlsClientError', () => {})
    const sockets = new Set<Socket>()
    server.on('connection', (socket) => {
        sockets.add(socket)
        socket.once('close', () => sockets.delete(socket))
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('missing-port')
    const extra = [`--TenonEdge:Outbound:LocalPorts:0=${address.port}`]
    let host = await startOwnedHost(data, 0, extra, httpAddress)
    const context = await browser.newContext({ baseURL: host.url })
    const close = async () => {
        try {
            try {
                await context.close()
            } finally {
                await host.stop()
            }
        } finally {
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve) => server.close(() => resolve()))
            await rm(data, { recursive: true, force: true })
        }
    }
    try {
        const page = await context.newPage()
        page.setDefaultTimeout(15_000)
        await page.goto('/')
        if (!['localhost', '127.0.0.1', '[::1]'].includes(httpAddress))
            expect(await page.evaluate(() => window.isSecureContext)).toBe(
                false,
            )
        await page.getByLabel('账号', { exact: true }).fill('client-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '创建管理员并登录' }).click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const headers = {
            'X-CSRF-Token': (
                await (await page.request.get(root + '/auth/session')).json()
            ).csrfToken,
        }
        const recipient = mqtt
            ? `mqtts://localhost:${address.port}`
            : `https://localhost:${address.port}/`
        const created = await page.request.post(root + '/connections', {
            headers,
            data: {
                expectedVersion: '0',
                key: 'client-target',
                name: 'Client TLS target',
                kind: mqtt ? 'mqtt' : 'http',
                ...(mqtt
                    ? {
                          mqtt: {
                              host: 'localhost',
                              port: address.port,
                              tls: true,
                          },
                      }
                    : { http: { baseAddress: recipient } }),
            },
        })
        expect(created.status(), await created.text()).toBe(200)
        const draft = await created.json()
        const connection = draft.connection
        const cert = new X509Certificate(ca)
        expect(
            (
                await page.request.put(
                    root + `/connections/${connection.id}/tls-trust`,
                    {
                        headers,
                        data: {
                            expectedDraftVersion: draft.version,
                            recipient,
                            mode: 'certificateAuthority',
                            certificateDerBase64: cert.raw.toString('base64'),
                            confirmedFingerprint:
                                cert.fingerprint256.replaceAll(':', ''),
                            confirmed: true,
                        },
                    },
                )
            ).status(),
        ).toBe(200)
        async function open(target = page) {
            await target.goto(host.url + '/connections')
            await target
                .getByRole('row')
                .filter({ hasText: 'Client TLS target' })
                .getByRole('button', { name: '编辑连接', exact: true })
                .click()
        }
        await open()
        async function upload(panel: Locator, format: 'pkcs12' | 'pem') {
            await panel
                .getByRole('combobox', { name: '材料格式', exact: true })
                .selectOption(format)
            if (format === 'pkcs12') {
                await panel
                    .getByLabel('PKCS#12 文件（最多 64 KiB）', { exact: true })
                    .setInputFiles({
                        name: 'client.pfx',
                        mimeType: 'application/x-pkcs12',
                        buffer: pfx,
                    })
                await expect(
                    panel.getByText('已载入（不回显）', { exact: true }),
                ).toHaveCount(1)
                await panel
                    .getByLabel('PKCS#12 容器口令（选填）', { exact: true })
                    .fill('fixture-pfx-password')
            } else {
                await panel
                    .getByLabel('PEM 证书链文件（最多 96 KiB，叶证书在前）', {
                        exact: true,
                    })
                    .setInputFiles({
                        name: 'client.pem',
                        mimeType: 'application/x-pem-file',
                        buffer: Buffer.concat([clientMaterial.cert, ca]),
                    })
                await expect(
                    panel.getByText('已载入（不回显）', { exact: true }),
                ).toHaveCount(1)
                await panel
                    .getByLabel('未加密 PEM 私钥文件（最多 16 KiB）', {
                        exact: true,
                    })
                    .setInputFiles({
                        name: 'client.key',
                        mimeType: 'application/x-pem-file',
                        buffer: clientMaterial.key,
                    })
                await expect(
                    panel.getByText('已载入（不回显）', { exact: true }),
                ).toHaveCount(2)
            }
        }
        return {
            page,
            context,
            connection,
            draft,
            recipient,
            headers,
            upload,
            open,
            received,
            setStatus: (value: number) => {
                statusCode = value
            },
            close,
            pfx,
            clientMaterial,
            endpoint:
                root +
                `/connections/${connection.id}/${mqtt ? 'mqtt' : 'http'}-credential`,
            reviewEndpoint:
                root +
                `/connections/${connection.id}/client-certificate-review`,
            url: host.url,
            restart: async () => {
                const port = Number(new URL(host.url).port)
                expect(await host.stop()).toBe(0)
                host = await startOwnedHost(data, port, extra, httpAddress)
                await open()
            },
        }
    } catch (error) {
        await close()
        throw error
    }
}

async function confirm(page: Page) {
    const panel = reviewPanel(page)
    await expect(
        panel.getByRole('button', { name: '确认保存客户端证书' }),
    ).toBeDisabled()
    await panel.getByRole('checkbox').check()
    await panel.getByRole('button', { name: '确认保存客户端证书' }).click()
}

for (const mqtt of [false, true])
    for (const format of ['pkcs12', 'pem'] as const) {
        test(`${mqtt ? 'MQTT' : 'HTTP'} ${format}: transient mutual TLS, identity review, save and persisted authentication after restart`, async ({
            browser,
        }) => {
            test.setTimeout(120_000)
            const f = await fixture(browser, mqtt)
            try {
                const { page } = f
                const transient = page.getByRole('region', {
                    name: mqtt ? 'MQTT 真实连接测试' : 'HTTP 真实连接测试',
                    exact: true,
                })
                await transient
                    .getByRole('combobox')
                    .first()
                    .selectOption('provided')
                await transient
                    .getByRole('combobox')
                    .nth(1)
                    .selectOption('clientCertificate')
                await f.upload(transient, format)
                await transient
                    .getByRole('button', {
                        name: mqtt ? '测试 MQTT 连接' : '测试 HTTP 连接',
                        exact: true,
                    })
                    .click()
                await expect.poll(() => f.received.length).toBe(1)
                await expect(
                    transient.getByText('已载入（不回显）', { exact: true }),
                ).toHaveCount(0)
                expect((await page.request.get(f.endpoint)).status()).toBe(404)
                const panel = credentialPanel(page)
                await panel
                    .getByRole('combobox', { name: '新认证方式', exact: true })
                    .selectOption('clientCertificate')
                await f.upload(panel, format)
                await panel
                    .getByRole('button', { name: '核对客户端证书（不保存）' })
                    .click()
                await expect(reviewPanel(page)).toContainText(
                    'CN=browser-client',
                )
                await expect(reviewPanel(page)).toContainText(
                    new X509Certificate(
                        f.clientMaterial.cert,
                    ).fingerprint256.replaceAll(':', ''),
                )
                expect((await page.request.get(f.endpoint)).status()).toBe(404)
                await expect(reviewPanel(page)).not.toContainText('PRIVATE KEY')
                await confirm(page)
                await expect(
                    panel.getByText('已设置', { exact: true }),
                ).toBeVisible()
                await expect(
                    panel.getByText('已载入（不回显）', { exact: true }),
                ).toHaveCount(0)
                const stored = await (await page.request.get(f.endpoint)).json()
                expect(JSON.stringify(stored)).not.toMatch(
                    /PRIVATE KEY|fixture-pfx-password|pkcs12/,
                )
                expect(f.received).toEqual([
                    { subject: 'browser-client', passwordSent: false },
                ])
                expect(
                    await page.evaluate(() =>
                        JSON.stringify({ ...localStorage, ...sessionStorage }),
                    ),
                ).not.toMatch(/PRIVATE KEY|fixture-pfx-password|pkcs12/)
                await f.restart()
                await transient
                    .getByRole('combobox')
                    .first()
                    .selectOption('stored')
                await transient
                    .getByRole('button', {
                        name: mqtt ? '测试 MQTT 连接' : '测试 HTTP 连接',
                        exact: true,
                    })
                    .click()
                await expect.poll(() => f.received.length).toBe(2)
                expect(f.received[1]).toEqual({
                    subject: 'browser-client',
                    passwordSent: false,
                })
                expect(
                    (await (await page.request.get(f.endpoint)).json()).version,
                ).toBe(stored.version)
            } finally {
                await f.close()
            }
        })
    }

test('certificate cancellation, strict file bounds, actual Kestrel upload limit and lost write response never replay', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const f = await fixture(browser)
    try {
        const { page } = f
        const panel = credentialPanel(page)
        await panel
            .getByRole('combobox', { name: '新认证方式', exact: true })
            .selectOption('clientCertificate')
        await panel
            .getByLabel('PKCS#12 文件（最多 64 KiB）', { exact: true })
            .setInputFiles({
                name: 'large.pfx',
                mimeType: 'application/x-pkcs12',
                buffer: Buffer.alloc(64 * 1024 + 1),
            })
        await expect(panel.getByRole('alert')).toContainText('文件须非空')
        await expect(
            panel.getByRole('button', { name: '核对客户端证书（不保存）' }),
        ).toBeDisabled()
        const oversized = await page.request.post(f.reviewEndpoint, {
            headers: f.headers,
            data: {
                kind: 'http',
                recipient: f.recipient,
                expectedDraftVersion: f.draft.version,
                expectedVersion: null,
                certificate: { pkcs12: 'A'.repeat(300_000) },
            },
        })
        expect(oversized.status()).toBe(413)
        await f.upload(panel, 'pem')
        await panel
            .getByRole('button', { name: '核对客户端证书（不保存）' })
            .click()
        await reviewPanel(page)
            .getByRole('button', { name: '取消核对并清除材料' })
            .click()
        await expect(
            panel.getByText('已载入（不回显）', { exact: true }),
        ).toHaveCount(0)
        expect((await page.request.get(f.endpoint)).status()).toBe(404)
        await f.upload(panel, 'pkcs12')
        await panel
            .getByRole('button', { name: '核对客户端证书（不保存）' })
            .click()
        expect(
            (
                await page.request.put(f.endpoint, {
                    headers: f.headers,
                    data: {
                        recipient: f.recipient,
                        expectedDraftVersion: f.draft.version,
                        kind: 'basic',
                        username: 'another-account',
                        secret: 'fixture-secret',
                    },
                })
            ).status(),
        ).toBe(200)
        await confirm(page)
        await expect(panel.getByRole('status')).toContainText('不会自动重试')
        await expect(
            panel.getByText('已载入（不回显）', { exact: true }),
        ).toHaveCount(0)
        await panel
            .getByRole('button', { name: '重新读取凭证状态', exact: true })
            .click()
        await f.upload(panel, 'pkcs12')
        await panel
            .getByRole('button', { name: '核对客户端证书（不保存）' })
            .click()
        await expect(reviewPanel(page)).toContainText('another-account')
        let writes = 0
        await page.route('**' + f.endpoint, async (route) => {
            if (route.request().method() !== 'PUT') {
                await route.continue()
                return
            }
            writes++
            const response = await route.fetch()
            expect(response.status(), await response.text()).toBe(200)
            await response.dispose()
            await route.abort('failed')
        })
        await confirm(page)
        await expect(panel.getByRole('status')).toContainText('不会自动重试')
        await expect(
            panel.getByText('已载入（不回显）', { exact: true }),
        ).toHaveCount(0)
        await expect(
            panel.getByRole('button', { name: '核对客户端证书（不保存）' }),
        ).toBeDisabled()
        await panel
            .getByRole('button', { name: '重新读取凭证状态', exact: true })
            .click()
        await expect(panel.getByText('已设置', { exact: true })).toBeVisible()
        await f.open()
        expect(writes).toBe(1)
        expect(f.received).toHaveLength(0)
    } finally {
        await f.close()
    }
})

test('configurator can review and save client identity; viewer cannot review or upload', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const f = await fixture(browser, true)
    try {
        for (const role of ['configurator', 'viewer']) {
            const accountName = 'client-' + role
            expect(
                (
                    await f.page.request.post(root + '/users', {
                        headers: f.headers,
                        data: {
                            accountName,
                            role,
                            temporaryPassword: password,
                            isEnabled: true,
                        },
                    })
                ).status(),
            ).toBe(200)
            const context = await browser.newContext({ baseURL: f.url })
            try {
                const page = await context.newPage()
                page.setDefaultTimeout(15_000)
                await page.goto('/connections')
                await page.getByLabel('账号', { exact: true }).fill(accountName)
                await page.getByLabel('密码', { exact: true }).fill(password)
                await page
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await page.getByLabel('原密码', { exact: true }).fill(password)
                await page
                    .getByLabel('新密码', { exact: true })
                    .fill('Client-changed-8361!')
                await page
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    page.getByLabel('原密码', { exact: true }),
                ).toHaveCount(0)
                if (role === 'configurator') {
                    await f.open(page)
                    const panel = credentialPanel(page)
                    await panel
                        .getByRole('combobox', {
                            name: '新认证方式',
                            exact: true,
                        })
                        .selectOption('clientCertificate')
                    await f.upload(panel, 'pem')
                    await panel
                        .getByRole('button', {
                            name: '核对客户端证书（不保存）',
                        })
                        .click()
                    await confirm(page)
                    await expect(
                        panel.getByText('已设置', { exact: true }),
                    ).toBeVisible()
                } else {
                    await page.goto('/connections')
                    await page
                        .getByRole('button', { name: '查看连接', exact: true })
                        .click()
                    await expect(
                        page.getByRole('button', {
                            name: '核对客户端证书（不保存）',
                        }),
                    ).toHaveCount(0)
                    const headers = {
                        'X-CSRF-Token': (
                            await (
                                await page.request.get(root + '/auth/session')
                            ).json()
                        ).csrfToken,
                    }
                    expect(
                        (
                            await page.request.post(f.reviewEndpoint, {
                                headers,
                                data: {
                                    kind: 'mqtt',
                                    recipient: f.recipient,
                                    expectedDraftVersion: f.draft.version,
                                    expectedVersion: null,
                                    certificate: {
                                        pkcs12: f.pfx.toString('base64'),
                                        password: 'fixture-pfx-password',
                                    },
                                },
                            })
                        ).status(),
                    ).toBe(403)
                }
            } finally {
                await context.close()
            }
        }
    } finally {
        await f.close()
    }
})

test('same-subject certificate with real pending HTTPS output requires an explicit account declaration', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const f = await fixture(browser)
    try {
        const { page, headers } = f
        let panel = credentialPanel(page)
        await panel
            .getByRole('combobox', { name: '新认证方式', exact: true })
            .selectOption('clientCertificate')
        await f.upload(panel, 'pem')
        await panel
            .getByRole('button', { name: '核对客户端证书（不保存）' })
            .click()
        await confirm(page)
        await expect(panel.getByText('已设置', { exact: true })).toBeVisible()
        async function post(path: string, data: unknown) {
            const response = await page.request.post(root + path, {
                headers,
                data,
            })
            expect(response.status(), await response.text()).toBe(200)
            return response.json()
        }
        const device = await post('/devices', {
            expectedVersion: f.draft.version,
            key: 'source',
            name: 'Source',
            kind: 'simulator',
        })
        const source = await post(`/devices/${device.device.id}/inputs`, {
            expectedVersion: device.version,
            key: 'sample',
            name: 'Sample',
            simulator: { periodMs: 1000, sampleJson: '{"value":42}' },
        })
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'certificate-queue',
            name: 'Certificate queue',
            enabled: true,
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
                    {
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: {
                            connectionId: f.connection.id,
                            path: '/capture',
                        },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: { nodes: {} },
            },
        })
        f.setStatus(503)
        const checked = await post('/configuration/review', {
            expectedVersion: route.version,
        })
        const apply = await page.request.post(root + '/configuration/apply', {
            headers,
            data: {
                expectedVersion: route.version,
                expectedEffectiveRevision: checked.effectiveRevision,
                expectedPlanHash: checked.planHash,
                confirmed: true,
            },
        })
        expect(apply.status(), await apply.text()).toBe(202)
        const operation = await apply.json()
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                root +
                                    '/configuration/operations/' +
                                    operation.id,
                            )
                        ).json()
                    ).stage,
            )
            .toBe('completed')
        await expect.poll(() => f.received.length).toBeGreaterThan(0)
        await f.open()
        panel = credentialPanel(page)
        await panel
            .getByRole('combobox', { name: '新认证方式', exact: true })
            .selectOption('clientCertificate')
        await f.upload(panel, 'pem')
        await panel
            .getByRole('button', { name: '核对客户端证书（不保存）' })
            .click()
        const review = reviewPanel(page)
        await expect(review).toContainText(/核对时待发 [1-9][0-9]* 条/)
        const account = review.getByRole('combobox')
        await expect(account).toHaveValue('')
        await review.getByRole('checkbox').check()
        await expect(
            review.getByRole('button', { name: '确认保存客户端证书' }),
        ).toBeDisabled()
        await account.selectOption('same')
        const old = (await (await page.request.get(f.endpoint)).json()).version
        await review.getByRole('button', { name: '确认保存客户端证书' }).click()
        await expect(panel.getByRole('status')).toContainText(
            '客户端证书已保存',
        )
        expect(
            (await (await page.request.get(f.endpoint)).json()).version,
        ).not.toBe(old)
        f.setStatus(200)
        const count = f.received.length
        await expect
            .poll(() => f.received.length, { timeout: 15_000 })
            .toBeGreaterThan(count)
        expect(
            f.received.every(
                (item) =>
                    item.subject === 'browser-client' && !item.passwordSent,
            ),
        ).toBe(true)
        const quarantined = await page.request.get(
            root +
                `/deliveries?state=quarantined&connectionId=${f.connection.id}`,
        )
        expect(quarantined.status()).toBe(200)
        expect((await quarantined.json()).items).toHaveLength(0)
    } finally {
        await f.close()
    }
})
