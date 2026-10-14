import { test, expect, type Page } from '@playwright/test'
import { DatabaseSync } from 'node:sqlite'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Recovery-fixture-9381!'
async function login(page: Page, setup: boolean) {
    await page.goto('/configuration')
    await page.getByLabel('账号', { exact: true }).fill('recovery-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page
        .getByRole('button', {
            name: setup ? '创建管理员并登录' : '登录',
            exact: true,
        })
        .click()
    await expect(
        page.getByRole('heading', { name: '配置应用', exact: true }),
    ).toBeVisible()
}
for (const lostResponse of [false, true]) {
    test(`administrator recovers damaged snapshot with dirty draft retained; lost response=${lostResponse}`, async ({
        browser,
    }) => {
        test.setTimeout(120_000)
        const data = await mkdtemp(
            join(tmpdir(), 'tenonedge-browser-recovery-'),
        )
        let host = await startOwnedHost(data)
        let context = await browser.newContext({ baseURL: host.url })
        try {
            let page = await context.newPage()
            await login(page, true)
            const session = await (
                await page.request.get(root + '/auth/session')
            ).json()
            const headers = { 'X-CSRF-Token': session.csrfToken }
            let version = '0'
            let id: number | undefined
            // Real API fixture, not full no-JSON configuration acceptance.
            for (const name of ['Historical', 'Effective', 'Unapplied']) {
                const saved = await page.request[id ? 'put' : 'post'](
                    root + '/devices' + (id ? '/' + id : ''),
                    {
                        headers,
                        data: {
                            expectedVersion: version,
                            key: 'meter',
                            name,
                            kind: 'simulator',
                            enabled: false,
                        },
                    },
                )
                expect(saved.status()).toBe(200)
                const device = await saved.json()
                version = device.version
                id = device.device.id
                if (name === 'Unapplied') continue
                const checked = await page.request.post(
                    root + '/configuration/review',
                    { headers, data: { expectedVersion: version } },
                )
                expect(checked.status()).toBe(200)
                const review = await checked.json()
                const applied = await page.request.post(
                    root + '/configuration/apply',
                    {
                        headers,
                        data: {
                            expectedVersion: version,
                            expectedEffectiveRevision: review.effectiveRevision,
                            expectedPlanHash: review.planHash,
                            confirmed: true,
                        },
                    },
                )
                expect(applied.status()).toBe(202)
                const operation = await applied.json()
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
            }
            const draftBefore = await (
                await page.request.get(root + '/devices')
            ).json()
            await context.close()
            expect(await host.stop()).toBe(0)
            const db = new DatabaseSync(join(data, 'config.db'))
            try {
                db.exec(
                    "UPDATE runtime_snapshot SET content='{}' WHERE revision=2",
                )
            } finally {
                db.close()
            }
            host = await startOwnedHost(data)
            context = await browser.newContext({ baseURL: host.url })
            page = await context.newPage()
            await login(page, false)
            expect((await page.request.get('/readyz')).status()).toBe(503)
            await expect(
                page.getByRole('button', {
                    name: '检查当前有效版本重试',
                    exact: true,
                }),
            ).toBeDisabled()
            await page
                .getByRole('button', { name: '查看 r1', exact: true })
                .click()
            const detail = page.getByRole('region', {
                name: '版本 r1',
                exact: true,
            })
            await detail
                .getByText('完整草稿（精确 JSON）', { exact: true })
                .click()
            await expect(detail.locator('pre').first()).toContainText(
                'Historical',
            )
            const panel = detail.getByRole('region', {
                name: '损坏有效快照恢复',
                exact: true,
            })
            await expect(panel).toContainText('当前全部草稿')
            let writes = 0
            page.on('request', (request) => {
                if (
                    request.method() === 'POST' &&
                    request.url().endsWith('/1/recover')
                )
                    writes++
            })
            await expect(
                panel.getByRole('button', {
                    name: '确认恢复完整历史版本',
                    exact: true,
                }),
            ).toHaveCount(0)
            await panel
                .getByRole('button', {
                    name: '预检恢复到此历史版本',
                    exact: true,
                })
                .click()
            const submit = panel.getByRole('button', {
                name: '确认恢复完整历史版本',
                exact: true,
            })
            await expect(submit).toBeDisabled()
            await panel
                .getByLabel(
                    '我已检查完整历史内容和目标，确认恢复并生成新版本',
                    { exact: true },
                )
                .check()
            await expect(submit).toBeDisabled()
            await panel
                .getByLabel(
                    '我理解全部草稿保留且不会应用，恢复后会重新核对未应用修改',
                    { exact: true },
                )
                .check()
            expect(writes).toBe(0)
            if (lostResponse) {
                await page.route(
                    '**/configuration/versions/1/recover',
                    async (route) => {
                        expect((await route.fetch()).status()).toBe(202)
                        await route.abort('failed')
                    },
                )
            }
            await submit.click()
            await expect
                .poll(
                    async () =>
                        (
                            await (
                                await page.request.get(
                                    root + '/configuration/runtime',
                                )
                            ).json()
                        ).effectiveRevision,
                )
                .toBe(3)
            await expect
                .poll(async () => (await page.request.get('/readyz')).status())
                .toBe(200)
            expect(writes).toBe(1)
            expect(
                await (await page.request.get(root + '/devices')).json(),
            ).toEqual(draftBefore)
            const history = await (
                await page.request.get(root + '/configuration/versions/3')
            ).json()
            expect(history.version.origin).toBe('recovery')
            expect(history.draft.devices[0].name).toBe('Historical')
            await page.reload()
            await expect(
                page.getByRole('region', {
                    name: '损坏有效快照恢复',
                    exact: true,
                }),
            ).toHaveCount(0)
            expect(writes).toBe(1)
            await context.close()
            expect(await host.stop()).toBe(0)
            host = await startOwnedHost(data)
            context = await browser.newContext({ baseURL: host.url })
            page = await context.newPage()
            await login(page, false)
            expect((await page.request.get('/readyz')).status()).toBe(200)
            expect(
                (
                    await (
                        await page.request.get(root + '/configuration/runtime')
                    ).json()
                ).effectiveRevision,
            ).toBe(3)
            expect(
                await (await page.request.get(root + '/devices')).json(),
            ).toEqual(draftBefore)
            expect(
                (
                    await (
                        await page.request.get(
                            root + '/audit?action=runtime.snapshotRecovered',
                        )
                    ).json()
                ).items,
            ).toHaveLength(1)
        } finally {
            await context.close()
            await host.stop()
            await rm(data, { recursive: true, force: true })
        }
    })
}
