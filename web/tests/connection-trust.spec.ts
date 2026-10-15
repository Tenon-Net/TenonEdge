import { expect, test, type Browser, type Page } from '@playwright/test'
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
const password = 'Trust-browser-7419!'
const panel = (page: Page) =>
    page.getByRole('region', { name: 'TLS 服务器信任', exact: true })
const confirmation = (page: Page) =>
    page.getByRole('region', { name: '确认更改连接信任', exact: true })

async function fixture(browser: Browser, mqtt = false) {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-trust-'))
    async function openssl(args: string[]) {
        await run('openssl', args, { cwd: data })
    }
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
        '/CN=Browser Trust Root',
        '-keyout',
        'root.key',
        '-out',
        'root.pem',
        '-addext',
        'basicConstraints=critical,CA:TRUE',
        '-addext',
        'keyUsage=critical,keyCertSign,cRLSign',
    ])
    async function leaf(name: string, serial: string) {
        await openssl([
            'req',
            '-new',
            '-newkey',
            'ec',
            '-pkeyopt',
            'ec_paramgen_curve:prime256v1',
            '-nodes',
            '-subj',
            '/CN=localhost',
            '-keyout',
            `${name}.key`,
            '-out',
            `${name}.csr`,
            '-addext',
            'subjectAltName=DNS:localhost',
            '-addext',
            'extendedKeyUsage=serverAuth',
            '-addext',
            'basicConstraints=critical,CA:FALSE',
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
            '-set_serial',
            serial,
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
    const first = await leaf('first', '1')
    const second = await leaf('second', '2')
    const ca = await readFile(join(data, 'root.pem'))
    let received = 0
    // A minimal TLS peer for CONNACK, not a Broker compatibility claim.
    const server = mqtt
        ? createTlsServer(first, (socket) => {
              socket.on('error', () => {})
              socket.once('data', (chunk) => {
                  if (chunk[0] !== 0x10) {
                      socket.destroy()
                      return
                  }
                  received++
                  socket.write(Buffer.from([0x20, 2, 0, 0]))
              })
          })
        : createHttpsServer(first, (_request, response) => {
              received++
              response.writeHead(200, { 'content-length': 0 })
              response.end()
          })
    const sockets = new Set<Socket>()
    server.on('connection', (socket) => {
        sockets.add(socket)
        socket.once('close', () => sockets.delete(socket))
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string')
        throw new Error('missing-test-port')
    const extra = [`--TenonEdge:Outbound:LocalPorts:0=${address.port}`]
    let host = await startOwnedHost(data, 0, extra)
    const context = await browser.newContext({ baseURL: host.url })
    const close = async () => {
        try {
            await context.close()
            await host.stop()
        } finally {
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve) => server.close(() => resolve()))
            await rm(data, { recursive: true, force: true })
        }
    }
    try {
        const page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('trust-admin')
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
        const created = await page.request.post(root + '/connections', {
            headers,
            data: {
                expectedVersion: '0',
                key: 'trust-target',
                name: 'TLS target',
                kind: mqtt ? 'mqtt' : 'http',
                ...(mqtt
                    ? {
                          mqtt: {
                              host: 'localhost',
                              port: address.port,
                              tls: true,
                          },
                      }
                    : {
                          http: {
                              baseAddress: `https://localhost:${address.port}/`,
                          },
                      }),
            },
        })
        expect(created.status(), await created.text()).toBe(200)
        const connection = (await created.json()).connection
        async function open(target = page) {
            await target.goto(host.url + '/connections')
            await target
                .getByRole('row')
                .filter({ hasText: 'TLS target' })
                .getByRole('button', { name: '编辑连接', exact: true })
                .click()
        }
        await open()

        return {
            page,
            context,
            ca,
            first,
            second,
            server,
            headers,
            connection,
            open,
            close,
            url: host.url,
            received: () => received,
            restart: async () => {
                const port = Number(new URL(host.url).port)
                await host.stop()
                host = await startOwnedHost(data, port, extra)
                await open()
            },
        }
    } catch (error) {
        await close()
        throw error
    }
}

async function confirm(page: Page, clear = false) {
    const region = confirmation(page)
    await expect(
        region.getByRole('button', { name: '确认执行信任变更' }),
    ).toBeDisabled()
    await region.getByRole('checkbox').check()
    await region.getByRole('button', { name: '确认执行信任变更' }).click()
    await expect(panel(page).getByRole('status')).toContainText(
        clear ? '连接信任已清除' : '信任已更新',
    )
}

for (const mqtt of [false, true])
    test(`${mqtt ? 'MQTT' : 'HTTP'} trust pins explicit certificate, rejects rotation, accepts CA renewal and survives Host restart`, async ({
        browser,
    }) => {
        test.setTimeout(120_000)
        const f = await fixture(browser, mqtt)
        try {
            const { page } = f
            const probe = () =>
                page
                    .getByRole('button', {
                        name: mqtt ? '测试 MQTT 连接' : '测试 HTTP 连接',
                        exact: true,
                    })
                    .click()
            const endpoint = `${root}/connections/${f.connection.id}/tls-trust`
            await probe()
            await expect(panel(page)).toContainText(
                new X509Certificate(f.first.cert).fingerprint256.replaceAll(
                    ':',
                    '',
                ),
            )
            expect(f.received()).toBe(0)
            await panel(page)
                .getByRole('button', { name: '核对并信任本次服务器证书' })
                .click()
            await confirmation(page)
                .getByRole('button', { name: '取消本次确认' })
                .click()
            expect((await page.request.get(endpoint)).status()).toBe(404)
            await panel(page)
                .getByRole('button', { name: '核对并信任本次服务器证书' })
                .click()
            await confirm(page)
            await probe()
            await expect.poll(f.received).toBe(1)
            f.server.setSecureContext(f.second)
            await probe()
            await expect(
                page
                    .getByRole('status')
                    .filter({ hasText: '服务器证书已变更' }),
            ).toBeVisible()
            expect(f.received()).toBe(1)
            await panel(page)
                .getByRole('button', { name: '重新读取信任状态' })
                .click()
            await panel(page).getByLabel('CA 公钥证书文件').setInputFiles({
                name: 'root.pem',
                mimeType: 'application/x-pem-file',
                buffer: f.ca,
            })
            await panel(page)
                .getByRole('button', { name: '解析 CA 证书（不保存）' })
                .click()
            await expect(
                panel(page).getByRole('button', { name: '核对并采用此 CA' }),
            ).toBeVisible()
            expect((await (await page.request.get(endpoint)).json()).mode).toBe(
                'pinnedCertificate',
            )
            await panel(page)
                .getByRole('button', { name: '核对并采用此 CA' })
                .click()
            await confirm(page)
            await probe()
            await expect.poll(f.received).toBe(2)
            f.server.setSecureContext(f.first)
            await probe()
            await expect.poll(f.received).toBe(3)
            await f.restart()
            await expect(panel(page)).toContainText('CA 信任')
            await probe()
            await expect.poll(f.received).toBe(4)
            await panel(page)
                .getByRole('button', { name: '准备清除连接信任' })
                .click()
            await confirm(page, true)
            await probe()
            await expect(
                page
                    .getByRole('status')
                    .filter({ hasText: '证书不受信任或证书链不完整' }),
            ).toBeVisible()
            expect(f.received()).toBe(4)
        } finally {
            await f.close()
        }
    })

test('trust upload is bounded, stale drafts conflict, and lost confirmation replies never replay', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const f = await fixture(browser)
    try {
        const { page } = f
        const endpoint = `${root}/connections/${f.connection.id}/tls-trust`
        await page
            .getByRole('button', { name: '测试 HTTP 连接', exact: true })
            .click()
        await expect(
            panel(page).getByRole('button', {
                name: '核对并信任本次服务器证书',
            }),
        ).toBeEnabled()
        await page
            .getByLabel('HTTP 基础地址', { exact: true })
            .fill('https://different.invalid/')
        await expect(
            panel(page).getByRole('button', {
                name: '核对并信任本次服务器证书',
            }),
        ).toBeDisabled()
        await expect(panel(page).getByLabel('CA 公钥证书文件')).toHaveCount(0)
        await page
            .getByLabel('HTTP 基础地址', { exact: true })
            .fill(f.connection.http.baseAddress)
        let inspectRequests = 0
        page.on('request', (request) => {
            if (request.url().endsWith('/tls-trust/inspect')) inspectRequests++
        })
        await panel(page)
            .getByLabel('CA 公钥证书文件')
            .setInputFiles({
                name: 'too-large.der',
                mimeType: 'application/octet-stream',
                buffer: Buffer.alloc(48 * 1024 + 1),
            })
        await panel(page)
            .getByRole('button', { name: '解析 CA 证书（不保存）' })
            .click()
        await expect(panel(page).getByRole('alert')).toContainText(
            '文件须为非空',
        )
        expect(inspectRequests).toBe(0)
        await panel(page).getByLabel('CA 公钥证书文件').setInputFiles({
            name: 'private.pem',
            mimeType: 'application/x-pem-file',
            buffer: f.first.key,
        })
        await panel(page)
            .getByRole('button', { name: '解析 CA 证书（不保存）' })
            .click()
        await expect(panel(page).getByRole('alert')).toContainText(
            '单个完整 PEM',
        )
        expect(inspectRequests).toBe(0)
        const oversized = await page.request.post(
            root + '/connections/tls-trust/inspect',
            {
                headers: f.headers,
                data: { certificateDerBase64: 'A'.repeat(64 * 1024) },
            },
        )
        expect(oversized.status()).toBe(413)
        await panel(page)
            .getByLabel('CA 公钥证书文件')
            .setInputFiles({
                name: 'root.der',
                mimeType: 'application/pkix-cert',
                buffer: new X509Certificate(f.ca).raw,
            })
        await panel(page)
            .getByRole('button', { name: '解析 CA 证书（不保存）' })
            .click()
        await panel(page)
            .getByRole('button', { name: '核对并采用此 CA' })
            .click()
        const drafts = await (
            await page.request.get(root + '/connections')
        ).json()
        expect(
            (
                await page.request.put(
                    root + `/connections/${f.connection.id}`,
                    {
                        headers: f.headers,
                        data: {
                            key: f.connection.key,
                            kind: f.connection.kind,
                            enabled: f.connection.enabled,
                            http: f.connection.http,
                            expectedVersion: drafts.version,
                            name: 'TLS target changed',
                        },
                    },
                )
            ).status(),
        ).toBe(200)
        await confirmation(page).getByRole('checkbox').check()
        await confirmation(page)
            .getByRole('button', { name: '确认执行信任变更' })
            .click()
        await expect(panel(page).getByRole('status')).toContainText(
            '不会自动重试',
        )
        expect((await page.request.get(endpoint)).status()).toBe(404)
        await f.open()
        await page
            .getByRole('button', { name: '测试 HTTP 连接', exact: true })
            .click()
        await panel(page)
            .getByRole('button', { name: '核对并信任本次服务器证书' })
            .click()
        let writes = 0
        await page.route('**' + endpoint, async (route) => {
            if (route.request().method() !== 'PUT') {
                await route.continue()
                return
            }
            writes++
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await response.dispose()
            await route.abort('failed')
        })
        await confirmation(page).getByRole('checkbox').check()
        await confirmation(page)
            .getByRole('button', { name: '确认执行信任变更' })
            .click()
        await expect(panel(page).getByRole('status')).toContainText(
            '不会自动重试',
        )
        await panel(page)
            .getByRole('button', { name: '重新读取信任状态' })
            .click()
        await expect(panel(page)).toContainText('固定服务器证书')
        await page.reload()
        await f.open()
        await expect(panel(page)).toContainText('固定服务器证书')
        expect(writes).toBe(1)
    } finally {
        await f.close()
    }
})

test('configurator sees public certificate but only administrators can manage trust; viewer has no test entry', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const f = await fixture(browser)
    try {
        for (const role of ['configurator', 'viewer']) {
            const accountName = 'trust-' + role
            const created = await f.page.request.post(root + '/users', {
                headers: f.headers,
                data: {
                    accountName,
                    role,
                    temporaryPassword: password,
                    isEnabled: true,
                },
            })
            expect(created.status(), await created.text()).toBe(200)
            const context = await browser.newContext({ baseURL: f.url })
            try {
                const page = await context.newPage()
                await page.goto('/connections')
                await page.getByLabel('账号', { exact: true }).fill(accountName)
                await page.getByLabel('密码', { exact: true }).fill(password)
                await page
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await page.getByLabel('原密码', { exact: true }).fill(password)
                await page
                    .getByLabel('新密码', { exact: true })
                    .fill('Trust-changed-5371!')
                await page
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    page.getByLabel('原密码', { exact: true }),
                ).toHaveCount(0)
                await page.goto('/connections')
                await page
                    .getByRole('row')
                    .filter({ hasText: 'TLS target' })
                    .getByRole('button', {
                        name: role === 'viewer' ? '查看连接' : '编辑连接',
                        exact: true,
                    })
                    .click()
                if (role === 'configurator') {
                    await page
                        .getByRole('button', {
                            name: '测试 HTTP 连接',
                            exact: true,
                        })
                        .click()
                    await expect(panel(page)).toContainText(
                        new X509Certificate(
                            f.first.cert,
                        ).fingerprint256.replaceAll(':', ''),
                    )
                    await expect(panel(page)).toContainText(
                        '只有管理员可以更改信任',
                    )
                    await expect(
                        panel(page).getByRole('button', {
                            name: '核对并信任本次服务器证书',
                        }),
                    ).toHaveCount(0)
                    await expect(
                        panel(page).getByLabel('CA 公钥证书文件'),
                    ).toHaveCount(0)
                } else {
                    await expect(panel(page)).toHaveCount(0)
                    await expect(
                        page.getByRole('button', {
                            name: '测试 HTTP 连接',
                            exact: true,
                        }),
                    ).toHaveCount(0)
                }
            } finally {
                await context.close()
            }
        }
    } finally {
        await f.close()
    }
})
