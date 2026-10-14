import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
export type OutboundView = components['schemas']['OutboundPolicyView']
export type PolicyWrite = components['schemas']['OutboundPolicyRequest']
export type PolicyForm = {
    expectedVersion: string | null
    networks: { key: string; cidr: string; port: string }[]
    localPorts: string
}
let rowKey = 0
// Only a component-local React key, never a credential/object identifier. Works on non-localhost HTTP too.
export function policyRow(cidr = '', port = '443') {
    return { key: String(++rowKey), cidr, port }
}
export function editPolicy(value: OutboundView): PolicyForm {
    return {
        expectedVersion: value.version,
        networks: value.networks.map((row) =>
            policyRow(row.cidr, String(row.port)),
        ),
        localPorts: value.localPorts.join('\n'),
    }
}
export function policyRequest(form: PolicyForm): PolicyWrite {
    function port(text: string) {
        if (
            !/^\d+$/.test(text.trim()) ||
            Number(text) < 1 ||
            Number(text) > 65535
        )
            throw new Error('policy-port')
        return Number(text)
    }
    const locals = form.localPorts
        .split(/\r?\n/)
        .map((value) => value.trim())
        .filter(Boolean)
    if (form.networks.length > 1000 || locals.length > 1000)
        throw new Error('policy-limit')
    const networks = form.networks.map((row) => {
        const cidr = row.cidr.trim()
        if (!cidr || cidr.length > 64) throw new Error('policy-cidr')
        // Full IPv4/IPv6 network validation and canonicalization belongs to the same server validator as runtime rules.
        return { cidr, port: port(row.port) }
    })
    if (
        form.expectedVersion !== null &&
        !/^[0-9a-f]{32}$/.test(form.expectedVersion)
    )
        throw new Error('policy-version')
    return {
        expectedVersion: form.expectedVersion,
        networks,
        localPorts: locals.map(port),
    }
}
export function gatewayNameRequest(
    name: string,
    expectedName: string | null,
): components['schemas']['GatewayNameRequest'] {
    const normalized = name.trim()
    // Unicode Cc matches the server's char.IsControl rule (both C0 and C1).
    if (normalized.length > 64 || /\p{Cc}/u.test(normalized))
        throw new Error('gateway-name')
    return { name: normalized || null, expectedName }
}
export function createSystemApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async writeName(
            request: components['schemas']['GatewayNameRequest'],
            confirmed: boolean,
        ) {
            if (!confirmed) throw new Error('name-confirmation')
            const { data } = await api.PUT('/api/edge-admin/v1/system/name', {
                body: {
                    name: request.name,
                    expectedName: request.expectedName,
                },
            })
            if (!data) throw new Error('missing-name')
            return data
        },
        async policy(signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/system/outbound-policy',
                { signal },
            )
            if (!data) throw new Error('missing-policy')
            return data
        },
        async writePolicy(request: PolicyWrite, confirmed: boolean) {
            if (!confirmed) throw new Error('policy-confirmation')
            // Never spread the read model: deployment grants are not editable.
            const { data } = await api.PUT(
                '/api/edge-admin/v1/system/outbound-policy',
                {
                    body: {
                        expectedVersion: request.expectedVersion,
                        networks: request.networks,
                        localPorts: request.localPorts,
                    },
                },
            )
            if (!data) throw new Error('missing-policy')
            return data
        },
    }
}
