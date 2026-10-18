import type { components } from '../../api/schema'
import type { Connection } from './model'

export type RuntimeConnection = components['schemas']['RuntimeConnectionInfo']
export type RuntimeConnectionList =
    components['schemas']['RuntimeConnectionList']
export type ConnectionRow = {
    id: number
    draft?: Connection
    runtime?: RuntimeConnection
}

// The only editable objects are real drafts. Never fabricate parameters from a
// frozen summary (notably legacy seeds or a deleted-but-still-effective row).
export function connectionRows(
    drafts: Connection[],
    runtime: RuntimeConnection[],
    search = '',
    kind = '',
): ConnectionRow[] {
    const rows = new Map<number, ConnectionRow>()
    for (const item of runtime)
        rows.set(Number(item.id), { id: Number(item.id), runtime: item })
    for (const draft of drafts) {
        const row = rows.get(Number(draft.id)) ?? { id: Number(draft.id) }
        rows.set(Number(draft.id), { ...row, draft })
    }
    const needle = search.toLowerCase()
    return [...rows.values()]
        .filter(({ draft, runtime }) => {
            const definitions = [draft, runtime?.effective].filter(
                (value) => value != null,
            )
            return (
                (!kind || definitions.some((item) => item.kind === kind)) &&
                definitions.some((item) =>
                    `${item.name} ${item.key ?? ''}`
                        .toLowerCase()
                        .includes(needle),
                )
            )
        })
        .sort((left, right) => left.id - right.id)
}

export function connectionSnapshotCurrent(
    version: string | undefined,
    snapshot: RuntimeConnectionList | undefined,
    failed: boolean,
): boolean {
    return !failed && !!snapshot && version === snapshot.draftVersion
}

export function draftTarget(connection: Connection): string {
    if (connection.http) return connection.http.baseAddress
    const settings = connection.modbus ?? connection.mqtt
    if (!settings) return ''
    const host = settings.host.includes(':')
        ? `[${settings.host}]`
        : settings.host
    return `${connection.mqtt ? (connection.mqtt.tls ? 'mqtts://' : 'mqtt://') : ''}${host}:${settings.port}`
}
