import type { RouteForm } from './api'

/** Advance only the concurrency token, after rereading the saved route. Neither
 * refreshes nor inline edits may replace the local graph, undo stack or sample. */
export function rebaseReferences(
    form: RouteForm,
    baseline: RouteForm,
    latest: RouteForm | string,
): { form: RouteForm; baseline: RouteForm } | null {
    if (form.id !== baseline.id) return null
    let version: string
    if (form.id === undefined) {
        if (typeof latest !== 'string') return null
        version = latest
    } else {
        if (typeof latest === 'string') return null
        const { version: _, ...before } = baseline
        const { version: next, ...after } = latest
        if (JSON.stringify(before) !== JSON.stringify(after)) return null
        version = next
    }
    return {
        form: { ...form, version },
        baseline: { ...baseline, version },
    }
}
