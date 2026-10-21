import assert from 'node:assert/strict'

// Test harness diagnostics only: close confirms stdio drained as well as exit.
export function observeProcessExit(child) {
    return new Promise((resolve) => {
        child.once('close', (code, signal) => {
            resolve({ pid: child.pid ?? null, code, signal })
        })
    })
}

export function assertCleanHostExit(exit) {
    assert(
        exit.code === 0 && exit.signal === null,
        `Owned Host exit was not clean: pid=${exit.pid}; code=${exit.code}; signal=${exit.signal ?? 'none'}`,
    )
}
