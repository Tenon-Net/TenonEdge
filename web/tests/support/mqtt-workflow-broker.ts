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
        const subscribeForReadinessAndOneMessage = async (topic: string) => {
            if (!/^[A-Za-z0-9/_-]{1,128}$/.test(topic))
                throw new Error('invalid workflow MQTT topic')
            const log = `/tmp/tenonedge-sub-${randomUUID()}.log`
            await docker([
                'exec',
                '-d',
                name,
                'sh',
                '-c',
                `mosquitto_sub -h 127.0.0.1 -t '${topic}' -q 1 -v -W 90 -C 2 > '${log}' 2>&1`,
            ])
            const read = async () => {
                try {
                    return (
                        await docker(['exec', name, 'sh', '-c', `cat '${log}'`])
                    ).stdout
                } catch {
                    return ''
                }
            }
            return {
                read,
                async waitForRetained(marker: string) {
                    await expect
                        .poll(read, { timeout: 10_000 })
                        .toContain(marker)
                },
                async waitForMessages(count: number) {
                    await expect
                        .poll(
                            async () =>
                                (await read()).split('\n').filter(Boolean)
                                    .length,
                            { timeout: 30_000 },
                        )
                        .toBe(count)
                    return read()
                },
            }
        }
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
        return { port, publish, subscribeForReadinessAndOneMessage, close }
    } catch (error) {
        await close()
        throw error
    }
}
