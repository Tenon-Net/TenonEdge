import { expect, test, type Browser } from '@playwright/test'
import { createServer } from 'node:http'
import type { Socket } from 'node:net'
import { mkdtemp, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Credential-review-browser-8361!'
const httpAddress = new URL(
    process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1:18082',
).hostname
async function fixture(browser: Browser) {
    let status = 503
    const received: {
        authorization: string | undefined
        key: string | undefined
        url: string
        body: string
        status: number
    }[] = []
    const sockets = new Set<Socket>()
    const server = createServer((request, response) => {
        let body = ''
        request.on('data', (data) => {
            body += data.toString()
        })
        request.on('end', () => {
            received.push({
                authorization: request.headers.authorization,
                key: request.headers['x-key'] as string | undefined,
                url: request.url ?? '',
                body,
                status,
            })
            response.writeHead(status).end('{}')
        })
    })
    server.on('connection', (socket) => {
        sockets.add(socket)
        socket.on('close', () => sockets.delete(socket))
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const address = server.address()
    if (!address || typeof address === 'string') throw new Error('missing-port')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-credential-review-'))
    const host = await startOwnedHost(
        data,
        0,
        [`--TenonEdge:Outbound:LocalPorts:0=${address.port}`],
        httpAddress,
    )
    const context = await browser.newContext({ baseURL: host.url })
    const page = await context.newPage()
    const recipient = `http://127.0.0.1:${address.port}/`
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill('review-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    if (httpAddress !== '127.0.0.1')
        expect(await page.evaluate(() => isSecureContext)).toBe(false)
    const session = await (
        await page.request.get(root + '/auth/session')
    ).json()
    const headers = { 'X-CSRF-Token': session.csrfToken }
    async function post(path: string, value: unknown, expected = 200) {
        const response = await page.request.post(root + path, {
            headers,
            data: value,
        })
        expect(response.status(), await response.text()).toBe(expected)
        return response.json()
    }
    const draft = await post('/connections', {
        expectedVersion: '0',
        key: 'receiver',
        name: 'Receiver',
        kind: 'http',
        http: { baseAddress: recipient },
    })
    const id = draft.connection.id
    const endpoint = `${root}/connections/${id}/http-credential`
    const panel = page.getByRole('region', {
        name: '连接认证凭证',
        exact: true,
    })
    const review = page.getByRole('region', {
        name: '确认凭证身份与积存',
        exact: true,
    })
    async function open() {
        await page.goto('/connections')
        await page
            .getByRole('row')
            .filter({ hasText: 'receiver' })
            .getByRole('button', { name: '编辑连接', exact: true })
            .click()
        await expect(
            panel.getByRole('button', { name: '核对凭证变更（不保存）' }),
        ).toBeEnabled()
    }
    async function prepare(
        kind: string,
        secret: string,
        username = 'original-user',
    ) {
        await panel
            .getByRole('combobox', { name: '新认证方式', exact: true })
            .selectOption(kind)
        if (kind === 'basic')
            await panel.getByLabel('认证用户名', { exact: true }).fill(username)
        if (kind.startsWith('apiKey'))
            await panel
                .getByLabel('API Key 参数名', { exact: true })
                .fill('X-Key')
        await panel.getByLabel('新密码或秘密', { exact: true }).fill(secret)
        const response = page.waitForResponse((value) =>
            value.url().endsWith('/credential-review'),
        )
        await panel
            .getByRole('button', { name: '核对凭证变更（不保存）' })
            .click()
        const inspected = await response
        expect(inspected.status()).toBe(200)
        expect(inspected.request().postData()).not.toContain(secret)
        expect(await inspected.text()).not.toContain(secret)
        await expect(review).toBeVisible()
    }
    async function save() {
        await review.getByRole('checkbox').check()
        await review
            .getByRole('button', { name: '立即保存凭证', exact: true })
            .click()
        await expect(panel.getByText('已设置', { exact: true })).toBeVisible()
        await expect(
            panel.getByLabel('新密码或秘密', { exact: true }),
        ).toHaveValue('')
        await expect(
            panel.getByRole('button', { name: '核对凭证变更（不保存）' }),
        ).toBeEnabled()
    }
    async function startQueue() {
        const version = (
            await (await page.request.get(root + '/connections')).json()
        ).version
        const device = await post('/devices', {
            expectedVersion: version,
            key: 'source',
            name: 'Source',
            kind: 'simulator',
        })
        const source = await post(`/devices/${device.device.id}/inputs`, {
            expectedVersion: device.version,
            key: 'sample',
            name: 'Sample',
            simulator: { periodMs: 60000, sampleJson: '{"value":42}' },
        })
        const route = await post('/routes', {
            expectedVersion: source.version,
            key: 'review-queue',
            name: 'Review queue',
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
                        config: { connectionId: id, path: '/capture' },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: { nodes: {} },
            },
        })
        const checked = await post('/configuration/review', {
            expectedVersion: route.version,
        })
        const operation = await post(
            '/configuration/apply',
            {
                expectedVersion: route.version,
                expectedEffectiveRevision: checked.effectiveRevision,
                expectedPlanHash: checked.planHash,
                confirmed: true,
            },
            202,
        )
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
        await expect.poll(() => received.length).toBeGreaterThan(0)
        await open()
    }
    return {
        page,
        context,
        panel,
        review,
        id,
        endpoint,
        recipient,
        headers,
        open,
        prepare,
        save,
        startQueue,
        received,
        post,
        setStatus(value: number) {
            status = value
        },
        async close() {
            await context.close()
            expect(await host.stop()).toBe(0)
            for (const socket of sockets) socket.destroy()
            await new Promise<void>((resolve) => server.close(() => resolve()))
            await rm(data, { recursive: true, force: true })
        },
    }
}

for (const [kind, change] of [
    ['basic', 'username'],
    ['bearer', 'same'],
    ['bearer', 'changed'],
    ['apiKeyHeader', 'changed'],
    ['apiKeyQuery', 'same'],
]) {
    test(`real HTTP backlog review: ${kind}, ${change}`, async ({
        browser,
    }) => {
        test.setTimeout(90_000)
        const f = await fixture(browser)
        try {
            await f.open()
            await f.prepare(kind, 'old-fixture-secret')
            await expect(f.review.getByRole('combobox')).toHaveCount(0)
            await f.save()
            await f.startQueue()
            const originalBody = f.received[0].body
            const metadata = await (await f.page.request.get(f.endpoint)).json()
            await f.prepare(
                kind,
                'new-fixture-secret',
                change === 'username' ? 'new-user' : 'original-user',
            )
            await expect(f.review).toContainText(/核对时待发 [1-9][0-9]* 条/)
            await f.review.getByRole('checkbox').check()
            if (change === 'username') {
                await expect(f.review).toContainText('original-user')
                await expect(f.review).toContainText('new-user')
                await expect(f.review).toContainText('将被隔离')
                await expect(f.review.getByRole('combobox')).toHaveCount(0)
            } else {
                await expect(f.review.getByRole('combobox')).toHaveValue('')
                await expect(
                    f.review.getByRole('button', { name: '立即保存凭证' }),
                ).toBeDisabled()
                await f.review.getByRole('combobox').selectOption(change)
            }
            await f.save()
            expect(
                (await (await f.page.request.get(f.endpoint)).json()).version,
            ).not.toBe(metadata.version)
            f.setStatus(200)
            if (change === 'same') {
                await expect
                    .poll(
                        () =>
                            f.received.filter(
                                (value) =>
                                    value.status === 200 &&
                                    value.body === originalBody,
                            ).length,
                        { timeout: 30_000 },
                    )
                    .toBeGreaterThan(0)
                const sent = f.received.find(
                    (value) =>
                        value.status === 200 && value.body === originalBody,
                )!
                expect(
                    kind === 'bearer' ? sent.authorization : sent.url,
                ).toContain('new-fixture-secret')
            } else {
                await expect
                    .poll(
                        async () =>
                            (
                                await (
                                    await f.page.request.get(
                                        root +
                                            `/deliveries?state=quarantined&connectionId=${f.id}`,
                                    )
                                ).json()
                            ).items.length,
                        { timeout: 30_000 },
                    )
                    .toBeGreaterThan(0)
                expect(
                    f.received.filter(
                        (value) =>
                            value.status === 200 && value.body === originalBody,
                    ),
                ).toHaveLength(0)
            }
        } finally {
            await f.close()
        }
    })
}

test('cancel, conflict and lost reply clear secrets; review is read only and writes never replay', async ({
    browser,
}) => {
    const f = await fixture(browser)
    try {
        await f.open()
        await f.prepare('bearer', 'cancel-fixture-secret')
        await f.review
            .getByRole('button', { name: '取消核对并清除秘密' })
            .click()
        await expect(
            f.panel.getByLabel('新密码或秘密', { exact: true }),
        ).toHaveValue('')
        expect((await f.page.request.get(f.endpoint)).status()).toBe(404)
        await f.prepare('bearer', 'conflict-fixture-secret')
        const drafts = await (
            await f.page.request.get(root + '/connections')
        ).json()
        const concurrent = await f.page.request.put(f.endpoint, {
            headers: f.headers,
            data: {
                recipient: f.recipient,
                expectedDraftVersion: drafts.version,
                expectedVersion: null,
                kind: 'basic',
                username: 'concurrent',
                secret: 'concurrent-fixture-secret',
            },
        })
        expect(concurrent.status()).toBe(200)
        await f.review.getByRole('checkbox').check()
        const conflict = f.page.waitForResponse(
            (response) =>
                response.url().endsWith('/http-credential') &&
                response.request().method() === 'PUT',
        )
        await f.review.getByRole('button', { name: '立即保存凭证' }).click()
        expect((await conflict).status()).toBe(409)
        await expect(
            f.panel.getByLabel('新密码或秘密', { exact: true }),
        ).toHaveValue('')
        await expect(
            f.panel.getByRole('button', { name: '核对凭证变更（不保存）' }),
        ).toBeDisabled()
        await f.panel
            .getByRole('button', { name: '重新读取凭证状态', exact: true })
            .click()
        await expect(
            f.panel.getByRole('button', { name: '核对凭证变更（不保存）' }),
        ).toBeEnabled()
        await f.prepare('bearer', 'lost-fixture-secret')
        let puts = 0
        await f.page.route('**/http-credential', async (route) => {
            if (route.request().method() !== 'PUT') return route.continue()
            puts++
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await f.review.getByRole('checkbox').check()
        await f.review.getByRole('button', { name: '立即保存凭证' }).click()
        await expect(f.panel.getByText(/本次保存未取得成功确认/)).toBeVisible()
        await expect(
            f.panel.getByLabel('新密码或秘密', { exact: true }),
        ).toHaveValue('')
        await expect(
            f.panel.getByRole('button', { name: '核对凭证变更（不保存）' }),
        ).toBeDisabled()
        await f.panel
            .getByRole('button', { name: '重新读取凭证状态', exact: true })
            .click()
        await expect(
            f.panel.getByRole('button', { name: '核对凭证变更（不保存）' }),
        ).toBeEnabled()
        await f.page.reload()
        expect(puts).toBe(1)
        expect(
            await f.page.evaluate(() =>
                JSON.stringify({ ...localStorage, ...sessionStorage }),
            ),
        ).not.toContain('fixture-secret')
    } finally {
        await f.close()
    }
})
