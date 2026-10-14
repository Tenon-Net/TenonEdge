import { test, expect } from '@playwright/test'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const exportRoot = root + '/audit/export'
const password = 'Audit-browser-9472!'

test('real read-only audit paging, complete native CSV, lost response recovery and role boundaries', async ({
    browser,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-audit-browser-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({
        baseURL: host.url,
        acceptDownloads: true,
    })
    try {
        const page = await context.newPage()
        await page.addInitScript(() => {
            const original = window.fetch
            window.fetch = (...args) => {
                const input = args[0]
                const url = input instanceof Request ? input.url : String(input)
                if (url.includes('/audit/export/') && url.endsWith('/download'))
                    throw new Error('CSV must be a native download')
                return original(...args)
            }
        })
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('audit-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('link', { name: '审计记录', exact: true }),
        ).toBeVisible()
        // Fixture inventory, not simulated product functionality. Real Host reads/exports these rows from config.db.
        const database = new DatabaseSync(join(data, 'config.db'))
        try {
            database.exec('PRAGMA busy_timeout=30000; BEGIN IMMEDIATE')
            const insert = database.prepare(
                "INSERT INTO audit_log (id,occurred_at,account_name,action,target,result,detail) VALUES (?,?,'fixture','fixture.csv','object','success',?)",
            )
            for (let id = 1; id <= 1205; id++)
                insert.run(id, Date.now(), 'row-' + id)
            database.exec('COMMIT')
        } finally {
            database.close()
        }
        await page.getByRole('link', { name: '审计记录', exact: true }).click()
        await page
            .getByLabel('账号（精确匹配）', { exact: true })
            .fill('fixture')
        await page
            .getByLabel('操作代码（精确匹配）', { exact: true })
            .fill('fixture.csv')
        await page
            .getByLabel('对象（精确匹配）', { exact: true })
            .fill('object')
        await page
            .getByRole('button', { name: '应用筛选', exact: true })
            .click()
        const table = page.getByRole('table', { name: '审计记录', exact: true })
        await expect(table.locator('tbody tr')).toHaveCount(50)
        await expect(
            table.locator('tbody tr').first().locator('td').first(),
        ).toHaveText('1205')
        await page.getByRole('button', { name: '下一页', exact: true }).click()
        await expect(
            table.locator('tbody tr').first().locator('td').first(),
        ).toHaveText('1155')
        const panel = page.getByRole('region', {
            name: '导出审计 CSV',
            exact: true,
        })
        await expect(
            panel.getByRole('button', {
                name: '确认生成审计 CSV',
                exact: true,
            }),
        ).toBeDisabled()
        await page
            .getByLabel('对象（精确匹配）', { exact: true })
            .fill('unapplied')
        await expect(panel.getByRole('checkbox')).toBeDisabled()
        await page
            .getByLabel('对象（精确匹配）', { exact: true })
            .fill('object')
        let starts = 0
        await page.route('**/api/edge-admin/v1/audit/export', async (route) => {
            if (route.request().method() !== 'POST') return route.continue()
            starts++
            const response = await route.fetch()
            expect(response.status()).toBe(202)
            await route.abort('failed')
        })
        await panel.getByRole('checkbox').check()
        await panel
            .getByRole('button', { name: '确认生成审计 CSV', exact: true })
            .click()
        await expect(
            panel.getByText(/请求结果未确认，未自动重放/),
        ).toBeVisible()
        await expect(
            panel.getByRole('button', {
                name: '确认生成审计 CSV',
                exact: true,
            }),
        ).toBeDisabled()
        await page.unroute('**/api/edge-admin/v1/audit/export')
        await page.reload()
        await expect(
            panel.getByText('文件已生成并记审计，可以下载', { exact: true }),
        ).toBeVisible()
        await expect(panel.getByText(/1205 条记录/)).toBeVisible()
        await expect(
            panel.getByText(/本次导出的固定筛选：.*fixture.csv/),
        ).toBeVisible()
        const before = (await (await page.request.get(exportRoot)).json())
            .export
        const event = page.waitForEvent('download')
        await panel
            .getByRole('link', { name: '下载完整审计 CSV', exact: true })
            .click()
        const file = await event
        expect(await file.failure()).toBeNull()
        expect(file.suggestedFilename()).toMatch(
            /^tenonedge-audit-[0-9a-f-]+\.csv$/,
        )
        const bytes = await readFile((await file.path())!)
        expect(Array.from(bytes.subarray(0, 3))).toEqual([239, 187, 191])
        const rows = bytes.toString('utf8').split('\r\n').filter(Boolean)
        expect(rows).toHaveLength(1206)
        expect(rows[1]).toMatch(/^"1205",/)
        expect(rows.at(-1)).toMatch(/^"1",/)
        expect(bytes.length.toString()).toBe(before.fileBytes)
        expect(bytes.toString('utf8')).not.toContain('audit.export')
        expect(starts).toBe(1)
        await expect(
            panel.getByText('HTTP 响应已写出', { exact: true }),
        ).toBeVisible()
        const audit = await (
            await page.request.get(root + '/audit?action=audit.export')
        ).json()
        expect(audit.items).toHaveLength(1)
        expect(audit.items[0].accountName).toBe('audit-admin')
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        for (const role of ['viewer', 'configurator']) {
            const created = await page.request.post(root + '/users', {
                headers: { 'X-CSRF-Token': session.csrfToken },
                data: {
                    accountName: role,
                    role,
                    temporaryPassword: password,
                    isEnabled: true,
                },
            })
            expect(created.status()).toBe(200)
            const other = await browser.newContext({ baseURL: host.url })
            try {
                const user = await other.newPage()
                await user.goto('/audit')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user.getByLabel('密码', { exact: true }).fill(password)
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user.getByLabel('原密码', { exact: true }).fill(password)
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Audit-changed-6821!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    user.getByText('仅管理员可查询和导出审计记录。', {
                        exact: true,
                    }),
                ).toBeVisible()
                await expect(
                    user.getByRole('link', { name: '审计记录', exact: true }),
                ).toHaveCount(0)
                expect((await user.request.get(root + '/audit')).status()).toBe(
                    403,
                )
                expect(
                    (
                        await user.request.get(
                            exportRoot + '/' + before.id + '/download',
                        )
                    ).status(),
                ).toBe(403)
            } finally {
                await other.close()
            }
        }
        await page.screenshot({
            path: '/tmp/tenonedge-audit-csv/browser-audit.png',
            fullPage: true,
        })
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
