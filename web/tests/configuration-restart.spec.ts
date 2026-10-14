import { test, expect, type Browser } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

test('fresh browser login after a normal Host restart reads persistent draft and history without replaying an application', async ({
    playwright,
}) => {
    test.setTimeout(120_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-restart-'))
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    let first: Browser | undefined
    let second: Browser | undefined
    try {
        host = await startOwnedHost(data)
        const url = host.url
        first = await playwright.chromium.launch()
        const context = await first.newContext({ baseURL: url })
        context.setDefaultTimeout(15_000)
        context.setDefaultNavigationTimeout(30_000)
        const page = await context.newPage()
        await page.goto('/configuration')
        await expect(page.getByText('首次设置', { exact: true })).toBeVisible()
        await page.getByLabel('账号', { exact: true }).fill('restart-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Restart-fixture-5917')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '配置应用', exact: true }),
        ).toBeVisible()

        // Persist a real nonempty checkpoint through the device form. There is
        // intentionally no input or route: this proves management durability,
        // not device acquisition or the full no-JSON source-to-output workflow.
        await page.getByRole('link', { name: '设备草稿', exact: true }).click()
        await page
            .getByRole('button', { name: '新建设备', exact: true })
            .click()
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('simulator')
        await page
            .getByLabel('设备 key', { exact: true })
            .fill('restart-device')
        await page
            .getByLabel('设备名称', { exact: true })
            .fill('Restart retained device')
        await page
            .getByRole('button', { name: '保存设备草稿', exact: true })
            .click()
        await expect(
            page.getByText('设备草稿已保存；尚未应用。', { exact: true }),
        ).toBeVisible()
        await page.goto('/configuration')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        const review = page.getByRole('region', {
            name: '应用前确认',
            exact: true,
        })
        await expect(review).toContainText('restart-device')
        await review
            .getByLabel('变更说明（选填，最多 1000 字符）')
            .fill('Browser restart checkpoint')
        await review.getByLabel('我已检查变更及影响，确认应用这份草稿').check()
        const admitted = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/configuration/apply'),
        )
        await review
            .getByRole('button', { name: '确认应用配置', exact: true })
            .click()
        const response = await admitted
        expect(response.status()).toBe(202)
        const application = await response.json()
        await expect(
            page.locator(`[data-operation-id="${application.id}"]`),
        ).toContainText('配置已提交并激活')
        const initial = await (
            await page.request.get('/api/edge-admin/v1/configuration/draft')
        ).json()
        expect(initial.changeCount).toBe(0)
        const revision = String(initial.effectiveRevision)
        await expect(
            page.locator(`[data-version="${revision}"]`),
        ).toContainText('Browser restart checkpoint')

        // Close the actual browser process, stop the Host normally, then reopen
        // the same data and management port. No old cookies/CSRF or local editor
        // state are injected into the replacement browser.
        await first.close()
        first = undefined
        expect(await host.stop()).toBe(0)
        host = undefined
        host = await startOwnedHost(data, Number(new URL(url).port))
        expect(host.url).toBe(url)
        second = await playwright.chromium.launch()
        const restored = await second.newContext({ baseURL: url })
        restored.setDefaultTimeout(15_000)
        restored.setDefaultNavigationTimeout(30_000)
        const reopened = await restored.newPage()
        const applications: string[] = []
        reopened.on('request', (request) => {
            if (
                request.method() === 'POST' &&
                /\/configuration\/(apply|versions\/\d+\/rollback)$/.test(
                    new URL(request.url()).pathname,
                )
            )
                applications.push(request.url())
        })
        await reopened.goto('/configuration')
        await expect(
            reopened.getByRole('button', { name: '登录', exact: true }),
        ).toBeVisible()
        await reopened.getByLabel('账号', { exact: true }).fill('restart-admin')
        await reopened
            .getByLabel('密码', { exact: true })
            .fill('Restart-fixture-5917')
        await reopened
            .getByRole('button', { name: '登录', exact: true })
            .click()
        await expect(
            reopened.getByRole('heading', { name: '配置应用', exact: true }),
        ).toBeVisible()
        await expect(
            reopened.getByText(`有效配置：r${revision}`, { exact: true }),
        ).toBeVisible()
        await expect(
            reopened.getByText(
                '本进程尚无操作。重启后操作列表会清空，不代表没有持久配置版本。',
            ),
        ).toBeVisible()
        const history = reopened.getByRole('region', {
            name: '持久配置历史',
            exact: true,
        })
        await expect(history.locator('[data-version]')).toHaveCount(1)
        await expect(
            history.locator(`[data-version="${revision}"]`),
        ).toContainText('Browser restart checkpoint')
        await history
            .getByRole('button', { name: `查看 r${revision}`, exact: true })
            .click()
        const detail = history.getByRole('region', {
            name: `版本 r${revision}`,
            exact: true,
        })
        await detail
            .locator('summary')
            .filter({ hasText: '完整草稿（精确 JSON）' })
            .click()
        await expect(detail.locator('pre')).toContainText('restart-device')
        await expect(detail.getByLabel('版本差异')).toContainText(
            '没有对象内容差异',
        )
        // The history row remains visible during refresh. Its old count is not
        // a completion signal. Await the actual UI reads before adding a test-
        // side request that shares their bounded per-session preview budget.
        const refreshed = [
            '/api/edge-admin/v1/configuration/draft',
            '/api/edge-admin/v1/configuration/versions',
            `/api/edge-admin/v1/configuration/versions/${revision}`,
            `/api/edge-admin/v1/configuration/versions/${revision}/compare-draft`,
        ].map((path) =>
            reopened.waitForResponse(
                (read) =>
                    read.request().method() === 'GET' &&
                    new URL(read.url()).pathname.replace(/\/$/, '') === path,
            ),
        )
        await reopened
            .getByRole('button', { name: '刷新配置状态', exact: true })
            .click()
        for (const read of await Promise.all(refreshed)) {
            expect(read.status(), new URL(read.url()).pathname).toBe(200)
            await read.finished()
        }
        await expect(history.locator('[data-version]')).toHaveCount(1)
        const restoredDraftResponse = await reopened.request.get(
            '/api/edge-admin/v1/configuration/draft',
        )
        const restoredDraft = await restoredDraftResponse.json()
        expect(
            restoredDraftResponse.status(),
            JSON.stringify(restoredDraft),
        ).toBe(200)
        expect(restoredDraft.version).toBe(initial.version)
        expect(
            await (
                await reopened.request.get(
                    '/api/edge-admin/v1/configuration/operations',
                )
            ).json(),
        ).toEqual([])
        const oldOperation = await reopened.request.get(
            '/api/edge-admin/v1/configuration/operations/' + application.id,
        )
        expect(oldOperation.status()).toBe(404)
        await reopened
            .getByRole('link', { name: '设备草稿', exact: true })
            .click()
        await expect(
            reopened.getByRole('button', {
                name: /Restart retained device restart-device/,
            }),
        ).toBeVisible()
        expect(applications).toEqual([])
        expect(await reopened.evaluate(() => ({ ...localStorage }))).toEqual({})
        expect(await reopened.evaluate(() => ({ ...sessionStorage }))).toEqual(
            {},
        )
        console.log(
            'Normal restart browser:',
            await reopened.evaluate(() => navigator.userAgent),
            'owned origin:',
            url,
        )
    } finally {
        try {
            await first?.close()
            await second?.close()
        } finally {
            try {
                if (host) expect(await host.stop()).toBe(0)
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
