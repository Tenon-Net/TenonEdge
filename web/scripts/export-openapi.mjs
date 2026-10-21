import { spawn } from 'node:child_process'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import openapiTS, { astToString, COMMENT_HEADER } from 'openapi-typescript'

const root = fileURLToPath(new URL('../../', import.meta.url))
const web = join(root, 'web')

// Normalize only the deployment address; schema/default/required changes must
// remain visible. Generate both artifacts before writing either of them.
export async function syncContract(document, directory, check = false) {
    if (
        !document?.openapi?.startsWith('3.') ||
        !document.paths?.['/api/edge-admin/v1/auth/session']
    )
        throw new Error('Not a TenonEdge OpenAPI document')
    const normalized = { ...document, servers: [{ url: '/' }] }
    const artifacts = {
        'openapi.json': JSON.stringify(normalized, null, 2) + '\n',
        'src/api/schema.d.ts':
            COMMENT_HEADER + astToString(await openapiTS(normalized)),
    }
    const changed = []
    for (const [name, content] of Object.entries(artifacts)) {
        const path = join(directory, name)
        if (check) {
            const current = await readFile(path, 'utf8').catch((error) => {
                if (error.code === 'ENOENT') return null
                throw error
            })
            if (current !== content) changed.push(name)
        } else await writeFile(path, content)
    }
    if (changed.length)
        throw new Error(
            `OpenAPI artifacts are stale: ${changed.join(', ')}. Run npm run api:generate and review the diff.`,
        )
}

async function readDocument(base) {
    // Read a Development Host without cookies or credentials. The deadline
    // covers the body too; a stuck server must fail the CI gate.
    const response = await fetch(
        new URL('/api/edge-admin/v1/openapi/v1.json', base),
        { signal: AbortSignal.timeout(15_000) },
    )
    if (!response.ok) throw new Error(`OpenAPI HTTP ${response.status}`)
    return response.json()
}

async function withDevelopmentHost(action) {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-openapi-'))
    const child = spawn(
        'dotnet',
        [
            join(
                root,
                'src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll',
            ),
            `--TenonEdge:DataDirectory=${data}`,
            '--TenonEdge:Http:Enabled=true',
            '--TenonEdge:Http:Address=127.0.0.1',
            '--TenonEdge:Http:Port=0',
            '--TenonEdge:Https:Address=127.0.0.1',
            '--TenonEdge:Https:Port=0',
            '--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information',
        ],
        {
            cwd: root,
            env: {
                ...process.env,
                ASPNETCORE_ENVIRONMENT: 'Development',
                DOTNET_ENVIRONMENT: 'Development',
            },
            stdio: ['ignore', 'pipe', 'pipe'],
        },
    )
    const closed = new Promise((resolve) => child.once('close', resolve))
    const terminate = () => child.kill('SIGTERM')
    const interrupt = (signal) => {
        process.exitCode = signal === 'SIGINT' ? 130 : 143
        terminate()
    }
    process.once('exit', terminate)
    process.once('SIGINT', interrupt)
    process.once('SIGTERM', interrupt)
    let timer
    let output = ''
    try {
        const base = await new Promise((resolve, reject) => {
            const collect = (chunk) => {
                output = (output + chunk.toString('utf8')).slice(-16 * 1024)
                const url = output.match(
                    /Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/,
                )?.[1]
                if (url) resolve(url)
            }
            child.stdout.on('data', collect)
            child.stderr.on('data', collect)
            child.once('error', reject)
            child.once('exit', (code) =>
                reject(new Error(`OpenAPI Host exited (${code})\n${output}`)),
            )
            timer = setTimeout(
                () =>
                    reject(
                        new Error(`OpenAPI Host startup timed out\n${output}`),
                    ),
                30_000,
            )
        })
        clearTimeout(timer)
        return await action(base)
    } finally {
        clearTimeout(timer)
        terminate()
        const killTimer = setTimeout(() => child.kill('SIGKILL'), 10_000)
        await closed
        clearTimeout(killTimer)
        process.removeListener('exit', terminate)
        process.removeListener('SIGINT', interrupt)
        process.removeListener('SIGTERM', interrupt)
        await rm(data, { recursive: true, force: true })
    }
}

if (
    process.argv[1] &&
    import.meta.url === pathToFileURL(process.argv[1]).href
) {
    try {
        const args = process.argv.slice(2)
        if (args.length > 1 || (args.length === 1 && args[0] !== '--check'))
            throw new Error('Usage: node scripts/export-openapi.mjs [--check]')
        const check = args.includes('--check')
        // CI always uses this checkout's built Host, even if an external URL
        // is set. Manual generation may explicitly target a running dev Host.
        const run = async (base) =>
            syncContract(await readDocument(base), web, check)
        if (!check && process.env.TENONEDGE_API_URL)
            await run(process.env.TENONEDGE_API_URL)
        else await withDevelopmentHost(run)
        console.log(
            check
                ? 'OpenAPI artifacts are current.'
                : 'OpenAPI artifacts generated.',
        )
    } catch (error) {
        console.error(error.message)
        process.exitCode ||= 1
    }
}
