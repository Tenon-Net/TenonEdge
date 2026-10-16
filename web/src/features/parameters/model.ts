import type { components } from '../../api/schema'
import { readRouteJson, RouteNumber } from '../canvas/json'

export type ParameterSchema = components['schemas']['ParameterSchema']
export type Parameter = components['schemas']['ParameterDefinition']
// Numbers stay editable JSON text, not browser floating-point numbers.
export type ParameterValues = Record<string, string | boolean | undefined>
export type ParameterConnection = { id: string; kind: string; name: string }
export type ParameterIssue = { name: string; code: string }

export function parameterDefaults(schema: ParameterSchema): ParameterValues {
    return Object.fromEntries(
        (schema.parameters ?? []).flatMap((field) => {
            if (field.type === 'credential' || field.defaultJson == null)
                return []
            const value = readRouteJson(field.defaultJson, 128 * 1024)
            return value instanceof RouteNumber
                ? [[field.name, value.text]]
                : typeof value === 'boolean' || typeof value === 'string'
                  ? [[field.name, value]]
                  : []
        }),
    )
}

function decimal(raw: string) {
    if (
        raw.length > 128 ||
        !/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(raw)
    )
        return null
    const [mantissa, exp = '0'] = raw.replace(/^-/, '').split(/[eE]/)
    // Only the bounded exponent uses Number, never the mantissa.
    const exponent = Number(exp)
    if (!Number.isInteger(exponent) || Math.abs(exponent) > 1_000_000)
        return null
    const dot = mantissa.indexOf('.')
    const digits = mantissa.replace('.', '').replace(/^0+/, '')
    if (!digits) return { digits: '0', power: 0, negative: false }
    const trimmed = digits.replace(/0+$/, '')
    return {
        digits: trimmed,
        power:
            exponent -
            (dot < 0 ? 0 : mantissa.length - dot - 1) +
            digits.length -
            trimmed.length,
        negative: raw.startsWith('-'),
    }
}
// Existing typed Int32 DTOs accept decimal integer text, not exponent/fraction syntax.
// Canonicalize only integral values with a bounded representation; never use Number.
export function wholeNumberText(raw: string): string {
    const number = decimal(raw)
    if (
        !number ||
        number.power < 0 ||
        number.digits.length + number.power > 128
    )
        return raw
    return `${number.negative ? '-' : ''}${number.digits}${'0'.repeat(number.power)}`
}

function compare(a: NonNullable<ReturnType<typeof decimal>>, raw: string) {
    const b = decimal(raw.trim())
    if (!b) throw new Error('parameter-schema-number')
    if (a.negative !== b.negative) return a.negative ? -1 : 1
    let result =
        a.digits === '0'
            ? b.digits === '0'
                ? 0
                : -1
            : b.digits === '0'
              ? 1
              : Math.sign(a.digits.length + a.power - b.digits.length - b.power)
    if (!result && a.digits !== '0') {
        const size = Math.max(a.digits.length, b.digits.length)
        const left = a.digits.padEnd(size, '0'),
            right = b.digits.padEnd(size, '0')
        result = left === right ? 0 : left < right ? -1 : 1
    }
    return a.negative ? -result : result
}

export function parameterIssues(
    schema: ParameterSchema,
    values: ParameterValues,
    connections: readonly ParameterConnection[] = [],
    configuredCredentials: ReadonlySet<string> = new Set(),
): ParameterIssue[] {
    const fields = schema.parameters ?? []
    const issues: ParameterIssue[] = []
    for (const name of Object.keys(values)) {
        if (!fields.some((field) => field.name === name))
            issues.push({ name: '', code: 'unknown' })
    }
    for (const field of fields) {
        const value = values[field.name]
        let code: string | undefined
        if (field.type === 'credential') {
            if (Object.hasOwn(values, field.name)) code = 'credentialSeparate'
            else if (field.required && !configuredCredentials.has(field.name))
                code = 'required'
        } else if (value === undefined || value === '') {
            if (field.required) code = 'required'
        } else
            switch (field.type) {
                case 'text':
                    if (typeof value !== 'string') code = 'type'
                    else if (value.length > Number(field.maximumLength))
                        code = 'length'
                    else if (field.required && !value.trim()) code = 'required'
                    break
                case 'number': {
                    const number =
                        typeof value === 'string' ? decimal(value) : null
                    if (!number) code = 'number'
                    else if (field.wholeNumber && number.power < 0)
                        code = 'integer'
                    else if (
                        (field.minimumJson != null &&
                            compare(number, field.minimumJson) < 0) ||
                        (field.maximumJson != null &&
                            compare(number, field.maximumJson) > 0)
                    )
                        code = 'range'
                    break
                }
                case 'switch':
                    if (typeof value !== 'boolean') code = 'type'
                    break
                case 'choice':
                    if (
                        !field.options?.some((option) => option.value === value)
                    )
                        code = 'choice'
                    break
                case 'connectionReference':
                    if (
                        typeof value !== 'string' ||
                        !/^[1-9]\d{0,15}$/.test(value) ||
                        BigInt(value) > 9007199254740991n ||
                        !connections.some(
                            (connection) =>
                                connection.id === value &&
                                field.connectionKinds?.includes(
                                    connection.kind,
                                ),
                        )
                    )
                        code = 'connection'
                    break
            }
        if (code) issues.push({ name: field.name, code })
    }
    return issues
}
