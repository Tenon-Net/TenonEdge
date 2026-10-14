import type { components } from '../../api/schema'
import type { createGatewayClient } from '../../api/client'
import type { ConnectionForm } from './model'

export type OutboundTarget = { host: string; port: number }
export type OutboundInspection =
    components['schemas']['OutboundInspectionResult']

// Called after a successful draft save; secrets and HTTP path/header fields never enter the inspection body.
export function outboundTarget(form: ConnectionForm): OutboundTarget {
    if (form.kind === 'http') {
        const url = new URL(form.baseAddress)
        if (
            !['http:', 'https:'].includes(url.protocol) ||
            url.username ||
            url.password
        )
            throw new Error('outbound-target')
        return {
            host: url.hostname.replace(/^\[|\]$/g, ''),
            port: Number(url.port || (url.protocol === 'https:' ? 443 : 80)),
        }
    }
    return { host: form.host.trim(), port: Number(form.port) }
}

export function outboundGrantRequest(
    view: OutboundInspection,
): components['schemas']['OutboundPolicyRequest'] {
    const grants = view.addresses.flatMap((row) =>
        row.decision === 'publicGrantRequired' && row.grant ? [row.grant] : [],
    )
    if (!grants.length) throw new Error('outbound-no-grants')
    const networks = [...view.policy.networks]
    for (const grant of grants) {
        if (
            !networks.some(
                (row) =>
                    row.cidr === grant.cidr &&
                    String(row.port) === String(grant.port),
            )
        )
            networks.push(grant)
    }
    if (networks.length > 1000) throw new Error('outbound-grant-limit')
    return {
        expectedVersion: view.policy.version,
        networks,
        localPorts: [...view.policy.localPorts],
    }
}

export function createOutboundInspectionApi(
    api: ReturnType<typeof createGatewayClient>,
) {
    return async (target: OutboundTarget, signal?: AbortSignal) => {
        const { data } = await api.POST(
            '/api/edge-admin/v1/connections/inspect-outbound',
            {
                body: { host: target.host, port: target.port },
                signal,
            },
        )
        if (!data) throw new Error('missing-outbound-inspection')
        return data
    }
}
