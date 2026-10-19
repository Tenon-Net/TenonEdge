import type { components } from '../../api/schema'
import { routeObject } from '../canvas/codec'
import { readRouteJson, writeRouteJson, RouteNumber } from '../canvas/json'
import { transformDefaults } from '../canvas/transform-model'
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
        },
    }
    const serialized = writeRouteJson({
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        enabled: form.enabled,
        kind: 'module',
        module: { kind: form.kind, version: form.moduleVersion, parameters },
    })
    return { body, serialized }
}
