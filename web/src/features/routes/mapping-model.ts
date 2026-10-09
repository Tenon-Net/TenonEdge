import { routeObject } from '../canvas/codec'
import {
    readRouteJson,
    writeRouteJson,
    RouteNumber,
    type RouteJson,
} from '../canvas/json'
import { scalar } from './output-model'

export const fieldTypes = [
    '',
    'integer',
    'number',
    'boolean',
    'string',
] as const
export type MappingRow = {
    target: string
    type: (typeof fieldTypes)[number]
    source: 'field' | 'path' | 'constant'
    value: string
    unit: string
    required: boolean
    scale: string
    offset: string
    decimalPlaces: string
}
export const newMappingRow = (): MappingRow => ({
    target: '',
    type: '',
    source: 'field',
    value: '',
    unit: '',
    required: false,
    scale: '',
    offset: '',
    decimalPlaces: '',
})
function text(value: RouteJson | undefined): string {
    if (value == null) return ''
    if (typeof value !== 'string') throw new Error('mapping')
    return value
}
function numberText(value: RouteJson | undefined): string {
    if (value == null) return ''
    if (!(value instanceof RouteNumber)) throw new Error('mapping')
    return value.text
}
export function mappingRows(configText: string): MappingRow[] {
    const config = routeObject(readRouteJson(configText))
    if (
        Object.keys(config).some((key) => key !== 'rules') ||
        !Array.isArray(config.rules) ||
        config.rules.length > 1000
    )
        throw new Error('mapping')
    return config.rules.map((value) => {
        const rule = routeObject(value)
        if (
            Object.keys(rule).some(
                (key) =>
                    ![
                        'target',
                        'type',
                        'sourceField',
                        'sourcePath',
                        'constant',
                        'unit',
                        'required',
                        'scale',
                        'offset',
                        'decimalPlaces',
                    ].includes(key),
            )
        )
            throw new Error('mapping')
        const type = fieldTypes.find((type) => type === (rule.type ?? ''))
        if (
            type === undefined ||
            (rule.required != null && typeof rule.required !== 'boolean')
        )
            throw new Error('mapping')
        const hasConstant = Object.hasOwn(rule, 'constant')
        if (
            Number(rule.sourceField != null) +
                Number(rule.sourcePath != null) +
                Number(hasConstant) >
            1
        )
            throw new Error('mapping')
        return {
            target: text(rule.target),
            type,
            source: hasConstant
                ? 'constant'
                : rule.sourcePath != null
                  ? 'path'
                  : 'field',
            value: hasConstant
                ? writeRouteJson(rule.constant)
                : text(rule.sourcePath ?? rule.sourceField),
            unit: text(rule.unit),
            required: rule.required === true,
            scale: numberText(rule.scale),
            offset: numberText(rule.offset),
            decimalPlaces: numberText(rule.decimalPlaces),
        }
    })
}
export function mappingConfig(rows: MappingRow[]): string {
    if (rows.length > 1000) throw new Error('limit')
    const numeric = (value: string) =>
        value === '' ? null : new RouteNumber(value)
    return writeRouteJson({
        rules: rows.map((row) => {
            const decimalPlaces = numeric(row.decimalPlaces)
            if (decimalPlaces && !/^(?:0|[1-9]\d*)$/.test(decimalPlaces.text))
                throw new Error('mapping')
            return {
                target: row.target,
                type: row.type || null,
                unit: row.unit || null,
                required: row.required,
                scale: numeric(row.scale),
                offset: numeric(row.offset),
                decimalPlaces,
                ...(row.source === 'constant'
                    ? { constant: scalar(row.value) }
                    : row.source === 'path'
                      ? { sourcePath: row.value }
                      : { sourceField: row.value }),
            }
        }),
    })
}
