import type { components } from '../../api/schema'
import { routeObject } from './codec'
import { isOutputModuleKind } from './model'
import { readRouteJson, RouteNumber, writeRouteJson } from './json'
import {
    transformConfig,
    transformDefaults,
    transformValues,
} from './transform-model'
import type { ParameterSchema, ParameterValues } from '../parameters/model'

export type OutputModule = components['schemas']['OutputModuleDefinition']

function reference(value: unknown): string {
    if (
        !(value instanceof RouteNumber) ||
        !/^[1-9]\d{0,15}$/.test(value.text) ||
        BigInt(value.text) > 9007199254740991n
    )
        throw new Error('output-module-reference')
    return value.text
}

export function outputModuleValues(text: string) {
    const config = routeObject(readRouteJson(text))
    if (
        Object.keys(config).some(
            (name) =>
                !['connectionId', 'parameters', 'connections'].includes(name),
        )
    )
        throw new Error('output-module-config')
    const parameters = routeObject(config.parameters)
    const connections =
        config.connections == null ? {} : routeObject(config.connections)
    if (
        Object.keys(connections).some((name) => Object.hasOwn(parameters, name))
    )
        throw new Error('output-module-reference')
    return {
        target:
            config.connectionId == null ? '' : reference(config.connectionId),
        parameters,
        connections: Object.fromEntries(
            Object.entries(connections).map(([name, value]) => [
                name,
                reference(value),
            ]),
        ),
        values: transformValues(writeRouteJson(parameters)),
    }
}

export function outputModuleDefaults(schema: ParameterSchema): string {
    const scalars = {
        ...schema,
        parameters: schema.parameters?.filter(
            (field) => field.type !== 'connectionReference',
        ),
    }
    return writeRouteJson({
        connectionId: null,
        parameters: readRouteJson(transformDefaults(scalars)),
        connections: {},
    })
}

/** Explicit resource references are never inferred from scalar values. Untouched number
 * lexemes, nulls and invalid-but-editable draft types are preserved verbatim. */
export function outputModuleConfig(
    original: string,
    schema: ParameterSchema,
    edits: ParameterValues,
    target: string,
): string {
    const form = outputModuleValues(original)
    const connections = { ...form.connections }
    const scalars: ParameterValues = {}
    for (const [name, value] of Object.entries(edits)) {
        const field = schema.parameters?.find((field) => field.name === name)
        if (!field || field.type === 'credential')
            throw new Error('output-module-field')
        if (field.type === 'connectionReference') {
            if (Object.hasOwn(form.parameters, name))
                throw new Error('output-module-reference')
            if (value === '' || value === undefined) delete connections[name]
            else {
                if (typeof value !== 'string')
                    throw new Error('output-module-reference')
                connections[name] = reference(new RouteNumber(value))
            }
        } else scalars[name] = value
    }
    if (
        schema.parameters?.some(
            (field) =>
                field.type === 'connectionReference' &&
                Object.hasOwn(form.parameters, field.name),
        ) ||
        Object.keys(connections).some(
            (name) =>
                !schema.parameters?.some(
                    (field) =>
                        field.name === name &&
                        field.type === 'connectionReference',
                ),
        )
    )
        throw new Error('output-module-reference')
    return writeRouteJson({
        connectionId:
            target === ''
                ? null
                : new RouteNumber(reference(new RouteNumber(target))),
        parameters: readRouteJson(
            transformConfig(writeRouteJson(form.parameters), schema, scalars),
        ),
        connections: Object.fromEntries(
            Object.entries(connections).map(([name, value]) => [
                name,
                new RouteNumber(value),
            ]),
        ),
    })
}

export function validateOutputModules(data: OutputModule[]): OutputModule[] {
    if (!Array.isArray(data)) throw new Error('missing-output-modules')
    const seen = new Set<string>()
    for (const module of data) {
        const version = Number(module.version)
        const key = `${module.kind}@${version}`
        if (
            !isOutputModuleKind(module.kind) ||
            !Number.isInteger(version) ||
            version < 1 ||
            version > 2147483647 ||
            !module.displayName ||
            seen.has(key) ||
            module.parameters?.scope !== 'output' ||
            !Array.isArray(module.parameters.parameters) ||
            module.parameters.parameters.some(
                (field) =>
                    ![
                        'text',
                        'number',
                        'switch',
                        'choice',
                        'connectionReference',
                    ].includes(field.type),
            )
        )
            throw new Error('invalid-output-module')
        const references = new Set(
            module.parameters.parameters
                .filter((field) => field.type === 'connectionReference')
                .flatMap((field) => field.connectionKinds ?? []),
        )
        if (
            !Array.isArray(module.targetConnectionKinds) ||
            new Set(module.targetConnectionKinds).size !==
                module.targetConnectionKinds.length ||
            module.targetConnectionKinds.some(
                (kind) =>
                    typeof kind !== 'string' || !kind || !references.has(kind),
            )
        )
            throw new Error('invalid-output-module-targets')
        seen.add(key)
    }
    return data
}
