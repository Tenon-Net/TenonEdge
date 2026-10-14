import { test, expect, type Locator } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'

test('all business-key forms enforce the same native pattern in the actual browser', async ({
    browser,
}) => {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-key-pattern-'))
    const host = await startOwnedHost(data)
    const context = await browser.newContext({ baseURL: host.url })
    try {
        const page = await context.newPage()
        const patternErrors: string[] = []
        page.on('console', (message) => {
            if (
                /pattern attribute|invalid regular expression/i.test(
                    message.text(),
                )
            )
                patternErrors.push(message.text())
        })
        page.on('dialog', (dialog) => dialog.accept())
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('pattern-admin')
        await page
            .getByLabel('密码', { exact: true })
            .fill('Pattern-browser-5816!')
        await page
            .getByLabel('确认密码', { exact: true })
            .fill('Pattern-browser-5816!')
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const headers = { 'X-CSRF-Token': session.csrfToken }
        const deviceResponse = await page.request.post(root + '/devices', {
            headers,
            data: {
                expectedVersion: '0',
                key: 'fixture-meter',
                name: 'Fixture meter',
                kind: 'simulator',
            },
        })
        expect(deviceResponse.status(), await deviceResponse.text()).toBe(200)
        const device = await deviceResponse.json()
        const routeResponse = await page.request.post(root + '/routes', {
            headers,
            data: {
                expectedVersion: device.version,
                key: 'fixture-route',
                name: 'Fixture route',
                enabled: false,
                graph: {
                    schemaVersion: 1,
                    nodes: [],
                    edges: [],
                    layout: { nodes: {} },
                },
            },
        })
        expect(routeResponse.status(), await routeResponse.text()).toBe(200)
        for (const [path, button, label] of [
            ['/connections', '新建连接', '连接 key'],
            ['/devices', '新建设备', '设备 key'],
            [`/devices/${device.device.id}/inputs`, '新建数据来源', '来源 key'],
            ['/routes', '新建路线', '路线标识'],
            ['/routes', '打开 Fixture route', '副本标识'],
        ]) {
            await test.step(label, async () => {
                await page.goto(path)
                await page
                    .getByRole('button', { name: button, exact: true })
                    .click()
                await checkPattern(page.getByLabel(label, { exact: true }))
            })
        }
        expect(patternErrors).toEqual([])
        const runtime = await (
            await page.request.get(root + '/configuration/runtime')
        ).json()
        expect(runtime.effectiveRevision).toBeNull()
    } finally {
        try {
            await context.close()
        } finally {
            try {
                await host.stop()
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})

async function checkPattern(field: Locator) {
    await expect(field).toBeEnabled()
    for (const value of [
        '.bad',
        '_bad',
        '-bad',
        'bad key',
        '设备',
        'a/b',
        'a:b',
    ]) {
        await field.fill(value)
        expect(
            await field.evaluate(
                (element: HTMLInputElement) => element.validity.patternMismatch,
            ),
            value,
        ).toBe(true)
    }
    for (const value of ['meter-001.A_b', '0', 'a'.repeat(64)]) {
        await field.fill(value)
        expect(
            await field.evaluate((element: HTMLInputElement) =>
                element.checkValidity(),
            ),
            value,
        ).toBe(true)
    }
    await expect(field).toHaveAttribute('maxlength', '64')
}
