import type { JsonField } from './model'

export const maxSampleBytes = 1024 * 1024
export type SampleField = {
    path: string
    type: JsonField['type']
    preview: string
}
// Never turn a JSON number into a JS number for display or persistence.
class RawNumber {
    constructor(readonly text: string) {}
}

// JSON.parse discards duplicate object members, but runtime JSON Pointer rejects
// ambiguous members. Scan only strings/containers after native syntax validation.
function rejectAmbiguousMembers(text: string) {
    const stack: (Set<string> | null)[] = []
    const tokens = /"(?:[^"\\]|\\[\s\S])*"|[{}[\]]/g
    for (const match of text.matchAll(tokens)) {
        const token = match[0]
        if (token === '{' || token === '[') {
            stack.push(token === '{' ? new Set() : null)
            if (stack.length > 64) throw new Error('limit')
        } else if (token === '}' || token === ']') stack.pop()
        else {
            let next = match.index! + token.length
            while (/\s/.test(text[next] ?? '') && next < text.length) next++
            if (text[next] !== ':') continue
            const keys = stack.at(-1)
            const key: string = JSON.parse(token)
            if (keys?.has(key)) throw new Error('duplicate')
            keys?.add(key)
        }
    }
}

/** Page-local inference only. Returns no input values in the generated descriptors. */
export function inferSample(text: string): SampleField[] {
    if (
        text.length > maxSampleBytes ||
        new TextEncoder().encode(text).length > maxSampleBytes
    )
        throw new Error('limit')
    let root: unknown
    try {
        root = JSON.parse(
            text,
            (_key: string, value: unknown, context?: { source?: string }) => {
                if (typeof value !== 'number') return value
                // Older engines must not silently round an int64/decimal sample.
                if (!context?.source) throw new Error('browser')
                return new RawNumber(context.source)
            },
        )
    } catch (error) {
        if (error instanceof Error && error.message === 'browser') throw error
        throw new Error('json')
    }
    rejectAmbiguousMembers(text)
    const fields: SampleField[] = []
    function visit(value: unknown, path: string, depth: number) {
        if (depth > 64 || path.length > 4096) throw new Error('limit')
        if (
            value instanceof RawNumber ||
            value === null ||
            typeof value !== 'object'
        ) {
            if (fields.length >= 1000) throw new Error('limit')
            fields.push({
                path,
                type:
                    value instanceof RawNumber
                        ? 'number'
                        : typeof value === 'boolean'
                          ? 'boolean'
                          : typeof value === 'string'
                            ? 'string'
                            : null,
                preview:
                    value instanceof RawNumber
                        ? value.text
                        : JSON.stringify(value),
            })
            return
        }
        for (const [key, child] of Object.entries(value))
            visit(
                child,
                path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1'),
                depth + 1,
            )
    }
    visit(root, '', 0)
    return fields
}

export function selectedFields(
    candidates: SampleField[],
    paths: ReadonlySet<string>,
    existing: JsonField[],
): JsonField[] {
    const selected = candidates.filter(
        (field) => paths.has(field.path) && field.type !== null,
    )
    if (existing.length + selected.length > 1000) throw new Error('limit')
    const keys = new Set(existing.map((field) => field.key))
    return selected.map((field) => {
        const leaf =
            field.path
                .split('/')
                .at(-1)
                ?.replaceAll('~1', '/')
                .replaceAll('~0', '~') ?? ''
        const base = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(leaf)
            ? leaf
            : 'field'
        let key = base
        for (let suffix = 2; keys.has(key); suffix++) {
            const ending = '_' + suffix
            key = base.slice(0, 64 - ending.length) + ending
        }
        keys.add(key)
        return { key, path: field.path, type: field.type, name: '', unit: '' }
    })
}
