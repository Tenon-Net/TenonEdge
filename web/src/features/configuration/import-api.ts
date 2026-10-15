import type { createGatewayClient } from '../../api/client'
import { decodeCurrentImport, decodeImportReview } from './api'

export const maximumImportBytes = 16 * 1024 * 1024
export function createConfigurationImportApi(
    client: ReturnType<typeof createGatewayClient>,
) {
    return {
        async prepare(
            file: File,
            mode: 'merge' | 'replace',
            expectedVersion: string,
            signal: AbortSignal,
        ) {
            if (file.size > maximumImportBytes)
                throw new Error('configuration-import-size')
            const { data } = await client.POST(
                '/api/edge-admin/v1/configuration/import/prepare',
                {
                    params: { query: { mode, expectedVersion } },
                    // Pass the original bytes, never JSON.parse/stringify a portable file.
                    body: file,
                    bodySerializer: () => file,
                    headers: { 'Content-Type': 'application/json' },
                    parseAs: 'text',
                    signal,
                },
            )
            if (!data) throw new Error('configuration-import-response')
            return decodeImportReview(data)
        },
        async current(signal: AbortSignal) {
            const { data } = await client.GET(
                '/api/edge-admin/v1/configuration/import',
                { parseAs: 'text', signal },
            )
            if (!data) throw new Error('configuration-import-response')
            return decodeCurrentImport(data)
        },
        async confirm(id: string, signal: AbortSignal) {
            const { data } = await client.POST(
                '/api/edge-admin/v1/configuration/import/confirm',
                { body: { id, confirmed: true }, signal },
            )
            if (!data) throw new Error('configuration-import-response')
            return data
        },
        async cancel(id: string, signal: AbortSignal) {
            await client.DELETE(
                '/api/edge-admin/v1/configuration/import/{id}',
                { params: { path: { id } }, signal },
            )
        },
    }
}
