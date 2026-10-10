import {
    test,
    expect,
    type Browser,
    type BrowserContext,
    type Page,
} from '@playwright/test'
import { DatabaseSync } from 'node:sqlite'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const prefix = '/api/edge-admin/v1'
const password = 'Retry-fixture-5917'

function database<T>(data: string, action: (db: DatabaseSync) => T) {
    const db = new DatabaseSync(join(data, 'config.db'))
    try {
        return action(db)
    } finally {
        db.close()
    }
}

async function initialize(browser: Browser, url: string) {
    const context = await browser.newContext({ baseURL: url })
    context.setDefaultTimeout(15_000)
    try {
        const page = await context.newPage()
        await page.goto('/configuration')
        await page.getByLabel('账号', { exact: true }).fill('retry-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '配置应用', exact: true }),
        ).toBeVisible()
        await page.getByRole('link', { name: '设备草稿', exact: true }).click()
        await page
            .getByRole('button', { name: '新建设备', exact: true })
            .click()
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('simulator')
        await page
            .getByLabel('设备 key', { exact: true })
            .fill('unapplied-retry')
        await page
            .getByLabel('设备名称', { exact: true })
            .fill('Retry must not apply this draft')
        await page
            .getByRole('button', { name: '保存设备草稿', exact: true })
            .click()
        await expect(
            page.getByText('设备草稿已保存；尚未应用。', { exact: true }),
        ).toBeVisible()
        return (await (await page.request.get(prefix + '/devices')).json())
            .version as string
    } finally {
        await context.close()
    }
}

async function login(
    browser: Browser,
    url: string,
): Promise<{ context: BrowserContext; page: Page }> {
    const context = await browser.newContext({ baseURL: url })
    context.setDefaultTimeout(15_000)
    const page = await context.newPage()
    await page.goto('/configuration')
    await page.getByLabel('账号', { exact: true }).fill('retry-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByRole('button', { name: '登录', exact: true }).click()
    await expect(
        page.getByRole('heading', { name: '配置应用', exact: true }),
    ).toBeVisible()
    return { context, page }
}

for (const lostResponse of [false, true]) {
    test(`runtime retry activates the same effective snapshot without applying a dirty draft; lost response=${lostResponse}`, async ({
        browser,
    }) => {
        test.setTimeout(90_000)
        const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-retry-'))
        let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
        let context: BrowserContext | undefined
        try {
            const seed = join(data, 'seed.json')
            // Software fixture: configuration is seeded, not a no-JSON webpage acceptance.
            await writeFile(
                seed,
                JSON.stringify({
                    schemaVersion: 1,
                    units: [
                        {
                            kind: 'input.simulator',
                            key: 'retry-meter/sample',
                            configuration: { periodMs: 1000, seed: 42 },
                        },
                    ],
                }),
            )
            const args = [
                `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
                '--TenonEdge:Runtime:StableAfterSeconds=600',
            ]
            host = await startOwnedHost(data, 0, args)
            const version = await initialize(browser, host.url)
            const url = host.url
            expect(await host.stop()).toBe(0)
            host = undefined
            // Construct startup protection only while the owned Host is stopped.
            // Actual repeated strong-kill acceptance is covered separately, not claimed here.
            database(data, (db) =>
                db.exec(
                    'UPDATE runtime_snapshot SET abnormal_exit_count = 3 WHERE revision = 1',
                ),
            )
            host = await startOwnedHost(data, Number(new URL(url).port), args)
            const loggedIn = await login(browser, url)
            context = loggedIn.context
            const page = loggedIn.page
            const runtime = page.getByRole('region', {
                name: '有效配置运行状态',
                exact: true,
            })
            await expect(runtime).toContainText('没有上一生效版本可以回滚')
            expect((await page.request.get('/readyz')).status()).toBe(503)
            let requests = 0
            let admittedId = ''
            page.on('request', (request) => {
                if (
                    request.method() === 'POST' &&
                    request.url().endsWith('/configuration/runtime/retry')
                )
                    requests++
            })
            if (lostResponse) {
                await page.route(
                    '**/configuration/runtime/retry',
                    async (route) => {
                        const response = await route.fetch()
                        expect(response.status()).toBe(202)
                        admittedId = (await response.json()).id
                        await route.abort('failed')
                    },
                )
            }
            await runtime
                .getByRole('button', {
                    name: '检查当前有效版本重试',
                    exact: true,
                })
                .click()
            const retry = runtime.getByRole('button', {
                name: '确认重试有效配置',
                exact: true,
            })
            await expect(retry).toBeDisabled()
            await runtime
                .getByLabel(
                    '我已检查运行失败原因，确认清零次数并重试当前有效版本',
                )
                .check()
            const admitted = !lostResponse
                ? page.waitForResponse(
                      (response) =>
                          response.request().method() === 'POST' &&
                          response
                              .url()
                              .endsWith('/configuration/runtime/retry'),
                  )
                : null
            await retry.click()
            if (admitted) {
                const response = await admitted
                expect(response.status()).toBe(202)
                admittedId = (await response.json()).id
            } else await expect(runtime).toContainText('禁止自动重试')
            await expect
                .poll(async () => (await page.request.get('/readyz')).status())
                .toBe(200)
            await expect(
                page.locator(`[data-operation-id="${admittedId}"]`),
            ).toContainText('人工重试已激活原有效版本')
            await expect(runtime).toContainText('有效配置已激活')
            await expect
                .poll(async () => {
                    const state = await (
                        await page.request.get(prefix + '/runtime/status')
                    ).json()
                    return state.runtime.units.some(
                        (unit: {
                            id: { kind: string }
                            reads: { readsCompleted: number } | null
                        }) =>
                            unit.id.kind === 'input.simulator' &&
                            Number(unit.reads?.readsCompleted) > 0,
                    )
                })
                .toBe(true)
            await page
                .getByRole('button', { name: '刷新配置状态', exact: true })
                .click()
            await page.reload()
            await expect(
                page.getByRole('region', {
                    name: '有效配置运行状态',
                    exact: true,
                }),
            ).toContainText('有效配置已激活')
            expect(requests).toBe(1)
            const devices = await (
                await page.request.get(prefix + '/devices')
            ).json()
            expect(devices.version).toBe(version)
            expect(devices.devices).toHaveLength(1)
            expect(devices.devices[0].key).toBe('unapplied-retry')
            expect(
                database(data, (db) =>
                    db
                        .prepare(
                            'SELECT revision, abnormal_exit_count FROM runtime_snapshot',
                        )
                        .all(),
                ),
            ).toEqual([{ revision: 1, abnormal_exit_count: 0 }])
            console.log(
                'Runtime retry browser:',
                await page.evaluate(() => navigator.userAgent),
                'owned origin:',
                url,
                'lost response:',
                lostResponse,
            )
        } finally {
            try {
                await context?.close()
            } finally {
                try {
                    if (host) expect(await host.stop()).toBe(0)
                } finally {
                    await rm(data, { recursive: true, force: true })
                }
            }
        }
    })
}

test('damaged effective snapshot keeps management available and cannot be retried or silently treated as empty', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-damaged-'))
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let context: BrowserContext | undefined
    try {
        const seed = join(data, 'seed.json')
        await writeFile(seed, JSON.stringify({ schemaVersion: 1, units: [] }))
        host = await startOwnedHost(data, 0, [
            `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
        ])
        const version = await initialize(browser, host.url)
        const url = host.url
        expect(await host.stop()).toBe(0)
        host = undefined
        database(data, (db) =>
            db.exec(
                "UPDATE runtime_snapshot SET content_hash = 'private-damaged-detail' WHERE revision = 1",
            ),
        )
        host = await startOwnedHost(data, Number(new URL(url).port))
        const loggedIn = await login(browser, url)
        context = loggedIn.context
        const page = loggedIn.page
        const runtime = page.getByRole('region', {
            name: '有效配置运行状态',
            exact: true,
        })
        await expect(runtime).toContainText('内容哈希不符')
        await expect(runtime).toContainText('不能当作空配置')
        await expect(
            page.getByText('有效配置：尚无有效版本', { exact: true }),
        ).toHaveCount(0)
        await expect(
            runtime.getByRole('button', {
                name: '检查当前有效版本重试',
                exact: true,
            }),
        ).toBeDisabled()
        await expect(
            page.getByRole('button', { name: '查看应用变更清单', exact: true }),
        ).toBeDisabled()
        expect((await page.request.get('/readyz')).status()).toBe(503)
        const state = await (
            await page.request.get(prefix + '/configuration/runtime')
        ).json()
        expect(state.effectiveRevision).toBe(1)
        expect(state.canRetry).toBe(false)
        expect(state.errorCode).toBe('configuration.invalidSnapshot')
        expect(
            (await (await page.request.get(prefix + '/devices')).json())
                .version,
        ).toBe(version)
        expect(
            await (
                await page.request.get(prefix + '/configuration/operations')
            ).json(),
        ).toEqual([])
        expect(
            database(data, (db) =>
                db.prepare('SELECT effective_revision FROM system_state').get(),
            ),
        ).toEqual({ effective_revision: 1 })
    } finally {
        try {
            await context?.close()
        } finally {
            try {
                if (host) expect(await host.stop()).toBe(0)
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
