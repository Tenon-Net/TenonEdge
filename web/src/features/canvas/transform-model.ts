import type { components } from '../../api/schema'
import { routeObject } from './codec'
import {
    readRouteJson,
    RouteNumber,
    writeRouteJson,
    type RouteJson,
} from './json'
import type { ParameterSchema, ParameterValues } from '../parameters/model'

export type TransformModule = components['schemas']['TransformModuleDefinition']
export const moduleKey = (kind: string, version: number) => `${kind}@${version}`
export const builtInMap = (kind: string, version = 1) =>
    kind === 'transform.map' && version === 1

export function transformDefaults(schema: ParameterSchema): string {
    return writeRouteJson(
        Object.fromEntries(
            (schema.parameters ?? []).flatMap((field) =>
                field.defaultJson == null
                    ? []
                    : [
                          [
                              field.name,
                              readRouteJson(field.defaultJson, 128 * 1024),
                          ],
                      ],
            ),
        ),
    )
}

export function transformValues(text: string): ParameterValues {
    return Object.fromEntries(
        Object.entries(routeObject(readRouteJson(text))).flatMap(
            ([name, value]) => {
                if (value instanceof RouteNumber) return [[name, value.text]]
                if (typeof value === 'string' || typeof value === 'boolean')
                    return [[name, value]]
                if (value === null) return []
                throw new Error('transform-scalar')
            },
        ),
    )
}

/** Preserve untouched scalar types, nulls and numeric tokens. Invalid numeric text remains
 * a string in an incomplete draft; only explicit edits can replace the original value. */
export function transformConfig(
    original: string,
    schema: ParameterSchema,
    edits: ParameterValues,
): string {
    const values: Record<string, RouteJson> = {
        ...routeObject(readRouteJson(original)),
    }
    for (const [name, value] of Object.entries(edits)) {
        const field = schema.parameters?.find((field) => field.name === name)
        if (
            !field ||
            field.type === 'credential' ||
            field.type === 'connectionReference'
        )
            throw new Error('transform-field')
        if (value === undefined || (value === '' && field.type !== 'text')) {
            delete values[name]
            continue
        }
        if (field.type === 'number' && typeof value === 'string') {
            let number: RouteJson
            try {
                number = readRouteJson(value, 128)
            } catch {
                number = value
            }
            values[name] = number instanceof RouteNumber ? number : value
        } else values[name] = value
    }
    return writeRouteJson(values)
}
