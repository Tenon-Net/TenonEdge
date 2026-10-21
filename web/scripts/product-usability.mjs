import { createRequire } from 'node:module'
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root =
    process.env.TENONEDGE_QA_ROOT ??
    fileURLToPath(new URL('../../', import.meta.url))
const require = createRequire(join(root, 'web/package.json'))
const { chromium, expect } = require('@playwright/test')
const { startOwnedHost } = await import(
    pathToFileURL(join(root, 'web/tests/support/owned-host.ts'))
)
const evidence = process.env.TENONEDGE_QA_RESULTS
if (!evidence) throw new Error('TENONEDGE_QA_RESULTS is required')
await mkdir(evidence, { recursive: true })
const data = await mkdtemp(join(tmpdir(), 'tenonedge-usability-'))
let host, browser, failure, hostExitCode
const findings = [],
    errors = []
const check = (id, passed, observation) =>
    findings.push({ id, passed, observation })
async function helpFor(input) {
    return input.evaluate((element) =>
        (element.getAttribute('aria-describedby') ?? '')
            .split(/\s+/)
            .filter(Boolean)
            .map((id) => document.getElementById(id)?.textContent ?? '')
            .join(' '),
    )
}
try {
    host = await startOwnedHost(data)
    browser = await chromium.launch()
    const page = await browser.newPage({
        viewport: { width: 1440, height: 900 },
        locale: 'zh-CN',
    })
    page.on('pageerror', (error) => errors.push(error.message))
    await page.goto(host.url)
    await page.getByLabel('账号', { exact: true }).fill('usability-admin')
    await page
        .getByLabel('密码', { exact: true })
        .fill('Usability-fixture-8426!')
    await page
        .getByLabel('确认密码', { exact: true })
        .fill('Usability-fixture-8426!')
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(page.locator('[data-dashboard="operations"]')).toBeVisible()
    await expect(
        page.getByRole('button', { name: '刷新看板', exact: true }),
    ).toBeEnabled()
    const homeLinks = await page.locator('#content a').allTextContents()
    check(
        'UX-01',
        homeLinks.some((text) => /建立连接|新建连接|开始接入/.test(text)),
        { scenario: '首次设置后，空网关是否给出接入的下一步', homeLinks },
    )
    await page.screenshot({
        path: join(evidence, 'empty-overview.png'),
        fullPage: true,
    })
    await page.goto(host.url + '/connections')
    await page.getByRole('button', { name: '新建连接', exact: true }).click()
    const key = page.getByLabel('连接 key', { exact: true })
    await key.fill('现场 PLC')
    await page.getByLabel('连接名称', { exact: true }).fill('现场电表')
    await page.getByLabel('主机或 IP', { exact: true }).fill('192.168.10.10')
    const keyHelp = await helpFor(key)
    let saves = 0
    page.on('request', (request) => {
        if (
            request.method() === 'POST' &&
            new URL(request.url()).pathname === '/api/edge-admin/v1/connections'
        )
            saves++
    })
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    expect(saves).toBe(0)
    check(
        'UX-02',
        /字母|英文/.test(keyHelp) && /数字/.test(keyHelp) && /64/.test(keyHelp),
        {
            scenario: '内部标识格式是否在出错前有中文解释和例子',
            help: keyHelp,
            validity: await key.evaluate((element) => ({
                valid: element.validity.valid,
                patternMismatch: element.validity.patternMismatch,
                validationMessage: element.validationMessage,
            })),
        },
    )
    await page.screenshot({
        path: join(evidence, 'connection-key.png'),
        fullPage: true,
    })
    await key.fill('plc_01')
    expect(await key.evaluate((element) => element.checkValidity())).toBe(true)
    await page.getByRole('button', { name: '保存到草稿', exact: true }).click()
    await expect(
        page.getByRole('row').filter({ hasText: '现场电表' }),
    ).toBeVisible()
    expect(saves).toBe(1)
    const persisted = await page.request.get(
        host.url + '/api/edge-admin/v1/connections',
    )
    expect(persisted.status()).toBe(200)
    expect(await persisted.text()).toContain('plc_01')
    check('UX-04', true, {
        scenario: '非法标识不发送写请求；修正后经真实提交持久化草稿',
        saves,
    })
    page.once('dialog', (dialog) => dialog.accept())
    await page.goto(host.url + '/devices')
    await page.getByRole('button', { name: '新建设备', exact: true }).click()
    const unit = page.getByLabel('UnitId', { exact: true })
    const unitHelp = await helpFor(unit)
    check('UX-03', /从站/.test(unitHelp) && /手册/.test(unitHelp), {
        scenario: 'Modbus 的 UnitId 是否解释为手册中的从站地址',
        help: unitHelp,
    })
    await page.screenshot({
        path: join(evidence, 'device-unitid.png'),
        fullPage: true,
    })
    check('JS-01', errors.length === 0, { errors })
} catch (error) {
    failure = error.message
} finally {
    await browser?.close()
    if (host) hostExitCode = await host.stop()
    await rm(data, { recursive: true, force: true })
    await writeFile(
        join(evidence, 'usability.json'),
        JSON.stringify({ findings, errors, failure, hostExitCode }, null, 2) +
            '\n',
    )
}
console.log(JSON.stringify(findings, null, 2))
if (
    failure ||
    hostExitCode !== 0 ||
    findings.length !== 5 ||
    findings.some((item) => !item.passed)
) {
    console.error(failure ?? 'Usability checks or Host shutdown did not pass')
    process.exitCode = 1
}
