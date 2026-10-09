import { routeObject } from '../canvas/codec'
import {
    readRouteJson,
    writeRouteJson,
    RouteNumber,
    type RouteJson,
} from '../canvas/json'

export type PayloadFieldForm = {
    target: string
    source: 'field' | 'placeholder' | 'constant'
    value: string
    required: boolean
}
export type OutputForm = {
    topic: string
    qos: string
    path: string
    headers: { name: string; value: string }[]
    successPath: string
    successValue: string
    mode: 'envelope' | 'values' | 'custom'
    fields: PayloadFieldForm[]
}
function text(value: RouteJson | undefined, fallback = ''): string {
    if (value == null) return fallback
    if (typeof value !== 'string') throw new Error('output')
    return value
}
export function scalar(text: string): RouteJson {
    const value = readRouteJson(text)
    if (value instanceof RouteNumber) {
        if (!Number.isFinite(Number(value.text))) throw new Error('scalar')
    } else if (value !== null && typeof value === 'object')
        throw new Error('scalar')
    return value
}
export function outputForm(configText: string): OutputForm {
    const config = routeObject(readRouteJson(configText))
    const payload = config.payload == null ? {} : routeObject(config.payload)
    if (Object.keys(payload).some((key) => !['mode', 'fields'].includes(key)))
        throw new Error('output')
    const mode = payload.mode ?? 'envelope'
    if (!['envelope', 'values', 'custom'].includes(String(mode)))
        throw new Error('output')
    if (
        payload.fields != null &&
        (!Array.isArray(payload.fields) || mode !== 'custom')
    )
        throw new Error('output')
    const fields = (payload.fields as RouteJson[] | undefined) ?? []
    if (fields.length > 1000) throw new Error('output')
    const qos =
        config.qos == null
            ? '1'
            : config.qos instanceof RouteNumber
              ? config.qos.text
              : ''
    if (!['0', '1'].includes(qos)) throw new Error('output')
    return {
        topic: text(config.topic, 'tenonedge/{deviceKey}/data'),
        qos,
        path: text(config.path),
        headers: Object.entries(
            config.headers == null ? {} : routeObject(config.headers),
        ).map(([name, value]) => ({ name, value: text(value) })),
        successPath: text(config.successPath),
        successValue: Object.hasOwn(config, 'successValue')
            ? writeRouteJson(config.successValue)
            : 'true',
        mode: mode as OutputForm['mode'],
        fields: fields.map((value) => {
            const field = routeObject(value)
            if (
                Object.keys(field).some(
                    (key) =>
                        ![
                            'target',
                            'sourceField',
                            'placeholder',
                            'constant',
                            'required',
                        ].includes(key),
                )
            )
                throw new Error('output')
            const hasConstant = Object.hasOwn(field, 'constant')
            if (
                Number(field.sourceField != null) +
                    Number(field.placeholder != null) +
                    Number(hasConstant) >
                1
            )
                throw new Error('output')
            if (field.required != null && typeof field.required !== 'boolean')
                throw new Error('output')
            return {
                target: text(field.target),
                source: hasConstant
                    ? 'constant'
                    : field.placeholder != null
                      ? 'placeholder'
                      : 'field',
                value: hasConstant
                    ? writeRouteJson(field.constant)
                    : text(field.placeholder ?? field.sourceField),
                required: field.required === true,
            }
        }),
    }
}
export function outputConfig(
    configText: string,
    kind: 'output.http' | 'output.mqtt',
    form: OutputForm,
): string {
    const config = routeObject(readRouteJson(configText))
    if (kind === 'output.mqtt') {
        if (!['0', '1'].includes(form.qos)) throw new Error('output')
        config.topic = form.topic
        config.qos = new RouteNumber(form.qos)
    } else {
        config.path = form.path
        if (form.headers.length > 100) throw new Error('limit')
        const names = new Set<string>()
        for (const header of form.headers) {
            if (!header.name || names.has(header.name.toLowerCase()))
                throw new Error('headers')
            names.add(header.name.toLowerCase())
        }
        config.headers = Object.fromEntries(
            form.headers.map((header) => [header.name, header.value]),
        )
        config.successPath = form.successPath || null
        if (form.successPath) config.successValue = scalar(form.successValue)
        else delete config.successValue
    }
    if (form.fields.length > 1000) throw new Error('limit')
    config.payload =
        form.mode === 'custom'
            ? {
                  mode: form.mode,
                  fields: form.fields.map((field) => ({
                      target: field.target,
                      required: field.required,
                      ...(field.source === 'constant'
                          ? { constant: scalar(field.value) }
                          : field.source === 'placeholder'
                            ? { placeholder: field.value }
                            : { sourceField: field.value }),
                  })),
              }
            : { mode: form.mode }
    return writeRouteJson(config)
}
