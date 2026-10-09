import { describe, expect, it } from 'vitest'
import { connectionForm, connectionRequest, type Connection } from './model'

describe('typed draft connection editor', () => {
    it('keeps the captured global version and sends only the selected protocol', () => {
        const form = connectionForm('captured-version')
        form.key = 'plc-1'
        form.host = '10.0.0.1'
        const request = connectionRequest(form)
        expect(request.expectedVersion).toBe('captured-version')
        expect(request.modbus?.port).toBe('502')
        expect(request.mqtt).toBeUndefined()
        expect(request.http).toBeUndefined()
        expect(request).not.toHaveProperty('id')
    })
    it('preserves loaded protocol parameters, identifiers and disabled state', () => {
        const connection: Connection = {
            id: 45000000000001,
            key: 'broker',
            name: 'Broker',
            kind: 'mqtt',
            enabled: false,
            modbus: null,
            http: null,
            mqtt: {
                host: '10.1.2.3',
                port: 8883,
                tls: true,
                clientId: 'client-1',
                timeoutMs: 2000,
                pingIntervalMs: 10000,
            },
        }
        const form = connectionForm('version', connection)
        expect(form.id).toBe(connection.id)
        expect(connectionRequest(form)).toMatchObject({
            key: 'broker',
            enabled: false,
            mqtt: {
                tls: true,
                clientId: 'client-1',
                port: '8883',
                pingIntervalMs: '10000',
            },
        })
    })
    it('leaves default MQTT client identity generation to the server/runtime', () => {
        const form = connectionForm('0')
        form.kind = 'mqtt'
        expect(connectionRequest(form).mqtt?.clientId).toBeNull()
        expect(connectionRequest(form).modbus).toBeUndefined()
    })
    it('does not silently merge case-insensitive duplicate HTTP headers', () => {
        const form = connectionForm('0')
        form.kind = 'http'
        form.baseAddress = 'https://example.test/'
        form.headers = [
            { name: 'Accept', value: 'application/json' },
            { name: 'accept', value: 'text/plain' },
        ]
        expect(() => connectionRequest(form)).toThrow('duplicateHeaders')
        form.headers.pop()
        expect(connectionRequest(form).http?.headers).toEqual({
            Accept: 'application/json',
        })
    })
})

describe('local draft editor lifetime', () => {
    it('keeps captured version while editing and isolates page instances', async () => {
        const { createConnectionEditor } = await import('./store')
        const store = createConnectionEditor()
        store.getState().setForm(connectionForm('captured'))
        store.getState().setOriginal(JSON.stringify(store.getState().form))
        store
            .getState()
            .setForm((current) => current && { ...current, name: 'unsaved' })
        expect(store.getState().form?.version).toBe('captured')
        expect(store.getState().original).not.toContain('unsaved')
        expect(createConnectionEditor().getState().form).toBeNull()
        store.getState().setForm(null)
        expect(store.getState().form).toBeNull()
    })
})

describe('credential recipient uses only the saved non-secret connection', () => {
    it.each(['10.0.0.1', '2001:db8::1'])(
        'renders a canonical MQTT recipient for %s',
        async (host) => {
            const { credentialRecipient } = await import('./model')
            const connection: Connection = {
                id: 1,
                key: 'broker',
                name: 'Broker',
                enabled: true,
                kind: 'mqtt',
                modbus: null,
                http: null,
                mqtt: {
                    host,
                    port: 8883,
                    tls: true,
                    timeoutMs: 5000,
                    pingIntervalMs: 15000,
                },
            }
            expect(credentialRecipient(connection)).toBe(
                `mqtts://${host.includes(':') ? `[${host}]` : host}:8883`,
            )
        },
    )
    it('preserves the normalized HTTP base path as the credential boundary', async () => {
        const { credentialRecipient } = await import('./model')
        expect(
            credentialRecipient({
                id: 1,
                key: 'http',
                name: 'HTTP',
                enabled: true,
                kind: 'http',
                modbus: null,
                mqtt: null,
                http: {
                    baseAddress: 'https://example.test/api/',
                    timeoutMs: 5000,
                },
            }),
        ).toBe('https://example.test/api/')
    })
})
