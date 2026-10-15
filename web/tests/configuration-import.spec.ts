import { test, expect, type Page } from '@playwright/test'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const endpoint = root + '/configuration/import'
const password = 'Import-browser-6843!'
const owned = test.extend<{
    importHost: { url: string; restart: () => Promise<void> }
}>({
    baseURL: async ({ importHost }, use) => {
        await use(importHost.url)
    },
    importHost: async ({ browserName }, use) => {
        const data = await mkdtemp(
            join(tmpdir(), `tenonedge-import-${browserName}-`),
        )
        let host = await startOwnedHost(data)
        try {
            await use({
                url: host.url,
                restart: async () => {
                    const port = Number(new URL(host.url).port)
                    await host.stop()
                    host = await startOwnedHost(data, port)
                },
            })
        } finally {
            await host.stop()
            await rm(data, { recursive: true, force: true })
        }
    },
})
async function setup(page: Page, url: string) {
    await page.goto(url + '/system')
    await page.getByLabel('账号', { exact: true }).fill('import-admin')
    await page.getByLabel('密码', { exact: true }).fill(password)
    await page.getByLabel('确认密码', { exact: true }).fill(password)
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '系统信息与设置', exact: true }),
    ).toBeVisible()
}
function panel(page: Page) {
    return page.getByRole('region', { name: '配置草稿导入', exact: true })
}
async function headers(page: Page) {
    return {
        'X-CSRF-Token': (
            await (await page.request.get(root + '/auth/session')).json()
        ).csrfToken,
    }
}
async function connections(page: Page) {
    return (await page.request.get(root + '/connections')).json()
}
async function upload(page: Page, file: string, replace = false) {
    await panel(page)
        .getByLabel('选择配置 JSON 文件')
        .setInputFiles({
            name: 'configuration.json',
            mimeType: 'application/json',
            buffer: Buffer.from(file),
        })
    await panel(page)
        .getByLabel('导入模式')
        .selectOption(replace ? 'replace' : 'merge')
    await panel(page)
        .getByRole('button', { name: '重新读取草稿版本', exact: true })
        .click()
    await expect(
        panel(page).getByRole('button', {
            name: '重新读取草稿版本',
            exact: true,
        }),
    ).toBeEnabled()
    const response = page.waitForResponse((response) =>
        response.url().includes('/configuration/import/prepare'),
    )
    await panel(page)
        .getByRole('button', { name: '上传并预检', exact: true })
        .click()
    return response
}
async function confirm(page: Page) {
    const button = panel(page).getByRole('button', {
        name: '确认写入草稿',
        exact: true,
    })
    await expect(button).toBeDisabled()
    await panel(page).getByRole('checkbox').check()
    await button.click()
}
const empty = JSON.stringify({
    formatVersion: 1,
    connections: [],
    devices: [],
    inputs: [],
    routes: [],
})
// Explicit API fixture: this tests portable file transfer, not T3-11 no-JSON configuration.
async function seed(page: Page) {
    let version = (await connections(page)).version
    const csrf = await headers(page)
    async function create(path: string, data: object) {
        const response = await page.request.post(root + path, {
            headers: csrf,
            data: { ...data, expectedVersion: version },
        })
        expect(response.status(), await response.text()).toBe(200)
        const saved = await response.json()
        version = saved.version
        return saved
    }
    const connection = (
        await create('/connections', {
            key: 'receiver',
            name: 'Receiver',
            kind: 'http',
            http: { baseAddress: 'https://example.com/base/' },
        })
    ).connection
    const device = (
        await create('/devices', {
            key: 'meter',
            name: 'Meter',
            kind: 'simulator',
        })
    ).device
    const input = (
        await create(`/devices/${device.id}/inputs`, {
            key: 'values',
            name: 'Values',
            enabled: false,
            simulator: { sampleJson: '{"value":9223372036854775807}' },
        })
    ).input
    const credential = await page.request.put(
        root + `/connections/${connection.id}/http-credential`,
        {
            headers: csrf,
            data: {
                recipient: 'https://example.com/base/',
                kind: 'bearer',
                secret: 'not-real-import-secret',
                expectedDraftVersion: version,
            },
        },
    )
    expect(credential.status(), await credential.text()).toBe(200)
    const route = await create('/routes', {
        key: 'forward',
        name: 'Forward',
        enabled: false,
        graph: {
            schemaVersion: 1,
            nodes: [
                {
                    id: 'in',
                    type: 'input.simulator',
                    version: 1,
                    name: 'Input',
                    config: { sourceIds: [input.id] },
                },
                {
                    id: 'out',
                    type: 'output.http',
                    version: 1,
                    name: 'Output',
                    config: {
                        connectionId: connection.id,
                        path: 'events',
                        payload: { mode: 'values' },
                    },
                },
            ],
            edges: [{ source: 'in', target: 'out' }],
            layout: { nodes: {} },
        },
    })
    expect(route.version).toBe(version)
    const exported = await page.request.get(root + '/configuration/export')
    expect(exported.status()).toBe(200)
    return { file: await exported.text(), connection, device, input }
}

owned(
    'cross-Host export/import remaps IDs, preserves numbers and credential requirements across restart',
    async ({ page, browser, importHost }) => {
        test.setTimeout(90000)
        const sourceData = await mkdtemp(
            join(tmpdir(), 'tenonedge-import-source-'),
        )
        const source = await startOwnedHost(sourceData)
        const sourceContext = await browser.newContext({ baseURL: source.url })
        try {
            const sourcePage = await sourceContext.newPage()
            await setup(sourcePage, source.url)
            const exported = await seed(sourcePage)
            expect(exported.file).not.toContain('not-real-import-secret')
            const downloading = sourcePage.waitForEvent('download')
            await sourcePage
                .getByRole('region', { name: '配置草稿导出', exact: true })
                .getByRole('button', {
                    name: '下载已保存草稿 JSON',
                    exact: true,
                })
                .click()
            const download = await downloading
            const downloadPath = await download.path()
            if (!downloadPath) throw new Error('missing-configuration-download')
            const downloaded = await readFile(downloadPath, 'utf8')
            expect(downloaded).toBe(exported.file)
            await setup(page, importHost.url)
            const before = await connections(page)
            const preview = await upload(page, downloaded)
            expect(preview.status(), await preview.text()).toBe(200)
            await expect(
                panel(page).getByText(/receiver · 需重新填写凭证/),
            ).toBeVisible()
            await panel(page)
                .locator('summary')
                .filter({ hasText: 'values' })
                .click()
            await expect(
                panel(page)
                    .locator('pre')
                    .filter({ hasText: '9223372036854775807' })
                    .first(),
            ).toBeVisible()
            expect((await connections(page)).version).toBe(before.version)
            await confirm(page)
            await expect(panel(page).getByRole('status')).toContainText(
                '已写入草稿',
            )
            const after = await connections(page)
            expect(after.connections).toHaveLength(1)
            expect(after.connections[0].id).not.toBe(exported.connection.id)
            expect(after.connections[0].key).toBe('receiver')
            const devices = await (
                await page.request.get(root + '/devices')
            ).json()
            const inputs = await (
                await page.request.get(
                    root + `/devices/${devices.devices[0].id}/inputs`,
                )
            ).json()
            expect(inputs.inputs[0].deviceId).toBe(devices.devices[0].id)
            expect(inputs.inputs[0].simulator.sampleJson).toBe(
                '{"value":9223372036854775807}',
            )
            expect(
                (
                    await (
                        await page.request.get(root + '/configuration/runtime')
                    ).json()
                ).effectiveRevision,
            ).toBeNull()
            const again = await page.request.get(root + '/configuration/export')
            expect((await again.json()).connections[0].credential.status).toBe(
                'requiresInput',
            )
            await importHost.restart()
            await page.reload()
            await expect(
                page.getByRole('heading', {
                    name: '系统信息与设置',
                    exact: true,
                }),
            ).toBeVisible()
            expect((await connections(page)).version).toBe(after.version)
            const persisted = await (
                await page.request.get(root + '/configuration/export')
            ).json()
            expect(persisted.connections[0].credential.status).toBe(
                'requiresInput',
            )
            await panel(page)
                .getByRole('button', {
                    name: '读取本会话待确认导入',
                    exact: true,
                })
                .click()
            await expect(panel(page).getByRole('status')).toContainText(
                '没有待确认导入',
            )
        } finally {
            await sourceContext.close()
            await source.stop()
            await rm(sourceData, { recursive: true, force: true })
        }
    },
)

owned(
    'merge, replace, kind conflicts, draft conflicts, cancel and lost confirmation do not replay',
    async ({ page, importHost }) => {
        test.setTimeout(90000)
        await setup(page, importHost.url)
        await panel(page)
            .getByLabel('选择配置 JSON 文件')
            .setInputFiles({
                name: 'too-large.json',
                mimeType: 'application/json',
                buffer: Buffer.alloc(16 * 1024 * 1024 + 1, 32),
            })
        await expect(
            panel(page).getByText('文件超过 16 MiB，未上传；不能截断后导入。'),
        ).toBeVisible()
        await expect(
            panel(page).getByRole('button', {
                name: '上传并预检',
                exact: true,
            }),
        ).toBeDisabled()
        const oversized = await page.request.post(
            endpoint + '/prepare?expectedVersion=0',
            {
                headers: {
                    ...(await headers(page)),
                    'Content-Type': 'application/json',
                },
                data: Buffer.alloc(16 * 1024 * 1024 + 1, 32),
            },
        )
        expect(oversized.status()).toBe(413)
        const seeded = await seed(page)
        const original = await connections(page)
        expect((await upload(page, empty)).status()).toBe(200)
        await expect(
            panel(page).getByText(
                '没有对象内容差异；确认仍只写草稿，不暂停输入、不生成运行版本。',
                { exact: true },
            ),
        ).toBeVisible()
        expect(await panel(page).locator('summary').count()).toBe(0)
        await panel(page)
            .getByRole('button', { name: '取消本次导入', exact: true })
            .click()
        await expect(panel(page).getByRole('status')).toContainText(
            '候选已取消',
        )
        expect((await connections(page)).version).toBe(original.version)
        const mismatch = JSON.parse(seeded.file)
        mismatch.connections[0] = {
            ...mismatch.connections[0],
            kind: 'mqtt',
            http: null,
            mqtt: { host: 'broker.example.com' },
        }
        for (const replace of [false, true]) {
            const rejected = await upload(
                page,
                JSON.stringify(mismatch),
                replace,
            )
            expect(rejected.status()).toBe(400)
            await expect(panel(page).getByRole('alert')).toContainText(
                'import.connectionKindImmutable',
            )
            expect((await connections(page)).version).toBe(original.version)
        }
        expect((await upload(page, empty, true)).status()).toBe(200)
        await expect(panel(page).locator('summary')).toHaveCount(4)
        const changed = await page.request.post(root + '/connections', {
            headers: await headers(page),
            data: {
                expectedVersion: original.version,
                key: 'newer',
                name: 'Newer',
                kind: 'http',
                http: { baseAddress: 'https://example.org/' },
            },
        })
        expect(changed.status()).toBe(200)
        await confirm(page)
        await expect(panel(page).getByRole('alert').first()).toBeVisible()
        await expect(
            panel(page).getByRole('button', {
                name: '确认写入草稿',
                exact: true,
            }),
        ).toHaveCount(0)
        expect((await connections(page)).connections).toHaveLength(2)
        expect((await upload(page, empty, true)).status()).toBe(200)
        let calls = 0
        await page.route('**/configuration/import/confirm', async (route) => {
            calls++
            const response = await route.fetch()
            expect(response.status()).toBe(200)
            await route.abort('failed')
        })
        await confirm(page)
        await expect(panel(page).getByRole('alert').first()).toBeVisible()
        await expect(panel(page).getByRole('status')).toContainText(
            '可能已经提交',
        )
        expect((await connections(page)).connections).toHaveLength(0)
        const version = (await connections(page)).version
        await page.reload()
        await panel(page)
            .getByRole('button', { name: '读取本会话待确认导入', exact: true })
            .click()
        await expect(panel(page).getByRole('status')).toContainText(
            '没有待确认导入',
        )
        expect(calls).toBe(1)
        expect((await connections(page)).version).toBe(version)
    },
)

owned(
    'three roles and session isolation guard import and confirmation',
    async ({ page, browser, importHost }) => {
        test.setTimeout(60000)
        await setup(page, importHost.url)
        const preview = await upload(page, empty)
        const review = await preview.json()
        const user = await page.request.post(root + '/users', {
            headers: await headers(page),
            data: {
                accountName: 'import-viewer',
                role: 'viewer',
                temporaryPassword: 'Viewer-import-2357!',
                isEnabled: true,
            },
        })
        expect(user.status(), await user.text()).toBe(200)
        const context = await browser.newContext({ baseURL: importHost.url })
        try {
            const other = await context.newPage()
            await other.goto(importHost.url + '/system')
            await other.getByLabel('账号', { exact: true }).fill('import-admin')
            await other.getByLabel('密码', { exact: true }).fill(password)
            await other
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await expect(
                other.getByRole('heading', {
                    name: '系统信息与设置',
                    exact: true,
                }),
            ).toBeVisible()
            const denied = await other.request.post(endpoint + '/confirm', {
                headers: await headers(other),
                data: { id: review.id, confirmed: true },
            })
            expect(denied.status()).toBe(404)
            expect((await upload(other, empty)).status()).toBe(409)
            await other.request.post(root + '/auth/logout', {
                headers: await headers(other),
            })
            await other.goto(importHost.url + '/system')
            await other
                .getByLabel('账号', { exact: true })
                .fill('import-viewer')
            await other
                .getByLabel('密码', { exact: true })
                .fill('Viewer-import-2357!')
            await other
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await other
                .getByLabel('原密码', { exact: true })
                .fill('Viewer-import-2357!')
            await other
                .getByLabel('新密码', { exact: true })
                .fill('Viewer-changed-3489!')
            await other
                .getByRole('button', { name: '保存新密码', exact: true })
                .click()
            await expect(
                panel(other).getByText('仅配置人员和管理员可以导入配置。'),
            ).toBeVisible()
            await expect(panel(other).getByRole('button')).toHaveCount(0)
            expect(
                (
                    await other.request.post(
                        endpoint + '/prepare?expectedVersion=0',
                        {
                            headers: {
                                ...(await headers(other)),
                                'Content-Type': 'application/json',
                            },
                            data: empty,
                        },
                    )
                ).status(),
            ).toBe(403)
            const cancelled = await page.request.delete(
                endpoint + '/' + review.id,
                { headers: await headers(page) },
            )
            expect(cancelled.status()).toBe(204)
            const configurator = await page.request.post(root + '/users', {
                headers: await headers(page),
                data: {
                    accountName: 'import-configurator',
                    role: 'configurator',
                    temporaryPassword: 'Config-import-4689!',
                    isEnabled: true,
                },
            })
            expect(configurator.status()).toBe(200)
            await other.request.post(root + '/auth/logout', {
                headers: await headers(other),
            })
            await other.goto(importHost.url + '/system')
            await other
                .getByLabel('账号', { exact: true })
                .fill('import-configurator')
            await other
                .getByLabel('密码', { exact: true })
                .fill('Config-import-4689!')
            await other
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await other
                .getByLabel('原密码', { exact: true })
                .fill('Config-import-4689!')
            await other
                .getByLabel('新密码', { exact: true })
                .fill('Config-changed-3579!')
            await other
                .getByRole('button', { name: '保存新密码', exact: true })
                .click()
            await expect(
                panel(other).getByRole('button', {
                    name: '上传并预检',
                    exact: true,
                }),
            ).toBeVisible()
            expect((await upload(other, empty)).status()).toBe(200)
            await confirm(other)
            await expect(panel(other).getByRole('status')).toContainText(
                '已写入草稿',
            )
        } finally {
            await context.close()
        }
    },
)
