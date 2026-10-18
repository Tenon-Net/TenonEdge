import { expect, test, type Browser } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Device-workflow-browser-8361!'
const httpAddress = new URL(
    process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
).hostname
async function fixture(browser: Browser) {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-device-workflow-'))
    const host = await startOwnedHost(data, 0, [], httpAddress)
    const context = await browser.newContext({ baseURL: host.url })
    const page = await context.newPage()
    page.setDefaultTimeout(15_000)
    await page.goto('/')
    await page.getByLabel('账号', { exact: true }).fill('device-admin')
    for (const name of ['密码', '确认密码'])
        await page.getByLabel(name, { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    async function write(path: string, body: unknown, expected = 200) {
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const response = await page.request.post(root + path, {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: body,
        })
        expect(response.status(), await response.text()).toBe(expected)
        return response.json()
    }
    return {
        page,
        write,
        close: async () => {
            await context.close()
            await host.stop()
            await rm(data, { recursive: true, force: true })
        },
    }
}

test('device keys follow names until manual editing, avoid existing suggestions and stay immutable after real draft persistence', async ({
    browser,
}) => {
    const f = await fixture(browser)
    const { page } = f
    try {
        page.on('dialog', (dialog) => dialog.accept())
        await page.goto('/devices')
        const key = page.getByLabel('设备 key', { exact: true })
        const name = page.getByLabel('设备名称', { exact: true })
        const add = page.getByRole('button', { name: '新建设备', exact: true })
        const save = page.getByRole('button', {
            name: '保存设备草稿',
            exact: true,
        })
        const refresh = page.getByRole('button', {
            name: '刷新设备与连接',
            exact: true,
        })
        const posted: string[] = []
        page.on('request', (request) => {
            if (
                request.method() !== 'GET' &&
                request.url().includes('/devices')
            )
                posted.push(request.url())
        })
        await add.click()
        await name.fill('电表一')
        await expect(key).toHaveValue('u7535-u8868-u4e00')
        await name.fill('Meter One')
        await expect(key).toHaveValue('meter-one')
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('simulator')
        await refresh.click()
        await expect(key).toHaveValue('meter-one')
        expect(posted).toHaveLength(0)
        await save.click()
        await expect(page.getByRole('status')).toHaveText(
            '设备草稿已保存；尚未应用。',
        )
        await page.getByRole('button', { name: /Meter One meter-one/ }).click()
        await expect(key).toBeDisabled()
        await name.fill('Renamed')
        await expect(key).toHaveValue('meter-one')
        await expect(
            page.getByRole('button', { name: '删除设备', exact: true }),
        ).toBeDisabled()
        await save.click()
        await expect(page.getByRole('status')).toHaveText(
            '设备草稿已保存；尚未应用。',
        )
        await add.click()
        await name.fill('Meter One')
        await expect(key).toHaveValue('meter-one-2')
        await key.fill('custom-key')
        await name.fill('Meter Two')
        await expect(key).toHaveValue('custom-key')
        await key.fill('')
        await name.fill('Meter Three')
        await expect(key).toHaveValue('')
        await page
            .getByRole('button', { name: '重新按名称生成 key', exact: true })
            .click()
        await expect(key).toHaveValue('meter-three')
        await page
            .getByRole('combobox', { name: '设备类型', exact: true })
            .selectOption('simulator')
        // Another editor claims the suggestion. A refresh must not mutate this editor or its captured version.
        const before = await (await page.request.get(root + '/devices')).json()
        await f.write('/devices', {
            expectedVersion: before.version,
            key: 'meter-three',
            name: 'Concurrent',
            kind: 'simulator',
        })
        await refresh.click()
        await expect(key).toHaveValue('meter-three')
        const rejected = page.waitForResponse(
            (response) =>
                response.request().method() === 'POST' &&
                response.url().endsWith('/devices') &&
                response.status() === 409,
        )
        await save.click()
        await rejected
        await expect(name).toHaveValue('Meter Three')
        await expect(key).toHaveValue('meter-three')
        await page
            .getByRole('button', { name: '关闭设备编辑', exact: true })
            .click()
        await add.click()
        await name.fill('Meter Three')
        await expect(key).toHaveValue('meter-three-2')
        const current = await (await page.request.get(root + '/devices')).json()
        expect(
            current.devices.map((device: { key: string }) => device.key),
        ).toEqual(['meter-one', 'meter-three'])
        expect(
            (await (await page.request.get(root + '/runtime/status')).json())
                .coordinator.activeRevision,
        ).toBeNull()
    } finally {
        await f.close()
    }
})

test('deletion review lists disabled routes and retained versions, fails closed on errors and never replays a lost deletion response', async ({
    browser,
}) => {
    test.setTimeout(100_000)
    const f = await fixture(browser)
    const { page, write } = f
    try {
        page.on('dialog', (dialog) => dialog.accept())
        // API fixtures build references; all review/confirmation actions use the real product page.
        const connection = await write('/connections', {
            expectedVersion: '0',
            key: 'target',
            name: 'Blocked local target',
            kind: 'http',
            http: { baseAddress: 'http://127.0.0.1:1/' },
        })
        const device = await write('/devices', {
            expectedVersion: connection.version,
            key: 'meter',
            name: 'Meter',
            kind: 'simulator',
        })
        const input = await write(`/devices/${device.device.id}/inputs`, {
            expectedVersion: device.version,
            key: 'read',
            name: 'Read',
            simulator: { sampleJson: '{"value":42}' },
        })
        const disabledRoute = await write('/routes', {
            expectedVersion: input.version,
            key: 'using-meter',
            name: 'Disabled reference',
            enabled: false,
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
                ],
                edges: [],
                layout: { nodes: {} },
            },
        })
        // An enabled wired path is needed for a real frozen input; an entirely
        // disabled route correctly compiles no input and must not be called a retained use.
        const route = await write('/routes', {
            expectedVersion: disabledRoute.version,
            key: 'forward',
            name: 'Forward reference',
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
                        config: { connectionId: connection.connection.id },
                    },
                ],
                edges: [{ source: 'in', target: 'out' }],
                layout: { nodes: {} },
            },
        })
        await page.goto('/devices')
        await page.getByRole('button', { name: /Meter meter/ }).click()
        const remove = page.getByRole('button', {
            name: '删除设备',
            exact: true,
        })
        await remove.click()
        const panel = page.getByRole('region', {
            name: '设备删除核对',
            exact: true,
        })
        await expect(panel).toContainText('Disabled reference')
        await expect(panel).toContainText('using-meter')
        await expect(panel).toContainText('草稿停用')
        await expect(
            panel.getByRole('button', {
                name: '确认删除设备草稿',
                exact: true,
            }),
        ).toBeDisabled()
        const checked = await write('/configuration/review', {
            expectedVersion: route.version,
        })
        const operation = await write(
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
        await panel
            .getByRole('button', { name: '重新核对删除条件', exact: true })
            .click()
        await expect(panel).toContainText('有效版本')
        await panel.getByText(/^r1 · 有效版本 \(\d+\)$/).click()
        await expect(panel).toContainText('input.simulator/meter/read')
        await page.route('**/deletion-review?*', (route) =>
            route.fulfill({
                status: 500,
                contentType: 'application/problem+json',
                body: JSON.stringify({
                    status: 500,
                    title: 'Injected read failure',
                }),
            }),
        )
        await panel
            .getByRole('button', { name: '重新核对删除条件', exact: true })
            .click()
        await expect(
            panel.getByRole('button', {
                name: '确认删除设备草稿',
                exact: true,
            }),
        ).toHaveCount(0)
        await page.unroute('**/deletion-review?*')
        await panel
            .getByRole('button', { name: '取消删除核对', exact: true })
            .click()
        // An unreferenced draft is reviewed separately; deleting it does not alter r1.
        const unused = await write('/devices', {
            expectedVersion: route.version,
            key: 'unused',
            name: 'Unused',
            kind: 'simulator',
        })
        await page
            .getByRole('button', { name: '刷新设备与连接', exact: true })
            .click()
        await page.getByRole('button', { name: /Unused unused/ }).click()
        await remove.click()
        await expect(
            panel.getByRole('button', {
                name: '确认删除设备草稿',
                exact: true,
            }),
        ).toBeEnabled()
        let deletes = 0
        await page.route(`**/devices/${unused.device.id}?*`, async (route) => {
            if (route.request().method() !== 'DELETE') {
                await route.continue()
                return
            }
            deletes++
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await panel
            .getByRole('button', { name: '确认删除设备草稿', exact: true })
            .click()
        await expect(panel).toContainText('删除未取得成功确认')
        await expect(
            panel.getByRole('button', {
                name: '确认删除设备草稿',
                exact: true,
            }),
        ).toHaveCount(0)
        await panel
            .getByRole('button', { name: '重新核对删除条件', exact: true })
            .click()
        await expect(
            panel.getByRole('button', {
                name: '确认删除设备草稿',
                exact: true,
            }),
        ).toHaveCount(0)
        expect(deletes).toBe(1)
        await panel
            .getByRole('button', { name: '取消删除核对', exact: true })
            .click()
        await page
            .getByRole('button', { name: '刷新设备与连接', exact: true })
            .click()
        await expect(
            page.getByRole('button', { name: /Unused unused/ }),
        ).toHaveCount(0)
        expect(
            (
                await (
                    await page.request.get(root + '/configuration/runtime')
                ).json()
            ).effectiveRevision,
        ).toBe(1)
        const audit = await (
            await page.request.get(root + '/audit?action=device.draftDeleted')
        ).json()
        expect(audit.items).toHaveLength(1)
    } finally {
        await f.close()
    }
})
