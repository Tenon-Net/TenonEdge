import { expect, test, type Page, type Frame } from '@playwright/test'
import { startTrustedBrowser } from '../tests/support/trusted-browser'
import {
    rows,
    replacement,
    second,
    prepareExcel,
    copyExcel,
} from './excel-source'
const workbookLink = process.env.TENONEDGE_EXCEL_TEST_LINK

async function pasteGateway(page: Page, text: string) {
    await page.bringToFront()
    const grid = page.getByTestId('point-grid')
    await grid.evaluate((element) => {
        element.addEventListener(
            'paste',
            (event) => {
                const clipboard = event as ClipboardEvent
                element.setAttribute(
                    'data-acceptance-paste-trusted',
                    String(event.isTrusted),
                )
                element.setAttribute(
                    'data-acceptance-paste-text',
                    clipboard.clipboardData?.getData('text/plain') ?? '',
                )
            },
            { once: true, capture: true },
        )
    })
    await grid.click()
    await grid.focus()
    await page.keyboard.press('Control+v')
    await expect(grid).toHaveAttribute('data-acceptance-paste-trusted', 'true')
    // Full equality, not just a row count/preview or a generated paste event.
    await expect(grid).toHaveAttribute('data-acceptance-paste-text', text)
}

async function createSource(page: Page, base: string, protocol: string) {
    await page.goto(base + '/connections')
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    await page
        .getByLabel('连接 key', { exact: true })
        .fill(`excel-plc-${protocol}`)
    await page
        .getByLabel('连接名称', { exact: true })
        .fill(`Excel ${protocol} 测试连接`)
    await page
        .getByRole('combobox', { name: '协议类型', exact: true })
        .selectOption('modbus')
    await page.getByLabel('主机或 IP', { exact: true }).fill('192.0.2.1')
    await page.getByLabel('端口', { exact: true }).fill('502')
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByRole('row').filter({ hasText: `Excel ${protocol} 测试连接` }),
    ).toBeVisible()
    await page.goto(base + '/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    await page
        .getByLabel('设备 key', { exact: true })
        .fill(`excel-device-${protocol}`)
    await page
        .getByLabel('设备名称', { exact: true })
        .fill(`Excel ${protocol} 测试设备`)
    await page
        .getByRole('combobox', { name: '设备类型', exact: true })
        .selectOption('modbus')
    await page
        .getByRole('combobox', { name: '引用连接', exact: true })
        .selectOption({
            label: `Excel ${protocol} 测试连接 (excel-plc-${protocol})`,
        })
    await page.getByLabel('UnitId', { exact: true }).fill('1')
    await page
        .getByRole('button', { name: '保存设备草稿', exact: true })
        .click()
    await page
        .getByRole('button', {
            name: new RegExp(
                `Excel ${protocol} 测试设备 excel-device-${protocol}`,
            ),
        })
        .click()
    await page
        .getByRole('link', { name: '数据来源与点位', exact: true })
        .click()
    await page.getByRole('button', { name: '新建采集组', exact: true }).click()
    await page.getByLabel('来源 key', { exact: true }).fill('excel-source')
    await page.getByLabel('来源名称', { exact: true }).fill('Excel 点表来源')
    await page.getByLabel('采集周期（毫秒）', { exact: true }).fill('5000')
}

// One dedicated Excel editor and one owned dual-protocol Host per worker.
// Reopening the capability URL between protocols is unnecessary and can fail
// remotely; reuse the actual editor, not generated clipboard text or cookies.
let host: Awaited<ReturnType<typeof startTrustedBrowser>> | undefined
let source: Page
let sheet: Frame

test.beforeAll(async () => {
    if (!workbookLink)
        throw new Error(
            'TENONEDGE_EXCEL_TEST_LINK must be supplied privately for a dedicated editable artificial workbook',
        )
    const address = process.env.TENONEDGE_EXCEL_HTTP_ADDRESS
    if (!address || ['localhost', '127.0.0.1', '::1'].includes(address))
        throw new Error(
            'TENONEDGE_EXCEL_HTTP_ADDRESS must be the local non-loopback NIC address',
        )
    // Keep the capability in this worker's closure, not in child Host/browser
    // environments. Collection in the runner must not delete the worker input.
    delete process.env.TENONEDGE_EXCEL_TEST_LINK
    host = await startTrustedBrowser(address)
    source = await host.context.newPage()
    await source.setViewportSize({ width: 1600, height: 1100 })
    for (const origin of [
        'https://onedrive.live.com',
        'https://excel.officeapps.live.com',
    ])
        await host.context.grantPermissions(
            ['clipboard-read', 'clipboard-write'],
            { origin },
        )
    sheet = await prepareExcel(source, workbookLink)
    const setup = await host.context.newPage()
    await setup.goto(host.httpUrl + '/prototypes/points')
    await setup.getByLabel('账号', { exact: true }).fill('excel-admin')
    await setup
        .getByLabel('密码', { exact: true })
        .fill('Excel-Acceptance-5839!')
    await setup
        .getByLabel('确认密码', { exact: true })
        .fill('Excel-Acceptance-5839!')
    await setup.getByRole('button', { name: '创建管理员并登录' }).click()
    await expect(setup.getByTestId('point-grid')).toBeVisible()
    await setup.getByRole('button', { name: '退出登录', exact: true }).click()
    await setup.close()
})
test.afterAll(async ({ browserName }, info) => {
    if (!host) return
    await host.close()
    // close() rejects nonzero/signal exits; a green teardown never ignores them.
    await info.attach('owned-host-clean-exit', {
        contentType: 'application/json',
        body: JSON.stringify({ browserName, verifiedExitCode: 0 }),
    })
})

for (const protocol of ['http', 'https'] as const) {
    test(`real Excel clipboard to ${protocol} point draft survives save and refresh`, async ({
        browserName,
    }, info) => {
        expect(browserName).toBe('chromium')
        const owned = host!
        const page = await owned.context.newPage()
        const base = protocol === 'http' ? owned.httpUrl : owned.url
        let phase = 'source-initialization'
        const copies: object[] = []
        const errors: string[] = []
        const requests: { method: string; path: string }[] = []
        try {
            await page.setViewportSize({ width: 1600, height: 1100 })
            page.on('pageerror', (error) => errors.push(error.message))
            page.on('request', (request) =>
                requests.push({
                    method: request.method(),
                    path: new URL(request.url()).pathname,
                }),
            )
            await page.goto(base + '/prototypes/points')
            const secureContext = await page.evaluate(() => isSecureContext)
            expect(secureContext).toBe(protocol === 'https')
            await page.getByLabel('账号', { exact: true }).fill('excel-admin')
            await page
                .getByLabel('密码', { exact: true })
                .fill('Excel-Acceptance-5839!')
            await page
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await expect(page.getByTestId('point-grid')).toBeVisible()
            for (const managed of [false, true]) {
                phase = managed ? 'create-real-source' : 'prototype'
                if (managed) await createSource(page, base, protocol)
                const label = managed ? 'saved-source' : 'prototype'
                if (managed)
                    await page
                        .getByText('从 Excel 粘贴', { exact: true })
                        .click()
                await page
                    .getByRole('combobox', { name: '粘贴方式', exact: true })
                    .selectOption('append')
                await page
                    .getByRole('combobox', { name: '地址写法', exact: true })
                    .selectOption('offset')
                const copied = await copyExcel(
                    source,
                    sheet,
                    0,
                    rows,
                    info.outputPath(`${label}-excel-selection.png`),
                )
                copies.push({ label, ...copied, text: undefined })
                await pasteGateway(page, copied.text)
                await expect(page.getByTestId('point-count')).toContainText(
                    '100 行 · 0 行选中 · 2 行错误',
                )
                if (managed) {
                    const before = requests.length
                    await page
                        .getByRole('button', {
                            name: '保存来源草稿',
                            exact: true,
                        })
                        .click()
                    await expect(
                        page.getByText(
                            '点表或来源表单存在错误，请检查标红行及重复请求头。',
                        ),
                    ).toBeVisible()
                    expect(
                        requests
                            .slice(before)
                            .filter((request) => request.method !== 'GET'),
                    ).toEqual([])
                }
                await page
                    .getByText('定位错误（前 20 行）', { exact: true })
                    .click()
                for (const row of [51, 76]) {
                    await page
                        .getByRole('button', {
                            name: new RegExp(`^定位第 ${row} 行`),
                        })
                        .click()
                    await expect(
                        page.locator(`[data-point-index="${row - 1}"]`),
                    ).toHaveAttribute('data-invalid', 'true')
                    await page.screenshot({
                        path: info.outputPath(`${label}-error-${row}.png`),
                    })
                }
                phase = label + ':correct-address-76'
                await page
                    .getByLabel('第 76 行 地址', { exact: true })
                    .fill('75')
                phase = label + ':locate-key-51'
                await page
                    .getByRole('button', { name: /^定位第 51 行/ })
                    .click()
                await page
                    .getByLabel('第 51 行 key', { exact: true })
                    .fill('excel_50')
                await expect(page.getByTestId('point-count')).toContainText(
                    '0 行错误',
                )
                phase = label + ':bulk-select'
                for (const row of [51, 52])
                    await page
                        .getByLabel(`选择第 ${row} 行`, { exact: true })
                        .check()
                await page.getByLabel('批量值', { exact: true }).fill('℃')
                await page
                    .getByRole('button', { name: '修改所选行', exact: true })
                    .click()
                for (const row of [51, 52])
                    await expect(
                        page.getByLabel(`第 ${row} 行 单位`, { exact: true }),
                    ).toHaveValue('℃')
                await expect(
                    page.getByLabel('第 53 行 单位', { exact: true }),
                ).toHaveValue('kPa')
                await page.getByLabel('选择第 52 行', { exact: true }).uncheck()
                await page
                    .getByRole('combobox', { name: '粘贴方式', exact: true })
                    .selectOption('replace')
                phase = label + ':copy-replacement'
                const one = await copyExcel(
                    source,
                    sheet,
                    102,
                    [replacement],
                    info.outputPath(`${label}-excel-replacement.png`),
                )
                copies.push({ label, ...one, text: undefined })
                await pasteGateway(page, one.text)
                await expect(
                    page.getByLabel('第 51 行 key', { exact: true }),
                ).toHaveValue('excel_replacement')
                await expect(page.getByTestId('point-count')).toContainText(
                    '100 行 · 1 行选中 · 0 行错误',
                )
                phase = label + ':copy-two'
                const two = await copyExcel(
                    source,
                    sheet,
                    102,
                    [replacement, second],
                    info.outputPath(`${label}-excel-two.png`),
                )
                copies.push({ label, ...two, text: undefined })
                await pasteGateway(page, two.text)
                await expect(
                    page.getByRole('alert').filter({
                        hasText: '覆盖须先选择与粘贴行数相同的行，本次未应用。',
                    }),
                ).toHaveText('覆盖须先选择与粘贴行数相同的行，本次未应用。')
                await expect(
                    page.getByLabel('第 51 行 key', { exact: true }),
                ).toHaveValue('excel_replacement')
                await expect(page.getByTestId('point-count')).toContainText(
                    '100 行 · 1 行选中 · 0 行错误',
                )
                if (managed) {
                    await page
                        .getByRole('button', {
                            name: '保存来源草稿',
                            exact: true,
                        })
                        .click()
                    await expect(page.getByRole('status')).toHaveText(
                        '来源与点表已保存到网关草稿，尚未应用。',
                    )
                    await page.reload()
                    await page
                        .getByRole('button', {
                            name: /Excel 点表来源 \(excel-source\)/,
                        })
                        .click()
                    await expect(page.getByTestId('point-count')).toContainText(
                        '100 行 · 0 行选中 · 0 行错误',
                    )
                    const result = await page.evaluate(
                        async (path) => {
                            const response = await fetch(path)
                            return {
                                status: response.status,
                                data: await response.json(),
                            }
                        },
                        '/api/edge-admin/v1' + new URL(page.url()).pathname,
                    )
                    // Use browser TLS trust and read-only API to verify ALL rows.
                    expect(result.status).toBe(200)
                    const saved = result.data
                    const points = saved.inputs[0].modbus.points
                    expect(points).toHaveLength(100)
                    for (let index = 0; index < 100; index++) {
                        const expected =
                            index === 50 ? replacement : rows[index]
                        expect(points[index]).toEqual({
                            key: expected[0],
                            name: expected[1],
                            functionCode: 3,
                            address: index === 50 ? 150 : index,
                            type: 'uint16',
                            bit: null,
                            byteOrder: null,
                            unit: index === 51 ? '℃' : expected[7],
                            enabled: true,
                        })
                    }
                    await info.attach('saved-points', {
                        contentType: 'application/json',
                        body: JSON.stringify(points, null, 2),
                    })
                    await page.screenshot({
                        path: info.outputPath('saved-source-refreshed.png'),
                    })
                }
            }
            expect(
                requests.filter((request) =>
                    /\/test|\/preview|\/read|\/apply/.test(request.path),
                ),
            ).toEqual([])
            expect(errors).toEqual([])
            await info.attach('excel-acceptance', {
                contentType: 'application/json',
                body: JSON.stringify(
                    {
                        at: new Date().toISOString(),
                        browser: owned.browser.version(),
                        protocol,
                        secureContext,
                        gatewayOrigin: new URL(page.url()).origin,
                        certificateFingerprint: owned.certificateFingerprint,
                        sourceOrigin: 'https://excel.officeapps.live.com',
                        sourceVersion: 'not displayed by the visible editor',
                        headers: false,
                        copies,
                        errors,
                        requests,
                        deviceReadOrApplied: false,
                    },
                    null,
                    2,
                ),
            })
        } catch (error) {
            if (page && !page.isClosed() && new URL(page.url()).origin === base)
                await page.screenshot({
                    path: info.outputPath('gateway-failure.png'),
                })
            throw new Error(`Acceptance phase ${phase}: ` + String(error))
        } finally {
            await info.attach('partial-progress', {
                contentType: 'application/json',
                body: JSON.stringify({ phase, copies, requests }, null, 2),
            })
            await page.close()
        }
    })
}
