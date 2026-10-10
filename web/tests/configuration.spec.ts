import { test, expect } from '@playwright/test'
import { createServer } from 'node:http'

test('configuration UI confirms real application, keeps exact diff values, tracks lost responses and atomically discards drafts', async ({
    page,
    context,
}) => {
    test.setTimeout(120_000)
    const setup = await (
        await page.request.get('/api/edge-admin/v1/setup')
    ).json()
    if (setup.state === 'uninitialized') {
        expect(
            (
                await page.request.post('/api/edge-admin/v1/setup', {
                    data: {
                        accountName: 'browser-admin',
                        password: 'Changed-fixture-5938',
                        gatewayName: null,
                    },
                })
            ).status(),
        ).toBe(200)
        await context.clearCookies()
    }
    await page.goto('/configuration')
    await page.getByLabel('账号', { exact: true }).fill('browser-admin')
    await page.getByLabel('密码', { exact: true }).fill('Changed-fixture-5938')
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await expect(
        page.getByRole('heading', { name: '配置应用', exact: true }),
    ).toBeVisible()
    const session = await (
        await page.request.get('/api/edge-admin/v1/auth/session')
    ).json()
    const headers = { 'X-CSRF-Token': session.csrfToken }
    const prefix = '/api/edge-admin/v1'
    const readSummary = async () =>
        (await page.request.get(prefix + '/configuration/draft')).json()
    const initial = await readSummary()
    expect(
        (
            await page.request.post(prefix + '/configuration/draft/discard', {
                headers,
                data: {
                    expectedVersion: initial.version,
                    expectedEffectiveRevision: initial.effectiveRevision,
                    confirmed: true,
                },
            })
        ).status(),
    ).toBe(200)
    const bodies: string[] = []
    const peer = createServer(async (request, response) => {
        const chunks: Buffer[] = []
        for await (const chunk of request) chunks.push(Buffer.from(chunk))
        bodies.push(Buffer.concat(chunks).toString('utf8'))
        response.writeHead(204)
        response.end()
    })
    await new Promise<void>((resolve) => peer.listen(0, '127.0.0.1', resolve))
    const address = peer.address()
    if (!address || typeof address === 'string')
        throw new Error('fixture-listener')
    try {
        const policy = await (
            await page.request.get(prefix + '/system/outbound-policy')
        ).json()
        expect(
            (
                await page.request.put(prefix + '/system/outbound-policy', {
                    headers,
                    data: {
                        expectedVersion: policy.version,
                        networks: policy.networks,
                        localPorts: [...policy.localPorts, address.port],
                    },
                })
            ).status(),
        ).toBe(200)
        const connectionResponse = await page.request.post(
            prefix + '/connections',
            {
                headers,
                data: {
                    expectedVersion: (await readSummary()).version,
                    key: 'apply-http',
                    name: 'Apply HTTP',
                    kind: 'http',
                    http: { baseAddress: `http://127.0.0.1:${address.port}/` },
                },
            },
        )
        expect(connectionResponse.status()).toBe(200)
        const connection = await connectionResponse.json()
        const deviceResponse = await page.request.post(prefix + '/devices', {
            headers,
            data: {
                expectedVersion: connection.version,
                key: 'apply-meter',
                name: 'Apply meter',
                kind: 'simulator',
            },
        })
        expect(deviceResponse.status()).toBe(200)
        const device = await deviceResponse.json()
        const sourceResponse = await page.request.post(
            `${prefix}/devices/${device.device.id}/inputs`,
            {
                headers,
                data: {
                    expectedVersion: device.version,
                    key: 'sample',
                    name: 'Sample',
                    simulator: { periodMs: 1000, sampleJson: '{"value":42}' },
                },
            },
        )
        expect(sourceResponse.status()).toBe(200)
        const source = await sourceResponse.json()
        const route = {
            expectedVersion: source.version,
            key: 'apply-route',
            name: 'Apply route',
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
                            connectionId: connection.connection.id,
                            path: '/capture',
                            payload: {
                                mode: 'custom',
                                fields: [
                                    { target: 'value', sourceField: 'value' },
                                    {
                                        target: 'exact',
                                        constant: 'EXACT_NUMBER',
                                    },
                                ],
                            },
                        },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: {
                    nodes: { in: { x: 10, y: 10 }, out: { x: 300, y: 10 } },
                },
            },
        }
        expect(
            (
                await page.request.post(prefix + '/routes', {
                    headers: { ...headers, 'Content-Type': 'application/json' },
                    data: JSON.stringify(route).replace(
                        '"EXACT_NUMBER"',
                        '9223372036854775807',
                    ),
                })
            ).status(),
        ).toBe(200)
        await page
            .getByRole('button', { name: '刷新配置状态', exact: true })
            .click()
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        const review = page.getByRole('region', { name: '应用前确认' })
        await expect(review).toBeVisible()
        await review
            .locator('summary')
            .filter({ hasText: 'apply-route' })
            .click()
        await expect(review.getByText(/9223372036854775807/)).toBeVisible()
        const apply = review.getByRole('button', {
            name: '确认应用配置',
            exact: true,
        })
        await expect(apply).toBeDisabled()
        await review
            .getByLabel('变更说明（选填，最多 1000 字符）')
            .fill('Browser confirmed application')
        await review.getByLabel('我已检查变更及影响，确认应用这份草稿').check()
        const acceptedResponse = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/configuration/apply'),
        )
        await apply.click()
        const accepted = await acceptedResponse
        expect(accepted.status()).toBe(202)
        const firstOperation = await accepted.json()
        await expect(
            page.locator(`[data-operation-id="${firstOperation.id}"]`),
        ).toContainText('配置已提交并激活')
        await expect
            .poll(() =>
                bodies.some(
                    (body) =>
                        body.includes('"exact":9223372036854775807') &&
                        body.includes('"value":42'),
                ),
            )
            .toBe(true)
        await expect.poll(async () => (await readSummary()).changeCount).toBe(0)
        const firstRevision = (await readSummary()).effectiveRevision
        // New confirmation becomes invalid when another client edits after review.
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await expect(review).toBeVisible()
        await review
            .getByLabel('变更说明（选填，最多 1000 字符）')
            .fill('Preserve this note')
        const changed = await page.request.put(
            `${prefix}/devices/${device.device.id}`,
            {
                headers,
                data: {
                    expectedVersion: (await readSummary()).version,
                    key: 'apply-meter',
                    name: 'Edited meter',
                    kind: 'simulator',
                },
            },
        )
        expect(changed.status()).toBe(200)
        await page
            .getByRole('button', { name: '刷新配置状态', exact: true })
            .click()
        await expect(review).toContainText('草稿或有效版本已变化')
        await expect(apply).toBeDisabled()
        await expect(
            review.getByLabel('变更说明（选填，最多 1000 字符）'),
        ).toHaveValue('Preserve this note')
        await page.route('**/configuration/review', (intercept) =>
            intercept.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"title":"fixture-unavailable"}',
            }),
        )
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await expect(
            page.getByRole('alert').filter({ hasText: '请求失败' }),
        ).toBeVisible()
        await expect(
            review.getByLabel('变更说明（选填，最多 1000 字符）'),
        ).toHaveValue('Preserve this note')
        await expect(apply).toBeDisabled()
        await page.unroute('**/configuration/review')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await review.getByLabel('我已检查变更及影响，确认应用这份草稿').check()
        await review
            .locator('summary')
            .filter({ hasText: 'apply-meter' })
            .click()
        await expect(review.getByText('/name', { exact: true })).toBeVisible()
        let writes = 0
        await page.route('**/configuration/apply', async (intercept) => {
            writes++
            expect((await intercept.fetch()).status()).toBe(202)
            await intercept.abort('failed') // Backend accepted, but caller lost the response.
        })
        await apply.click()
        await expect(
            page.getByText(
                '请求失败或响应丢失，结果可能未知。禁止自动重试；先核对操作记录和有效版本，再重新读取清单。',
            ),
        ).toBeVisible()
        await expect
            .poll(async () => (await readSummary()).effectiveRevision)
            .not.toBe(firstRevision)
        await page.waitForTimeout(2500)
        expect(writes).toBe(1)
        await page.unroute('**/configuration/apply')
        // Reload does not replay the write; server-owned operation history remains queryable.
        await page.reload()
        await expect(
            page.getByRole('heading', { name: '配置应用', exact: true }),
        ).toBeVisible()
        await expect(page.locator('[data-operation-id]')).toHaveCount(2)
        const add = await page.request.post(prefix + '/devices', {
            headers,
            data: {
                expectedVersion: (await readSummary()).version,
                key: 'discard-me',
                name: 'Discard me',
                kind: 'simulator',
            },
        })
        expect(add.status()).toBe(200)
        await page
            .getByRole('button', { name: '刷新配置状态', exact: true })
            .click()
        page.once('dialog', (dialog) => dialog.accept())
        await page
            .getByRole('button', { name: '放弃全部未应用修改', exact: true })
            .click()
        await expect(
            page.getByText(
                '未应用修改已放弃。当前凭证未恢复；其他打开的编辑页面须刷新，旧版本保存会被拒绝。',
            ),
        ).toBeVisible()
        expect(
            (
                await (await page.request.get(prefix + '/devices')).json()
            ).devices.some(
                (item: { key: string }) => item.key === 'discard-me',
            ),
        ).toBe(false)
        expect((await readSummary()).changeCount).toBe(0)
        expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
        expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
        await page.screenshot({
            path: 'test-results/configuration-application.png',
            fullPage: true,
        })
        const sent = bodies.length
        await page.close()
        await expect.poll(() => bodies.length).toBeGreaterThan(sent)
    } finally {
        peer.closeAllConnections()
        await new Promise<void>((resolve) => peer.close(() => resolve()))
    }
})
