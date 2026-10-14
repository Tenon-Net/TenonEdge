import { test, expect } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'
const root = '/api/edge-admin/v1'
const endpoint = root + '/openapi/v1.json'
test('live documentation: roles, deep refresh, current schema, offline resources and request errors', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-docs-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        expect((await page.request.get(endpoint)).status()).toBe(401)
        const external: string[] = []
        page.on('request', (request) => {
            if (
                request.url().startsWith('http') &&
                new URL(request.url()).origin !== new URL(host.url).origin
            )
                external.push(request.url())
        })
        await page.goto('/api-docs')
        await page.getByLabel('账号', { exact: true }).fill('docs-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Docs-test-password-1739!')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Docs-test-password-1739!')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: 'API 文档', exact: true }),
        ).toBeVisible()
        const response = await page.request.get(endpoint)
        expect(response.status()).toBe(200)
        const document = await response.json()
        const count = Object.values(document.paths).reduce(
            (sum: number, item) =>
                sum +
                Object.keys(item as object).filter((key) =>
                    [
                        'get',
                        'post',
                        'put',
                        'patch',
                        'delete',
                        'options',
                        'head',
                        'trace',
                    ].includes(key),
                ).length,
            0,
        )
        const countText = `匹配 ${count} / ${count} 个操作`
        await expect(page.getByText(countText, { exact: true })).toBeVisible()
        await page
            .getByLabel('筛选方法、路径、说明或 operationId', { exact: true })
            .fill('getRuntimeParameters')
        const operation = page.locator('details').filter({
            has: page.locator('summary', {
                hasText: 'GET /api/edge-admin/v1/system/runtime-parameters',
            }),
        })
        await operation.locator('summary').click()
        await expect(operation.getByTestId('operation-document')).toContainText(
            'getRuntimeParameters',
        )
        await page
            .getByText('完整 OpenAPI 文档（含模型和引用定义）', { exact: true })
            .click()
        await expect(page.getByTestId('complete-document')).toContainText(
            'debugObjectBytes',
        )
        expect(
            JSON.parse(await page.getByTestId('complete-document').innerText()),
        ).toEqual(document)
        await page.reload()
        await expect(page.getByText(countText, { exact: true })).toBeVisible()
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
                            temporaryPassword: 'Docs-temp-password-9813!',
                            isEnabled: true,
                        },
                    })
                ).status(),
            ).toBe(200)
            const other = await browser.newContext({ baseURL: host.url })
            try {
                const user = await other.newPage()
                await user.goto('/api-docs')
                await user.getByLabel('账号', { exact: true }).fill(role)
                await user
                    .getByLabel('密码', { exact: true })
                    .fill('Docs-temp-password-9813!')
                await user
                    .getByRole('button', { name: '登录', exact: true })
                    .click()
                await user
                    .getByLabel('原密码', { exact: true })
                    .fill('Docs-temp-password-9813!')
                await user
                    .getByLabel('新密码', { exact: true })
                    .fill('Docs-changed-password-9813!')
                await user
                    .getByRole('button', { name: '保存新密码', exact: true })
                    .click()
                await expect(
                    user.getByRole('heading', {
                        name: 'API 文档',
                        exact: true,
                    }),
                ).toBeVisible()
                await expect(
                    user.getByText(countText, { exact: true }),
                ).toBeVisible()
            } finally {
                await other.close()
            }
        }
        let requests = 0
        await page.route('**/openapi/v1.json', (route) => {
            requests++
            return route.fulfill({
                status: 503,
                contentType: 'application/problem+json',
                body: '{"status":503}',
            })
        })
        await page
            .getByRole('button', { name: '重新读取 API 文档', exact: true })
            .click()
        await expect(page.getByText(/本次文档读取失败/)).toBeVisible()
        await expect(page.getByText(countText, { exact: true })).toHaveCount(0)
        expect(requests).toBe(1)
        await page.unroute('**/openapi/v1.json')
        await page
            .getByRole('button', { name: '重新读取 API 文档', exact: true })
            .click()
        await expect(page.getByText(countText, { exact: true })).toBeVisible()
        expect(external).toEqual([])
        await page.route('**/openapi/v1.json', (route) =>
            route.fulfill({
                status: 401,
                contentType: 'application/problem+json',
                body: '{"status":401}',
            }),
        )
        await page
            .getByRole('button', { name: '重新读取 API 文档', exact: true })
            .click()
        await expect(
            page.getByRole('button', { name: '登录', exact: true }),
        ).toBeVisible()
        await expect(page.getByTestId('complete-document')).toHaveCount(0)
    } finally {
        await context.close()
        await host.stop()
        await rm(data, { recursive: true, force: true })
    }
})
