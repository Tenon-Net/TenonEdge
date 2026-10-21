import { EventEmitter } from 'node:events'
import { expect, it } from 'vitest'
import {
    assertCleanHostExit,
    observeProcessExit,
} from '../../scripts/process-exit.mjs'

// Simulates the Node boundary, not actual product Host shutdown.
it('waits for close and preserves the numeric success code and null signal', async () => {
    const child = Object.assign(new EventEmitter(), { pid: 321 })
    const pending = observeProcessExit(child)
    let closed = false
    void pending.then(() => {
        closed = true
    })
    child.emit('exit', 0, null)
    await Promise.resolve()
    expect(closed).toBe(false)
    child.emit('close', 0, null)
    const exit = await pending
    expect(exit).toEqual({ pid: 321, code: 0, signal: null })
    expect(() => assertCleanHostExit(exit)).not.toThrow()
})

it.each([
    { code: 6, signal: null },
    { code: null, signal: 'SIGTERM' },
    { code: null, signal: 'SIGABRT' },
    { code: 0, signal: 'SIGTERM' },
])(
    'does not accept code=$code, signal=$signal as Host success',
    async (status) => {
        const child = Object.assign(new EventEmitter(), { pid: 321 })
        const pending = observeProcessExit(child)
        child.emit('close', status.code, status.signal)
        const exit = await pending
        expect(exit).toEqual({ pid: 321, ...status })
        expect(() => assertCleanHostExit(exit)).toThrow(
            `pid=321; code=${status.code}; signal=${status.signal ?? 'none'}`,
        )
    },
)
