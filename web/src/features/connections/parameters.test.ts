import { expect, it } from 'vitest'
import { connectionForm, connectionRequest } from './model'
import {
    changeConnectionParameter,
    connectionParameterValues,
    initializeConnectionParameters,
} from './parameters'
import type { ParameterSchema } from '../parameters/model'
const schema: ParameterSchema = {
    id: 'connection.mqtt',
    scope: 'connection',
    parameters: [
        {
            name: 'port',
            displayName: 'Port',
            type: 'number',
            defaultJson: '1883',
        },
        {
            name: 'tls',
            displayName: 'TLS',
            type: 'switch',
            defaultJson: 'false',
        },
        {
            name: 'pingIntervalMs',
            displayName: 'Ping',
            type: 'number',
            defaultJson: '15000',
        },
    ],
}
it('initializes explicitly from declarations, bridges aliases and keeps MQTT default-port convenience', () => {
    const original = { ...connectionForm('1'), kind: 'mqtt' as const }
    let form = initializeConnectionParameters(original, schema)
    expect(form.port).toBe('1883')
    form = changeConnectionParameter(form, 'tls', true)
    expect(form.port).toBe('8883')
    form = changeConnectionParameter(form, 'port', '9999')
    form = changeConnectionParameter(form, 'tls', false)
    form = changeConnectionParameter(form, 'pingIntervalMs', '1.2345e4')
    expect(connectionParameterValues(form, schema)).toEqual({
        port: '9999',
        tls: false,
        pingIntervalMs: '1.2345e4',
    })
    expect(connectionRequest(form).mqtt?.pingIntervalMs).toBe('12345')
    expect(original.port).toBe('502')
})
it('does not accept secret fields, arbitrary property assignment or invalid format from metadata', () => {
    for (const [name, value] of [
        ['password', 'secret'],
        ['format', 'unknown'],
        ['tls', 'true'],
        ['__proto__', 'x'],
    ] as const)
        expect(() =>
            changeConnectionParameter(connectionForm('1'), name, value),
        ).toThrow()
})
