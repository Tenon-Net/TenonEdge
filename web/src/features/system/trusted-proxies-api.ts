import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
export type TrustedProxyState = components['schemas']['TrustedProxyState']
export type TrustedProxyForm = {
    expectedVersion: string | null
    networks: string
}
export function editTrustedProxies(value: TrustedProxyState): TrustedProxyForm {
    return {
        expectedVersion: value.version,
        networks: value.networks.join('\n'),
    }
}
export function trustedProxyRequest(
    form: TrustedProxyForm,
    confirmed: boolean,
) {
    if (!confirmed) throw new Error('proxy-confirmation')
    if (
        form.expectedVersion !== null &&
        !/^[0-9a-f]{32}$/.test(form.expectedVersion)
    )
        throw new Error('proxy-version')
    const networks = form.networks
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean)
    if (
        networks.length > 1000 ||
        networks.some(
            (line) =>
                line.length > 80 ||
                Array.from(line).some(
                    (character) =>
                        character.charCodeAt(0) < 32 ||
                        character.charCodeAt(0) === 127,
                ),
        )
    )
        throw new Error('proxy-range')
    return { expectedVersion: form.expectedVersion, networks, confirmed: true }
}
export function createTrustedProxyApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return {
        async read(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/system/trusted-proxies',
                { signal },
            )
            if (!data) throw new Error('missing-proxy-state')
            return data
        },
        async write(form: TrustedProxyForm, confirmed: boolean) {
            const { data } = await api.PUT(
                '/api/edge-admin/v1/system/trusted-proxies',
                { body: trustedProxyRequest(form, confirmed) },
            )
            if (!data) throw new Error('missing-proxy-state')
            return data
        },
    }
}
