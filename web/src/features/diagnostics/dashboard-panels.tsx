import type { ReactNode } from 'react'
import { Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { ArrowUpRight, Check, CircleHelp } from 'lucide-react'
import { Card } from '../../components/ui/card'
import { ErrorMessage } from '../auth/error-message'
import type { EventPage, RuntimeDeviceList, RuntimeOutputPage } from './api'
import {
    bytesText,
    countText,
    dashboardDeviceCounts,
    queuePressure,
} from './dashboard-model'
import { dateText } from './view'

type DashboardQuery<T> = { data?: T; error: unknown; isPending: boolean }
export function DashboardPanel({
    title,
    children,
    className = '',
}: {
    title: string
    children: ReactNode
    className?: string
}) {
    return (
        <section aria-label={title} className={`min-w-0 ${className}`}>
            <Card className="h-full gap-0 p-5 shadow-none">{children}</Card>
        </section>
    )
}
export function DashboardQueryNotice({
    query,
}: {
    query: { error: unknown; isPending: boolean }
}) {
    const { t } = useTranslation()
    return (
        <>
            <ErrorMessage error={query.error} />
            {query.isPending && (
                <p className="py-3 text-sm text-muted-foreground" role="status">
                    {t('loading')}
                </p>
            )}
        </>
    )
}
const linkStyle =
    'inline-flex w-fit items-center gap-1 whitespace-nowrap rounded-sm text-xs font-medium text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-ring'
const outputStates = [
    'normal',
    'backoff',
    'automaticallyPaused',
    'manuallyPaused',
    'notRunning',
] as const

export function DeviceDistribution({
    query,
}: {
    query: DashboardQuery<RuntimeDeviceList>
}) {
    const { t } = useTranslation()
    const devicesQuery = query
    const devices = query.error ? undefined : query.data
    const groups = devices ? dashboardDeviceCounts(devices.devices) : undefined
    return (
        <DashboardPanel title={t('dashboard.distribution')}>
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="font-semibold">{t('dashboard.distribution')}</h2>
                <Link to="/runtime/devices" className={linkStyle}>
                    {t('currentValues.title')}
                    <ArrowUpRight className="size-3.5" aria-hidden="true" />
                </Link>
            </div>
            <DashboardQueryNotice query={devicesQuery} />
            <div className="mt-6 flex items-baseline gap-2">
                <span className="text-4xl font-semibold tracking-tight tabular-nums">
                    {devices ? countText(devices.devices.length) : '—'}
                </span>
                <span className="text-xs text-muted-foreground">
                    {t('dashboard.totalDevices')}
                </span>
            </div>
            <div
                className="mt-4 flex h-2.5 overflow-hidden rounded-full bg-muted"
                aria-hidden="true"
            >
                {groups?.map((group) => (
                    <span
                        key={group.key}
                        style={{
                            width: `${devices?.devices.length ? (100 * group.count) / devices.devices.length : 0}%`,
                            background: `var(--dashboard-${group.key})`,
                        }}
                    />
                ))}
            </div>
            <dl className="mt-5 space-y-3 text-sm">
                {(groups ?? dashboardDeviceCounts([])).map((group) => (
                    <div
                        className="flex items-center justify-between gap-3"
                        key={group.key}
                    >
                        <dt className="flex items-center gap-2 text-muted-foreground">
                            <span
                                className="size-2 rounded-sm"
                                style={{
                                    background: `var(--dashboard-${group.key})`,
                                }}
                            />
                            {t(`dashboard.groups.${group.key}`)}
                        </dt>
                        <dd className="font-medium tabular-nums">
                            {groups ? countText(group.count) : '—'}
                        </dd>
                    </div>
                ))}
            </dl>
            <p className="mt-auto pt-5 text-[11px] leading-relaxed text-muted-foreground">
                {devices?.devices.length === 0
                    ? t('dashboard.noDevices')
                    : t('dashboard.deviceNotice')}
            </p>
        </DashboardPanel>
    )
}

export function QueuePressure({
    query,
}: {
    query: DashboardQuery<RuntimeOutputPage>
}) {
    const { t } = useTranslation()
    const outputsQuery = query
    const outputs = query.error ? undefined : query.data
    const pressure = outputs
        ? queuePressure(
              outputs.usage.pendingBytes,
              outputs.usage.quarantinedBytes,
              outputs.limits.globalBytes,
          )
        : undefined
    return (
        <DashboardPanel title={t('dashboard.queue')}>
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="font-semibold">{t('dashboard.queue')}</h2>
                <Link to="/runtime/outputs" className={linkStyle}>
                    {t('outputs.title')}
                    <ArrowUpRight className="size-3.5" aria-hidden="true" />
                </Link>
            </div>
            <DashboardQueryNotice query={outputsQuery} />
            {outputs && !outputs.metadataAvailable && (
                <p className="mt-3 text-xs text-muted-foreground">
                    {t('outputs.missingMetadata')}
                </p>
            )}
            <div className="mt-5 flex items-center justify-between gap-3 text-sm">
                <span className="text-muted-foreground">
                    {t('dashboard.quota')}
                </span>
                <strong className="font-medium tabular-nums">
                    {pressure?.percent === undefined
                        ? '—'
                        : `${pressure.percent}%`}
                </strong>
            </div>
            <div
                className="mt-3 h-2 overflow-hidden rounded-full bg-muted"
                role={pressure?.percent === undefined ? 'progressbar' : 'meter'}
                aria-label={t('dashboard.quota')}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={
                    pressure?.percent === undefined
                        ? undefined
                        : Math.min(100, pressure.percent)
                }
                aria-valuetext={
                    pressure?.percent === undefined
                        ? t('diagnostics.unknown')
                        : `${pressure.percent}%`
                }
            >
                <div
                    className="h-full rounded-full"
                    style={{
                        width: `${Math.min(100, pressure?.percent ?? 0)}%`,
                        background:
                            pressure && (pressure.percent ?? 0) >= 80
                                ? 'var(--dashboard-attention)'
                                : 'var(--dashboard-received)',
                    }}
                />
            </div>
            <p
                className="mt-2 text-xs tabular-nums text-muted-foreground"
                title={
                    outputs && pressure
                        ? `${pressure.used} / ${outputs.limits.globalBytes} B`
                        : undefined
                }
            >
                {t('dashboard.quotaValue', {
                    used: bytesText(pressure?.used),
                    limit: bytesText(outputs?.limits.globalBytes),
                })}
            </p>
            <dl className="mt-5 grid grid-cols-3 gap-3 border-y py-4">
                {(
                    ['pendingCount', 'failedCount', 'quarantinedCount'] as const
                ).map((key) => (
                    <div key={key} className="min-w-0">
                        <dt className="text-xs text-muted-foreground">
                            {t(`outputs.${key}`)}
                        </dt>
                        <dd className="mt-2 text-lg font-semibold break-words tabular-nums">
                            {countText(outputs?.usage[key])}
                        </dd>
                    </div>
                ))}
            </dl>
            <div className="mt-4 flex flex-wrap gap-x-4 gap-y-2 text-xs">
                <span className="text-muted-foreground">
                    {t('dashboard.outputStates')}
                </span>
                {outputStates.map((state) => (
                    <span key={state}>
                        {t(`dashboard.stateShort.${state}`)}{' '}
                        <strong className="font-medium tabular-nums">
                            {countText(
                                outputs?.metadataAvailable
                                    ? (outputs.activeStates[state] ?? 0)
                                    : undefined,
                            )}
                        </strong>
                    </span>
                ))}
            </div>
            <p className="mt-4 text-[11px] leading-relaxed text-muted-foreground">
                {t('dashboard.quotaHint')}
            </p>
            <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
                {t('dashboard.outputNotice')}
            </p>
        </DashboardPanel>
    )
}

export function RecentExceptions({
    query,
}: {
    query: DashboardQuery<EventPage['items']>
}) {
    const { t } = useTranslation()
    const events = query
    const exceptions = query.error ? undefined : query.data
    return (
        <DashboardPanel title={t('dashboard.exceptions')}>
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="font-semibold">{t('dashboard.exceptions')}</h2>
                <Link to="/diagnostics" className={linkStyle}>
                    {t('diagnostics.events')}
                    <ArrowUpRight className="size-3.5" aria-hidden="true" />
                </Link>
            </div>
            <p
                className="mt-2 text-[11px] text-muted-foreground"
                title={t('dashboard.exceptionScope')}
            >
                {t('dashboard.exceptionHint')}
            </p>
            <DashboardQueryNotice query={events} />
            {exceptions?.length === 0 && (
                <div className="flex min-h-48 flex-1 flex-col items-center justify-center gap-2 py-8">
                    <Check
                        className="mb-1 size-6 text-muted-foreground"
                        aria-hidden="true"
                    />
                    <p className="text-sm font-medium">
                        {t('dashboard.noExceptions')}
                    </p>
                    <p className="text-xs text-muted-foreground">
                        {t('dashboard.noExceptionsHint')}
                    </p>
                </div>
            )}
            {exceptions && exceptions.length > 0 && (
                <ol className="mt-2 divide-y">
                    {exceptions.map((item) => (
                        <li
                            className="flex min-w-0 gap-3 py-3"
                            key={String(item.id)}
                        >
                            <span
                                className="mt-1 size-1.5 shrink-0 rounded-full"
                                style={{
                                    background:
                                        item.level === 'error'
                                            ? 'var(--destructive)'
                                            : 'var(--dashboard-attention)',
                                }}
                            />
                            <div className="min-w-0 flex-1">
                                <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
                                    <span className="text-xs font-medium">
                                        {t(`diagnostics.levels.${item.level}`)}{' '}
                                        · {item.type}
                                    </span>
                                    <time
                                        className="text-[11px] whitespace-nowrap text-muted-foreground"
                                        dateTime={item.lastOccurredAt}
                                        title={dateText(item.lastOccurredAt)}
                                    >
                                        {dateText(item.lastOccurredAt)}
                                    </time>
                                </div>
                                <p
                                    className="mt-1 line-clamp-2 text-xs leading-relaxed break-words text-muted-foreground"
                                    title={item.message}
                                >
                                    {item.message}
                                </p>
                                <p className="mt-1 text-[11px] break-words text-muted-foreground">
                                    {item.target ?? '—'} ·{' '}
                                    {t('dashboard.occurrences', {
                                        count: item.occurrenceCount,
                                    })}
                                </p>
                            </div>
                        </li>
                    ))}
                </ol>
            )}
            {Boolean(events.error) && (
                <div className="flex min-h-36 items-center justify-center gap-2 text-sm text-muted-foreground">
                    <CircleHelp className="size-4" aria-hidden="true" />
                    {t('dashboard.unavailable')}
                </div>
            )}
        </DashboardPanel>
    )
}
