import { test, expect, type Page, type Locator } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Gateway-name-7831!'
const original = '原网关'
const renamed = '配电房 <img src=x onerror=alert(1)>'
function editor(page: Page) {
    return page.getByRole('region', { name: '网关显示名称', exact: true })
}
async function setup(page: Page) {
    await page.goto('/system')
    await page.getByLabel('账号', { exact: true }).fill('name-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page.getByLabel('网关名称（可选）', { exact: true }).fill(original)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        editor(page).getByRole('button', { name: '修改网关名称', exact: true }),
    ).toBeEnabled()
}
async function save(panel: Locator) {
    await panel.getByRole('checkbox').check()
    await panel
        .getByRole('button', { name: '确认保存名称', exact: true })
        .click()
}
async function expectSaved(panel: Locator) {
    await expect(
        panel.getByText('名称已保存并记审计。', { exact: true }),
    ).toBeVisible()
    await expect(
        panel.getByRole('button', { name: '修改网关名称', exact: true }),
    ).toBeEnabled()
}

test('administrator renames and clears with audit, restart persistence, live title updates and read-only roles', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-name-browser-'))
    let host = await startOwnedHost(data)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await setup(page)
        const before = await (
            await page.request.get(root + '/system/info')
        ).json()
        const draft = await (await page.request.get(root + '/devices')).json()
        let panel = editor(page)
        await panel
            .getByRole('button', { name: '修改网关名称', exact: true })
            .click()
        await expect(
            panel.getByRole('button', { name: '确认保存名称', exact: true }),
        ).toBeDisabled()
        await panel
            .getByLabel('新网关名称', { exact: true })
            .fill('  ' + renamed + '  ')
        await save(panel)
        await expectSaved(panel)
        await expect(page).toHaveTitle(renamed + ' · TenonEdge')
        await expect(page.getByRole('banner')).toContainText(renamed)
        await expect(page.getByRole('banner').locator('img')).toHaveCount(0)
        expect(
            (await (await page.request.get(root + '/system/info')).json())
                .nodeId,
        ).toBe(before.nodeId)
        await context.close()
        expect(await host.stop()).toBe(0)
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/system')
        await page.getByLabel('账号', { exact: true }).fill('name-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        panel = editor(page)
        await expect(page).toHaveTitle(renamed + ' · TenonEdge')
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        for (const role of ['viewer', 'configurator']) {
            expect(
                (
                    await page.request.post(root + '/users', {
                        headers: { 'X-CSRF-Token': session.csrfToken },
                        data: {
                            accountName: role,
                            role,
                            temporaryPassword: password,
                            isEnabled: true,
                        },
                    })
                ).status(),
            ).toBe(200)
            const other = await browser.newContext({ baseURL: host.url })
            try {
                const user = await other.newPage()
                await user.goto('/system')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user.getByLabel('密码', { exact: true }).fill(password)
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user.getByLabel('原密码', { exact: true }).fill(password)
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Changed-name-5613!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(user).toHaveTitle(renamed + ' · TenonEdge')
                await expect(editor(user)).toHaveCount(0)
                const read = await (
                    await user.request.get(root + '/system/info')
                ).json()
                expect(read.gatewayName).toBe(renamed)
                const own = await (
                    await user.request.get(root + '/auth/session')
                ).json()
                expect(
                    (
                        await user.request.put(root + '/system/name', {
                            headers: { 'X-CSRF-Token': own.csrfToken },
                            data: { name: 'forbidden', expectedName: renamed },
                        })
                    ).status(),
                ).toBe(403)
            } finally {
                await other.close()
            }
        }
        await panel
            .getByRole('button', { name: '修改网关名称', exact: true })
            .click()
        await save(panel)
        await expect(
            panel.getByText('名称没有变化，未重复写入或记变更审计。', {
                exact: true,
            }),
        ).toBeVisible()
        await panel
            .getByRole('button', { name: '修改网关名称', exact: true })
            .click()
        await panel.getByLabel('新网关名称', { exact: true }).fill('')
        await save(panel)
        await expectSaved(panel)
        await expect(page).toHaveTitle('TenonEdge')
        await expect(page.getByRole('banner')).toContainText('未命名网关')
        const after = await (
            await page.request.get(root + '/system/info')
        ).json()
        expect(after.gatewayName).toBeNull()
        expect(after.nodeId).toBe(before.nodeId)
        expect(after.firstSetup).toEqual(before.firstSetup)
        expect(
            await (await page.request.get(root + '/devices')).json(),
        ).toEqual(draft)
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .coordinator.activeRevision,
        ).toBeNull()
        const audit = await (
            await page.request.get(
                root + '/audit?action=system.gatewayNameChanged',
            )
        ).json()
        expect(audit.items).toHaveLength(2)
        expect(JSON.parse(audit.items[0].detail)).toEqual({
            previousName: renamed,
            name: null,
        })
        expect(JSON.parse(audit.items[1].detail)).toEqual({
            previousName: original,
            name: renamed,
        })
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})

test('rename editor freezes original name, preserves validation input and never replays unknown results', async ({
    browser,
}) => {
    test.setTimeout(60_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-name-errors-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        await setup(page)
        const panel = editor(page)
        let writes = 0
        page.on('request', (request) => {
            if (
                request.method() === 'PUT' &&
                request.url().endsWith('/system/name')
            )
                writes++
        })
        await panel
            .getByRole('button', { name: '修改网关名称', exact: true })
            .click()
        await panel.getByLabel('新网关名称', { exact: true }).fill('local')
        page.once('dialog', (dialog) => dialog.dismiss())
        await page.getByRole('link', { name: '总览', exact: true }).click()
        await expect(page).toHaveURL(/\/system$/)
        await expect(
            panel.getByLabel('新网关名称', { exact: true }),
        ).toHaveValue('local')
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        expect(
            (
                await page.request.put(root + '/system/name', {
                    headers: { 'X-CSRF-Token': session.csrfToken },
                    data: { name: 'other-admin', expectedName: original },
                })
            ).status(),
        ).toBe(200)
        await page
            .getByRole('region', { name: '系统信息', exact: true })
            .getByRole('button', { name: '刷新本区域', exact: true })
            .click()
        await expect(panel.getByText(/本次编辑仍绑定原名称/)).toBeVisible()
        await expect(
            panel.getByLabel('新网关名称', { exact: true }),
        ).toHaveValue('local')
        await save(panel)
        await expect(
            panel.getByText(/改名请求失败或结果尚未确认/),
        ).toBeVisible()
        await expect(panel.getByRole('form')).toHaveCount(0)
        await expect(
            panel.getByRole('button', { name: '修改网关名称', exact: true }),
        ).toBeDisabled()
        expect(writes).toBe(1)
        await panel
            .getByRole('button', { name: '重新读取名称', exact: true })
            .click()
        await panel
            .getByRole('button', { name: '修改网关名称', exact: true })
            .click()
        await expect(
            panel.getByLabel('新网关名称', { exact: true }),
        ).toHaveValue('other-admin')
        await panel
            .getByLabel('新网关名称', { exact: true })
            .fill('correctable')
        // Controlled 400 response fixture proves UI retention; real server validation is covered by GatewayNameTests.
        await page.route('**/system/name', (route) =>
            route.fulfill({
                status: 400,
                contentType: 'application/problem+json',
                body: '{"status":400,"errors":{"name":["validation-fixture"]}}',
            }),
        )
        await save(panel)
        await expect(panel.getByText(/validation-fixture/)).toBeVisible()
        await expect(
            panel.getByLabel('新网关名称', { exact: true }),
        ).toHaveValue('correctable')
        await expect(panel.getByRole('checkbox')).not.toBeChecked()
        await page.unroute('**/system/name')
        await panel
            .getByLabel('新网关名称', { exact: true })
            .fill('saved-with-lost-response')
        await page.route('**/system/name', async (route) => {
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await save(panel)
        await expect(
            panel.getByText(/改名请求失败或结果尚未确认/),
        ).toBeVisible()
        await expect(
            panel.getByRole('button', { name: '修改网关名称', exact: true }),
        ).toBeDisabled()
        await page.unroute('**/system/name')
        await panel
            .getByRole('button', { name: '重新读取名称', exact: true })
            .click()
        await expect(page).toHaveTitle('saved-with-lost-response · TenonEdge')
        await expect(
            panel.getByRole('button', { name: '修改网关名称', exact: true }),
        ).toBeEnabled()
        expect(writes).toBe(3)
        expect(
            (
                await (
                    await page.request.get(
                        root + '/audit?action=system.gatewayNameChanged',
                    )
                ).json()
            ).items,
        ).toHaveLength(2)
        await page.route('**/system/info', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: '{"status":500}',
            }),
        )
        await panel
            .getByRole('button', { name: '重新读取名称', exact: true })
            .click()
        await expect(
            panel.getByText('名称读取失败，暂不能开始或提交修改。', {
                exact: true,
            }),
        ).toBeVisible()
        await expect(
            panel.getByRole('button', { name: '修改网关名称', exact: true }),
        ).toBeDisabled()
        await page.unroute('**/system/info')
        await panel
            .getByRole('button', { name: '重新读取名称', exact: true })
            .click()
        await expect(
            panel.getByRole('button', { name: '修改网关名称', exact: true }),
        ).toBeEnabled()
        expect(writes).toBe(3)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
