import type { createGatewayClient } from '../../api/client'
import type { components } from '../../api/schema'
import { revisionId } from '../configuration/api'

type Schema = components['schemas']
export type MessageObject = Schema['RuntimeMessageObject']
export type MessageInfo = Schema['RuntimeMessageInfo']
export function createMessagesApi(api: ReturnType<typeof createGatewayClient>) {
    return {
        async objects(after?: string, revision?: string, signal?: AbortSignal) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/messages/objects',
                {
                    params: { query: { after, revision, limit: 100 } },
                    signal,
                },
            )
            if (!data || data.items.length > 100)
                throw new Error('message-objects')
            if (data.revision !== null) revisionId(data.revision)
            return data
        },
        async records(
            object: MessageObject,
            revision: string,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/messages',
                {
                    params: {
                        query: {
                            kind: object.kind,
                            key: object.key,
                            revision: revisionId(revision),
                        },
                    },
                    signal,
                },
            )
            if (!data || data.items.length > 100)
                throw new Error('message-records')
            return data
        },
        async body(
            object: MessageObject,
            revision: string,
            record: MessageInfo,
            signal?: AbortSignal,
        ) {
            const { data } = await api.GET(
                '/api/edge-admin/v1/runtime/messages/{sequence}/body',
                {
                    params: {
                        path: { sequence: revisionId(record.sequence) },
                        query: {
                            kind: object.kind,
                            key: object.key,
                            revision: revisionId(revision),
                            recordRevision: revisionId(record.revision),
                            sourceMessageId: record.sourceMessageId,
                        },
                    },
                    signal,
                },
            )
            if (!data) throw new Error('message-body')
            // Body stays a string, including truncated JSON and exact Int64/UInt64 values.
            return data
        },
    }
}
