import { RouteNumber, type RouteJson } from '../canvas/json'

/** Generated integer metadata permits strings, so large revision numbers need not be rounded. */
export function metadata(value: RouteJson): unknown {
    if (value instanceof RouteNumber) return value.text
    if (Array.isArray(value)) return value.map(metadata)
    if (value !== null && typeof value === 'object')
        return Object.fromEntries(
            Object.entries(value).map(([key, item]) => [key, metadata(item)]),
        )
    return value
}
export function revisionId(value: number | string): string {
    if (typeof value === 'number' && !Number.isSafeInteger(value))
        throw new Error('configuration-revision')
    const text = String(value)
    if (
        !/^[1-9]\d*$/.test(text) ||
        text.length > 19 ||
        (text.length === 19 && text > '9223372036854775807')
    )
        throw new Error('configuration-revision')
    return text
}
export const maximumResponseBytes = 128 * 1024 * 1024
