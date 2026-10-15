import { readRouteJson, RouteNumber } from '../canvas/json'
import type { JsonField } from './model'

export const maxSampleBytes = 1024 * 1024
export type SampleField = {
    path: string
    type: JsonField['type']
    preview: string
}
/** Page-local inference only. Returns no input values in the generated descriptors. */
export function inferSample(text: string): SampleField[] {
    let root: unknown
    try {
        // Reuse the bounded lexical reader: JSON.parse's numeric result is never
        // used, and Chrome/Edge 111 do not need the newer reviver context.source.
        root = readRouteJson(text, maxSampleBytes, 64)
    } catch (error) {
        if (
            error instanceof Error &&
            ['limit', 'duplicate'].includes(error.message)
        )
            throw error
        throw new Error('json')
    }
    const fields: SampleField[] = []
    function visit(value: unknown, path: string, depth: number) {
        if (
            depth > 64 ||
            path.length > 4096 ||
            (depth === 64 &&
                value !== null &&
                typeof value === 'object' &&
                !(value instanceof RouteNumber))
        )
            throw new Error('limit')
        if (
            value instanceof RouteNumber ||
            value === null ||
            typeof value !== 'object'
        ) {
            if (fields.length >= 1000) throw new Error('limit')
            fields.push({
                path,
                type:
                    value instanceof RouteNumber
                        ? 'number'
                        : typeof value === 'boolean'
                          ? 'boolean'
                          : typeof value === 'string'
                            ? 'string'
                            : null,
                preview:
                    value instanceof RouteNumber
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
