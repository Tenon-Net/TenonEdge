import { PageHeading } from '../../components/layout/page'
import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { api, useSession } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import {
    createMessagesApi,
    type MessageInfo,
    type MessageObject,
} from './messages-api'
import { dateText } from './view'

const messages = createMessagesApi(api)
const options = {
    retry: false,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
} as const
const identity = (object: MessageObject) => `${object.kind}/${object.key}`

export function RuntimeMessagesPage() {
    const { t } = useTranslation()
    const queries = useQueryClient()
    const [paused, pause] = useState(false)
    const [cursors, setCursors] = useState<
        { after: string; revision: string }[]
    >([])
    const [selected, select] = useState<{
        object: MessageObject
        revision: string
    }>()
    const cursor = cursors.at(-1)
    const query = useQuery({
        ...options,
        queryKey: ['runtime-messages', 'objects', cursor],
        queryFn: ({ signal }) =>
            messages.objects(cursor?.after, cursor?.revision, signal),
        enabled: !paused,
        refetchInterval: paused ? false : 1000,
    })
    const changed =
        (selected && query.data && selected.revision !== query.data.revision) ||
        (query.error instanceof ApiError && query.error.status === 409)
    return (
        <div className="product-page">
            <PageHeading
                description={t('messages.notice')}
                title={t('messages.title')}
            />

            <Button
                onClick={() => {
                    if (!paused)
                        void queries.cancelQueries({
                            queryKey: ['runtime-messages'],
                        })
                    pause(!paused)
                }}
            >
                {t(paused ? 'messages.resume' : 'messages.pause')}
            </Button>
            {paused && <p role="status">{t('messages.paused')}</p>}
            <div className={selected && !changed ? 'product-workbench' : ''}>
                <section
                    className="product-panel space-y-3"
                    aria-label={t('messages.objects')}
                >
                    <h2 className="text-lg font-semibold">
                        {t('messages.objects')}
                    </h2>
                    <ErrorMessage error={query.error} />
                    <p>
                        {t('messages.revision', {
                            revision: query.data?.revision ?? '—',
                        })}
                    </p>
                    {changed && <p role="alert">{t('messages.changed')}</p>}
                    <div>
                        <label htmlFor="runtime-message-object">
                            {t('messages.choose')}
                        </label>
                        <select
                            id="runtime-message-object"
                            className="ml-3 max-w-full rounded border bg-background p-2"
                            value={selected ? identity(selected.object) : ''}
                            disabled={paused || !!query.error || !!changed}
                            onChange={(event) => {
                                const object = query.data?.items.find(
                                    (item) =>
                                        identity(item) === event.target.value,
                                )
                                select(
                                    object && query.data?.revision
                                        ? {
                                              object,
                                              revision: query.data.revision,
                                          }
                                        : undefined,
                                )
                            }}
                        >
                            <option value="">—</option>
                            {query.data?.items.map((object) => (
                                <option
                                    key={identity(object)}
                                    value={identity(object)}
                                >
                                    {identity(object)}
                                </option>
                            ))}
                        </select>
                    </div>
                    {query.data?.items.length === 0 && (
                        <p>{t('messages.empty')}</p>
                    )}
                    <div className="flex flex-wrap gap-3">
                        <Button
                            variant="outline"
                            disabled={
                                paused || !cursors.length || query.isFetching
                            }
                            onClick={() => {
                                select(undefined)
                                setCursors(cursors.slice(0, -1))
                            }}
                        >
                            {t('diagnostics.previous')}
                        </Button>
                        <Button
                            variant="outline"
                            disabled={
                                paused ||
                                !query.data?.nextCursor ||
                                query.isFetching ||
                                !!query.error
                            }
                            onClick={() => {
                                if (
                                    query.data?.nextCursor &&
                                    query.data.revision
                                ) {
                                    select(undefined)
                                    setCursors([
                                        ...cursors,
                                        {
                                            after: query.data.nextCursor,
                                            revision: query.data.revision,
                                        },
                                    ])
                                }
                            }}
                        >
                            {t('diagnostics.next')}
                        </Button>
                        <Button
                            variant="outline"
                            disabled={paused}
                            onClick={() => {
                                select(undefined)
                                setCursors([])
                                if (!cursor) void query.refetch()
                            }}
                        >
                            {t('messages.reset')}
                        </Button>
                    </div>
                </section>
                {selected && !changed && (
                    <Records
                        key={`${selected.revision}/${identity(selected.object)}`}
                        object={selected.object}
                        revision={selected.revision}
                        paused={paused}
                    />
                )}
            </div>
        </div>
    )
}

function Records({
    object,
    revision,
    paused,
}: {
    object: MessageObject
    revision: string
    paused: boolean
}) {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const canRead = role === 'administrator' || role === 'configurator'
    const [selected, select] = useState<MessageInfo>()
    const query = useQuery({
        ...options,
        queryKey: [
            'runtime-messages',
            'records',
            object.kind,
            object.key,
            revision,
        ],
        queryFn: ({ signal }) => messages.records(object, revision, signal),
        enabled: !paused,
        refetchInterval: paused ? false : 1000,
    })
    return (
        <>
            <section
                className="space-y-4 rounded-lg border p-5"
                aria-label={t('messages.records')}
            >
                <h2 className="text-lg font-semibold">
                    {t('messages.records')} · {identity(object)}
                </h2>
                <ErrorMessage error={query.error} />
                {object.unresolved && (
                    <p role="note">{t('messages.unresolved')}</p>
                )}
                {!canRead && <p>{t('messages.viewer')}</p>}
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead>
                            <tr>
                                {[
                                    'time',
                                    'identity',
                                    'source',
                                    'outcome',
                                    'size',
                                ].map((key) => (
                                    <th className="p-2" key={key}>
                                        {t(`messages.${key}`)}
                                    </th>
                                ))}
                                {canRead && <th />}
                            </tr>
                        </thead>
                        <tbody>
                            {query.data?.items.map((record) => (
                                <tr
                                    className="border-t align-top"
                                    key={record.sequence}
                                >
                                    <td className="p-2">
                                        {dateText(record.capturedAt)}
                                    </td>
                                    <td className="p-2">
                                        {record.sequence} / r{record.revision}
                                    </td>
                                    <td className="max-w-xs p-2 break-all">
                                        {record.details?.inputKey}
                                        <small className="block">
                                            {record.sourceMessageId}
                                        </small>
                                        <small className="block">
                                            {record.details?.pathKey}
                                        </small>
                                    </td>
                                    <td className="p-2">
                                        {record.details
                                            ? t(
                                                  `messages.outcomes.${record.details.outcome}`,
                                                  {
                                                      defaultValue:
                                                          record.details
                                                              .outcome,
                                                  },
                                              )
                                            : '—'}
                                        {record.details && (
                                            <small className="block">
                                                {t('messages.fields', {
                                                    invalid:
                                                        record.details
                                                            .invalidFields,
                                                    missing:
                                                        record.details
                                                            .missingFields,
                                                    stage:
                                                        record.details
                                                            .failedMappingStage ??
                                                        '—',
                                                })}
                                            </small>
                                        )}
                                    </td>
                                    <td className="p-2">
                                        {record.storedBytes} /{' '}
                                        {record.originalBytes} B
                                        {record.truncated && (
                                            <p>{t('messages.truncated')}</p>
                                        )}
                                        {record.privacyBlocked && (
                                            <p>{t('messages.private')}</p>
                                        )}
                                    </td>
                                    {canRead && (
                                        <td className="p-2">
                                            <Button
                                                variant="outline"
                                                disabled={
                                                    record.privacyBlocked ||
                                                    !!query.error
                                                }
                                                onClick={() => select(record)}
                                            >
                                                {t('messages.view', {
                                                    sequence: record.sequence,
                                                })}
                                            </Button>
                                        </td>
                                    )}
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
                {query.data?.items.length === 0 && <p>{t('messages.empty')}</p>}
            </section>
            {canRead && selected && (
                <Body
                    key={`${selected.sequence}/${selected.sourceMessageId}`}
                    object={object}
                    revision={revision}
                    record={selected}
                    close={() => select(undefined)}
                />
            )}
        </>
    )
}

function Body({
    object,
    revision,
    record,
    close,
}: {
    object: MessageObject
    revision: string
    record: MessageInfo
    close: () => void
}) {
    const { t } = useTranslation()
    const query = useQuery({
        ...options,
        queryKey: [
            'runtime-messages',
            'body',
            object.kind,
            object.key,
            revision,
            record.sequence,
            record.revision,
            record.sourceMessageId,
        ],
        queryFn: ({ signal }) =>
            messages.body(object, revision, record, signal),
        staleTime: Infinity,
    })
    return (
        <section
            className="space-y-4 rounded-lg border p-5"
            aria-label={t('messages.body')}
        >
            <h2 className="text-lg font-semibold">
                {t('messages.body')} · {record.sequence} / r{record.revision}
            </h2>
            <p>{t('messages.bodyNotice')}</p>
            <ErrorMessage error={query.error} />
            {query.error && <p>{t('messages.gone')}</p>}
            {query.data && !query.error && (
                <>
                    <p>
                        {query.data.record.storedBytes} /{' '}
                        {query.data.record.originalBytes} B
                    </p>
                    {query.data.record.truncated && (
                        <p role="note">{t('messages.truncated')}</p>
                    )}
                    <pre className="max-h-[32rem] overflow-auto rounded bg-muted p-3 whitespace-pre-wrap break-all text-xs">
                        {query.data.body}
                    </pre>
                </>
            )}
            <Button variant="outline" onClick={close}>
                {t('messages.close')}
            </Button>
        </section>
    )
}
