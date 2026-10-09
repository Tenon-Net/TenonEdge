/** Route configuration numbers stay lexical: never round through a JS number. */
export class RouteNumber {
    constructor(readonly text: string) {
        if (!/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(text))
            throw new Error('json')
    }
}
export type RouteJson =
    | null
    | boolean
    | string
    | number
    | RouteNumber
    | RouteJson[]
    | { [key: string]: RouteJson }
export const maxGraphBytes = 1024 * 1024

/** Bounded token reader works without JSON.rawJSON or reviver source support. */
export function readRouteJson(text: string): RouteJson {
    if (
        text.length > maxGraphBytes ||
        new TextEncoder().encode(text).length > maxGraphBytes
    )
        throw new Error('limit')
    // Native syntax validation only. Its rounded result is deliberately discarded.
    JSON.parse(text)
    const tokens =
        text.match(
            /"(?:[^"\\]|\\[\s\S])*"|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null|[{}[\],:]/g,
        ) ?? []
    let cursor = 0
    function read(depth: number): RouteJson {
        if (depth > 16) throw new Error('limit')
        const token = tokens[cursor++]
        if (token === '{') {
            const entries: [string, RouteJson][] = []
            const keys = new Set<string>()
            while (tokens[cursor] !== '}') {
                const key: string = JSON.parse(tokens[cursor++])
                if (keys.has(key)) throw new Error('duplicate')
                keys.add(key)
                cursor++ // colon; syntax was validated above
                entries.push([key, read(depth + 1)])
                if (tokens[cursor] !== ',') break
                cursor++
            }
            cursor++
            return Object.fromEntries(entries)
        }
        if (token === '[') {
            const values: RouteJson[] = []
            while (tokens[cursor] !== ']') {
                values.push(read(depth + 1))
                if (tokens[cursor] !== ',') break
                cursor++
            }
            cursor++
            return values
        }
        if (token.startsWith('"') || ['true', 'false', 'null'].includes(token))
            return JSON.parse(token)
        return new RouteNumber(token)
    }
    return read(0)
}

export function writeRouteJson(value: RouteJson): string {
    const parts: string[] = []
    let bytes = 0
    const encoder = new TextEncoder()
    function append(text: string) {
        bytes += encoder.encode(text).length
        if (bytes > maxGraphBytes) throw new Error('limit')
        parts.push(text)
    }
    function write(item: RouteJson, depth: number): void {
        if (depth > 16) throw new Error('limit')
        if (item instanceof RouteNumber) {
            append(new RouteNumber(item.text).text)
            return
        }
        if (item === null || typeof item !== 'object') {
            if (typeof item === 'number' && !Number.isFinite(item))
                throw new Error('json')
            append(JSON.stringify(item))
            return
        }
        if (Array.isArray(item)) {
            append('[')
            item.forEach((child, index) => {
                if (index) append(',')
                write(child, depth + 1)
            })
            append(']')
            return
        }
        append('{')
        Object.entries(item).forEach(([key, child], index) => {
            if (index) append(',')
            append(JSON.stringify(key))
            append(':')
            write(child, depth + 1)
        })
        append('}')
    }
    write(value, 0)
    return parts.join('')
}
