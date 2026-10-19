import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './owned-host'
import { mqttOutputPeer } from './mqtt-output-peer'

export const sampleTime = '2026-01-02T03:04:05Z'
export const sample = `{"energy":9007199254740993,"temperature":2385,"sampleTime":"${sampleTime}"}`

/** Independent real HTTP peer and Mosquitto, never management/configuration fixtures. */
export async function startJsonWorkflow() {
    const broker = await mqttOutputPeer('workflow/output/meter')
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-json-workflow-'))
    const reads: string[] = []
    const deliveries: { body: string; path?: string; id?: string }[] = []
    const errors: string[] = []
    const http = createServer(async (request, response) => {
        if (request.method === 'GET' && request.url === '/readings?floor=1') {
            reads.push(request.url)
            response
                .writeHead(200, { 'Content-Type': 'application/json' })
                .end(sample)
            return
        }
        if (request.method !== 'POST' || request.url !== '/capture') {
            errors.push('unexpected HTTP method/path')
            response.writeHead(404).end()
            return
        }
        const chunks: Buffer[] = []
        let bytes = 0
        try {
            for await (const chunk of request) {
                bytes += chunk.length
                if (bytes > 262144) {
                    errors.push('oversized HTTP output')
                    response.writeHead(413).end()
                    return
                }
                chunks.push(Buffer.from(chunk))
            }
            const id = request.headers['idempotency-key']
            deliveries.push({
                body: Buffer.concat(chunks).toString('utf8'),
                path: request.url,
                id: typeof id === 'string' ? id : undefined,
            })
            response.writeHead(204).end()
        } catch {
            errors.push('HTTP request aborted')
            response.destroy()
        }
    })
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    const close = async () => {
        try {
            if (host && (await host.stop()) !== 0)
                throw new Error(
                    'Owned JSON workflow Host failed to stop normally',
                )
        } finally {
            try {
                http.closeAllConnections()
                if (http.listening)
                    await new Promise<void>((resolve, reject) =>
                        http.close((error) =>
                            error ? reject(error) : resolve(),
                        ),
                    )
            } finally {
                try {
                    await broker.close()
                } finally {
                    await rm(data, { recursive: true, force: true })
                }
            }
        }
    }
    try {
        await new Promise<void>((resolve, reject) => {
            http.once('error', reject)
            http.listen(0, '127.0.0.1', resolve)
        })
        const endpoint = http.address()
        if (!endpoint || typeof endpoint === 'string')
            throw new Error('HTTP listener missing')
        const args = [
            `--TenonEdge:Outbound:LocalPorts:0=${endpoint.port}`,
            `--TenonEdge:Outbound:LocalPorts:1=${broker.port}`,
        ]
        const address = new URL(
            process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1',
        ).hostname
        host = await startOwnedHost(data, 0, args, address)
        const url = host.url
        return {
            url,
            httpPort: endpoint.port,
            broker,
            reads,
            deliveries,
            errors,
            close,
            async restart() {
                if ((await host!.stop()) !== 0)
                    throw new Error('Host did not stop normally')
                host = await startOwnedHost(
                    data,
                    Number(new URL(url).port),
                    args,
                    address,
                )
                if (host.url !== url) throw new Error('Host address changed')
            },
        }
    } catch (error) {
        await close()
        throw error
    }
}
