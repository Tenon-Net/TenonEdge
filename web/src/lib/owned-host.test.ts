import { EventEmitter } from 'node:events'
import { afterEach, expect, it, vi } from 'vitest'
import { startOwnedHost } from '../../tests/support/owned-host'

const { spawn } = vi.hoisted(() => ({ spawn: vi.fn() }))
vi.mock('node:child_process', () => ({ spawn }))
vi.mock('@playwright/test', () => ({
    expect: { poll: () => ({ toBe: async () => {} }) },
}))

// Only the Node child-process boundary is simulated here. These tests cover
// lossless failure reporting by the browser harness, not real Host shutdown.
class Child extends EventEmitter {
    pid = 321
    exitCode: number | null = null
    signalCode: NodeJS.Signals | null = null
    stdout = new EventEmitter()
    stderr = new EventEmitter()
    kills: NodeJS.Signals[] = []

    kill(signal: NodeJS.Signals) {
        this.kills.push(signal)
        return true
    }

    close(code: number | null, signal: NodeJS.Signals | null) {
        this.exitCode = code
        this.signalCode = signal
        this.emit('exit', code, signal)
        this.emit('close', code, signal)
    }
}

afterEach(() => vi.clearAllMocks())

async function ready() {
    const child = new Child()
    spawn.mockImplementationOnce(() => {
        queueMicrotask(() =>
            child.stdout.emit(
                'data',
                Buffer.from('Now listening on: http://127.0.0.1:12345\n'),
            ),
        )
        return child
    })
    const host = await startOwnedHost('/test-only-directory')
    return { child, host }
}

it('waits for close, returns zero and does not signal an already closed child', async () => {
    const { child, host } = await ready()
    const stopped = host.stop()
    expect(child.kills).toEqual(['SIGTERM'])
    child.close(0, null)
    expect(await stopped).toBe(0)
    expect(await host.stop()).toBe(0)
    expect(child.kills).toEqual(['SIGTERM'])
})

it.each(['SIGTERM', 'SIGABRT'] satisfies NodeJS.Signals[])(
    'reports pid, null code, actual %s and bounded console instead of silently returning null',
    async (signal) => {
        const { child, host } = await ready()
        child.stderr.emit('data', Buffer.from('diagnostic-tail'))
        const stopped = host.stop()
        child.close(null, signal)
        await expect(stopped).rejects.toThrow(
            `owned-host-stop-failed pid=321; code=null; signal=${signal}`,
        )
        await expect(host.stop()).rejects.toThrow('diagnostic-tail')
        expect(host.exitSignal).toBe(signal)
        expect(child.kills).toEqual(['SIGTERM'])
    },
)

it('does not accept a nonzero numeric exit as successful cleanup', async () => {
    const { child, host } = await ready()
    const stopped = host.stop()
    child.close(6, null)
    await expect(stopped).rejects.toThrow(
        'owned-host-stop-failed pid=321; code=6; signal=none',
    )
})
