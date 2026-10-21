import { expect } from '@playwright/test'
import { execFile, spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { promisify } from 'node:util'

// Isolated test-only broker. Anonymous loopback listener is not a deployment template.
export async function mqttOutputPeer(topic: string) {
    const files = await mkdtemp(join(tmpdir(), 'tenonedge-output-peer-'))
    const name = 'tenonedge-output-peer-' + randomUUID()
    const run = promisify(execFile)
    const docker = (args: string[]) => run('docker', args, { timeout: 30_000 })
    let subscriber: ReturnType<typeof spawn> | undefined
    let exited: Promise<unknown> | undefined
    const close = async () => {
        try {
            await docker(['rm', '-f', name])
            if (exited) await exited
        } finally {
            subscriber?.kill('SIGTERM')
            await rm(files, { recursive: true, force: true })
        }
    }
    try {
        await writeFile(
            join(files, 'mosquitto.conf'),
            'listener 1883\nallow_anonymous true\n',
        )
        await docker([
            'run',
            '-d',
            '--name',
            name,
            '-p',
            '127.0.0.1::1883',
            '-v',
            `${files}:/probe:ro`,
            'eclipse-mosquitto:2.1.2-alpine',
            'mosquitto',
            '-c',
            '/probe/mosquitto.conf',
        ])
        const mapping = await docker(['port', name, '1883/tcp'])
        const port = Number(mapping.stdout.trim().split(':').at(-1))
        await expect
            .poll(
                async () => {
                    try {
                        await docker([
                            'exec',
                            name,
                            'timeout',
                            '2',
                            'mosquitto_pub',
                            '-h',
                            '127.0.0.1',
                            '-t',
                            'fixture/readiness',
                            '-n',
                            '-q',
                            '1',
                        ])
                        return true
                    } catch {
                        return false
                    }
                },
                { timeout: 10_000 },
            )
            .toBe(true)
        const child = spawn(
            'docker',
            [
                'exec',
                '-t',
                name,
                'mosquitto_sub',
                '-h',
                '127.0.0.1',
                '-t',
                topic,
                '-q',
                '1',
                '-d',
            ],
            { stdio: ['ignore', 'pipe', 'pipe'] },
        )
        subscriber = child
        exited = new Promise((resolve) => child.once('close', resolve))
        let output = ''
        child.stdout.on('data', (chunk) => {
            output = (output + chunk.toString()).slice(-1024 * 1024)
        })
        child.stderr.on('data', (chunk) => {
            output = (output + chunk.toString()).slice(-1024 * 1024)
        })
        await expect.poll(() => output, { timeout: 10_000 }).toContain('SUBACK')
        return {
            port,
            async publish(
                inputTopic: string,
                payload: string,
                retained = false,
            ) {
                await docker([
                    'exec',
                    name,
                    'timeout',
                    '3',
                    'mosquitto_pub',
                    '-h',
                    '127.0.0.1',
                    '-t',
                    inputTopic,
                    '-m',
                    payload,
                    '-q',
                    '1',
                    ...(retained ? ['-r'] : []),
                ])
            },
            bodies: () =>
                output
                    .split('\n')
                    .map((line) => line.trim())
                    .filter((line) => line.startsWith('{')),
            close,
        }
    } catch (error) {
        await close()
        throw error
    }
}
