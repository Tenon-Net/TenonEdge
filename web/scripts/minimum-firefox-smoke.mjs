// Native Firefox UI smoke via W3C WebDriver and Gecko clipboard setup.
// No production API seeds or driver/browser patches; Linux headless fixtures only.
// An external, loopback WebDriver is required; this script owns a fresh temporary Host.
import assert from 'node:assert/strict'
import { spawn, execFileSync } from 'node:child_process'
import { X509Certificate } from 'node:crypto'
import { writeFile, mkdir, mkdtemp, rm } from 'node:fs/promises'
import { networkInterfaces, tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import { join, resolve } from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { assertCleanHostExit, observeProcessExit } from './process-exit.mjs'

assert.equal(
    process.platform,
    'linux',
    'This headless clipboard fixture is verified on Linux only',
)
const root = fileURLToPath(new URL('../../', import.meta.url))
const origin = new URL(process.env.TENONEDGE_TEST_URL ?? '')
const driver = new URL(process.env.TENONEDGE_WEBDRIVER_URL ?? '')
assert(['http:', 'https:'].includes(origin.protocol))
const secure = origin.protocol === 'https:'
assert(origin.port, 'Choose an explicit, unused test port')
assert(
    Object.values(networkInterfaces())
        .flat()
        .some(
            (nic) =>
                !nic.internal &&
                nic.address === origin.hostname.replace(/^\[|\]$/g, ''),
        ),
    'Use a real local NIC address; this script must not target another Host',
)
assert(
    ['localhost', '127.0.0.1', '[::1]'].includes(driver.hostname),
    'WebDriver must be local',
)
const output = resolve(
    process.env.TENONEDGE_SMOKE_RESULTS ?? 'test-results/minimum-browser',
)
await mkdir(output, { recursive: true })
const report = {
    startedAt: new Date().toISOString(),
    origin: origin.origin,
    phases: [],
    passed: false,
}
const started = performance.now()
const temporary = await mkdtemp(join(tmpdir(), 'tenonedge-firefox-smoke-'))
await mkdir(join(temporary, 'data'), { mode: 0o700 })
let session
let hostLog = ''
let hostReady = false
let hostError
const host = spawn(
    'dotnet',
    [
        join(root, 'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll'),
        `--webroot=${join(root, 'web/dist')}`,
        `--TenonEdge:DataDirectory=${join(temporary, 'data')}`,
        `--TenonEdge:Http:Address=${secure ? '127.0.0.1' : '0.0.0.0'}`,
        `--TenonEdge:Http:Port=${secure ? 0 : origin.port}`,
        `--TenonEdge:Https:Address=${secure ? '0.0.0.0' : '127.0.0.1'}`,
        `--TenonEdge:Https:Port=${secure ? origin.port : 0}`,
    ],
    {
        cwd: root,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production' },
        stdio: ['ignore', 'pipe', 'pipe'],
    },
)
const collect = (chunk) => {
    hostLog = (hostLog + chunk.toString()).slice(-32768)
    if (hostLog.includes('Application started.')) hostReady = true
}
host.stdout.on('data', collect)
host.stderr.on('data', collect)
host.once('error', (error) => {
    hostError = error
})
const hostExited = observeProcessExit(host)
const stopOnExit = () => host.kill('SIGTERM')
process.once('exit', stopOnExit)
const elementKey = 'element-6066-11e4-a52e-4f735466cecf'

async function command(
    path,
    body,
    method = body === undefined ? 'GET' : 'POST',
) {
    const options = {
        method,
        headers: { 'Content-Type': 'application/json' },
        signal: AbortSignal.timeout(30000),
    }
    if (body !== undefined) {
        assert.notEqual(method, 'GET')
        options.body = JSON.stringify(body)
    }
    const response = await fetch(new URL(path, driver), options)
    const result = await response.json()
    if (!response.ok || result.value?.error)
        throw new Error(
            `WebDriver ${path}: ${result.value?.error ?? response.status} ${result.value?.message ?? ''}`,
        )
    return result.value
}
const run = (path, body, method) =>
    command(`/session/${session}/${path}`, body, method)
const js = (script, ...args) => run('execute/sync', { script, args })
async function until(check, description, timeout = 10000) {
    const end = performance.now() + timeout
    while (performance.now() < end) {
        const value = await check()
        if (value) return value
        await delay(100)
    }
    throw new Error(`Timed out: ${description}`)
}
async function element(script, ...args) {
    return until(() => js(script, ...args), `element ${args[0] ?? ''}`)
}
const label = (name) =>
    element(
        `const label = [...document.querySelectorAll('label')].find(e => e.textContent.trim() === arguments[0]); return label?.control ?? label?.querySelector('input,select,textarea') ?? document.querySelector('[aria-label="' + arguments[0] + '"]');`,
        name,
    )
const button = (name) =>
    element(
        `return [...document.querySelectorAll('button')].find(e => e.textContent.trim() === arguments[0] && e.getClientRects().length);`,
        name,
    )
const css = (selector) =>
    element('return document.querySelector(arguments[0]);', selector)
const click = async (ref) => run(`element/${(await ref)[elementKey]}/click`, {})
async function fill(ref, text) {
    const id = (await ref)[elementKey]
    await run(`element/${id}/clear`, {})
    await run(`element/${id}/value`, { text })
}
const heading = (name) =>
    until(
        () =>
            js(
                'return [...document.querySelectorAll("h1,h2")].some(e => e.textContent.trim() === arguments[0]);',
                name,
            ),
        name,
    )
const navigate = (path) => run('url', { url: new URL(path, origin).href })
const graph = () =>
    js(
        'return JSON.parse(document.querySelector("[data-testid=graph-document]").textContent);',
    )
function residentBytes() {
    const pid = report.capabilities?.['moz:processID']
    if (!pid || process.platform !== 'linux') return null
    const rows = execFileSync('ps', ['-e', '-o', 'pid=,ppid=,rss='], {
        encoding: 'utf8',
    })
        .trim()
        .split('\n')
        .map((row) => row.trim().split(/\s+/).map(Number))
    const owned = new Set([pid])
    let count
    do {
        count = owned.size
        for (const [child, parent] of rows)
            if (owned.has(parent)) owned.add(child)
    } while (owned.size !== count)
    return rows
        .filter(([child]) => owned.has(child))
        .reduce((total, row) => total + row[2] * 1024, 0)
}
async function phase(name, action) {
    const at = performance.now()
    await action()
    report.phases.push({
        name,
        durationMs: Math.round(performance.now() - at),
        sampledProcessTreeRssBytes: residentBytes(),
    })
}
async function drag(source, target) {
    const center = (ref) =>
        js(
            'const r=arguments[0].getBoundingClientRect(); return {x:Math.round(r.x+r.width/2), y:Math.round(r.y+r.height/2)};',
            ref,
        )
    const from = await center(await source)
    const destination = await target
    const to =
        typeof destination.x === 'number'
            ? destination
            : await center(destination)
    await run('actions', {
        actions: [
            {
                type: 'pointer',
                id: 'mouse',
                parameters: { pointerType: 'mouse' },
                actions: [
                    {
                        type: 'pointerMove',
                        duration: 0,
                        origin: 'viewport',
                        ...from,
                    },
                    { type: 'pointerDown', button: 0 },
                    { type: 'pause', duration: 100 },
                    {
                        type: 'pointerMove',
                        duration: 500,
                        origin: 'viewport',
                        ...to,
                    },
                    { type: 'pause', duration: 100 },
                    { type: 'pointerUp', button: 0 },
                ],
            },
        ],
    })
    await run('actions', undefined, 'DELETE')
}
async function screenshot(name) {
    await writeFile(
        join(output, name + '.png'),
        Buffer.from(await run('screenshot'), 'base64'),
    )
}
try {
    await until(
        () => {
            if (hostError) throw hostError
            assert.equal(
                host.exitCode,
                null,
                'Owned test Host exited during startup',
            )
            return hostReady
        },
        'owned Host startup',
        30000,
    )
    const capabilities = {
        browserName: 'firefox',
        acceptInsecureCerts: false,
        'moz:firefoxOptions': {
            args: ['-headless'],
            ...(process.env.TENONEDGE_TEST_EXECUTABLE
                ? { binary: process.env.TENONEDGE_TEST_EXECUTABLE }
                : {}),
            prefs: {
                'datareporting.healthreport.uploadEnabled': false,
                'toolkit.telemetry.enabled': false,
            },
        },
    }
    let trustedCertificate
    if (secure) {
        // Retrieve only the public certificate; no HTTP request or secret is sent.
        // Trust is anchored in our owned Host's console fingerprint, not the peer.
        const handshake = execFileSync(
            'openssl',
            ['s_client', '-connect', origin.host, '-showcerts'],
            {
                input: '',
                encoding: 'utf8',
                timeout: 10000,
                stdio: ['pipe', 'pipe', 'pipe'],
            },
        )
        const pem = handshake.match(
            /-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----/,
        )?.[0]
        assert(pem, 'Owned Host must present a certificate')
        const expected = hostLog.match(/SHA-256 指纹 ([0-9A-F]{64})/)?.[1]
        assert(expected, 'Owned Host must report its certificate fingerprint')
        report.certificateFingerprint = new X509Certificate(
            pem,
        ).fingerprint256.replaceAll(':', '')
        assert.equal(report.certificateFingerprint, expected)

        const untrusted = await command('/session', {
            capabilities: { alwaysMatch: capabilities },
        })
        session = untrusted.sessionId
        assert.equal(untrusted.capabilities.acceptInsecureCerts, false)
        await assert.rejects(() => navigate('/account'), /insecure certificate/)
        report.untrustedCertificateRejected = true
        await command(`/session/${session}`, undefined, 'DELETE')
        session = undefined

        trustedCertificate = new X509Certificate(pem).raw.toString('base64')
    }
    const created = await command('/session', {
        capabilities: { alwaysMatch: capabilities },
    })
    session = created.sessionId
    report.capabilities = created.capabilities
    assert.equal(report.capabilities.acceptInsecureCerts, false)
    if (secure) {
        // Import the fingerprint-checked public certificate using Firefox's own
        // certificate database (nsIX509CertDB, Firefox 128 IDL), avoiding an NSS
        // database produced by a different system version. C is its trust-anchor
        // bit; no acceptInsecureCerts, error exceptions, or verifier overrides.
        // This privileged fixture touches only the driver's disposable profile.
        await run('moz/context', { context: 'chrome' })
        try {
            report.profileTrust = await js(
                `const db=Components.classes['@mozilla.org/security/x509certdb;1'].getService(Components.interfaces.nsIX509CertDB);
                const cert=db.addCertFromBase64(arguments[0], 'C,,');
                return { fingerprint:cert.sha256Fingerprint, trustedForTls:db.isCertTrusted(cert,Components.interfaces.nsIX509Cert.CA_CERT,Components.interfaces.nsIX509CertDB.TRUSTED_SSL) };`,
                trustedCertificate,
            )
        } finally {
            await run('moz/context', { context: 'content' })
        }
        assert.equal(
            report.profileTrust.fingerprint.replaceAll(':', ''),
            report.certificateFingerprint,
        )
        assert.equal(report.profileTrust.trustedForTls, true)
    }
    assert(
        report.capabilities.browserVersion,
        'Driver must report its actual version',
    )
    assert.equal(
        report.capabilities.browserVersion.split('.')[0],
        process.env.TENONEDGE_EXPECT_BROWSER_MAJOR ?? '128',
    )
    await run('window/rect', { width: 1700, height: 1200 })
    await phase('real setup, logout and login', async () => {
        await navigate('/account')
        assert.equal(await js('return window.isSecureContext;'), secure)
        report.userAgent = await js('return navigator.userAgent;')
        await fill(label('账号'), 'minimum-admin')
        await fill(label('密码'), 'Minimum-browser-5938!')
        await fill(label('确认密码'), 'Minimum-browser-5938!')
        await click(button('创建管理员并登录'))
        await heading('修改密码')
        await click(button('退出登录'))
        await fill(label('账号'), 'minimum-admin')
        await fill(label('密码'), 'Minimum-browser-5938!')
        await click(button('登录'))
        await heading('修改密码')
        assert.equal(
            await js('return document.cookie.includes("Session");'),
            false,
        )
    })
    await phase('deep route and refresh', async () => {
        await navigate('/prototypes/points')
        await heading('点表交互原型')
        await run('refresh', {})
        await heading('点表交互原型')
        assert.equal(await js('return window.isSecureContext;'), secure)
    })
    const paste = async (count, start, bad = false) => {
        const text = Array.from({ length: count }, (_, offset) => {
            const i = start + offset
            return [
                bad && i === 0 ? 'bad key' : `point_${i}`,
                `测点${i}`,
                '3',
                String(i),
                'uint16',
                '',
                '',
                '',
                'true',
            ].join('\t')
        }).join('\n')
        // Gecko's untrusted ClipboardEvent does not expose supplied data. Seed the
        // isolated headless browser clipboard, then send real Ctrl+V through W3C
        // keyboard actions. This is a browser automation extension, not app state.
        assert.equal(report.capabilities.browserName, 'firefox')
        assert.equal(report.capabilities['moz:headless'], true)
        await run('moz/context', { context: 'chrome' })
        try {
            await js(
                'Components.classes["@mozilla.org/widget/clipboardhelper;1"].getService(Components.interfaces.nsIClipboardHelper).copyString(arguments[0]);',
                text,
            )
        } finally {
            await run('moz/context', { context: 'content' })
        }
        // Establish real content focus before Ctrl+V. In system Firefox 158,
        // privileged script focus alone delivered trusted keys but no paste;
        // a native WebDriver click matches the user's gesture. Do not replace
        // the trusted paste with a constructed ClipboardEvent or app-state write.
        await click(css('[data-testid=point-grid]'))
        await js(`const grid=document.querySelector('[data-testid=point-grid]'); grid.focus();
          window.__minimumPaste = null;
          grid.addEventListener('paste', event => { window.__minimumPaste = { trusted:event.isTrusted, length:event.clipboardData.getData('text/plain').length }; }, {once:true,capture:true});`)
        await run('actions', {
            actions: [
                {
                    type: 'key',
                    id: 'keyboard',
                    actions: [
                        { type: 'keyDown', value: '\uE009' },
                        { type: 'keyDown', value: 'v' },
                        { type: 'keyUp', value: 'v' },
                        { type: 'keyUp', value: '\uE009' },
                    ],
                },
            ],
        })
        await run('actions', undefined, 'DELETE')
        const pasteEvent = await until(
            () => js('return window.__minimumPaste;'),
            'native Ctrl+V paste',
        )
        assert.deepEqual(pasteEvent, {
            trusted: true,
            length: text.length,
        })
    }
    await phase('1000-row paste, localized error and correction', async () => {
        await paste(1000, 0, true)
        await until(
            () =>
                js(
                    'return document.querySelector("[data-testid=point-count]").textContent.includes("1000 行 · 0 行选中 · 1 行错误");',
                ),
            '1000 rows with one invalid key',
        )
        await fill(label('第 1 行 key'), 'point_0')
        await until(
            () =>
                js(
                    'return document.querySelector("[data-testid=point-count]").textContent.includes("0 行错误");',
                ),
            'corrected key',
        )
        await screenshot('points-1000')
    })
    await phase('10000-row virtual grid and last-row editing', async () => {
        await paste(9000, 1000)
        await until(
            () =>
                js(
                    'return document.querySelector("[data-testid=point-count]").textContent.includes("10000 行");',
                ),
            '10000 rows',
        )
        assert(
            (await js(
                'return document.querySelectorAll("[data-point-index]").length;',
            )) < 40,
        )
        await js(
            'const grid=document.querySelector("[data-testid=point-grid]"); grid.scrollTop=grid.scrollHeight;',
        )
        await fill(label('第 10000 行 key'), 'edited_last')
        assert.equal(
            await js(
                'return arguments[0].value;',
                await label('第 10000 行 key'),
            ),
            'edited_last',
        )
        report.pointTimings = await js(
            'return document.querySelector("[data-testid=point-timing]").textContent;',
        )
        await screenshot('points-10000')
    })
    await phase(
        'canvas nodes, real pointer connections and cycle rejection',
        async () => {
            await navigate('/prototypes/canvas')
            await heading('画布交互原型')
            const area = await js(
                'const r=document.querySelector(\'[aria-label="路线画布"]\').getBoundingClientRect(); return {x:r.x,y:r.y};',
            )
            const kinds = ['Modbus 输入', '字段映射', '字段映射', 'MQTT 输出']
            for (let i = 0; i < kinds.length; i++) {
                await click(button(kinds[i]))
                // Move each click-created node so other handles are not obscured.
                await drag(css('.react-flow__node.selected'), {
                    x: Math.round(area.x + 120 + i * 225),
                    y: Math.round(area.y + 180),
                })
            }
            await click(
                element(
                    'return [...document.querySelectorAll("summary")].find(e => e.textContent.trim() === arguments[0]);',
                    '查看自有格式（定义与布局分离）',
                ),
            )
            const doc = await graph()
            assert.equal(doc.definition.nodes.length, 4)
            const ids = doc.definition.nodes.map((n) => n.id)
            const handle = (id, kind) =>
                css(
                    `.react-flow__node[data-id="${id}"] .react-flow__handle.${kind}`,
                )
            for (let i = 0; i < 3; i++)
                await drag(
                    handle(ids[i], 'source'),
                    handle(ids[i + 1], 'target'),
                )
            await until(
                async () => (await graph()).definition.edges.length === 3,
                'three connected edges',
            )
            await drag(handle(ids[2], 'source'), handle(ids[1], 'target'))
            await until(
                () =>
                    js(
                        'return [...document.querySelectorAll("[role=alert]")].some(e => e.textContent.includes("不允许自环或循环路线"));',
                    ),
                'cycle rejected',
            )
            assert.equal((await graph()).definition.edges.length, 3)
            await screenshot('canvas')
        },
    )
    await phase('50-step undo and redo', async () => {
        await navigate('/prototypes/canvas')
        await heading('画布交互原型')
        await click(button('字段映射'))
        await click(
            element(
                'return [...document.querySelectorAll("summary")].find(e => e.textContent.trim() === arguments[0]);',
                '查看自有格式（定义与布局分离）',
            ),
        )
        for (let i = 0; i < 55; i++) {
            await fill(label('节点名称'), `映射${i}`)
            await click(button('修改名称'))
        }
        for (let i = 0; i < 50; i++) await click(button('撤销'))
        assert.equal((await graph()).definition.nodes[0].name, '映射4')
        assert.equal(
            await js('return arguments[0].disabled;', await button('撤销')),
            true,
        )
        for (let i = 0; i < 50; i++) await click(button('重做'))
        assert.equal((await graph()).definition.nodes[0].name, '映射54')
        assert.equal(
            await js('return arguments[0].disabled;', await button('重做')),
            true,
        )
    })
    report.passed = true
} catch (error) {
    report.error = error.message
    if (session && secure)
        report.certificateError = await js(
            'return document.getElementById("errorCode")?.textContent;',
        ).catch(() => null)
    if (session) await screenshot('failure').catch(() => {})
    process.exitCode = 1
} finally {
    const cleanupFailed = (error) => {
        report.cleanupError = error.message
        report.passed = false
        process.exitCode = 1
    }
    if (session)
        await command(`/session/${session}`, undefined, 'DELETE').catch(
            cleanupFailed,
        )
    if (host.exitCode === null && host.signalCode === null) host.kill('SIGTERM')
    let exited = await Promise.race([
        hostExited.then(() => true),
        delay(15000, false, { ref: false }),
    ])
    if (!exited) {
        cleanupFailed(new Error('Owned Host did not stop gracefully'))
        host.kill('SIGKILL')
        exited = await Promise.race([
            hostExited.then(() => true),
            delay(5000, false, { ref: false }),
        ])
    }
    if (exited) {
        report.hostExit = await hostExited
        try {
            assertCleanHostExit(report.hostExit)
        } catch (error) {
            cleanupFailed(error)
        }
        await rm(temporary, { recursive: true, force: true }).catch(
            cleanupFailed,
        )
    } else
        cleanupFailed(
            new Error(`Owned Host exit unconfirmed; preserve ${temporary}`),
        )
    process.removeListener('exit', stopOnExit)
    report.durationMs = Math.round(performance.now() - started)
    await writeFile(
        join(output, 'result.json'),
        JSON.stringify(report, null, 2),
    )
    await writeFile(join(output, 'host.log'), hostLog)
    console.log(JSON.stringify(report, null, 2))
}
