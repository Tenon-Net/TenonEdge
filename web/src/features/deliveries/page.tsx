import { useState } from 'react'
import { useSearch } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { api, useSession } from '../../lib/session'
import { DiagnosticPanel, dateText } from '../diagnostics/view'
import {
    createDeliveryApi,
    readFilter,
    type DeliveryArea,
    type DeliveryFilter,
    type DeliveryItem,
} from './api'
import { FailedRetryControls } from './retry'

const deliveries = createDeliveryApi(api)
export function DeliveryRecordsPage() {
    const { connectionId } = useSearch({ from: '/deliveries' })
    return <Records key={connectionId ?? ''} connectionId={connectionId} />
}
function Records({ connectionId }: { connectionId?: string }) {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    const canReadBody = role === 'administrator' || role === 'configurator'
    const [draft, setDraft] = useState({
        state: 'failed' as DeliveryArea,
        connectionId: connectionId ?? '',
        deviceKey: '',
        reason: '',
        from: '',
        to: '',
    })
    const [filter, setFilter] = useState<DeliveryFilter>({
        state: 'failed',
        connectionId,
    })
    const [cursors, setCursors] = useState<string[]>([])
    const [invalid, setInvalid] = useState(false)
    const [selected, select] = useState<DeliveryItem>()
    const [checked, check] = useState<DeliveryItem[]>([])
    const [retryLocked, lockRetry] = useState(false)
    const canRetry = canReadBody && filter.state === 'failed'
    const after = cursors.at(-1)
    const query = useQuery({
        queryKey: ['delivery-records', filter, after],
        queryFn: ({ signal }) => deliveries.records(filter, after, signal),
        gcTime: 0,
    })
    const visibleChecked = checked.filter((entry) =>
        query.data?.items.some(
            (item) =>
                item.sequence === entry.sequence &&
                item.deliveryId === entry.deliveryId,
        ),
    )
    return (
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold">{t('deliveries.title')}</h1>
            <p className="text-sm text-muted-foreground">
                {t('deliveries.notice')}
            </p>
            <DiagnosticPanel title={t('deliveries.records')} query={query}>
                <fieldset disabled={retryLocked}>
                    <form
                        className="flex flex-wrap items-end gap-3"
                        onSubmit={(event) => {
                            event.preventDefault()
                            try {
                                setFilter(readFilter(draft))
                                setCursors([])
                                check([])
                                select(undefined)
                                setInvalid(false)
                            } catch {
                                setInvalid(true)
                            }
                        }}
                    >
                        <div className="space-y-1 text-sm">
                            <label htmlFor="delivery-area">
                                {t('deliveries.area')}
                            </label>
                            <select
                                id="delivery-area"
                                className="block h-9 rounded-md border bg-background px-3"
                                value={draft.state}
                                onChange={(event) =>
                                    setDraft({
                                        ...draft,
                                        state: event.target
                                            .value as DeliveryArea,
                                    })
                                }
                            >
                                <option value="failed">
                                    {t('deliveries.failed')}
                                </option>
                                <option value="quarantined">
                                    {t('deliveries.quarantined')}
                                </option>
                            </select>
                        </div>
                        {(
                            [
                                'connectionId',
                                'deviceKey',
                                'reason',
                                'from',
                                'to',
                            ] as const
                        ).map((key) => (
                            <label key={key} className="space-y-1 text-sm">
                                {t(`deliveries.${key}`)}
                                <Input
                                    type={
                                        key === 'from' || key === 'to'
                                            ? 'datetime-local'
                                            : 'text'
                                    }
                                    maxLength={key === 'reason' ? 1024 : 128}
                                    value={draft[key]}
                                    onChange={(event) =>
                                        setDraft({
                                            ...draft,
                                            [key]: event.target.value,
                                        })
                                    }
                                />
                            </label>
                        ))}
                        <Button type="submit">{t('deliveries.filter')}</Button>
                    </form>
                </fieldset>
                <p className="text-sm text-muted-foreground">
                    {t('deliveries.filterNotice')}
                </p>
                <p className="text-sm text-muted-foreground">
                    {t('deliveries.failureNotice')}
                </p>
                {invalid && <p role="alert">{t('deliveries.invalid')}</p>}
                {!canReadBody && <p>{t('deliveries.viewer')}</p>}
                {query.data && (
                    <>
                        <p className="font-semibold">
                            {t(`deliveries.${filter.state}`)}
                        </p>
                        <div className="overflow-x-auto">
                            <table className="w-full text-left text-sm">
                                <thead>
                                    <tr>
                                        {canRetry && (
                                            <th className="p-2">
                                                {t('deliveries.retry.select')}
                                            </th>
                                        )}
                                        {[
                                            'target',
                                            'identity',
                                            'cause',
                                            'failureCount',
                                            'times',
                                            'source',
                                        ].map((key) => (
                                            <th key={key} className="p-2">
                                                {t(`deliveries.${key}`)}
                                            </th>
                                        ))}
                                        {canReadBody && <th />}
                                    </tr>
                                </thead>
                                <tbody>
                                    {query.data.items.map((item) => (
                                        <tr
                                            className="border-t align-top"
                                            key={item.deliveryId}
                                        >
                                            {canRetry && (
                                                <td className="p-2">
                                                    <input
                                                        type="checkbox"
                                                        aria-label={t(
                                                            'deliveries.retry.selectRecord',
                                                            {
                                                                id: item.deliveryId,
                                                            },
                                                        )}
                                                        checked={checked.some(
                                                            (entry) =>
                                                                entry.sequence ===
                                                                    item.sequence &&
                                                                entry.deliveryId ===
                                                                    item.deliveryId,
                                                        )}
                                                        disabled={
                                                            retryLocked ||
                                                            query.isFetching ||
                                                            !!query.error
                                                        }
                                                        onChange={(event) =>
                                                            check(
                                                                event.target
                                                                    .checked
                                                                    ? [
                                                                          ...visibleChecked,
                                                                          item,
                                                                      ]
                                                                    : visibleChecked.filter(
                                                                          (
                                                                              entry,
                                                                          ) =>
                                                                              entry.deliveryId !==
                                                                              item.deliveryId,
                                                                      ),
                                                            )
                                                        }
                                                    />
                                                </td>
                                            )}
                                            <td className="p-2">
                                                <p>{item.connectionId}</p>
                                                <p>{item.deviceKey}</p>
                                                <small>
                                                    {t('deliveries.bytes', {
                                                        bytes: item.byteSize,
                                                    })}
                                                </small>
                                            </td>
                                            <td className="max-w-48 p-2 break-all">
                                                <p>{item.deliveryId}</p>
                                                <p>#{item.sequence}</p>
                                            </td>
                                            <td className="max-w-xs p-2 break-words whitespace-pre-wrap">
                                                {item.quarantineReason && (
                                                    <p>
                                                        {t(
                                                            `deliveries.reasons.${item.quarantineReason}`,
                                                            {
                                                                defaultValue:
                                                                    item.quarantineReason,
                                                            },
                                                        )}
                                                    </p>
                                                )}
                                                {item.lastError ||
                                                    (!item.quarantineReason &&
                                                        t(
                                                            'deliveries.unknownReason',
                                                        ))}
                                            </td>
                                            <td className="p-2">
                                                {item.failureCount}
                                            </td>
                                            <td className="p-2 whitespace-nowrap">
                                                <p>
                                                    {dateText(item.createdAt)}
                                                </p>
                                                <p>{dateText(item.failedAt)}</p>
                                                {item.clockAnomaly && (
                                                    <p>
                                                        {t(
                                                            'deliveries.clockAnomaly',
                                                        )}
                                                    </p>
                                                )}
                                            </td>
                                            <td className="max-w-48 p-2 break-words">
                                                <p>
                                                    {t('deliveries.revision', {
                                                        revision: item.revision,
                                                        route:
                                                            item.routeId ?? '—',
                                                    })}
                                                </p>
                                                {item.nodeLabel ?? '—'}
                                            </td>
                                            {canReadBody && (
                                                <td className="p-2">
                                                    <Button
                                                        variant="outline"
                                                        aria-label={t(
                                                            'deliveries.view',
                                                            {
                                                                id: item.deliveryId,
                                                            },
                                                        )}
                                                        disabled={
                                                            query.isFetching ||
                                                            !!query.error
                                                        }
                                                        onClick={() =>
                                                            select(item)
                                                        }
                                                    >
                                                        {t(
                                                            'deliveries.viewShort',
                                                        )}
                                                    </Button>
                                                </td>
                                            )}
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                        {query.data.items.length === 0 && (
                            <p>{t('deliveries.empty')}</p>
                        )}
                        <div className="flex items-center gap-3">
                            <Button
                                variant="outline"
                                disabled={
                                    retryLocked ||
                                    !cursors.length ||
                                    query.isFetching
                                }
                                onClick={() => {
                                    check([])
                                    setCursors(cursors.slice(0, -1))
                                    select(undefined)
                                }}
                            >
                                {t('diagnostics.previous')}
                            </Button>
                            <span>
                                {t('diagnostics.page', {
                                    page: cursors.length + 1,
                                })}
                            </span>
                            <Button
                                variant="outline"
                                disabled={
                                    retryLocked ||
                                    query.data.nextSequence === null ||
                                    query.isFetching ||
                                    !!query.error
                                }
                                onClick={() => {
                                    if (query.data?.nextSequence != null) {
                                        check([])
                                        setCursors([
                                            ...cursors,
                                            String(query.data.nextSequence),
                                        ])
                                        select(undefined)
                                    }
                                }}
                            >
                                {t('diagnostics.next')}
                            </Button>
                        </div>
                    </>
                )}
            </DiagnosticPanel>
            {canRetry && (
                <FailedRetryControls
                    key={JSON.stringify([filter, after])}
                    filter={filter}
                    selected={visibleChecked}
                    unavailable={
                        query.isFetching ||
                        !!query.error ||
                        !query.data?.items.length
                    }
                    lock={lockRetry}
                    reset={() => {
                        check([])
                        select(undefined)
                        void query.refetch()
                    }}
                />
            )}
            {canReadBody && selected && (
                <Body
                    key={`${filter.state}/${selected.deliveryId}`}
                    id={selected.deliveryId}
                    state={filter.state}
                    close={() => select(undefined)}
                />
            )}
        </div>
    )
}
function Body({
    id,
    state,
    close,
}: {
    id: string
    state: DeliveryArea
    close: () => void
}) {
    const { t } = useTranslation()
    const query = useQuery({
        queryKey: ['delivery-body', id, state],
        queryFn: ({ signal }) => deliveries.body(id, state, signal),
        staleTime: Infinity,
        gcTime: 0,
    })
    const body = !query.error ? query.data : undefined
    return (
        <DiagnosticPanel title={t('deliveries.body')} query={query}>
            <p className="break-all font-mono text-sm">{id}</p>
            <p className="text-sm text-muted-foreground">
                {t('deliveries.bodyNotice')}
            </p>
            {query.error && <p>{t('deliveries.recordGone')}</p>}
            {body && (
                <>
                    <p>
                        {t('deliveries.bodySize', {
                            displayed: body.displayedBytes,
                            total: body.totalBytes,
                        })}
                    </p>
                    {body.truncated && (
                        <p role="note">{t('deliveries.truncated')}</p>
                    )}
                    {body.encoding === 'base64' && (
                        <p role="note">{t('deliveries.binary')}</p>
                    )}
                    <pre className="max-h-96 overflow-auto rounded border bg-background p-4 text-xs break-all whitespace-pre-wrap">
                        {body.content}
                    </pre>
                </>
            )}
            <Button variant="outline" onClick={close}>
                {t('deliveries.close')}
            </Button>
        </DiagnosticPanel>
    )
}
