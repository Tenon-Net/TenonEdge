import { expect, test as base, type Page } from '@playwright/test'
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { BundleReport } from '../bundle-budget'
import { startOwnedHost } from './support/owned-host'

const report: BundleReport = JSON.parse(
    await readFile(new URL('../reports/bundle.json', import.meta.url), 'utf8'),
)
const files = new Map(report.files.map((file) => [file.file, file]))
await Promise.all(
    report.files.map(async (file) => {
        const output = await stat(
            new URL('../dist/' + file.file, import.meta.url),
        )
        if (output.size !== file.bytes)
            throw new Error(
                `Bundle report differs from emitted asset: ${file.file}`,
            )
    }),
)
const root = '/api/edge-admin/v1'
const test = base.extend<{
    gateway: Awaited<ReturnType<typeof startOwnedHost>>
}>({
    gateway: async ({ browserName }, use) => {
        const directory = await mkdtemp(
            join(tmpdir(), `tenonedge-page-loading-${browserName}-`),
        )
        const host = await startOwnedHost(directory, 0)
        try {
            await use(host)
        } finally {
            await host.stop()
            await rm(directory, { recursive: true, force: true })
        }
    },
})

function asset(module: string) {
    const matches = report.files.filter((file) => file.modules.includes(module))
    expect(matches).toHaveLength(1)
    return matches[0].file
}

function requests(page: Page, origin: string) {
    const scripts = new Set<string>()
    const writes: string[] = []
    page.on('request', (request) => {
        const url = new URL(request.url())
        if (url.origin !== origin) return
        if (url.pathname.endsWith('.js')) scripts.add(url.pathname.slice(1))
        if (request.method() === 'POST') writes.push(url.pathname)
    })
    return {
        scripts,
        writes,
        bytes: () =>
            [...scripts].reduce((sum, file) => {
                const entry = files.get(file)
                if (!entry)
                    throw new Error(`Unmeasured browser script: ${file}`)
                return sum + entry.bytes
            }, 0),
    }
}

async function setup(page: Page) {
    await page.getByLabel('账号', { exact: true }).fill('loading-admin')
    for (const field of ['密码', '确认密码'])
        await page
            .getByLabel(field, { exact: true })
            .fill('Loading-fixture-7349!')
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
}

test('cold login and restored session load only their own code; deep links retain feature text', async ({
    browser,
    gateway,
}) => {
    const context = await browser.newContext()
    try {
        // Routing also disables the HTTP cache, so the observations are cold loads.
        await context.route('**/*', (route) =>
            new URL(route.request().url()).origin === gateway.url
                ? route.continue()
                : route.abort(),
        )
        const page = await context.newPage()
        const observed = requests(page, gateway.url)
        await page.goto(gateway.url)
        await expect(page.getByLabel('账号', { exact: true })).toBeVisible()
        expect(
            observed.scripts.has(asset('src/features/auth/auth-form.tsx')),
        ).toBe(true)
        expect(
            observed.scripts.has(asset('src/components/layout/workspace.tsx')),
        ).toBe(false)
        expect(
            observed.scripts.has(
                asset('src/features/configuration/migration-api.ts'),
            ),
        ).toBe(false)
        expect(observed.bytes()).toBeLessThan(580_000)
        console.log('Cold login JavaScript bytes:', observed.bytes())
        await setup(page)
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()

        const restored = await browser.newContext({
            storageState: await context.storageState(),
        })
        try {
            await restored.route('**/*', (route) =>
                new URL(route.request().url()).origin === gateway.url
                    ? route.continue()
                    : route.abort(),
            )
            const restoredPage = await restored.newPage()
            const loaded = requests(restoredPage, gateway.url)
            await restoredPage.goto(gateway.url)
            await expect(
                restoredPage.getByRole('heading', {
                    name: '总览',
                    exact: true,
                }),
            ).toBeVisible()
            expect(
                loaded.scripts.has(asset('src/features/auth/auth-form.tsx')),
            ).toBe(false)
            expect(
                loaded.scripts.has(
                    asset('src/features/configuration/migration-api.ts'),
                ),
            ).toBe(false)
            expect(loaded.bytes()).toBeLessThan(650_000)
            console.log('Restored overview JavaScript bytes:', loaded.bytes())
            expect(loaded.writes).toEqual([])
            await restoredPage.goto(gateway.url + '/connections')
            await expect(
                restoredPage.getByRole('heading', {
                    name: '连接',
                    exact: true,
                }),
            ).toBeVisible()
            await expect(
                restoredPage.getByRole('button', {
                    name: '新建连接',
                    exact: true,
                }),
            ).toBeEnabled()
            await restoredPage.goto(gateway.url + '/routes')
            await expect(
                restoredPage.getByRole('heading', {
                    name: '路线草稿',
                    exact: true,
                }),
            ).toBeVisible()
            await expect(
                restoredPage.getByRole('button', {
                    name: '新建路线',
                    exact: true,
                }),
            ).toBeEnabled()
        } finally {
            await restored.close()
        }
    } finally {
        await context.close()
    }
})

test('failed login-form asset shows an explicit reload without navigating or sending a write', async ({
    page,
    gateway,
}) => {
    const pattern = `**/${asset('src/features/auth/auth-form.tsx')}`
    let attempts = 0
    let navigations = 0
    const observed = requests(page, gateway.url)
    page.on('request', (request) => {
        if (
            request.isNavigationRequest() &&
            request.frame() === page.mainFrame()
        )
            navigations++
    })
    await page.route(pattern, (route) => {
        attempts++
        return route.abort()
    })
    await page.goto(gateway.url)
    await expect(page.getByRole('alert')).toContainText('页面暂时无法打开')
    expect(attempts).toBe(1)
    expect(navigations).toBe(1)
    expect(observed.writes).toEqual([])
    await page.unroute(pattern)
    await page
        .getByRole('button', { name: '重新加载页面', exact: true })
        .click()
    await expect(page.getByLabel('账号', { exact: true })).toBeVisible()
    expect(observed.writes).toEqual([])
})

test('failed workspace asset after setup never replays the completed setup', async ({
    page,
    gateway,
}) => {
    const pattern = `**/${asset('src/components/layout/workspace.tsx')}`
    const observed = requests(page, gateway.url)
    await page.route(pattern, (route) => route.abort())
    await page.goto(gateway.url)
    await setup(page)
    await expect(page.getByRole('alert')).toContainText('页面暂时无法打开')
    expect(observed.writes).toEqual([root + '/setup'])
    await page.unroute(pattern)
    await page
        .getByRole('button', { name: '重新加载页面', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    expect(observed.writes).toEqual([root + '/setup'])
})

test('a failed route keeps navigation usable and recovers only after an explicit reload', async ({
    page,
    gateway,
}) => {
    const pattern = `**/${asset('src/features/connections/page.tsx')}`
    const observed = requests(page, gateway.url)
    let attempts = 0
    let navigations = 0
    page.on('request', (request) => {
        if (
            request.isNavigationRequest() &&
            request.frame() === page.mainFrame()
        )
            navigations++
    })
    await page.route(pattern, (route) => {
        attempts++
        return route.abort()
    })
    await page.goto(gateway.url)
    await setup(page)
    await expect(
        page.getByRole('heading', { name: '总览', exact: true }),
    ).toBeVisible()
    await page.getByRole('link', { name: '连接', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('页面暂时无法打开')
    await page.getByRole('link', { name: '设备草稿', exact: true }).click()
    await expect(
        page.getByRole('heading', { name: '设备草稿', exact: true }),
    ).toBeVisible()
    expect(navigations).toBe(1)
    expect(observed.writes).toEqual([root + '/setup'])

    // React.lazy retains the rejected import. Returning to the failed page must
    // not silently reload the document or replay writes from the prior page.
    await page.unroute(pattern)
    await page.getByRole('link', { name: '连接', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('页面暂时无法打开')
    expect(attempts).toBe(1)
    expect(navigations).toBe(1)
    await page
        .getByRole('button', { name: '重新加载页面', exact: true })
        .click()
    await expect(
        page.getByRole('heading', { name: '连接', exact: true }),
    ).toBeVisible()
    expect(observed.writes).toEqual([root + '/setup'])
})
