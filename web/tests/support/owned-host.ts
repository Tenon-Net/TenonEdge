import { expect } from '@playwright/test'
import { spawn } from 'node:child_process'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../../../', import.meta.url))

async function bounded<T>(
    task: Promise<T>,
    milliseconds: number,
    diagnostic: () => string,
) {
    let timer: ReturnType<typeof setTimeout> | undefined
    try {
        return await Promise.race([
            task,
            new Promise<T>((_, reject) => {
                timer = setTimeout(
                    () => reject(new Error(diagnostic())),
                    milliseconds,
                )
            }),
        ])
    } finally {
        clearTimeout(timer)
    }
}

// This test owns these processes and this data directory; no control API or
// fault-injection switch is added to the product or the shared test Host.
export async function startOwnedHost(
    data: string,
    port = 0,
    extraArguments: string[] = [],
    httpAddress = '127.0.0.1',
    moduleHost = false,
) {
    const env: NodeJS.ProcessEnv = {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Production',
    }
    for (const key of [
        'DOTNET_ENVIRONMENT',
        'ASPNETCORE_URLS',
        'DOTNET_URLS',
        'TenonEdge__DataDirectory',
    ])
        delete env[key]
    const child = spawn(
        'dotnet',
        [
            join(
                root,
                moduleHost
                    ? 'tests/IntegrationTests/bin/Release/net10.0/TenonEdge.IntegrationTests.dll'
                    : 'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll',
            ),
            ...(moduleHost
                ? [
                      '--tenonedge-test-transform-host',
                      `--contentRoot=${join(root, 'src/TenonEdge.Host')}`,
                  ]
                : []),
            `--webroot=${process.env.TENONEDGE_TEST_WEBROOT ?? join(root, 'web/dist')}`,
            `--TenonEdge:DataDirectory=${data}`,
            `--TenonEdge:Http:Address=${httpAddress}`,
            `--TenonEdge:Http:Port=${port}`,
            '--TenonEdge:Https:Address=127.0.0.1',
            '--TenonEdge:Https:Port=0',
            '--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information',
            ...extraArguments,
        ],
        { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] },
    )
    const terminateOnWorkerExit = () => {
        child.kill('SIGTERM')
    }
    process.once('exit', terminateOnWorkerExit)
    child.once('close', () =>
        process.removeListener('exit', terminateOnWorkerExit),
    )
    let output = ''
    let admitted!: (address: string) => void
    let failed!: (error: Error) => void
    const address = new Promise<string>((resolve, reject) => {
        admitted = resolve
        failed = reject
    })
    const exited = new Promise<number | null>((resolve) =>
        child.once('close', resolve),
    )
    const collect = (chunk: Buffer) => {
        output = (output + chunk.toString('utf8')).slice(-16 * 1024)
        const url = output.match(/Now listening on:\s*(http:\/\/\S+)/)?.[1]
        if (url) admitted(url)
    }
    child.stdout.on('data', collect)
    child.stderr.on('data', collect)
    child.once('error', failed)
    child.once('exit', (code) =>
        failed(new Error(`owned-host-exited ${code}\n${output}`)),
    )
    const stop = async () => {
        if (child.exitCode === null && child.signalCode === null)
            child.kill('SIGTERM')
        try {
            return await bounded(
                exited,
                15_000,
                () => `owned-host-stop-timeout\n${output}`,
            )
        } catch (error) {
            child.kill('SIGKILL')
            await exited
            throw error
        }
    }
    try {
        const url = await bounded(
            address,
            30_000,
            () => `owned-host-start-timeout\n${output}`,
        )
        await expect
            .poll(async () => (await fetch(url + '/healthz')).status)
            .toBe(200)
        // Public trust material comes from this owned process's console, not a network response.
        const httpsUrl = output.match(
            /Now listening on:\s*(https:\/\/127\.0\.0\.1:\d+)/,
        )?.[1]
        const certificateFingerprint = output.match(
            /SHA-256 指纹 ([0-9A-F]{64})/,
        )?.[1]
        return {
            url,
            httpsUrl,
            certificateFingerprint,
            stop,
            get exitSignal() {
                return child.signalCode
            },
        }
    } catch (error) {
        await stop()
        throw error
    }
}
