import { execFile } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { promisify } from 'node:util'
import { expect } from '@playwright/test'

const exec = promisify(execFile)

/** Isolated real MQTT broker used only by the browser workflow test. */
export async function startMqttWorkflowBroker() {
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-mqtt-workflow-'))
    const name = 'tenonedge-mqtt-workflow-' + randomUUID()
    const docker = (args: string[]) => exec('docker', args, { timeout: 30_000 })
    const close = async () => {
        try {
            await docker(['rm', '-f', name])
        } finally {
            await rm(data, { recursive: true, force: true })
        }
    }
    try {
        await writeFile(
            join(data, 'mosquitto.conf'),
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
            `${data}:/probe:ro`,
            'eclipse-mosquitto:2.1.2-alpine',
            'mosquitto',
            '-c',
            '/probe/mosquitto.conf',
        ])
        const mapping = await docker(['port', name, '1883/tcp'])
        const port = Number(mapping.stdout.trim().split(':').at(-1))
        const publish = (topic: string, payload: string, retained = false) =>
            docker([
                'exec',
                name,
                'timeout',
                '3',
                'mosquitto_pub',
                '-h',
                '127.0.0.1',
                '-t',
                topic,
                '-m',
                payload,
                '-q',
                '1',
                ...(retained ? ['-r'] : []),
            ])
        await expect
            .poll(
                async () => {
                    try {
                        await publish('fixture/readiness', '{}')
                        return true
                    } catch {
                        return false
                    }
                },
                { timeout: 10_000 },
            )
            .toBe(true)
        return { port, publish, close }
    } catch (error) {
        await close()
        throw error
    }
}
