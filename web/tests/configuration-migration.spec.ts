import {
    test,
    expect,
    type Page,
    type BrowserContext,
    type Route,
} from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:http'
import { once } from 'node:events'
import { randomUUID } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const migration = root + '/configuration/migrations'
const password = 'Form-A-browser-fixture-5834'
const button = (page: Page, name: string) =>
    page.getByRole('button', { name, exact: true })
async function login(page: Page, first = false) {
    await page.goto('/configuration')
    await page.getByLabel('账号', { exact: true }).fill('form-a-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    if (first) await page.getByLabel('确认密码', { exact: true }).fill(password)
    await button(page, first ? '创建管理员并登录' : '登录').click()
    await expect(
        page.getByRole('heading', { name: '配置应用', exact: true }),
    ).toBeVisible()
}
async function headers(page: Page) {
    return {
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    }
}
async function agree(page: Page, retry = false) {
    await page
        .getByLabel(
            retry
                ? '我已核对原始、上次确认和当前身份及当前配额，确认重试'
                : '我已核对全部配置差异、迁移连接和配额，确认应用',
            { exact: true },
        )
        .check()
    await page
        .getByLabel(
            retry
                ? '我再次确认当前接收方与原接收方属于同一数据所有者'
                : '我确认每个所选连接的新旧接收方属于同一数据所有者',
            { exact: true },
        )
        .check()
}

test('form-A UI applies real blocked drafts once, preserves HTTP payloads and failures, recovers persistent intents after restart, and explicitly retries a legacy zero-record fixture', async ({
    browser,
}) => {
    test.setTimeout(240_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-form-a-browser-'))
    let sourceCalls = 0
    const delivered: {
        id: string | undefined
        body: string
        path: string | undefined
    }[] = []
    const peer = createServer(async (request, response) => {
        let body = ''
        for await (const chunk of request) body += chunk.toString()
        if (request.url?.startsWith('/old/'))
            response.writeHead(++sourceCalls === 1 ? 422 : 401)
        else {
            delivered.push({
                id: request.headers['idempotency-key'] as string | undefined,
                body,
                path: request.url,
            })
            response.writeHead(204)
        }
        response.end()
    })
    peer.listen(0, '127.0.0.1')
    await once(peer, 'listening')
    const address = peer.address()
    if (!address || typeof address === 'string') throw new Error('peer-address')
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    const start = async (first = false) => {
        host = await startOwnedHost(data, 0, [
            `--TenonEdge:Outbound:LocalPorts:0=${address.port}`,
        ])
        context = await browser.newContext({ baseURL: host.url })
        const page = await context.newPage()
        page.setDefaultTimeout(15000)
        await login(page, first)
        return page
    }
    const stop = async () => {
        const ownedContext = context
        const ownedHost = host
        context = undefined
        host = undefined
        try {
            await ownedContext?.close()
        } finally {
            await ownedHost?.stop()
        }
    }
    type Row = {
        seq: number
        delivery_id: string
        state: string
        payload: Uint8Array
        created_at: number
    }
    function rows(): Row[] {
        const db = new DatabaseSync(join(data, 'queue.db'), { readOnly: true })
        try {
            return db
                .prepare(
                    'SELECT seq,delivery_id,state,payload,created_at FROM delivery ORDER BY seq',
                )
                .all() as unknown as Row[]
        } finally {
            db.close()
        }
    }
    try {
        let page = await start(true)
        const summary = async () =>
            (await page.request.get(root + '/configuration/draft')).json()
        async function post(path: string, data: unknown) {
            const response = await page.request.post(root + path, {
                headers: await headers(page),
                data,
            })
            expect(response.status(), await response.text()).toBe(200)
            return response.json()
        }
        const connection = await post('/connections', {
            expectedVersion: (await summary()).version,
            key: 'form-a-http',
            name: '换址 HTTP',
            kind: 'http',
            http: { baseAddress: `http://127.0.0.1:${address.port}/old/` },
        })
        const connectionId = connection.connection.id
        const device = await post('/devices', {
            expectedVersion: connection.version,
            key: 'meter',
            name: 'Meter',
            kind: 'simulator',
        })
        const input = await post(`/devices/${device.device.id}/inputs`, {
            expectedVersion: device.version,
            key: 'sample',
            name: 'Sample',
            simulator: {
                periodMs: 1000,
                sampleJson: '{"value":9223372036854775807}',
            },
        })
        await post('/routes', {
            expectedVersion: input.version,
            key: 'migration-route',
            name: 'Migration route',
            enabled: true,
            graph: {
                schemaVersion: 1,
                nodes: [
                    {
                        id: 'in',
                        type: 'input.simulator',
                        version: 1,
                        name: 'Input',
                        config: { sourceIds: [input.input.id] },
                    },
                    {
                        id: 'out',
                        type: 'output.http',
                        version: 1,
                        name: 'Output',
                        config: {
                            connectionId,
                            path: 'send',
                            payload: {
                                mode: 'custom',
                                fields: [
                                    { target: 'value', sourceField: 'value' },
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
        })
        const initialReview = await post('/configuration/review', {
            expectedVersion: (await summary()).version,
        })
        expect(initialReview.preflightPassed).toBe(true)
        const initialApply = await page.request.post(
            root + '/configuration/apply',
            {
                headers: await headers(page),
                data: {
                    expectedVersion: initialReview.version,
                    expectedEffectiveRevision: initialReview.effectiveRevision,
                    expectedPlanHash: initialReview.planHash,
                    confirmed: true,
                },
            },
        )
        expect(initialApply.status()).toBe(202)
        await expect
            .poll(async () => (await summary()).effectiveRevision)
            .toBe(1)
        await expect
            .poll(() => rows().filter((row) => row.state === 'failed').length)
            .toBe(1)
        await expect
            .poll(() => rows().filter((row) => row.state === 'pending').length)
            .toBeGreaterThanOrEqual(3)
        const control = await (
            await page.request.get(
                root + `/connections/${connectionId}/delivery-control`,
            )
        ).json()
        expect(
            (
                await page.request.put(
                    root + `/connections/${connectionId}/delivery-control`,
                    {
                        headers: await headers(page),
                        data: {
                            paused: true,
                            confirmed: true,
                            expectedVersion: control.resumeVersion,
                        },
                    },
                )
            ).status(),
        ).toBe(200)
        // Slow new sampling so original frozen records can be examined after migration.
        expect(
            (
                await page.request.put(
                    root +
                        `/devices/${device.device.id}/inputs/${input.input.id}`,
                    {
                        headers: await headers(page),
                        data: {
                            expectedVersion: (await summary()).version,
                            key: 'sample',
                            name: 'Sample',
                            simulator: {
                                periodMs: 60000,
                                sampleJson: '{"value":9223372036854775807}',
                            },
                        },
                    },
                )
            ).status(),
        ).toBe(200)
        expect(
            (
                await page.request.put(root + `/connections/${connectionId}`, {
                    headers: await headers(page),
                    data: {
                        expectedVersion: (await summary()).version,
                        key: 'form-a-http',
                        name: '换址 HTTP',
                        kind: 'http',
                        http: {
                            baseAddress: `http://127.0.0.1:${address.port}/new/`,
                        },
                    },
                })
            ).status(),
        ).toBe(200)
        await button(page, '刷新配置状态').click()
        await button(page, '查看应用变更清单').click()
        await expect(button(page, '确认应用配置')).toBeDisabled()
        await page
            .getByRole('link', { name: '核对同连接换地址迁移', exact: true })
            .click()
        await button(page, '读取换址变更清单').click()
        await page
            .getByLabel(`迁移连接 ${connectionId} 的旧记录`, { exact: true })
            .check()
        await button(page, '准备所选连接的换址应用').click()
        const review = page.getByRole('region', {
            name: '换址应用最终核对',
            exact: true,
        })
        await expect(review).toBeVisible()
        await expect(
            review.getByText(`http://127.0.0.1:${address.port}/old/`, {
                exact: true,
            }),
        ).toBeVisible()
        await expect(
            review.getByText(`http://127.0.0.1:${address.port}/new/`, {
                exact: true,
            }),
        ).toBeVisible()
        await expect(button(page, '确认换址并应用配置')).toBeDisabled()
        await agree(page)
        await page.reload()
        await expect(review).toBeVisible()
        await expect(button(page, '确认换址并应用配置')).toBeDisabled()
        await expect(
            page.getByLabel(
                '我确认每个所选连接的新旧接收方属于同一数据所有者',
                { exact: true },
            ),
        ).not.toBeChecked()
        const failRead = (route: Route) =>
            route.fulfill({ status: 503, json: {} })
        await page.route('**/configuration/migrations/preparation', failRead)
        await button(page, '刷新换址应用状态').click()
        await expect(button(page, '确认换址并应用配置')).toBeDisabled()
        await expect(
            page.getByText(
                '读取失败，不能当作空队列、无确认清单或迁移失败。请重新读取。',
                { exact: true },
            ),
        ).toBeVisible()
        await page.unroute('**/configuration/migrations/preparation', failRead)
        await button(page, '刷新换址应用状态').click()
        await expect(
            page.getByLabel('我已核对全部配置差异、迁移连接和配额，确认应用', {
                exact: true,
            }),
        ).not.toBeChecked()
        const capturedSummary = await summary()
        const staleSummary = (route: Route) =>
            route.fulfill({
                json: {
                    ...capturedSummary,
                    version: 'fixture-changed-version',
                },
            })
        await page.route('**/configuration/draft', staleSummary)
        await button(page, '刷新换址应用状态').click()
        await expect(button(page, '确认换址并应用配置')).toBeDisabled()
        await expect(
            page.getByText('草稿或有效版本已变化，请重新读取清单后确认。', {
                exact: true,
            }),
        ).toBeVisible()
        await page.unroute('**/configuration/draft', staleSummary)
        await button(page, '刷新换址应用状态').click()
        await button(page, '取消换址应用准备').click()
        await expect(review).toHaveCount(0)
        expect((await summary()).effectiveRevision).toBe(1)
        await button(page, '读取换址变更清单').click()
        await page
            .getByLabel(`迁移连接 ${connectionId} 的旧记录`, { exact: true })
            .check()
        await button(page, '准备所选连接的换址应用').click()
        await expect(review).toBeVisible()
        const prepared = (
            await (await page.request.get(migration + '/preparation')).json()
        ).preparation
        const intentId = prepared.targets[0].intentId
        await page.screenshot({
            path: '/tmp/tenonedge-form-a-browser-apply-review.png',
            fullPage: true,
        })
        await expect(review).toContainText('确认前旧配置仍会采样，条数可能增加')
        await expect
            .poll(() => rows().length)
            .toBeGreaterThan(prepared.targets[0].count)
        const original = rows()
        let confirmations = 0
        let urlRecorded = false
        await page.route(
            '**/configuration/migrations/*/confirm',
            async (route: Route) => {
                confirmations++
                urlRecorded =
                    new URL(page.url()).searchParams.get('operationId') ===
                    prepared.id
                const response = await route.fetch()
                expect(response.status()).toBe(202)
                await route.abort('failed')
            },
        )
        await agree(page)
        await button(page, '确认换址并应用配置').click()
        await expect(
            page.locator(`[data-operation-id="${prepared.id}"]`),
        ).toContainText('配置已提交并激活')
        expect(urlRecorded).toBe(true)
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await page.request.get(
                                migration + `/intents/${intentId}`,
                            )
                        ).json()
                    ).status,
                { timeout: 20000 },
            )
            .toBe('completed')
        const durableResult = await (
            await page.request.get(migration + `/intents/${intentId}`)
        ).json()
        expect(durableResult.affectedRecords).toBeGreaterThan(
            prepared.targets[0].count,
        )
        const migrated = rows()
        for (const row of original) {
            const actual = migrated.find(
                (r) => r.delivery_id === row.delivery_id,
            )!
            expect(actual).toEqual(row)
        }
        expect(delivered).toHaveLength(0)
        await page.reload()
        await expect(button(page, '确认换址并应用配置')).toHaveCount(0)
        expect(confirmations).toBe(1)
        await page
            .getByRole('link', { name: '持久迁移意图', exact: true })
            .first()
            .click()
        await page
            .getByLabel('筛选意图状态', { exact: true })
            .selectOption('completed')
        await page
            .getByLabel('筛选连接编号（选填）', { exact: true })
            .fill(String(connectionId))
        await button(page, '应用意图筛选').click()
        await page
            .getByRole('link', { name: `核对意图 ${intentId}`, exact: true })
            .click()
        const detail = page.getByRole('region', {
            name: '意图当前核对',
            exact: true,
        })
        await expect(detail).toContainText('迁移已完成（不表示已发送）')
        await expect(button(page, '准备重新审批此意图')).toBeDisabled()
        await page.screenshot({
            path: '/tmp/tenonedge-form-a-browser-completed.png',
            fullPage: true,
        })
        await page.goto('/runtime/outputs')
        await button(page, `查看目标 ${connectionId}`).click()
        const controls = page.getByRole('region', {
            name: '目标发送操作',
            exact: true,
        })
        await controls
            .getByRole('button', { name: '恢复目标', exact: true })
            .click()
        await controls
            .getByLabel('我已核对目标并确认此发送状态变更', { exact: true })
            .check()
        await controls
            .getByRole('button', { name: '确认执行', exact: true })
            .click()
        const pending = original.filter((row) => row.state === 'pending')
        await expect
            .poll(() =>
                pending.every((row) =>
                    delivered.some((d) => d.id === row.delivery_id),
                ),
            )
            .toBe(true)
        for (const row of pending)
            expect(delivered.find((d) => d.id === row.delivery_id)).toEqual({
                id: row.delivery_id,
                path: '/new/send',
                body: Buffer.from(row.payload).toString('utf8'),
            })
        expect(
            delivered.some(
                (d) =>
                    d.id ===
                    original.find((row) => row.state === 'failed')!.delivery_id,
            ),
        ).toBe(false)
        await stop()
        page = await start()
        await page.goto(`/configuration/migrations?operationId=${prepared.id}`)
        await expect(
            page.getByText(
                '未能取得应用结果。应用状态仅在当前进程保存，404 不证明未提交；请核对持久迁移意图和配置历史，不要重复确认。',
                { exact: true },
            ),
        ).toBeVisible()
        await expect(button(page, '确认换址并应用配置')).toHaveCount(0)
        await page.goto(`/configuration/migration-intents?intentId=${intentId}`)
        await expect(
            page.getByRole('region', { name: '意图当前核对', exact: true }),
        ).toContainText('迁移已完成（不表示已发送）')
        await stop()
        // Explicit offline legacy fixture: retain the real original hold, but represent an old
        // unapproved intent with zero still-isolated records. Not a process-interruption proof.
        const db = new DatabaseSync(join(data, 'config.db'))
        try {
            db.prepare(
                "UPDATE delivery_migration_intent SET status='waiting',last_operation_id=NULL,target_fingerprint=NULL,target_summary=NULL WHERE intent_id=?",
            ).run(intentId)
        } finally {
            db.close()
        }
        page = await start()
        await page.goto(`/configuration/migration-intents?intentId=${intentId}`)
        await button(page, '准备重新审批此意图').click()
        const retryReview = page.getByRole('region', {
            name: '重新审批确认清单',
            exact: true,
        })
        await expect(retryReview).toBeVisible()
        await expect(retryReview).toContainText('仍属原隔离意图的记录：0')
        await agree(page, true)
        await page.reload()
        await expect(retryReview).toBeVisible()
        await expect(button(page, '确认重新审批并重试')).toBeDisabled()
        let retries = 0
        let retryId = ''
        await page.route(
            '**/configuration/migrations/intent-retry/*/confirm',
            async (route) => {
                retries++
                retryId = route.request().url().split('/').at(-2)!
                expect(new URL(page.url()).searchParams.get('attemptId')).toBe(
                    retryId,
                )
                expect((await route.fetch()).status()).toBe(200)
                await route.abort('failed')
            },
        )
        await agree(page, true)
        await button(page, '确认重新审批并重试').click()
        await expect(
            page.getByRole('region', { name: '查询迁移持久结果', exact: true }),
        ).toContainText('实际迁移 0 条')
        await page.reload()
        await expect(
            page.getByRole('region', { name: '查询迁移持久结果', exact: true }),
        ).toContainText('实际迁移 0 条')
        await expect(button(page, '确认重新审批并重试')).toHaveCount(0)
        expect(retries).toBe(1)
        await page.screenshot({
            path: '/tmp/tenonedge-form-a-browser-retry.png',
            fullPage: true,
        })
        for (const role of ['Configurator', 'Viewer']) {
            await stop()
            const config = new DatabaseSync(join(data, 'config.db'))
            try {
                config
                    .prepare(
                        'UPDATE user_account SET role=?,security_stamp=? WHERE account_name=?',
                    )
                    .run(role, randomUUID(), 'form-a-admin')
            } finally {
                config.close()
            }
            page = await start()
            await expect(
                page.getByRole('link', { name: '持久迁移意图', exact: true }),
            ).toHaveCount(0)
            let adminReads = 0
            page.on('request', (request) => {
                if (
                    request
                        .url()
                        .includes('/api/edge-admin/v1/configuration/migrations')
                )
                    adminReads++
            })
            for (const path of [
                '/configuration/migrations',
                '/configuration/migration-intents',
            ]) {
                await page.goto(path)
                await expect(page.getByRole('alert')).toContainText('仅管理员')
            }
            expect(adminReads).toBe(0)
            expect(
                (await page.request.get(migration + '/intents')).status(),
            ).toBe(403)
            expect(
                (
                    await page.request.post(
                        migration + `/intent-retry/${retryId}/confirm`,
                        {
                            headers: await headers(page),
                            data: { confirmed: true, sameOwnerConfirmed: true },
                        },
                    )
                ).status(),
            ).toBe(403)
        }
    } finally {
        try {
            await stop()
        } finally {
            peer.closeAllConnections()
            await new Promise<void>((resolve) => peer.close(() => resolve()))
            await rm(data, { recursive: true, force: true })
        }
    }
})

test('intent UI fails closed on unreadable current state, pages exact metadata, displays all current deficits and never replays an unknown retry', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-form-a-boundary-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    const page = await context.newPage()
    page.setDefaultTimeout(10_000)
    try {
        await login(page, true)
        // Explicit API-response fixtures for rare/error UI states, not queue execution evidence.
        const intentId = '12345678-1234-4321-9876-123456789abc'
        const attemptId = '12345678-1234-4321-9876-123456789def'
        const recipient = {
            protocol: 'http',
            address: 'http://old.test/',
            authentication: 'Basic',
            accountRecorded: false,
            account: null,
            clientCertificateRecorded: true,
            clientCertificateSubject: null,
        }
        const target = {
            ...recipient,
            address: 'http://current.test/',
            accountRecorded: true,
            account: 'owner',
        }
        const inspection = {
            intentId,
            status: 'rejected',
            effectiveRevision: '9223372036854775807',
            source: recipient,
            approvedTarget: null,
            currentTarget: target,
            targetMatchesConfirmation: false,
            remainingRecords: '12',
            currentQuota: {
                pendingRecords: '1',
                pendingBytes: '2',
                globalBytes: '9223372036854775807',
                failedRecords: '4',
                exceeded: true,
            },
            problem: null,
            retryProblem: null,
            canPrepareRetry: false,
        }
        const prepared = {
            id: attemptId,
            inspection,
            expiresAt: '2099-10-15T00:00:00Z',
            canConfirm: false,
        }
        let current: typeof prepared | null = null
        let readFailed = false
        let pageFailed = false
        let outcomeStatus = 'rejected'
        let confirmations = 0
        let preparations = 0
        const cursors: string[] = []
        const item = (n: number) => ({
            id:
                n === 1
                    ? intentId
                    : `12345678-1234-4321-9876-${String(n).padStart(12, '0')}`,
            revision: String(9223372036854775700n + BigInt(n)),
            sourceRevision: '1',
            connectionId: '7',
            status: 'rejected',
            registeredBy: 'fixture-admin',
            registeredAt: '1790000000000',
            lastOperationId: null,
            hasConfirmedTarget: false,
        })
        await page.route(
            '**/api/edge-admin/v1/configuration/migrations/**',
            async (route) => {
                const url = new URL(route.request().url())
                const path = url.pathname
                if (path.endsWith('/intent-retry/preparation')) {
                    if (readFailed) {
                        await route.fulfill({
                            status: 503,
                            json: { code: 'fixture-unavailable' },
                        })
                        return
                    }
                    await route.fulfill({ json: { preparation: current } })
                    return
                }
                if (path.endsWith('/intents')) {
                    cursors.push(url.search)
                    if (pageFailed) {
                        await route.fulfill({ status: 503, json: {} })
                        return
                    }
                    const items = url.searchParams.has('afterRevision')
                        ? [item(51)]
                        : Array.from({ length: 50 }, (_, i) => item(i + 1))
                    await route.fulfill({
                        json: {
                            items,
                            next:
                                items.length === 50
                                    ? {
                                          revision: items[49].revision,
                                          connectionId: '7',
                                      }
                                    : null,
                        },
                    })
                    return
                }
                if (path.endsWith('/preflight')) {
                    await route.fulfill({ json: inspection })
                    return
                }
                if (path.endsWith('/retry/prepare')) {
                    preparations++
                    current = prepared
                    await route.fulfill({ json: current })
                    return
                }
                if (path.endsWith(`/intent-retry/${attemptId}/confirm`)) {
                    confirmations++
                    expect(
                        new URL(page.url()).searchParams.get('attemptId'),
                    ).toBe(attemptId)
                    // Lost request before durable registration: a 404 lookup still must not re-enable confirm.
                    await route.abort('failed')
                    return
                }
                if (path.endsWith(`/intents/${intentId}`)) {
                    await route.fulfill({
                        json: {
                            id: intentId,
                            revision: '2',
                            sourceRevision: '1',
                            connectionId: '7',
                            status: outcomeStatus,
                            source: recipient,
                            target: null,
                            targetRecipient: 'http://approved.test/',
                            operationId: null,
                            attemptStatus: null,
                            affectedRecords: null,
                            errorCode: null,
                        },
                    })
                    return
                }
                await route.continue()
            },
        )
        await page.route(
            '**/api/edge-admin/v1/deliveries/migrations/*',
            (route) => route.fulfill({ status: 404, json: {} }),
        )
        await page.goto(`/configuration/migration-intents?intentId=${intentId}`)
        await expect(
            page.getByRole('link', { name: /^核对意图 / }),
        ).toHaveCount(50)
        await button(page, '下一批意图').click()
        await expect(
            page.getByRole('link', { name: /^核对意图 / }),
        ).toHaveCount(1)
        expect(cursors.at(-1)).toContain('afterRevision=9223372036854775750')
        expect(cursors.at(-1)).toContain('afterConnection=7')
        pageFailed = true
        await button(page, '重新读取本页意图').click()
        await expect(
            page.getByText(
                '读取失败，不能当作空队列、无确认清单或迁移失败。请重新读取。',
                { exact: true },
            ),
        ).toBeVisible()
        await expect(
            page.getByText('本页没有迁移意图。', { exact: true }),
        ).toHaveCount(0)
        await expect(button(page, '下一批意图')).toBeDisabled()
        pageFailed = false
        await button(page, '重新读取本页意图').click()
        const detail = page.getByRole('region', {
            name: '意图当前核对',
            exact: true,
        })
        const quotas = detail.getByRole('region', {
            name: '配额缺口（须全部满足，准备后还会复查）',
            exact: true,
        })
        await expect(quotas).toContainText('9223372036854775807')
        await expect(quotas.locator('dd')).toHaveText([
            '1',
            '2',
            '9223372036854775807',
            '4',
        ])
        await expect(detail).toContainText(
            '未记录，无法核实（不使用当前账户补填）',
        )
        await expect(button(page, '准备重新审批此意图')).toBeDisabled()
        Object.assign(inspection.currentQuota, {
            pendingRecords: '0',
            pendingBytes: '0',
            globalBytes: '0',
            failedRecords: '0',
            exceeded: false,
        })
        inspection.canPrepareRetry = true
        prepared.canConfirm = true
        await button(page, '重新核对当前身份与配额').click()
        await button(page, '准备重新审批此意图').click()
        expect(preparations).toBe(1)
        await agree(page, true)
        readFailed = true
        await button(page, '刷新本会话重试清单').click()
        await expect(button(page, '确认重新审批并重试')).toBeDisabled()
        readFailed = false
        await button(page, '刷新本会话重试清单').click()
        await expect(
            page.getByLabel(
                '我再次确认当前接收方与原接收方属于同一数据所有者',
                { exact: true },
            ),
        ).not.toBeChecked()
        await agree(page, true)
        await page.screenshot({
            path: '/tmp/tenonedge-form-a-browser-inspection.png',
            fullPage: true,
        })
        await button(page, '确认重新审批并重试').click()
        await expect(
            page.getByRole('region', { name: '查询迁移持久结果', exact: true }),
        ).toContainText('这不是迁移失败证明')
        await expect(button(page, '确认重新审批并重试')).toHaveCount(0)
        await page.reload()
        await expect(
            page.getByRole('region', { name: '查询迁移持久结果', exact: true }),
        ).toContainText('这不是迁移失败证明')
        await expect(button(page, '确认重新审批并重试')).toHaveCount(0)
        expect(confirmations).toBe(1)
        // An unreconciled started intent cannot become retryable just by opening detail.
        current = null
        inspection.canPrepareRetry = false
        Object.assign(inspection, {
            status: 'waiting',
            retryProblem: 'configuration.migrationNotRetryable',
        })
        outcomeStatus = 'waiting'
        await page.goto(`/configuration/migration-intents?intentId=${intentId}`)
        await expect(button(page, '准备重新审批此意图')).toBeDisabled()
        expect(preparations).toBe(1)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
