import { createServer as createHttpServer } from 'node:http'
import { createServer as createTcpServer, type Socket } from 'node:net'
import { mkdtemp, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { startOwnedHost } from './owned-host'

/** Real socket peers, not management API mocks. Only known Modbus reads are served. */
export async function startWorkflowGateway() {
    const sockets = new Set<Socket>()
    const protocolErrors: string[] = []
    const reads: { unit: number; address: number; count: number }[] = []
    const deliveries: {
        body: string
        authorization?: string
        id?: string
        path?: string
        at: number
    }[] = []
    const registers = [0x0020, 0, 0, 1, 2385] // uint64 9007199254740993, then uint16 2385
    const track = (socket: Socket) => {
        sockets.add(socket)
        socket.on('close', () => sockets.delete(socket))
        socket.on('error', () => {})
    }
    const modbus = createTcpServer((socket) => {
        track(socket)
        let pending = Buffer.alloc(0)
        socket.on('data', (chunk) => {
            pending = Buffer.concat([pending, chunk])
            if (pending.length > 4096) {
                protocolErrors.push('oversized Modbus request buffer')
                socket.destroy()
                return
            }
            while (pending.length >= 12) {
                const request = pending.subarray(0, 12)
                pending = pending.subarray(12)
                const address = request.readUInt16BE(8)
                const count = request.readUInt16BE(10)
                if (
                    request.readUInt16BE(2) !== 0 ||
                    request.readUInt16BE(4) !== 6 ||
                    request[6] !== 1 ||
                    request[7] !== 3 ||
                    count < 1 ||
                    address + count > registers.length
                ) {
                    protocolErrors.push('unexpected Modbus request')
                    socket.destroy()
                    return
                }
                reads.push({ unit: request[6], address, count })
                const reply = Buffer.alloc(9 + count * 2)
                request.copy(reply, 0, 0, 4)
                reply.writeUInt16BE(3 + count * 2, 4)
                reply[6] = request[6]
                reply[7] = 3
                reply[8] = count * 2
                for (let index = 0; index < count; index++)
                    reply.writeUInt16BE(
                        registers[address + index],
                        9 + index * 2,
                    )
                socket.write(reply)
            }
        })
    })
    const http = createHttpServer(async (request, response) => {
        const chunks: Buffer[] = []
        let bytes = 0
        for await (const chunk of request) {
            bytes += chunk.length
            if (bytes > 1024 * 1024) {
                protocolErrors.push('oversized HTTP request')
                response.writeHead(413).end()
                return
            }
            chunks.push(Buffer.from(chunk))
        }
        if (request.method !== 'POST')
            protocolErrors.push('unexpected HTTP method')
        deliveries.push({
            body: Buffer.concat(chunks).toString('utf8'),
            authorization: request.headers.authorization,
            id: request.headers['idempotency-key'] as string | undefined,
            path: request.url,
            at: Date.now(),
        })
        response.writeHead(204).end()
    })
    http.on('connection', track)
    const listen = (server: typeof modbus | typeof http) =>
        new Promise<number>((resolve, reject) => {
            server.once('error', reject)
            server.listen(0, '127.0.0.1', () => {
                const address = server.address()
                if (!address || typeof address === 'string')
                    return reject(new Error('missing listener'))
                resolve(address.port)
            })
        })
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-no-json-workflow-'))
    let host: Awaited<ReturnType<typeof startOwnedHost>> | undefined
    const close = async () => {
        let exit: number | null | undefined
        try {
            exit = await host?.stop()
        } finally {
            for (const socket of sockets) socket.destroy()
            try {
                for (const server of [modbus, http]) {
                    if (server.listening)
                        await new Promise<void>((resolve, reject) =>
                            server.close((error) =>
                                error ? reject(error) : resolve(),
                            ),
                        )
                }
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
        if (exit !== undefined && exit !== 0)
            throw new Error(`Owned Host exit: ${exit}`)
    }
    try {
        const modbusPort = await listen(modbus)
        const httpPort = await listen(http)
        const args = [
            `--TenonEdge:Outbound:LocalPorts:0=${modbusPort}`,
            `--TenonEdge:Outbound:LocalPorts:1=${httpPort}`,
        ]
        const address = new URL(
            process.env.TENONEDGE_TEST_URL ?? 'http://127.0.0.1:18082',
        ).hostname
        host = await startOwnedHost(data, 0, args, address)
        const url = host.url
        return {
            url,
            modbusPort,
            httpPort,
            reads,
            deliveries,
            protocolErrors,
            close,
            async restart() {
                if ((await host!.stop()) !== 0)
                    throw new Error('Owned Host failed to stop normally')
                host = await startOwnedHost(
                    data,
                    Number(new URL(url).port),
                    args,
                    address,
                )
                if (host.url !== url)
                    throw new Error('Owned Host changed address')
            },
        }
    } catch (error) {
        await close()
        throw error
    }
}
