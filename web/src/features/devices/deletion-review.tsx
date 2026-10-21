import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'

export function DeviceDeletionPanel({
    id,
    version,
    onDeleted,
    onClose,
}: {
    id: number
    version: string
    onDeleted: () => Promise<void>
    onClose: () => void
}) {
    const { t } = useTranslation()
    const [generation, setGeneration] = useState(0)
    const [consumed, setConsumed] = useState(false)
    const review = useQuery({
        queryKey: ['device-deletion-review', id, version, generation],
        gcTime: 0,
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/devices/{deviceId}/deletion-review',
                {
                    signal,
                    params: {
                        path: { deviceId: id },
                        query: { expectedVersion: version },
                    },
                },
            )
            if (!data || Number(data.id) !== id || data.version !== version)
                throw new Error('invalid-device-deletion-review')
            return data
        },
    })
    const remove = useMutation({
        retry: false,
        mutationFn: async () => {
            await api.DELETE('/api/edge-admin/v1/devices/{deviceId}', {
                params: {
                    path: { deviceId: id },
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
            aria-label={t('devices.deletion.title')}
        >
            <h2 className="font-semibold">{t('devices.deletion.title')}</h2>
            <p>{t('devices.deletion.notice')}</p>
            <ErrorMessage error={review.error ?? remove.error} />
            {remove.error && (
                <p role="alert">{t('devices.deletion.unknown')}</p>
            )}
            {!ready && <p role="status">{t('devices.deletion.unavailable')}</p>}
            {ready && review.data && (
                <>
                    <p>
                        {t('devices.deletion.target', {
                            name: review.data.name,
                            key: review.data.key,
                            count: review.data.inputCount,
                        })}
                    </p>
                    {review.data.routes.length > 0 && (
                        <div>
                            <h3>{t('devices.deletion.routes')}</h3>
                            <ul>
                                {review.data.routes.map((route) => (
                                    <li key={route.id}>
                                        {route.name} (<code>{route.key}</code>)
                                        —{' '}
                                        {t(
                                            route.enabled
                                                ? 'devices.enabled'
                                                : 'devices.disabled',
                                        )}
                                    </li>
                                ))}
                            </ul>
                            <Link to="/routes" className="underline">
                                {t('devices.deletion.openRoutes')}
                            </Link>
                        </div>
                    )}
                    {review.data.versions.length > 0 && (
                        <div>
                            <h3>{t('devices.deletion.versions')}</h3>
                            <p>{t('devices.deletion.retainedHelp')}</p>
                            {review.data.versions.map((reference) => (
                                <details key={reference.revision}>
                                    <summary className="cursor-pointer">
                                        r{reference.revision} ·{' '}
                                        {reference.positions
                                            .map((position) =>
                                                t(
                                                    `devices.deletion.positions.${position}`,
                                                ),
                                            )
                                            .join(' / ')}{' '}
                                        ({reference.units.length})
                                    </summary>
                                    <ul>
                                        {reference.units.map((unit) => (
                                            <li key={unit}>
                                                <code>{unit}</code>
                                            </li>
                                        ))}
                                    </ul>
                                </details>
                            ))}
                            <Link to="/configuration" className="underline">
                                {t('devices.deletion.openConfiguration')}
                            </Link>
                        </div>
                    )}
                    {!review.data.canDelete && (
                        <p role="alert">{t('devices.deletion.blocked')}</p>
                    )}
                    <Button
                        type="button"
                        variant="destructive"
                        disabled={!review.data.canDelete || remove.isPending}
                        onClick={() => {
                            if (
                                window.confirm(
                                    t('devices.confirmDelete', {
                                        name: review.data!.name,
                                    }),
                                )
                            ) {
                                setConsumed(true)
                                remove.mutate()
                            }
                        }}
                    >
                        {t('devices.deletion.confirm')}
                    </Button>
                </>
            )}
            <div className="flex flex-wrap gap-2">
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
                    {t('devices.deletion.refresh')}
                </Button>
                <Button
                    type="button"
                    variant="outline"
                    disabled={remove.isPending}
                    onClick={onClose}
                >
                    {t('devices.deletion.cancel')}
                </Button>
            </div>
        </section>
    )
}
