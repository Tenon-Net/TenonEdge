import { test, expect, type Page, type Route } from '@playwright/test'
import { createServer } from 'node:http'

test('configuration UI applies, compares, rolls back and discards real drafts without replaying lost responses', async ({
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
        // Change a real input value too: rollback must restore collection, not just a display name.
        expect(
            (
                await page.request.put(
                    `${prefix}/devices/${device.device.id}/inputs/${source.input.id}`,
                    {
                        headers,
                        data: {
                            expectedVersion: (await changed.json()).version,
                            key: 'sample',
                            name: 'Sample',
                            enabled: true,
                            simulator: {
                                periodMs: 1000,
                                sampleJson: '{"value":84}',
                            },
                        },
                    },
                )
            ).status(),
        ).toBe(200)
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
        await expect
            .poll(() => bodies.at(-1)?.includes('"value":84'))
            .toBe(true)
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
        const history = page.getByRole('region', { name: '持久配置历史' })
        await history
            .getByRole('button', {
                name: `查看 r${firstRevision}`,
                exact: true,
            })
            .click()
        const firstDetail = history.getByRole('region', {
            name: `版本 r${firstRevision}`,
            exact: true,
        })
        await expect(
            firstDetail.getByRole('button', {
                name: `检查回滚到 r${firstRevision}`,
                exact: true,
            }),
        ).toBeDisabled()
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
        const secondRevision = (await readSummary()).effectiveRevision
        await expect(
            history.locator(`[data-version="${firstRevision}"]`),
        ).toContainText('Browser confirmed application')
        await firstDetail
            .locator('summary')
            .filter({ hasText: '完整草稿（精确 JSON）' })
            .click()
        await expect(
            firstDetail
                .locator('pre')
                .filter({ hasText: '9223372036854775807' }),
        ).toBeVisible()
        const comparison = firstDetail.getByLabel('版本差异')
        await expect(comparison).toContainText('当前草稿（仅比较')
        await comparison
            .locator('summary')
            .filter({ hasText: 'apply-meter' })
            .click()
        await expect(
            comparison.getByText('/name', { exact: true }),
        ).toBeVisible()
        await expect(
            comparison
                .locator('pre')
                .filter({ hasText: 'Apply meter' })
                .first(),
        ).toContainText('Edited meter')
        await firstDetail
            .getByLabel('比较到')
            .selectOption(String(secondRevision))
        await expect(comparison).toContainText(
            `r${firstRevision} → r${secondRevision}`,
        )
        // A failed refresh cannot relabel a previously successful comparison as current.
        await page.route('**/configuration/versions/*/compare/*', (intercept) =>
            intercept.fulfill({
                status: 409,
                contentType: 'application/problem+json',
                body: '{"code":"configuration.versionNotFound"}',
            }),
        )
        await page
            .getByRole('button', { name: '刷新配置状态', exact: true })
            .click()
        await expect(
            firstDetail.getByText('版本不存在或已清理，请刷新历史。'),
        ).toBeVisible()
        await expect(comparison).toHaveCount(0)
        await page.unroute('**/configuration/versions/*/compare/*')
        await page
            .getByRole('button', { name: '刷新配置状态', exact: true })
            .click()
        await expect(comparison).toBeVisible()
        await firstDetail
            .getByRole('button', {
                name: `检查回滚到 r${firstRevision}`,
                exact: true,
            })
            .click()
        const rollbackReview = page.getByRole('region', {
            name: `回滚到 r${firstRevision} 前确认`,
            exact: true,
        })
        await expect(rollbackReview).toContainText('回滚不会撤回已发送数据')
        await expect(rollbackReview).toContainText('回滚使用当前凭证')
        const rollback = rollbackReview.getByRole('button', {
            name: '确认回滚配置',
            exact: true,
        })
        await expect(rollback).toBeDisabled()
        await rollbackReview
            .getByLabel('变更说明（选填，最多 1000 字符）')
            .fill('Browser confirmed rollback')
        // Browsing another version cannot retarget the already returned immutable confirmation.
        await history
            .getByRole('button', {
                name: `查看 r${secondRevision}`,
                exact: true,
            })
            .click()
        await expect(rollbackReview).toBeVisible()
        await rollbackReview
            .getByLabel('我已检查目标版本及影响，确认回滚并创建新版本')
            .check()
        const rollbackResponse = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith(`/versions/${firstRevision}/rollback`),
        )
        const rollbackOutputStart = bodies.length
        await rollback.click()
        const admitted = await rollbackResponse
        expect(admitted.status()).toBe(202)
        const rollbackOperation = await admitted.json()
        await expect(
            page.locator(`[data-operation-id="${rollbackOperation.id}"]`),
        ).toContainText('配置已提交并激活')
        const restored = await readSummary()
        expect(restored.effectiveRevision).not.toBe(firstRevision)
        expect(restored.effectiveRevision).not.toBe(secondRevision)
        expect(restored.changeCount).toBe(0)
        await expect
            .poll(() =>
                bodies
                    .slice(rollbackOutputStart)
                    .some(
                        (body) =>
                            body.includes('"value":42') &&
                            body.includes('"exact":9223372036854775807'),
                    ),
            )
            .toBe(true)
        expect(
            (
                await (await page.request.get(prefix + '/devices')).json()
            ).devices.find(
                (item: { id: number }) => item.id === device.device.id,
            ).name,
        ).toBe('Apply meter')
        await expect(
            history.locator(`[data-version="${restored.effectiveRevision}"]`),
        ).toContainText('Browser confirmed rollback')
        const firstContent = await (
            await page.request.get(
                `${prefix}/configuration/versions/${firstRevision}`,
            )
        ).json()
        const rollbackContent = await (
            await page.request.get(
                `${prefix}/configuration/versions/${restored.effectiveRevision}`,
            )
        ).json()
        expect(rollbackContent.planHash).toBe(firstContent.planHash)
        // Real rollback admission followed by caller response loss must not replay the write.
        const secondDetail = history.getByRole('region', {
            name: `版本 r${secondRevision}`,
            exact: true,
        })
        await secondDetail
            .getByRole('button', {
                name: `检查回滚到 r${secondRevision}`,
                exact: true,
            })
            .click()
        const lostReview = page.getByRole('region', {
            name: `回滚到 r${secondRevision} 前确认`,
            exact: true,
        })
        await lostReview
            .getByLabel('我已检查目标版本及影响，确认回滚并创建新版本')
            .check()
        let rollbackWrites = 0
        await page.route(
            `**/configuration/versions/${secondRevision}/rollback`,
            async (intercept) => {
                rollbackWrites++
                expect((await intercept.fetch()).status()).toBe(202)
                await intercept.abort('failed')
            },
        )
        await lostReview
            .getByRole('button', { name: '确认回滚配置', exact: true })
            .click()
        await expect(
            page.getByText(
                '请求失败或响应丢失，结果可能未知。禁止自动重试；先核对操作记录和有效版本，再重新读取清单。',
            ),
        ).toBeVisible()
        await expect
            .poll(async () => (await readSummary()).effectiveRevision)
            .not.toBe(restored.effectiveRevision)
        await expect
            .poll(() => bodies.at(-1)?.includes('"value":84'))
            .toBe(true)
        await page.waitForTimeout(2500)
        expect(rollbackWrites).toBe(1)
        await page.unroute(
            `**/configuration/versions/${secondRevision}/rollback`,
        )
        await page.reload()
        await expect(history.locator('[data-version]')).toHaveCount(4)
        expect(rollbackWrites).toBe(1)
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

async function historyFixture(page: Page, role = 'administrator') {
    const prefix = '/api/edge-admin/v1'
    const setup = await (await page.request.get(prefix + '/setup')).json()
    if (setup.state === 'uninitialized') {
        expect(
            (
                await page.request.post(prefix + '/setup', {
                    data: {
                        accountName: 'browser-admin',
                        password: 'Changed-fixture-5938',
                        gatewayName: null,
                    },
                })
            ).status(),
        ).toBe(200)
    }
    expect(
        (
            await page.request.post(prefix + '/auth/login', {
                data: {
                    accountName: 'browser-admin',
                    password: 'Changed-fixture-5938',
                },
            })
        ).status(),
    ).toBe(200)
    // Explicit UI fixtures, not persisted historical versions or server-side authorization evidence.
    await page.route('**/auth/session', async (intercept) => {
        const response = await intercept.fetch()
        await intercept.fulfill({
            response,
            json: { ...(await response.json()), role },
        })
    })
    const largest = '9223372036854775807'
    const metadata = (revision: string) => ({
        revision,
        schemaVersion: revision === '20' ? 1 : 2,
        state:
            revision === '19'
                ? 'notEffective'
                : revision === largest
                  ? 'effective'
                  : 'history',
        origin: 'apply',
        originDetail: null,
        createdBy: 'history-fixture',
        createdAt: 1791590400000,
        activatedAt: revision === '19' ? null : 1791590401000,
        isStable: false,
        notEffectiveReason: revision === '19' ? 'fixture-prepare-failed' : null,
        changeDescription: 'UI fixture version',
    })
    const summary = {
        version: 'fixture-draft',
        effectiveRevision: largest,
        pendingRevision: null,
        comparisonAvailable: true,
        changeCount: 0,
        objects: [],
        points: [],
        warnings: [],
    }
    await page.route('**/configuration/draft', (intercept) =>
        intercept.fulfill({ json: summary }),
    )
    await page.route('**/configuration/operations', (intercept) =>
        intercept.fulfill({ json: [] }),
    )
    const reads: string[] = []
    let reviews = 0
    await page.route('**/configuration/versions**', (intercept) => {
        const url = new URL(intercept.request().url())
        reads.push(url.pathname + url.search)
        const parts = url.pathname
            .slice((prefix + '/configuration/versions').length)
            .split('/')
            .filter(Boolean)
        if (!parts.length)
            return intercept.fulfill({
                json: url.searchParams.has('beforeRevision')
                    ? [metadata('1')]
                    : [
                          metadata(largest),
                          ...Array.from({ length: 19 }, (_, index) =>
                              metadata(String(20 - index)),
                          ),
                      ],
            })
        const revision = parts[0]
        if (parts[1] === 'review') {
            reviews++
            return intercept.fulfill({
                status: 409,
                contentType: 'application/problem+json',
                body: '{"code":"configuration.rollbackCredentialMissing"}',
            })
        }
        if (parts[1]?.startsWith('compare')) {
            if (revision === '20')
                return intercept.fulfill({
                    status: 409,
                    contentType: 'application/problem+json',
                    body: '{"code":"configuration.unsupportedSnapshot"}',
                })
            return intercept.fulfill({
                json: {
                    beforeRevision: revision,
                    afterRevision: parts[2] ?? null,
                    draftVersion: parts[2] ? null : 'fixture-draft',
                    changes: [],
                    points: [],
                },
            })
        }
        return intercept.fulfill({
            contentType: 'application/json',
            body: JSON.stringify({
                version: metadata(revision),
                contentHash: 'sha256:' + 'b'.repeat(64),
                planHash: 'sha256:' + 'a'.repeat(64),
                draft: revision === '20' ? null : { integer: 'EXACT' },
                plan: { integer: 'EXACT' },
                warnings:
                    revision === '20'
                        ? ['configuration.legacyComparisonUnavailable']
                        : [],
            }).replaceAll('"EXACT"', largest),
        })
    })
    await page.goto('/configuration')
    await expect(
        page.getByRole('heading', { name: '配置应用', exact: true }),
    ).toBeVisible()
    return { largest, reads, summary, reviews: () => reviews }
}

test('history UI pages exact revision IDs, compares across pages, excludes legacy/failed rollback and explains missing credentials', async ({
    page,
}) => {
    const fixture = await historyFixture(page)
    const history = page.getByRole('region', { name: '持久配置历史' })
    await expect(history.locator('[data-version]')).toHaveCount(20)
    await history
        .getByRole('button', { name: `查看 r${fixture.largest}`, exact: true })
        .click()
    const largestDetail = history.getByRole('region', {
        name: `版本 r${fixture.largest}`,
        exact: true,
    })
    await largestDetail
        .locator('summary')
        .filter({ hasText: '完整草稿（精确 JSON）' })
        .click()
    await expect(
        largestDetail.locator('pre').filter({ hasText: fixture.largest }),
    ).toBeVisible()
    await largestDetail
        .getByRole('button', {
            name: `检查回滚到 r${fixture.largest}`,
            exact: true,
        })
        .click()
    await expect(
        page.getByText(
            '目标版本需要的当前凭证不存在；不能恢复历史秘密，请先配置凭证。',
        ),
    ).toBeVisible()
    await page.waitForTimeout(1000)
    expect(fixture.reviews()).toBe(1)
    const unknownSummary = (intercept: Route) =>
        intercept.fulfill({ json: { ...fixture.summary, changeCount: null } })
    await page.route('**/configuration/draft', unknownSummary)
    await page
        .getByRole('button', { name: '刷新配置状态', exact: true })
        .click()
    await expect(
        largestDetail.getByRole('button', {
            name: `检查回滚到 r${fixture.largest}`,
            exact: true,
        }),
    ).toBeDisabled()
    await page.unroute('**/configuration/draft', unknownSummary)
    await page
        .getByRole('button', { name: '刷新配置状态', exact: true })
        .click()
    await expect(
        largestDetail.getByRole('button', {
            name: `检查回滚到 r${fixture.largest}`,
            exact: true,
        }),
    ).toBeEnabled()
    await history.getByRole('button', { name: '查看 r20', exact: true }).click()
    const legacy = history.getByRole('region', {
        name: '版本 r20',
        exact: true,
    })
    await expect(legacy).toContainText('旧快照缺少完整草稿')
    await expect(
        legacy.getByRole('button', { name: '检查回滚到 r20', exact: true }),
    ).toBeDisabled()
    await expect(legacy).toContainText(
        '旧版快照缺少完整草稿，不能进行对象比较或正常回滚。',
    )
    await history.getByRole('button', { name: '查看 r19', exact: true }).click()
    await expect(history.locator('[data-version="19"]')).toContainText('未生效')
    await expect(history.locator('[data-version="19"]')).toContainText(
        'fixture-prepare-failed',
    )
    const failed = history.getByRole('region', {
        name: '版本 r19',
        exact: true,
    })
    await expect(
        failed.getByRole('button', { name: '检查回滚到 r19', exact: true }),
    ).toBeDisabled()
    await failed.getByLabel('比较到').selectOption(fixture.largest)
    await expect(failed.getByLabel('版本差异')).toContainText(
        `r19 → r${fixture.largest}`,
    )
    await history.getByRole('button', { name: '下一页', exact: true }).click()
    await expect(history.locator('[data-version]')).toHaveCount(1)
    expect(fixture.reads).toContain(
        '/api/edge-admin/v1/configuration/versions?beforeRevision=2&limit=20',
    )
    await history.getByRole('button', { name: '查看 r1', exact: true }).click()
    const oldest = history.getByRole('region', { name: '版本 r1', exact: true })
    await expect(oldest.getByLabel('版本差异')).toContainText(
        `r1 → r${fixture.largest}`,
    )
    expect(fixture.reads).toContain(
        `/api/edge-admin/v1/configuration/versions/1/compare/${fixture.largest}`,
    )
    await expect(
        history.getByRole('button', { name: '下一页', exact: true }),
    ).toBeDisabled()
    await history.getByRole('button', { name: '上一页', exact: true }).click()
    await expect(history.locator('[data-version]')).toHaveCount(20)
    expect(fixture.reviews()).toBe(1)
})

test('viewer history UI reads and compares without exposing confirmation writes', async ({
    page,
}) => {
    const fixture = await historyFixture(page, 'viewer')
    const writes: string[] = []
    page.on('request', (request) => {
        if (request.method() === 'POST') writes.push(request.url())
    })
    const history = page.getByRole('region', { name: '持久配置历史' })
    await history
        .getByRole('button', { name: `查看 r${fixture.largest}`, exact: true })
        .click()
    const detail = history.getByRole('region', {
        name: `版本 r${fixture.largest}`,
        exact: true,
    })
    await expect(detail.getByLabel('版本差异')).toContainText(
        '没有对象内容差异',
    )
    await detail.getByLabel('比较到').selectOption('19')
    await expect(detail.getByLabel('版本差异')).toContainText('→ r19')
    await expect(
        page.getByRole('button', { name: '查看应用变更清单', exact: true }),
    ).toHaveCount(0)
    await expect(
        history.getByRole('button', { name: /检查回滚到/ }),
    ).toHaveCount(0)
    expect(writes).toEqual([])
})
