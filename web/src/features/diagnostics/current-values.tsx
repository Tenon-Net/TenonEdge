import { useState } from 'react'
import { Link } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { DiagnosticPanel, dateText } from './view'
import { diagnostics, useRuntimeDevices } from './queries'
import type { RuntimeDeviceList } from './api'

export const deviceHealthKeys = [
    'normal',
    'partialFailure',
    'offline',
    'disabled',
    'push',
    'notRead',
    'inactive',
    'notApplied',
] as const
export function deviceCounts(devices: RuntimeDeviceList['devices']) {
    return Object.fromEntries(
        deviceHealthKeys.map((health) => [
            health,
            devices.filter((device) => device.health === health).length,
        ]),
    )
}
export function DeviceSummary() {
    const { t } = useTranslation()
    const query = useRuntimeDevices()
    const counts = deviceCounts(query.data?.devices ?? [])
    return (
        <DiagnosticPanel title={t('currentValues.summary')} query={query}>
            {query.data && (
                <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                    {deviceHealthKeys.map((health) => (
                        <div key={health}>
                            <dt className="text-sm text-muted-foreground">
                                {t(`currentValues.health.${health}`)}
                            </dt>
                            <dd className="text-xl font-semibold">
                                {counts[health]}
                            </dd>
                        </div>
                    ))}
                </dl>
            )}
            <Link className="underline" to="/runtime/devices">
                {t('currentValues.title')}
            </Link>
        </DiagnosticPanel>
    )
}

export function RuntimeDevicesPage() {
    const { t } = useTranslation()
    const query = useRuntimeDevices()
    const [selected, select] = useState('')
    const device = query.data?.devices.find((item) => item.key === selected)
    return (
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold">
                {t('currentValues.title')}
            </h1>
            <p className="text-sm text-muted-foreground">
                {t('currentValues.notice')}
            </p>
            <DiagnosticPanel title={t('currentValues.summary')} query={query}>
                <p>
                    {t('currentValues.revision', {
                        revision: query.data?.revision ?? '—',
                    })}
                </p>
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead>
                            <tr>
                                {['device', 'protocol', 'state', 'action'].map(
                                    (key) => (
                                        <th className="p-2" key={key}>
                                            {t(`currentValues.${key}`)}
                                        </th>
                                    ),
                                )}
                            </tr>
                        </thead>
                        <tbody>
                            {query.data?.devices.map((item) => (
                                <tr key={item.key} className="border-t">
                                    <td className="p-2">
                                        {item.name}{' '}
                                        <span className="text-muted-foreground">
                                            {item.key}
                                        </span>
                                        {item.removedFromDraft && (
                                            <p>{t('currentValues.removed')}</p>
                                        )}
                                    </td>
                                    <td className="p-2">{item.kind}</td>
                                    <td className="p-2">
                                        {t(
                                            `currentValues.health.${item.health}`,
                                            { defaultValue: item.health },
                                        )}
                                    </td>
                                    <td className="p-2">
                                        <Button
                                            variant="outline"
                                            disabled={item.draftOnly}
                                            onClick={() => select(item.key)}
                                        >
                                            {t('currentValues.view', {
                                                name: item.name,
                                            })}
                                        </Button>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
                {query.data && (
                    <p className="text-xs text-muted-foreground">
                        {t('currentValues.cache', {
                            fields: query.data.cache.fields,
                            bytes: query.data.cache.chargedBytes,
                            evictions: query.data.cache.evictions,
                        })}
                    </p>
                )}
                {query.data?.devices.length === 0 && (
                    <p>{t('currentValues.empty')}</p>
                )}
            </DiagnosticPanel>
            {device && query.data?.revision != null && (
                <div
                    key={`${device.key}/${query.data.revision}`}
                    className="space-y-5"
                >
                    <h2 className="text-xl font-semibold">{device.name}</h2>
                    <SourceValues
                        device={device}
                        revision={String(query.data.revision)}
                    />
                </div>
            )}
        </div>
    )
}

function SourceValues({
    device,
    revision,
}: {
    device: RuntimeDeviceList['devices'][number]
    revision: string
}) {
    const { t } = useTranslation()
    const [key, setKey] = useState(device.sources[0]?.key ?? '')
    const source = device.sources.find((item) => item.key === key)
    return (
        <>
            <label className="block">
                {t('currentValues.source')}
                <select
                    className="ml-3 rounded border bg-background p-2"
                    value={key}
                    onChange={(event) => setKey(event.target.value)}
                >
                    {device.sources.map((item) => (
                        <option key={item.key} value={item.key}>
                            {item.name} ({item.key})
                        </option>
                    ))}
                </select>
            </label>
            {source && (
                <>
                    <dl className="grid gap-4 text-sm sm:grid-cols-3">
                        <div>
                            <dt>{t('currentValues.loop')}</dt>
                            <dd>
                                {source.state
                                    ? t(
                                          `diagnostics.unitStates.${source.state}`,
                                      )
                                    : t('currentValues.notScheduled')}
                            </dd>
                        </div>
                        <div>
                            <dt>{t('currentValues.lastReceived')}</dt>
                            <dd>{dateText(source.lastReceivedAt)}</dd>
                        </div>
                        <div>
                            <dt>{t('currentValues.lastError')}</dt>
                            <dd>
                                {source.reads?.lastError ?? '—'}{' '}
                                {dateText(source.reads?.lastFailureAt)}
                            </dd>
                        </div>
                    </dl>
                    {source.kind === 'input.mqtt' && (
                        <p>{t('currentValues.pushNotice')}</p>
                    )}
                    <PointValues
                        key={`${source.key}/${revision}`}
                        deviceKey={device.key}
                        inputKey={source.key}
                        revision={revision}
                    />
                </>
            )}
        </>
    )
}

function PointValues({
    deviceKey,
    inputKey,
    revision,
}: {
    deviceKey: string
    inputKey: string
    revision: string
}) {
    const { t } = useTranslation()
    const [pages, setPages] = useState<(string | undefined)[]>([undefined])
    const after = pages.at(-1)
    const query = useQuery({
        queryKey: [
            'runtime-current-values',
            revision,
            deviceKey,
            inputKey,
            after,
        ],
        queryFn: ({ signal }) =>
            diagnostics.values(deviceKey, inputKey, revision, after, signal),
        refetchInterval: 2000,
        gcTime: 0,
    })
    return (
        <DiagnosticPanel title={t('currentValues.points')} query={query}>
            <p className="text-sm text-muted-foreground">
                {t('currentValues.expiryNotice')}
            </p>
            <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                    <thead>
                        <tr>
                            {[
                                'field',
                                'value',
                                'unit',
                                'quality',
                                'collectedAt',
                                'receivedAt',
                            ].map((key) => (
                                <th className="p-2" key={key}>
                                    {t(`currentValues.${key}`)}
                                </th>
                            ))}
                        </tr>
                    </thead>
                    <tbody>
                        {query.data?.items.map((item) => (
                            <tr
                                key={item.key}
                                className={`border-t align-top ${item.value.isStale ? 'text-muted-foreground' : ''}`}
                                data-stale={item.value.isStale}
                            >
                                <td className="p-2">
                                    {item.key}
                                    {item.name && <p>{item.name}</p>}
                                </td>
                                <td className="max-w-md p-2 break-words whitespace-pre-wrap">
                                    <span>{item.value.text ?? '—'}</span>
                                    {item.value.type && (
                                        <p className="text-xs">
                                            {item.value.type}
                                        </p>
                                    )}
                                    {item.value.isStale && (
                                        <p>{t('currentValues.stale')}</p>
                                    )}
                                </td>
                                <td className="p-2">{item.unit ?? '—'}</td>
                                <td className="p-2">
                                    {t(
                                        `currentValues.qualityNames.${item.value.quality}`,
                                        { defaultValue: item.value.quality },
                                    )}
                                    {item.value.reason && (
                                        <p>
                                            {t(
                                                `currentValues.reasons.${item.value.reason}`,
                                                {
                                                    defaultValue:
                                                        item.value.reason,
                                                },
                                            )}
                                        </p>
                                    )}
                                </td>
                                <td className="p-2">
                                    {dateText(item.value.collectedAt)}
                                    {item.value.timeSource && (
                                        <p>{item.value.timeSource}</p>
                                    )}
                                </td>
                                <td className="p-2">
                                    {dateText(item.value.receivedAt)}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
            {query.data?.items.length === 0 && (
                <p>{t('currentValues.noFields')}</p>
            )}
            <div className="flex flex-wrap items-center gap-3">
                <Button
                    variant="outline"
                    disabled={pages.length === 1 || query.isFetching}
                    onClick={() => setPages((current) => current.slice(0, -1))}
                >
                    {t('currentValues.previous')}
                </Button>
                <span>
                    {t('currentValues.page', {
                        page: pages.length,
                        total: query.data?.total ?? '—',
                    })}
                </span>
                <Button
                    variant="outline"
                    disabled={
                        !query.data?.nextKey ||
                        query.isFetching ||
                        !!query.error
                    }
                    onClick={() =>
                        setPages((current) => [
                            ...current,
                            query.data!.nextKey!,
                        ])
                    }
                >
                    {t('currentValues.next')}
                </Button>
            </div>
        </DiagnosticPanel>
    )
}
