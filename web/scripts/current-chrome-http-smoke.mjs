// V-03: native Google Chrome defaults, not Playwright's launch switches (which
// disable HttpsUpgrades). Attach over CDP only after our own browser starts.
import assert from 'node:assert/strict'
import { execFileSync, spawn } from 'node:child_process'
import {
    mkdir,
    mkdtemp,
    readFile,
    readdir,
    rm,
    writeFile,
} from 'node:fs/promises'
import { networkInterfaces, tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'
import { chromium } from '@playwright/test'
import { assertCleanHostExit, observeProcessExit } from './process-exit.mjs'

assert.equal(
    process.platform,
    'linux',
    'This fixture is verified on Linux only',
)
const root = fileURLToPath(new URL('../../', import.meta.url))
const origin = new URL(process.env.TENONEDGE_TEST_URL ?? '')
assert.equal(origin.protocol, 'http:')
assert(origin.port, 'Choose an explicit unused port')
assert(
    Object.values(networkInterfaces())
        .flat()
        .some(
            (nic) =>
                !nic.internal &&
                nic.family === 'IPv4' &&
                nic.address === origin.hostname,
        ),
    'Use a real local IPv4 NIC address',
)
const binary = process.env.TENONEDGE_TEST_EXECUTABLE
assert(binary, 'Supply a verified native Google Chrome binary')
const output = resolve(
    process.env.TENONEDGE_SMOKE_RESULTS ?? 'test-results/current-chrome-http',
)
await mkdir(output, { recursive: true })
const temporary = await mkdtemp(join(tmpdir(), 'tenonedge-default-chrome-'))
const data = join(temporary, 'data')
const home = join(temporary, 'home')
const profile = join(temporary, 'profile')
await mkdir(data, { mode: 0o700 })
await mkdir(home, { mode: 0o700 })
const report = {
    startedAt: new Date().toISOString(),
    origin: origin.origin,
    phases: [],
    passed: false,
}
const started = performance.now()
let browser
let page
const owned = []
function start(command, args, env = process.env) {
    const child = spawn(command, args, {
        cwd: root,
        env,
        stdio: ['ignore', 'pipe', 'pipe'],
    })
    const state = { child, log: '', error: null, exited: null }
    state.exited = observeProcessExit(child)
    const collect = (chunk) => {
        state.log = (state.log + chunk.toString()).slice(-65536)
    }
    child.stdout.on('data', collect)
    child.stderr.on('data', collect)
    child.once('error', (error) => {
        state.error = error
    })
    owned.push(state)
    return state
}
const terminateOwned = () => {
    for (const { child } of owned) child.kill('SIGTERM')
}
process.once('exit', terminateOwned)
async function until(check, description, timeout = 30000) {
    const end = performance.now() + timeout
    while (performance.now() < end) {
        for (const { child, error } of owned) {
            if (error) throw error
            assert(
                child.exitCode === null && child.signalCode === null,
                `Owned process exited: ${child.spawnfile}`,
            )
        }
        const result = await check()
        if (result) return result
        await delay(100)
    }
    throw new Error(`Timed out: ${description}`)
}
async function phase(name, action) {
    const at = performance.now()
    await action()
    report.phases.push({ name, durationMs: Math.round(performance.now() - at) })
}
try {
    report.binaryVersion = execFileSync(binary, ['--version'], {
        encoding: 'utf8',
        timeout: 10000,
    }).trim()
    const version = report.binaryVersion.match(/^Google Chrome (\d+)\./)
    assert(
        version && Number(version[1]) >= 154,
        'V-03 requires actual Google Chrome 154 or later, not Chromium',
    )
    // Machine policies could silently exempt HTTP sites. Refuse that environment
    // rather than overwrite policies or claim it represents browser defaults.
    report.policyFiles = []
    for (const directory of [
        '/etc/opt/chrome/policies/managed',
        '/etc/opt/chrome/policies/recommended',
    ]) {
        const files = await readdir(directory).catch((error) => {
            if (error.code === 'ENOENT') return []
            throw error
        })
        report.policyFiles.push(...files.map((file) => join(directory, file)))
    }
    assert.equal(
        report.policyFiles.length,
        0,
        'Machine policies present: defaults cannot be inferred',
    )
    // Do not contact the developer's desktop/keyring from a disposable headless
    // profile. An inherited session bus can leave cookie initialization waiting
    // for an unlock prompt that headless Chrome cannot display. This isolates
    // the test environment; it does not set a browser password-store flag or
    // override HTTP, TLS, or sandbox policy.
    report.sessionBus = 'isolated-unavailable'
    const host = start(
        'dotnet',
        [
            join(
                root,
                'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll',
            ),
            `--webroot=${join(root, 'web/dist')}`,
            `--TenonEdge:DataDirectory=${data}`,
            '--TenonEdge:Http:Address=0.0.0.0',
            `--TenonEdge:Http:Port=${origin.port}`,
            '--TenonEdge:Https:Address=127.0.0.1',
            '--TenonEdge:Https:Port=0',
        ],
        { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production' },
    )
    await until(() => host.log.includes('Application started.'), 'owned Host')
    report.launchArguments = [
        '--headless=new',
        '--remote-debugging-port=0',
        '--remote-debugging-address=127.0.0.1',
        `--user-data-dir=${profile}`,
        '--no-first-run',
        '--no-default-browser-check',
        'about:blank',
    ]
    const native = start(binary, report.launchArguments, {
        ...process.env,
        HOME: home,
        XDG_CONFIG_HOME: join(home, '.config'),
        XDG_CACHE_HOME: join(home, '.cache'),
        DBUS_SESSION_BUS_ADDRESS: `unix:path=${home}/no-session-bus`,
    })
    const endpoint = await until(
        () =>
            native.log.match(
                /DevTools listening on (ws:\/\/127\.0\.0\.1:\d+\/\S+)/,
            )?.[1],
        'native browser CDP endpoint',
    )
    report.actualCommandLine = (
        await readFile(`/proc/${native.child.pid}/cmdline`, 'utf8')
    )
        .split('\0')
        .filter(Boolean)
    assert(
        !report.actualCommandLine.some((arg) =>
            /--(?:disable-features|enable-features|ignore-certificate-errors|unsafely-treat-insecure-origin-as-secure|no-sandbox)/.test(
                arg,
            ),
        ),
        'Browser launch must not override security/HTTPS features',
    )
    browser = await chromium.connectOverCDP(endpoint)
    report.browserVersion = browser.version()
    assert.equal(
        report.browserVersion,
        report.binaryVersion.replace('Google Chrome ', ''),
    )
    const context = browser.contexts()[0]
    assert(context, 'Use the native default profile, not an incognito context')
    page = context.pages()[0] ?? (await context.newPage())
    const pageErrors = []
    page.on('pageerror', (error) => pageErrors.push(error.message))
    const visit = async (path) => {
        const response = await page.goto(new URL(path, origin).href, {
            waitUntil: 'domcontentloaded',
        })
        assert.equal(response?.status(), 200)
        assert.equal(
            new URL(page.url()).origin,
            origin.origin,
            'No HTTPS upgrade or interstitial',
        )
        assert.equal(await page.evaluate(() => window.isSecureContext), false)
    }
    await phase(
        'default LAN HTTP: setup, logout and login without an interstitial',
        async () => {
            await visit('/account')
            await page
                .getByLabel('账号', { exact: true })
                .fill('default-chrome-admin')
            await page
                .getByLabel('密码', { exact: true })
                .fill('Default-chrome-5938!')
            await page
                .getByLabel('确认密码', { exact: true })
                .fill('Default-chrome-5938!')
            await page
                .getByRole('button', { name: '创建管理员并登录', exact: true })
                .click()
            await page
                .getByRole('heading', { name: '修改密码', exact: true })
                .waitFor()
            await page
                .getByRole('button', { name: '退出登录', exact: true })
                .click()
            await page
                .getByLabel('账号', { exact: true })
                .fill('default-chrome-admin')
            await page
                .getByLabel('密码', { exact: true })
                .fill('Default-chrome-5938!')
            await page
                .getByRole('button', { name: '登录', exact: true })
                .click()
            await page
                .getByRole('heading', { name: '修改密码', exact: true })
                .waitFor()
            assert.equal(
                await page.evaluate(() => document.cookie.includes('Session')),
                false,
            )
            await page.screenshot({ path: join(output, 'http-account.png') })
        },
    )
    await phase(
        'deep link and reload retain the native HTTP session',
        async () => {
            await visit('/prototypes/points')
            await page
                .getByRole('heading', { name: '点表交互原型', exact: true })
                .waitFor()
            await page.reload({ waitUntil: 'domcontentloaded' })
            await page
                .getByRole('heading', { name: '点表交互原型', exact: true })
                .waitFor()
            assert.equal(new URL(page.url()).origin, origin.origin)
            assert.equal(
                await page.evaluate(() => window.isSecureContext),
                false,
            )
        },
    )
    await phase(
        'canvas can create a node under default HTTP settings',
        async () => {
            await visit('/prototypes/canvas')
            await page
                .getByRole('heading', { name: '画布交互原型', exact: true })
                .waitFor()
            await page
                .getByRole('button', { name: '字段映射', exact: true })
                .click()
            await page.locator('.react-flow__node').waitFor()
            assert.equal(await page.locator('.react-flow__node').count(), 1)
            await page.screenshot({ path: join(output, 'http-canvas.png') })
        },
    )
    assert.deepEqual(pageErrors, [])
    report.userAgent = await page.evaluate(() => navigator.userAgent)
    report.passed = true
} catch (error) {
    report.error = error.message
    process.exitCode = 1
    if (page)
        await page
            .screenshot({ path: join(output, 'failure.png'), timeout: 5000 })
            .catch(() => {})
} finally {
    const cleanupFailed = (error) => {
        report.cleanupError = error.message
        report.passed = false
        process.exitCode = 1
    }
    if (browser) await browser.close().catch(cleanupFailed)
    let allExited = true
    for (const state of owned.toReversed()) {
        if (state.child.exitCode === null && state.child.signalCode === null)
            state.child.kill('SIGTERM')
        let exited = await Promise.race([
            state.exited.then(() => true),
            delay(15000, false, { ref: false }),
        ])
        if (!exited) {
            cleanupFailed(new Error('Owned process did not stop gracefully'))
            state.child.kill('SIGKILL')
            exited = await Promise.race([
                state.exited.then(() => true),
                delay(5000, false, { ref: false }),
            ])
        }
        allExited &&= exited
        if (exited) {
            const exit = await state.exited
            report.processExits ??= []
            report.processExits.push({
                command: state.child.spawnfile,
                ...exit,
            })
            if (state.child.spawnfile === 'dotnet') {
                try {
                    assertCleanHostExit(exit)
                } catch (error) {
                    cleanupFailed(error)
                }
            }
        }
    }
    process.removeListener('exit', terminateOwned)
    for (const [index, state] of owned.entries())
        await writeFile(join(output, `process-${index}.log`), state.log)
    if (allExited)
        await rm(temporary, { recursive: true, force: true }).catch(
            cleanupFailed,
        )
    else
        cleanupFailed(
            new Error(`Unconfirmed process exit; preserve ${temporary}`),
        )
    report.durationMs = Math.round(performance.now() - started)
    await writeFile(
        join(output, 'result.json'),
        JSON.stringify(report, null, 2),
    )
    console.log(JSON.stringify(report, null, 2))
}
