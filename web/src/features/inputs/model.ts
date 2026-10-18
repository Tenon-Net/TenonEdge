import type { components } from '../../api/schema'
import { routeObject } from '../canvas/codec'
import {
    readRouteJson,
    RouteNumber,
    writeRouteJson,
    type RouteJson,
} from '../canvas/json'
import {
    changeAddressMode,
    validateRows,
    type AddressMode,
    type PointRow,
} from '../points/model'

export type Input = components['schemas']['InputDraft']
export type InputRequest = components['schemas']['InputDraftRequest']
type InputList = components['schemas']['InputDraftList']
/** Metadata uses bounded safe IDs; module parameters retain the original numeric tokens. */
export function decodeInputList(text: string): InputList {
    const lexical = routeObject(readRouteJson(text, 16 * 1024 * 1024, 64))
    const result: InputList = JSON.parse(text)
    if (
        !Array.isArray(lexical.inputs) ||
        !Array.isArray(result.inputs) ||
        lexical.inputs.length !== result.inputs.length
    )
        throw new Error('input-list')
    for (let index = 0; index < result.inputs.length; index++) {
        const module = result.inputs[index].module
        if (module)
            module.parameters = routeObject(
                routeObject(lexical.inputs[index]).module,
            ).parameters
    }
    return result
}
function moduleText(value: unknown): string {
    if (value === null || typeof value !== 'object' || Array.isArray(value))
        throw new Error('module-parameters')
    const fields: Record<string, RouteJson> = {}
    for (const [key, item] of Object.entries(value)) {
        if (
            item === null ||
            typeof item === 'string' ||
            typeof item === 'boolean' ||
            item instanceof RouteNumber
        )
            fields[key] = item
        else throw new Error('module-parameters') // Never accept a previously rounded JSON number.
    }
    return writeRouteJson(fields)
}
export type JsonField = components['schemas']['JsonSourceField']
export type TimeFormat = NonNullable<
    components['schemas']['UpstreamTimeFormat']
>
export type InputForm = {
    kind: Input['kind']
    fields: JsonField[]
    topic: string
    qos: number
    retained: boolean
    timeEnabled: boolean
    timePath: string
    timeFormat: TimeFormat
    httpPath: string
    headers: { name: string; value: string }[]
    simulatorMode: 'meter' | 'custom'
    sampleJson: string
    missing: number
    invalid: number
    connectionError: number
    id?: number
    version: string
    key: string
    name: string
    enabled: boolean
    period: number
    moduleKind: string
    moduleVersion: number
    moduleConfigText: string
}
export function inputForm(
    version: string,
    input?: Input,
    kind: Input['kind'] = 'modbus',
    moduleKind?: string | null,
): InputForm {
    return {
        kind: input?.kind ?? kind,
        moduleKind: input?.module?.kind ?? moduleKind ?? '',
        moduleVersion: Number(input?.module?.version ?? 0),
        moduleConfigText: input?.module
            ? moduleText(input.module.parameters)
            : '{}',
        fields: (input?.mqtt?.fields ?? input?.http?.fields ?? []).map(
            (field) => ({ ...field }),
        ),
        topic: input?.mqtt?.topicFilter ?? '',
        qos: Number(input?.mqtt?.qos ?? 1),
        retained: input?.mqtt?.processRetained ?? false,
        timeEnabled:
            input?.mqtt?.upstreamTime != null ||
            input?.http?.upstreamTime != null,
        timePath:
            input?.mqtt?.upstreamTime?.path ??
            input?.http?.upstreamTime?.path ??
            '',
        timeFormat:
            input?.mqtt?.upstreamTime?.format ??
            input?.http?.upstreamTime?.format ??
            'rfc3339',
        httpPath: input?.http?.path ?? '',
        headers: Object.entries(input?.http?.headers ?? {}).map(
            ([name, value]) => ({ name, value }),
        ),
        simulatorMode:
            input?.simulator?.sampleJson == null ? 'meter' : 'custom',
        sampleJson: input?.simulator?.sampleJson ?? '{}',
        missing: Number(input?.simulator?.missingField ?? 0),
        invalid: Number(input?.simulator?.invalidValue ?? 0),
        connectionError: Number(input?.simulator?.connectionError ?? 0),
        id: input ? Number(input.id) : undefined,
        version,
        key: input?.key ?? '',
        name: input?.name ?? '',
        enabled: input?.enabled ?? true,
        period: Number(
            input?.modbus?.periodMs ??
                input?.http?.periodMs ??
                input?.simulator?.periodMs ??
                5000,
        ),
    }
}
export function inputRows(
    input?: Input,
    mode: AddressMode = 'offset',
): PointRow[] {
    const rows = (input?.modbus?.points ?? []).map((p, index) => ({
        id: String(index + 1),
        key: p.key ?? '',
        name: p.name ?? '',
        functionCode: String(p.functionCode),
        address: String(p.address ?? ''),
        type: p.type ?? '',
        bit: p.bit == null ? '' : String(p.bit),
        byteOrder: p.byteOrder ?? '',
        unit: p.unit ?? '',
        enabled: String(p.enabled ?? true),
    }))
    return changeAddressMode(rows, 'offset', mode)
}
export function inputRequest(
    form: InputForm,
    rows: PointRow[],
    mode: AddressMode,
): InputRequest {
    const common = {
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        enabled: form.enabled,
    }
    const upstreamTime = !form.timeEnabled
        ? null
        : { path: form.timePath, format: form.timeFormat }
    if (form.kind === 'module')
        return {
            ...common,
            module: {
                kind: form.moduleKind,
                version: form.moduleVersion,
                parameters: routeObject(
                    readRouteJson(form.moduleConfigText, 128 * 1024),
                ),
            },
        }
    if (form.kind === 'mqtt')
        return {
            ...common,
            mqtt: {
                topicFilter: form.topic,
                qos: form.qos,
                processRetained: form.retained,
                fields: form.fields,
                upstreamTime,
            },
        }
    if (form.kind === 'http') {
        const names = form.headers.map((h) => h.name.toLowerCase())
        if (new Set(names).size !== names.length)
            throw new Error('duplicate-headers')
        return {
            ...common,
            http: {
                path: form.httpPath,
                periodMs: form.period,
                fields: form.fields,
                upstreamTime,
                headers: Object.fromEntries(
                    form.headers.map((h) => [h.name, h.value]),
                ),
            },
        }
    }
    if (form.kind === 'simulator')
        return {
            ...common,
            simulator: {
                periodMs: form.period,
                template: form.simulatorMode === 'meter' ? 'meter' : null,
                sampleJson:
                    form.simulatorMode === 'custom' ? form.sampleJson : null,
                missingField: form.missing,
                invalidValue: form.invalid,
                connectionError: form.connectionError,
            },
        }
    const results = validateRows(rows, mode)
    if (results.some((r) => r.errors.length))
        throw new Error('invalid-point-table')
    return {
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        enabled: form.enabled,
        modbus: {
            periodMs: form.period,
            points: rows.map((r, index) => ({
                key: r.key,
                name: r.name || null,
                functionCode: results[index].address!.functionCode,
                address: results[index].address!.offset,
                type: r.type,
                bit: r.bit === '' ? null : Number(r.bit),
                byteOrder: r.byteOrder || null,
                unit: r.unit || null,
                enabled: r.enabled === 'true',
            })),
        },
    }
}

export function inputRequestJson(form: InputForm, body: InputRequest): string {
    if (form.kind !== 'module') return JSON.stringify(body)
    return writeRouteJson({
        expectedVersion: body.expectedVersion,
        key: body.key,
        name: body.name,
        enabled: form.enabled,
        module: {
            kind: form.moduleKind,
            version: form.moduleVersion,
            parameters: routeObject(
                readRouteJson(form.moduleConfigText, 128 * 1024),
            ),
        },
    })
}
