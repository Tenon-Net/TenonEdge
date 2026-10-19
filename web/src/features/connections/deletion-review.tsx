import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { Connection } from './model'
import { useTranslation } from 'react-i18next'

export function ConnectionDeletionReview({
    connection,
    version,
    onDeleted,
    onClose,
}: {
    connection: Connection
    version: string
    onDeleted: () => Promise<void>
    onClose: () => void
}) {
    const { t } = useTranslation()
    const [generation, setGeneration] = useState(0)
    const [consumed, setConsumed] = useState(false)
    const review = useQuery({
        queryKey: [
            'connection-deletion-review',
            connection.id,
            version,
            generation,
        ],
        gcTime: 0,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/connections/{connectionId}/deletion-review',
                {
                    signal,
                    params: {
                        path: { connectionId: Number(connection.id) },
                        query: { expectedVersion: version },
                    },
                },
            )
            if (
                !data ||
                Number(data.id) !== Number(connection.id) ||
                data.version !== version
            )
                throw new Error('invalid-connection-deletion-review')
            return data
        },
    })
    const remove = useMutation({
        retry: false,
        mutationFn: async () => {
            await api.DELETE('/api/edge-admin/v1/connections/{connectionId}', {
                params: {
                    path: { connectionId: Number(connection.id) },
                    query: { expectedVersion: version },
                },
            })
        },
        onSuccess: onDeleted,
    })
    const ready = review.isSuccess && !review.isFetching && !consumed
    return (
        <section
            className="space-y-3 rounded border p-4"
            aria-label={t('connections.deletion.title')}
        >
            <h2 className="font-semibold">
                {t('connections.deletion.title')}：{connection.name}（
                {connection.key}）
            </h2>
            <p>{t('connections.deletion.disclaimer')}</p>
            <ErrorMessage error={review.error ?? remove.error} />
            {remove.error && (
                <p role="alert">{t('connections.deletion.unknown')}</p>
            )}
            {!ready && (
                <p role="status">{t('connections.deletion.unavailable')}</p>
            )}
            {ready && review.data && (
                <>
                    <h3>
                        {t('connections.deletion.devices', {
                            count: review.data.devices.length,
                        })}
                    </h3>
                    {review.data.devices.length > 0 && (
                        <ul>
                            {review.data.devices.map((item) => (
                                <li key={`${item.id}/${item.inputKey ?? ''}`}>
                                    {item.name} (<code>{item.key}</code>)
                                    {item.inputKey
                                        ? ` · ${t('connections.deletion.source', { key: item.inputKey })}`
                                        : ` · ${t('connections.deletion.deviceConnection')}`}
                                </li>
                            ))}
                        </ul>
                    )}
                    <h3>
                        {t('connections.deletion.routes', {
                            count: review.data.routes.length,
                        })}
                    </h3>
                    {review.data.routes.length > 0 && (
                        <ul>
                            {review.data.routes.map((item) => (
                                <li key={item.id}>
                                    {item.name} (<code>{item.key}</code>) ·{' '}
                                    {t(
                                        item.enabled
                                            ? 'connections.deletion.enabled'
                                            : 'connections.deletion.disabled',
                                    )}
                                </li>
                            ))}
                        </ul>
                    )}
                    <h3>
                        {t('connections.deletion.versions', {
                            count: review.data.versions.length,
                        })}
                    </h3>
                    {review.data.versions.map((item) => (
                        <details key={item.revision}>
                            <summary>
                                r{item.revision} · {item.positions.join(' / ')}{' '}
                                ·{' '}
                                {t('connections.deletion.units', {
                                    count: item.units.length,
                                })}
                            </summary>
                            <ul>
                                {item.units.map((unit) => (
                                    <li key={unit}>
                                        <code>{unit}</code>
                                    </li>
                                ))}
                            </ul>
                        </details>
                    ))}
                    <h3>{t('connections.deletion.deliveries')}</h3>
                    <p>
                        {t('connections.deletion.pending', {
                            count: review.data.pendingCount,
                        })}{' '}
                        ·{' '}
                        {t('connections.deletion.failed', {
                            count: review.data.failedCount,
                        })}{' '}
                        ·{' '}
                        {t('connections.deletion.quarantined', {
                            count: review.data.quarantinedCount,
                        })}
                    </p>
                    {!review.data.canDelete && (
                        <p role="alert">{t('connections.deletion.blocked')}</p>
                    )}
                    <Button
                        type="button"
                        variant="destructive"
                        disabled={!review.data.canDelete || remove.isPending}
                        onClick={() => {
                            if (
                                window.confirm(
                                    t('connections.deletion.prompt', {
                                        name: connection.name,
                                    }),
                                )
                            ) {
                                setConsumed(true)
                                remove.mutate()
                            }
                        }}
                    >
                        {t('connections.deletion.confirm')}
                    </Button>
                </>
            )}
            <div className="flex gap-2">
                <Button
                    type="button"
                    variant="outline"
                    disabled={review.isFetching || remove.isPending}
                    onClick={() => {
                        remove.reset()
                        setConsumed(false)
                        setGeneration((value) => value + 1)
                    }}
                >
                    {t('connections.deletion.refresh')}
                </Button>
                <Button
                    type="button"
                    variant="outline"
                    disabled={remove.isPending}
                    onClick={onClose}
                >
                    {t('connections.deletion.cancel')}
                </Button>
            </div>
        </section>
    )
}
