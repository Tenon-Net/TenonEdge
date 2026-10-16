import type { ConnectionForm } from './model'
import {
    parameterDefaults,
    type ParameterSchema,
    type ParameterValues,
} from '../parameters/model'

const fields = {
    host: 'host',
    port: 'port',
    format: 'format',
    connectTimeoutMs: 'connectTimeout',
    readTimeoutMs: 'readTimeout',
    requestIntervalMs: 'interval',
    rtuSilenceMs: 'rtuSilence',
    tls: 'tls',
    clientId: 'clientId',
    timeoutMs: 'timeout',
    pingIntervalMs: 'ping',
} as const
// Built-in DTO bridge only; this does not register new protocol kinds or factories.
export function connectionParameterValues(
    form: ConnectionForm,
    schema: ParameterSchema,
): ParameterValues {
    return Object.fromEntries(
        (schema.parameters ?? []).map((field) => {
            const key = fields[field.name as keyof typeof fields]
            if (!Object.hasOwn(fields, field.name))
                throw new Error('connection-parameter-schema')
            return [field.name, form[key]]
        }),
    )
}
export function changeConnectionParameter(
    form: ConnectionForm,
    name: string,
    value: string | boolean,
): ConnectionForm {
    const key = fields[name as keyof typeof fields]
    if (
        !Object.hasOwn(fields, name) ||
        (key === 'tls'
            ? typeof value !== 'boolean'
            : typeof value !== 'string') ||
        (key === 'format' && value !== 'tcp' && value !== 'rtuOverTcp')
    )
        throw new Error('connection-parameter-value')
    return {
        ...form,
        [key]: value,
        ...(key === 'tls' && ['1883', '8883'].includes(form.port)
            ? { port: value ? '8883' : '1883' }
            : {}),
    }
}
export function initializeConnectionParameters(
    form: ConnectionForm,
    schema: ParameterSchema,
): ConnectionForm {
    return Object.entries(parameterDefaults(schema)).reduce(
        (next, [name, value]) =>
            value === undefined
                ? next
                : changeConnectionParameter(next, name, value),
        form,
    )
}
