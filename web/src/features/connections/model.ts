import type { components } from '../../api/schema'
export type Connection = components['schemas']['ConnectionDraft']
export type ConnectionRequest = components['schemas']['ConnectionDraftRequest']
export type ConnectionForm = ReturnType<typeof connectionForm>
export function connectionForm(version: string, connection?: Connection) {
    return {
        id: connection?.id,
        version,
        key: connection?.key ?? '',
        name: connection?.name ?? '',
        kind: connection?.kind ?? 'modbus',
        enabled: connection?.enabled ?? true,
        host: connection?.modbus?.host ?? connection?.mqtt?.host ?? '',
        port: String(connection?.modbus?.port ?? connection?.mqtt?.port ?? 502),
        format: connection?.modbus?.format ?? 'tcp',
        connectTimeout: String(connection?.modbus?.connectTimeoutMs ?? 5000),
        readTimeout: String(connection?.modbus?.readTimeoutMs ?? 1000),
        interval: String(connection?.modbus?.requestIntervalMs ?? 0),
        rtuSilence: String(connection?.modbus?.rtuSilenceMs ?? 500),
        tls: connection?.mqtt?.tls ?? false,
        clientId: connection?.mqtt?.clientId ?? '',
        timeout: String(
            connection?.mqtt?.timeoutMs ?? connection?.http?.timeoutMs ?? 5000,
        ),
        ping: String(connection?.mqtt?.pingIntervalMs ?? 15000),
        baseAddress: connection?.http?.baseAddress ?? '',
        headers: Object.entries(connection?.http?.headers ?? {}).map(
            ([name, value]) => ({ name, value }),
        ),
    }
}
export function connectionRequest(form: ConnectionForm): ConnectionRequest {
    const request: ConnectionRequest = {
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        kind: form.kind,
        enabled: form.enabled,
    }
    if (form.kind === 'modbus')
        request.modbus = {
            host: form.host,
            port: form.port,
            format: form.format,
            connectTimeoutMs: form.connectTimeout,
            readTimeoutMs: form.readTimeout,
            requestIntervalMs: form.interval,
            rtuSilenceMs: form.rtuSilence,
        }
    if (form.kind === 'mqtt')
        request.mqtt = {
            host: form.host,
            port: form.port,
            tls: form.tls,
            clientId: form.clientId || null,
            timeoutMs: form.timeout,
            pingIntervalMs: form.ping,
        }
    if (form.kind === 'http') {
        const names = form.headers.map((header) => header.name.toLowerCase())
        if (new Set(names).size !== names.length)
            throw new Error('duplicateHeaders')
        request.http = {
            baseAddress: form.baseAddress,
            timeoutMs: form.timeout,
            headers: Object.fromEntries(
                form.headers.map((header) => [header.name, header.value]),
            ),
        }
    }
    return request
}

export function credentialRecipient(connection: Connection) {
    if (connection.http) return connection.http.baseAddress
    const mqtt = connection.mqtt!
    const host = mqtt.host.includes(':') ? `[${mqtt.host}]` : mqtt.host
    return `${mqtt.tls ? 'mqtts' : 'mqtt'}://${host}:${mqtt.port}`
}
