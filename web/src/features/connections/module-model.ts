import type { components } from '../../api/schema'
import { routeObject } from '../canvas/codec'
import { readRouteJson, writeRouteJson, RouteNumber } from '../canvas/json'
import { transformDefaults, transformValues } from '../canvas/transform-model'
import { parameterIssues, type ParameterConnection } from '../parameters/model'
import type { Connection } from './model'

export type ConnectionModule =
    components['schemas']['ConnectionModuleDefinition']
export type ModuleConnectionForm = {
    id?: Connection['id']
    version: string
    key: string
    name: string
    enabled: boolean
    kind: string
    moduleVersion: number
    parameters: string
    connections: Record<string, string>
}
export function moduleConnectionForm(
    version: string,
    connection?: Connection,
    parameters?: string,
): ModuleConnectionForm {
    if (connection && (!connection.module || connection.kind !== 'module'))
        throw new Error('module-connection')
    if (connection && parameters === undefined)
        throw new Error('module-parameters-missing')
    return {
        id: connection?.id,
        version,
        key: connection?.key ?? '',
        name: connection?.name ?? '',
        enabled: connection?.enabled ?? true,
        kind: connection?.module?.kind ?? '',
        moduleVersion: Number(connection?.module?.version ?? 0),
        parameters: parameters ?? '{}',
        connections: Object.fromEntries(
            Object.entries(connection?.module?.connections ?? {}).map(
                ([name, id]) => [name, String(id)],
            ),
        ),
    }
}
export function selectConnectionModule(
    form: ModuleConnectionForm,
    definition: ConnectionModule,
): ModuleConnectionForm {
    if (form.id && form.kind !== definition.kind)
        throw new Error('module-kind-immutable')
    return {
        ...form,
        kind: definition.kind,
        moduleVersion: Number(definition.version),
        parameters: transformDefaults(definition.parameters),
        connections: {},
    }
}
// Standard DTO fields are bounded IDs/Int32s. Only the original token reader is used
// to populate the module editor; JSON.parse's rounded parameters never enter it.
export function readConnectionList(text: string) {
    const raw = routeObject(readRouteJson(text, 16 * 1024 * 1024))
    if (!Array.isArray(raw.connections)) throw new Error('connection-list')
    const exactParameters: Record<string, string> = {}
    for (const item of raw.connections) {
        const connection = routeObject(item)
        if (connection.kind === 'module') {
            const module = routeObject(connection.module)
            const id =
                connection.id instanceof RouteNumber
                    ? connection.id.text
                    : String(connection.id)
            exactParameters[id] = writeRouteJson(routeObject(module.parameters))
        }
    }
    const typed: components['schemas']['ConnectionDraftList'] = JSON.parse(text)
    return { ...typed, exactParameters }
}
export function moduleConnectionRequest(form: ModuleConnectionForm) {
    const parameters = routeObject(readRouteJson(form.parameters, 128 * 1024))
    const connections = Object.fromEntries(
        Object.entries(form.connections).map(([name, text]) => {
            const id = Number(text)
            if (
                !Number.isSafeInteger(id) ||
                id < 1 ||
                String(id) !== text ||
                String(form.id) === text ||
                Object.hasOwn(parameters, name)
            )
                throw new Error('module-connection')
            return [name, id]
        }),
    )
    const references: Record<string, Record<string, number>> = Object.keys(
        connections,
    ).length
        ? { connections }
        : {}
    const body: components['schemas']['ConnectionDraftRequest'] = {
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        enabled: form.enabled,
        kind: 'module',
        module: {
            kind: form.kind,
            version: form.moduleVersion,
            parameters: {},
            ...references,
        },
    }
    const serialized = writeRouteJson({
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        enabled: form.enabled,
        kind: 'module',
        module: {
            kind: form.kind,
            version: form.moduleVersion,
            parameters,
            ...references,
        },
    })
    return { body, serialized }
}

export function changeModuleConnectionReference(
    form: ModuleConnectionForm,
    name: string,
    value: string,
): ModuleConnectionForm {
    const parameters = { ...routeObject(readRouteJson(form.parameters)) }
    const connections = { ...form.connections }
    // Only an explicit selection can replace a legacy null/scalar. Do not infer
    // connection references from arbitrary numeric parameters.
    delete parameters[name]
    if (value) connections[name] = value
    else delete connections[name]
    return { ...form, parameters: writeRouteJson(parameters), connections }
}

export function moduleConnectionIssues(
    form: ModuleConnectionForm,
    definition: ConnectionModule,
    connections: readonly ParameterConnection[],
) {
    const schema = {
        ...definition.parameters,
        parameters: definition.parameters.parameters?.filter(
            (field) => field.type !== 'credential',
        ),
    }
    const parameters = routeObject(readRouteJson(form.parameters))
    const invalidReferences = Object.keys(form.connections).filter(
        (name) =>
            Object.hasOwn(parameters, name) ||
            !schema.parameters?.some(
                (field) =>
                    field.name === name && field.type === 'connectionReference',
            ),
    )
    for (const field of schema.parameters ?? [])
        if (
            field.type === 'connectionReference' &&
            parameters[field.name] != null
        )
            invalidReferences.push(field.name)
    return [
        ...invalidReferences.map((name) => ({ name, code: 'connection' })),
        ...parameterIssues(
            schema,
            { ...transformValues(form.parameters), ...form.connections },
            connections.filter((item) => item.id !== String(form.id)),
        ),
    ]
}
