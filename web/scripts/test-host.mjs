import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../../', import.meta.url))
const data = await mkdtemp(join(tmpdir(), 'tenonedge-browser-'))
const url = new URL(process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1:18082')
const child = spawn(
    'dotnet',
    [
        join(root, 'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll'),
        `--webroot=${process.env.TENONEDGE_TEST_WEBROOT ?? join(root, 'web/dist')}`,
        `--TenonEdge:DataDirectory=${data}`,
        '--TenonEdge:Http:Address=0.0.0.0',
        `--TenonEdge:Http:Port=${url.port}`,
        '--TenonEdge:Https:Address=127.0.0.1',
        '--TenonEdge:Https:Port=0',
    ],
    {
        cwd: root,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production' },
        stdio: 'inherit',
    },
)
for (const signal of ['SIGINT', 'SIGTERM'])
    process.on(signal, () => child.kill('SIGTERM'))
child.once('error', async (error) => {
    console.error(error)
    await rm(data, { recursive: true, force: true })
    process.exitCode = 1
})
child.once('exit', async (code) => {
    await rm(data, { recursive: true, force: true })
    process.exitCode = code ?? 0
})
