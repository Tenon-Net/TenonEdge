// Short, reproducible software workload; does not claim a hardware capacity limit.
import { chromium, expect } from '@playwright/test'
import { createServer } from 'node:http'
import { execFileSync } from 'node:child_process'
import {
    mkdtemp,
    mkdir,
    readFile,
    readdir,
    writeFile,
    rm,
} from 'node:fs/promises'
import { tmpdir, cpus, totalmem, loadavg } from 'node:os'
import { isAbsolute, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { startOwnedHost } from '../tests/support/owned-host.ts'

const root = fileURLToPath(new URL('../../', import.meta.url))
const evidence = process.env.TENONEDGE_QA_RESULTS
if (!evidence) throw new Error('TENONEDGE_QA_RESULTS is required')
const smoke = process.env.TENONEDGE_QA_SMOKE === '1'
const inputCount = Number(process.env.TENONEDGE_QA_INPUTS ?? 50)
const periodMs = Number(process.env.TENONEDGE_QA_PERIOD_MS ?? 1000)
const outageSeconds = Number(process.env.TENONEDGE_QA_OUTAGE_SECONDS ?? 15)
if (!Number.isInteger(inputCount) || inputCount < 1 || inputCount > 2000)
    throw new Error('TENONEDGE_QA_INPUTS must be an integer from 1 to 2000')
if (!Number.isInteger(periodMs) || periodMs < 1000 || periodMs > 60_000)
    throw new Error(
        'TENONEDGE_QA_PERIOD_MS must be an integer from 1000 to 60000',
    )
if (!Number.isInteger(outageSeconds) || outageSeconds < 1 || outageSeconds > 60)
    throw new Error(
        'TENONEDGE_QA_OUTAGE_SECONDS must be an integer from 1 to 60',
    )
const clockTicksPerSecond = Number(
    execFileSync('getconf', ['CLK_TCK'], { encoding: 'utf8' }).trim(),
)
const dataRoot = process.env.TENONEDGE_QA_DATA_ROOT ?? tmpdir()
if (!isAbsolute(dataRoot))
    throw new Error('TENONEDGE_QA_DATA_ROOT must be absolute')
await mkdir(evidence, { recursive: true })
await mkdir(dataRoot, { recursive: true })
const data = await mkdtemp(join(dataRoot, 'tenonedge-product-performance-'))
const report = {
    startedUtc: new Date().toISOString(),
    kind: smoke
        ? 'harness smoke test; not a performance result'
        : 'isolated short software workload',
    errors: [],
    resources: [],
    points: [],
    api: [],
    navigation: [],
    stages: [],
    workload: {
        inputCount,
        periodMs,
        outageSeconds,
        nominalMessagesPerSecond: (inputCount * 1000) / periodMs,
    },
    environment: {
        logicalCpus: cpus().length,
        totalMemoryBytes: totalmem(),
        loadAverageAtStart: loadavg(),
        clockTicksPerSecond,
        dataRoot,
    },
}
const sleep = (milliseconds) =>
    new Promise((resolve) => setTimeout(resolve, milliseconds))
const summary = (values) => {
    const sorted = [...values].sort((a, b) => a - b)
    return {
        count: sorted.length,
        min: sorted[0],
        median: sorted[Math.floor(sorted.length * 0.5)],
        p95: sorted[
            Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1)
        ],
        p99: sorted[
            Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.99) - 1)
        ],
        max: sorted.at(-1),
    }
}
let host,
    browser,
    page,
    get,
    sampler,
    sampling = false
let available = true,
    rejected = 0,
    badPayloads = 0
const accepted = [],
    deliveryLatencyMs = [],
    ids = new Set()
let duplicates = 0
const sockets = new Set()
const receiver = createServer(async (request, response) => {
    const chunks = []
    for await (const chunk of request) chunks.push(chunk)
    if (!available) {
        rejected++
        response.writeHead(503).end()
        return
    }
    const body = Buffer.concat(chunks).toString('utf8')
    const id = request.headers['idempotency-key']
    if (!/"energy"\s*:\s*9007199254740993\s*[,}]/.test(body) || !id)
        badPayloads++
    if (ids.has(id)) duplicates++
    ids.add(id)
    accepted.push(performance.now())
    try {
        const collectedAt = Date.parse(JSON.parse(body).collectedAt)
        if (!Number.isFinite(collectedAt)) badPayloads++
        deliveryLatencyMs.push(Date.now() - collectedAt)
    } catch {
        badPayloads++
        deliveryLatencyMs.push(Number.NaN)
    }
    response.writeHead(204).end()
})
receiver.on('connection', (socket) => {
    sockets.add(socket)
    socket.once('close', () => sockets.delete(socket))
})
async function ownProcess() {
    for (const name of await readdir('/proc')) {
        if (!/^\d+$/.test(name)) continue
        try {
            const args = (
                await readFile(`/proc/${name}/cmdline`, 'utf8')
            ).split('\0')
            if (
                args.includes(`--TenonEdge:DataDirectory=${data}`) &&
                args.includes(
                    join(
                        root,
                        'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll',
                    ),
                )
            ) {
                const stat = (await readFile(`/proc/${name}/stat`, 'utf8'))
                    .split(') ')[1]
                    .split(' ')
                return { pid: name, startTicks: stat[19] }
            }
        } catch {
            /* Process exited during enumeration. */
        }
    }
    throw new Error('Could not identify the owned Host process')
}
async function sampleResource(process) {
    const stat = (await readFile(`/proc/${process.pid}/stat`, 'utf8'))
        .split(') ')[1]
        .split(' ')
    if (stat[19] !== process.startTicks)
        throw new Error('Owned Host identity changed')
    const status = await readFile(`/proc/${process.pid}/status`, 'utf8')
    report.resources.push({
        at: performance.now(),
        cpuTicks: Number(stat[11]) + Number(stat[12]),
        rssKiB: Number(status.match(/^VmRSS:\s+(\d+)/m)?.[1]),
    })
}
async function paint(page) {
    await page.evaluate(
        () =>
            new Promise((resolve) =>
                requestAnimationFrame(() => requestAnimationFrame(resolve)),
            ),
    )
}
async function paste(page, count, start) {
    const text = Array.from({ length: count }, (_, i) =>
        [
            `point_${start + i}`,
            `测点${start + i}`,
            '3',
            start + i,
            'uint16',
            '',
            '',
            '',
            'true',
        ].join('\t'),
    ).join('\n')
    const begin = performance.now()
    await page.getByTestId('point-grid').evaluate((element, value) => {
        const clipboardData = new DataTransfer()
        clipboardData.setData('text/plain', value)
        element.dispatchEvent(
            new ClipboardEvent('paste', {
                bubbles: true,
                cancelable: true,
                clipboardData,
            }),
        )
    }, text)
    await expect(page.getByTestId('point-count')).toContainText(
        `${start + count} 行 · 0 行选中 · 0 行错误`,
    )
    await paint(page)
    return performance.now() - begin
}
try {
    await new Promise((resolve) => receiver.listen(0, '127.0.0.1', resolve))
    const port = receiver.address().port
    const units = Array.from({ length: inputCount }, (_, i) => ({
        kind: 'input.simulator',
        key: `load_${i}/sample`,
        configuration: {
            periodMs,
            sample: { energy: 'EXACT_FIXTURE_NUMBER', temperature: 2385 },
        },
    }))
    units.push({
        kind: 'output.http',
        key: 'load/http',
        configuration: {
            connectionId: 1,
            baseAddress: `http://127.0.0.1:${port}/`,
            path: 'capture',
            inputs: units.map((unit) => unit.key),
        },
    })
    const seed = join(data, 'seed.json')
    await writeFile(
        seed,
        JSON.stringify({ schemaVersion: 1, units }).replaceAll(
            '"EXACT_FIXTURE_NUMBER"',
            '9007199254740993',
        ),
    )
    const start = performance.now()
    host = await startOwnedHost(data, 0, [
        `--TenonEdge:Runtime:SeedSnapshot=${seed}`,
        `--TenonEdge:Outbound:LocalPorts:0=${port}`,
    ])
    report.hostReadyMs = performance.now() - start
    const process = await ownProcess()
    await sampleResource(process)
    sampler = setInterval(async () => {
        if (sampling) return
        sampling = true
        try {
            await sampleResource(process)
        } catch (error) {
            report.errors.push(error.message)
        } finally {
            sampling = false
        }
    }, 1000)
    browser = await chromium.launch()
    report.browser = browser.version()
    const context = await browser.newContext({
        viewport: { width: 1600, height: 1100 },
        locale: 'zh-CN',
    })
    page = await context.newPage()
    page.on('pageerror', (error) => report.errors.push(error.message))
    const initial = performance.now()
    await page.goto(host.url)
    await expect(
        page.getByRole('button', { name: '创建管理员并登录', exact: true }),
    ).toBeVisible()
    report.firstSetupVisibleMs = performance.now() - initial
    await page.getByLabel('账号', { exact: true }).fill('performance-admin')
    await page
        .getByLabel('密码', { exact: true })
        .fill('Performance-fixture-6732!')
    await page
        .getByLabel('确认密码', { exact: true })
        .fill('Performance-fixture-6732!')
    await page
        .getByRole('button', { name: '创建管理员并登录', exact: true })
        .click()
    await expect(page.locator('[data-dashboard="operations"]')).toBeVisible()
    await expect
        .poll(() => accepted.length, { timeout: 20_000 })
        .toBeGreaterThan(0)
    get = async (path) => {
        const response = await context.request.get(
            host.url + '/api/edge-admin/v1' + path,
        )
        if (response.status() !== 200)
            throw new Error(`${path}: HTTP ${response.status()}`)
        return response.json()
    }
    report.initialRuntime = await get('/runtime/status')
    const cdp = await context.newCDPSession(page)
    for (let trial = 1; trial <= (smoke ? 1 : 3); trial++) {
        await page.goto(host.url + '/prototypes/points')
        await expect(page.getByTestId('point-grid')).toBeVisible()
        const before = await cdp.send('Runtime.getHeapUsage')
        const thousandMs = await paste(page, 1000, 0)
        const tenThousandMs = await paste(page, 9000, 1000)
        const scroll = performance.now()
        await page.getByTestId('point-grid').evaluate((element) => {
            element.scrollTop = element.scrollHeight
        })
        await expect(
            page.getByLabel('第 10000 行 key', { exact: true }),
        ).toHaveValue('point_9999')
        const lastRowVisibleMs = performance.now() - scroll
        const edit = performance.now()
        await page
            .getByLabel('第 10000 行 key', { exact: true })
            .fill('edited_last')
        await paint(page)
        const renderedRows = await page.locator('[data-point-index]').count()
        expect(renderedRows).toBeLessThan(40)
        report.points.push({
            trial,
            thousandMs,
            append9000Ms: tenThousandMs,
            lastRowVisibleMs,
            lastRowEditMs: performance.now() - edit,
            renderedRows,
            before,
            after: await cdp.send('Runtime.getHeapUsage'),
            timings: await page.getByTestId('point-timing').textContent(),
            softwarePasteNotExcel: true,
        })
    }
    await page.goto(host.url + '/prototypes/canvas')
    const add = page.getByRole('button', { name: '字段映射', exact: true })
    await expect(add).toBeVisible()
    const canvasStart = performance.now()
    const canvasNodes = smoke ? 5 : 50
    for (let index = 0; index < canvasNodes; index++) await add.click()
    await expect(page.locator('.react-flow__minimap-node')).toHaveCount(
        canvasNodes,
    )
    const renameMs = []
    for (let i = 0; i < 3; i++) {
        const begin = performance.now()
        await page.getByLabel('节点名称', { exact: true }).fill(`性能测量 ${i}`)
        await page
            .getByRole('button', { name: '修改名称', exact: true })
            .click()
        await paint(page)
        renameMs.push(performance.now() - begin)
    }
    report.canvas = {
        nodes: canvasNodes,
        buildByRealUiMs: performance.now() - canvasStart,
        renameMs: summary(renameMs),
    }
    for (const [path, name] of [
        ['/connections', '连接'],
        ['/devices', '设备草稿'],
        ['/routes', '路线草稿'],
        ['/system', '系统信息与设置'],
    ]) {
        const measurements = []
        for (let i = 0; i < 3; i++) {
            await page.goto(host.url)
            await expect(
                page.locator('[data-dashboard="operations"]'),
            ).toBeVisible()
            const begin = performance.now()
            await page
                .getByRole('navigation', { name: '主导航' })
                .getByRole('link', { name, exact: true })
                .click()
            await expect(page).toHaveURL(host.url + path)
            await expect(page.locator('#content h1')).toHaveCount(1)
            await paint(page)
            measurements.push(performance.now() - begin)
        }
        report.navigation.push({
            path,
            visiblePageMs: summary(measurements),
            includesAllDataReady: false,
        })
    }
    await page.goto(host.url)
    for (const concurrency of [1, 5, 10]) {
        const timings = [],
            statuses = {},
            total = smoke ? 20 : 100
        let next = 0
        const begin = performance.now()
        await Promise.all(
            Array.from({ length: concurrency }, async () => {
                while (next++ < total) {
                    const start = performance.now()
                    const response = await context.request.get(
                        host.url + '/api/edge-admin/v1/runtime/status',
                    )
                    await response.body()
                    statuses[response.status()] =
                        (statuses[response.status()] ?? 0) + 1
                    timings.push(performance.now() - start)
                }
            }),
        )
        expect(statuses).toEqual({ 200: total })
        report.api.push({
            endpoint: 'authenticated runtime/status',
            concurrency,
            latencyMs: summary(timings),
            requestsPerSecond: total / ((performance.now() - begin) / 1000),
            statuses,
        })
    }
    const stage = async (name, seconds) => {
        const start = performance.now(),
            count = accepted.length
        await sleep(seconds * 1000)
        const outputs = await get('/runtime/outputs')
        report.stages.push({
            name,
            seconds: (performance.now() - start) / 1000,
            accepted: accepted.length - count,
            acceptedPerSecond:
                (accepted.length - count) /
                ((performance.now() - start) / 1000),
            usage: outputs.usage,
            counters: outputs.counters,
            collectedToReceiverMs: summary(deliveryLatencyMs.slice(count)),
        })
        return outputs
    }
    report.beforeSteadyOutputs = await get('/runtime/outputs')
    await stage('steady-software-inputs', smoke ? 2 : 60)
    if (!smoke)
        expect(report.stages.at(-1).acceptedPerSecond).toBeGreaterThanOrEqual(
            report.workload.nominalMessagesPerSecond * 0.95,
        )
    available = false
    const outage = await stage('receiver-503-outage', smoke ? 2 : outageSeconds)
    expect(Number(outage.usage.pendingCount)).toBeGreaterThan(0)
    available = true
    const recovery = performance.now(),
        beforeRecovery = accepted.length
    report.recoveryStartedAt = recovery
    await expect
        .poll(
            async () =>
                Number((await get('/runtime/outputs')).usage.pendingCount),
            { timeout: 60_000, intervals: [250] },
        )
        .toBe(0)
    report.recovery = {
        secondsToZeroPending: (performance.now() - recovery) / 1000,
        acceptedDuringRecovery: accepted.length - beforeRecovery,
    }
    await stage('post-recovery-steady', smoke ? 2 : 15)
    expect(duplicates).toBe(0)
    expect(badPayloads).toBe(0)
    expect(report.errors).toEqual([])
    report.completed = true
} catch (error) {
    report.failure = error.message
    throw error
} finally {
    clearInterval(sampler)
    while (sampling) await sleep(10)
    // Preserve useful results on overload and assertion failure too. A passing
    // lower load must never erase a failed higher-load recovery measurement.
    if (get) {
        try {
            report.finalRuntime = await get('/runtime/status')
            report.finalOutputs = await get('/runtime/outputs')
            report.noPolicyLoss = Object.values(
                report.finalOutputs.counters,
            ).every((count) => Number(count) === 0)
            const runtime = report.finalRuntime
            report.sourceOverruns = [
                runtime.persistentCounters,
                runtime.unpersistedCounters,
            ]
                .flatMap((counters) => Object.entries(counters))
                .filter(([name]) => name.endsWith('/overruns'))
                .reduce((sum, [, count]) => sum + Number(count), 0)
            report.noStorageLoss =
                runtime.queue.storageFault === false &&
                runtime.queue.failedBatches === 0 &&
                runtime.queue.rejectedRecords === 0 &&
                [runtime.persistentCounters, runtime.unpersistedCounters].every(
                    (counters) =>
                        Number(counters.storageLostSourceMessages ?? 0) === 0 &&
                        Number(counters.storageLostDeliveryRecords ?? 0) === 0,
                )
        } catch (error) {
            report.errors.push(`final snapshot: ${error.message}`)
        }
    }
    if (page) {
        try {
            await page.screenshot({
                path: join(evidence, 'loaded-overview.png'),
                fullPage: true,
            })
        } catch (error) {
            report.errors.push(`final screenshot: ${error.message}`)
        }
    }
    report.accepted = accepted.length
    report.rejectedAttempts = rejected
    report.duplicateAcceptedIds = duplicates
    report.badPayloads = badPayloads
    report.sampledHostRssMiB = summary(
        report.resources.map((point) => point.rssKiB / 1024),
    )
    const first = report.resources[0],
        last = report.resources.at(-1)
    if (first && last && last.at > first.at) {
        report.sampledHostCpu = {
            observationSeconds: (last.at - first.at) / 1000,
            cpuSeconds: (last.cpuTicks - first.cpuTicks) / clockTicksPerSecond,
            averageOneCorePercent:
                ((last.cpuTicks - first.cpuTicks) /
                    clockTicksPerSecond /
                    ((last.at - first.at) / 1000)) *
                100,
        }
    }
    report.pointsSummary = {
        thousandMs: summary(report.points.map((p) => p.thousandMs)),
        append9000Ms: summary(report.points.map((p) => p.append9000Ms)),
        lastRowEditMs: summary(report.points.map((p) => p.lastRowEditMs)),
    }
    report.finishedUtc = new Date().toISOString()
    report.environment.loadAverageAtEnd = loadavg()
    await browser?.close()
    if (host) report.hostExitCode = await host.stop()
    for (const socket of sockets) socket.destroy()
    await new Promise((resolve) => receiver.close(resolve))
    await rm(data, { recursive: true, force: true })
    report.passed =
        report.completed === true &&
        report.hostExitCode === 0 &&
        report.errors.length === 0 &&
        report.noPolicyLoss === true &&
        report.noStorageLoss === true &&
        report.sourceOverruns === 0 &&
        badPayloads === 0 &&
        duplicates === 0
    await writeFile(
        join(evidence, 'performance.json'),
        JSON.stringify(report, null, 2) + '\n',
    )
}
if (report.hostExitCode !== 0)
    throw new Error(`Owned Host exited ${report.hostExitCode}`)
if (!report.passed)
    throw new Error('Workload or final evidence collection did not pass')
console.log(
    JSON.stringify(
        {
            completed: report.completed,
            accepted: report.accepted,
            recovery: report.recovery,
            sampledHostRssMiB: report.sampledHostRssMiB,
            pointsSummary: report.pointsSummary,
            api: report.api,
        },
        null,
        2,
    ),
)
