import { routeObject } from '../canvas/codec'
import { readRouteJson, writeRouteJson, RouteNumber } from '../canvas/json'
import type { NodeKind } from '../canvas/model'

export function selectedReferences(kind: NodeKind, text: string): number[] {
    const config = routeObject(readRouteJson(text))
    const values = kind.startsWith('input.')
        ? config.sourceIds
        : config.connectionId === null
          ? []
          : [config.connectionId]
    if (!Array.isArray(values)) throw new Error('references')
    return values.map((value) => {
        if (!(value instanceof RouteNumber) || !/^[1-9]\d*$/.test(value.text))
            throw new Error('references')
        const id = Number(value.text)
        if (!Number.isSafeInteger(id)) throw new Error('references')
        return id
    })
}
/** Change only references; payload/mapping constants never pass through JS numbers. */
export function withReferences(
    kind: NodeKind,
    text: string,
    ids: number[],
): string {
    const config = routeObject(readRouteJson(text))
    if (
        ids.some((id) => !Number.isSafeInteger(id) || id <= 0) ||
        new Set(ids).size !== ids.length
    )
        throw new Error('references')
    if (kind.startsWith('input.')) {
        if (ids.length > 500) throw new Error('limit')
        config.sourceIds = ids
    } else if (kind === 'output.http' || kind === 'output.mqtt') {
        if (ids.length > 1) throw new Error('references')
        config.connectionId = ids[0] ?? null
    } else throw new Error('references')
    return writeRouteJson(config)
}
