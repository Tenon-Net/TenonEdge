import { useState, type ReactNode } from 'react'
import { Link } from '@tanstack/react-router'
import { useTranslation } from './dashboard-i18n'
import {
    Activity,
    ArrowDownToLine,
    ArrowUpRight,
    Database,
    RefreshCw,
    Server,
    type LucideIcon,
} from 'lucide-react'
import { Button } from '../../components/ui/button'
import { Card } from '../../components/ui/card'
import { useSession } from '../../lib/session'
import { ErrorMessage } from '../auth/error-message'
import { runtimeHealth } from './model'
import {
    useRuntimeDevices,
    useRuntimeOutputs,
    useRuntimeStatus,
    useSystemInfo,
} from './queries'
import { useDashboardExceptions, useDashboardTrends } from './dashboard-queries'
import { DashboardChart } from './dashboard-chart'
import {
    DashboardPanel,
    DashboardQueryNotice,
    DeviceDistribution,
    QueuePressure,
    RecentExceptions,
} from './dashboard-panels'
import {
    bytesText,
    countText,
    dashboardDeviceCounts,
    rateText,
} from './dashboard-model'
import { Fact, dateText } from './view'

function Metric({
    label,
    value,
    hint,
    icon: Icon,
    unit,
}: {
    label: string
    value: string
    hint: ReactNode
    icon: LucideIcon
    unit?: string
}) {
    return (
        <Card className="min-w-0 gap-0 p-5 shadow-none">
            <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
                <h2>{label}</h2>
                <Icon className="size-4 shrink-0" aria-hidden="true" />
            </div>
            <p className="mt-3 min-w-0 text-[clamp(1.4rem,2vw,2rem)] leading-tight font-semibold tracking-tight break-words tabular-nums">
                {value}
                {unit && (
                    <span className="ml-2 text-xs font-normal tracking-normal text-muted-foreground">
                        {unit}
                    </span>
                )}
            </p>
            <div className="mt-2 text-xs leading-relaxed text-muted-foreground">
                {hint}
            </div>
        </Card>
    )
}
const linkStyle =
    'inline-flex w-fit items-center gap-1 whitespace-nowrap rounded-sm text-xs font-medium text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-ring'

export function OverviewPage() {
    const { t } = useTranslation()
    const runtime = useRuntimeStatus()
    const info = useSystemInfo()
    const devicesQuery = useRuntimeDevices()
    const outputsQuery = useRuntimeOutputs()
    const trends = useDashboardTrends()
    const events = useDashboardExceptions()
    const session = useSession((state) => state.value)
    const [dismissed, dismiss] = useState(false)
    // Failed refreshes must not present cached values as current. The panels fail independently.
    const status = runtime.error ? undefined : runtime.data
    const devices = devicesQuery.error ? undefined : devicesQuery.data
    const outputs = outputsQuery.error ? undefined : outputsQuery.data
    const samples = trends.error ? undefined : trends.data
    const system = info.error ? undefined : info.data
    const configuration = status?.configuration
    const latest = samples?.at(-1)
    const groups = devices ? dashboardDeviceCounts(devices.devices) : undefined
    const queries = [runtime, info, devicesQuery, outputsQuery, trends, events]
    const health = status ? runtimeHealth(status) : undefined
    const versionDiffers =
        status &&
        configuration?.effectiveRevision != null &&
        String(configuration.effectiveRevision) !==
            String(status.runtime.activeRevision)
    return (
        <div className="min-w-0 space-y-4" data-dashboard="operations">
            <h1 className="sr-only">{t('diagnostics.overview')}</h1>
            <section aria-label={t('diagnostics.state')} className="space-y-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                    <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-2 text-sm">
                        <span className="inline-flex items-center gap-2 font-medium">
                            <span
                                className="size-2 rounded-full"
                                style={{
                                    background:
                                        health === 'active'
                                            ? 'var(--dashboard-normal)'
                                            : health === 'degraded' ||
                                                health === 'runFailed' ||
                                                health === 'blocked'
                                              ? 'var(--dashboard-attention)'
                                              : 'var(--muted-foreground)',
                                }}
                            />
                            {health
                                ? t(`diagnostics.states.${health}`)
                                : t('dashboard.stateUnknown')}
                        </span>
                        {status && (
                            <span className="border-l pl-3 text-xs text-muted-foreground">
                                {status.runtime.activeRevision === null
                                    ? t('diagnostics.noRevision')
                                    : `r${status.runtime.activeRevision}`}
                            </span>
                        )}
                        <Link className={linkStyle} to="/diagnostics">
                            {t('dashboard.details')}
                            <ArrowUpRight
                                className="size-3.5"
                                aria-hidden="true"
                            />
                        </Link>
                    </div>
                    <div className="flex flex-wrap items-center gap-3">
                        {runtime.dataUpdatedAt > 0 && !runtime.error && (
                            <span className="text-[11px] text-muted-foreground">
                                {t('dashboard.updated', {
                                    time: new Date(
                                        runtime.dataUpdatedAt,
                                    ).toLocaleTimeString('zh-CN', {
                                        hour12: false,
                                    }),
                                })}
                            </span>
                        )}
                        <Button
                            size="sm"
                            variant="outline"
                            disabled={queries.some((query) => query.isFetching)}
                            onClick={() => {
                                for (const query of queries)
                                    void query.refetch()
                            }}
                        >
                            <RefreshCw
                                className="size-3.5"
                                aria-hidden="true"
                            />
                            {t('dashboard.refresh')}
                        </Button>
                    </div>
                </div>
                <DashboardQueryNotice query={runtime} />
                {runtime.error && (
                    <p role="status" className="text-sm text-muted-foreground">
                        {t('diagnostics.statusUnknown')}
                    </p>
                )}
                {status?.coordinator.reason && (
                    <p
                        role="alert"
                        className="rounded-md border border-destructive/30 p-3 text-sm break-words text-destructive"
                    >
                        {status.coordinator.reason}
                    </p>
                )}
                {status?.queue.storageFault && (
                    <p
                        role="alert"
                        className="text-sm break-words text-destructive"
                    >
                        {t('diagnostics.queueFault')}: {status.queue.lastError}
                    </p>
                )}
                {status?.storageProtection.rejectNewRecords && (
                    <p role="alert" className="text-sm text-destructive">
                        {t('dashboard.storageProtected')}
                    </p>
                )}
                {versionDiffers && (
                    <p role="status" className="text-sm text-muted-foreground">
                        {t('dashboard.revisionMismatch')}
                    </p>
                )}
                {system?.runtimeStartBlockers.map((block) => (
                    <p
                        role="alert"
                        key={block.code}
                        className="text-sm break-words text-destructive"
                    >
                        {block.reason}
                    </p>
                ))}
                <details className="text-xs text-muted-foreground">
                    <summary className="w-fit cursor-pointer rounded-sm py-1 focus-visible:outline-2 focus-visible:outline-ring">
                        {t('dashboard.metadata')}
                    </summary>
                    {status && (
                        <dl className="mt-3 grid gap-4 rounded-lg border p-4 sm:grid-cols-2 xl:grid-cols-4">
                            <Fact
                                label={t('diagnostics.effectiveRevision')}
                                value={
                                    !configuration
                                        ? t('diagnostics.unknown')
                                        : configuration.effectiveRevision ===
                                            null
                                          ? t('diagnostics.noEffectiveRevision')
                                          : `r${configuration.effectiveRevision}`
                                }
                            />
                            <Fact
                                label={t('diagnostics.revision')}
                                value={
                                    status.runtime.activeRevision === null
                                        ? t('diagnostics.noRevision')
                                        : `r${status.runtime.activeRevision}`
                                }
                            />
                            <Fact
                                label={t('diagnostics.appliedBy')}
                                value={
                                    configuration?.effectiveRevision === null
                                        ? '—'
                                        : (configuration?.appliedBy ??
                                          t('diagnostics.unknown'))
                                }
                            />
                            <Fact
                                label={t('diagnostics.appliedAt')}
                                value={
                                    configuration?.effectiveRevision === null
                                        ? '—'
                                        : configuration?.appliedAt
                                          ? dateText(configuration.appliedAt)
                                          : t('diagnostics.unknown')
                                }
                            />
                            <Fact
                                label={t('diagnostics.clock')}
                                value={t(
                                    `diagnostics.clocks.${status.clockSyncState}`,
                                )}
                            />
                            <Fact
                                label={t('diagnostics.setupAt')}
                                value={
                                    system
                                        ? dateText(
                                              system.firstSetup?.completedAt,
                                          )
                                        : t('diagnostics.unknown')
                                }
                            />
                            <Fact
                                label={t('diagnostics.setupIp')}
                                value={
                                    system
                                        ? system.firstSetup?.sourceIp
                                        : t('diagnostics.unknown')
                                }
                            />
                            <Link className={linkStyle} to="/system">
                                {t('diagnostics.system')}
                                <ArrowUpRight
                                    className="size-3.5"
                                    aria-hidden="true"
                                />
                            </Link>
                        </dl>
                    )}
                    <p className="mt-2 leading-relaxed">
                        {t('diagnostics.versionNotice')}
                    </p>
                    <ErrorMessage error={info.error} />
                </details>
            </section>

            {status?.coordinator.state === 'idle' &&
                configuration?.effectiveRevision === null &&
                !dismissed &&
                session?.role !== 'viewer' && (
                    <section
                        aria-label={t('diagnostics.start')}
                        className="flex flex-wrap items-center justify-between gap-4 rounded-lg border bg-muted/40 p-4"
                    >
                        <div className="space-y-2">
                            <h2 className="text-sm font-semibold">
                                {t('diagnostics.start')}
                            </h2>
                            <p className="text-xs text-muted-foreground">
                                {t('diagnostics.startNotice')}
                            </p>
                            <ol className="flex flex-wrap gap-x-4 gap-y-2 text-sm">
                                {(
                                    [
                                        ['/connections', 'connection'],
                                        ['/devices', 'deviceStep'],
                                        ['/routes', 'route'],
                                        ['/configuration', 'apply'],
                                    ] as const
                                ).map(([to, key], index) => (
                                    <li key={key}>
                                        <Link
                                            to={to}
                                            className="whitespace-nowrap underline underline-offset-4"
                                        >
                                            {index + 1}.{' '}
                                            {t(`diagnostics.${key}`)}
                                        </Link>
                                    </li>
                                ))}
                            </ol>
                        </div>
                        <Button
                            size="sm"
                            variant="ghost"
                            onClick={() => dismiss(true)}
                        >
                            {t('diagnostics.dismiss')}
                        </Button>
                    </section>
                )}

            <div className="grid min-w-0 grid-cols-2 gap-3 xl:grid-cols-4">
                <Metric
                    label={t('dashboard.devices')}
                    value={devices ? countText(devices.devices.length) : '—'}
                    hint={
                        groups
                            ? t('dashboard.deviceHint', {
                                  normal: groups[0].count,
                                  attention: groups[1].count,
                              })
                            : t(
                                  devicesQuery.error
                                      ? 'dashboard.unavailable'
                                      : 'loading',
                              )
                    }
                    icon={Server}
                />
                <Metric
                    label={t('dashboard.received')}
                    value={rateText(latest?.rates[0])}
                    unit={t('dashboard.perSecond')}
                    hint={
                        latest
                            ? t('dashboard.rateHint', {
                                  seconds: Number(
                                      latest.point.durationSeconds,
                                  ).toLocaleString('zh-CN', {
                                      maximumFractionDigits: 1,
                                  }),
                              })
                            : t(
                                  trends.error
                                      ? 'dashboard.unavailable'
                                      : 'dashboard.waiting',
                              )
                    }
                    icon={ArrowDownToLine}
                />
                <Metric
                    label={t('dashboard.sent')}
                    value={rateText(latest?.rates[1])}
                    unit={t('dashboard.perSecond')}
                    hint={
                        latest
                            ? t('dashboard.rateHint', {
                                  seconds: Number(
                                      latest.point.durationSeconds,
                                  ).toLocaleString('zh-CN', {
                                      maximumFractionDigits: 1,
                                  }),
                              })
                            : t(
                                  trends.error
                                      ? 'dashboard.unavailable'
                                      : 'dashboard.waiting',
                              )
                    }
                    icon={Activity}
                />
                <Metric
                    label={t('dashboard.pending')}
                    value={countText(outputs?.usage.pendingCount)}
                    hint={
                        outputs
                            ? t('dashboard.pendingHint', {
                                  bytes: bytesText(outputs.usage.pendingBytes),
                              })
                            : t(
                                  outputsQuery.error
                                      ? 'dashboard.unavailable'
                                      : 'loading',
                              )
                    }
                    icon={Database}
                />
            </div>

            <div className="grid min-w-0 gap-4 xl:grid-cols-3">
                <DashboardPanel
                    title={t('diagnostics.trends')}
                    className="xl:col-span-2"
                >
                    <DashboardQueryNotice query={trends} />
                    {!trends.error && (
                        <DashboardChart samples={samples ?? []} />
                    )}
                </DashboardPanel>
                <DeviceDistribution query={devicesQuery} />
            </div>

            <div className="grid min-w-0 gap-4 xl:grid-cols-2">
                <QueuePressure query={outputsQuery} />
                <RecentExceptions query={events} />
            </div>
            {status && (
                <div className="flex flex-wrap justify-between gap-x-4 gap-y-2 px-1 text-[11px] text-muted-foreground">
                    <span>
                        {t('diagnostics.clock')}：
                        {t(`diagnostics.clocks.${status.clockSyncState}`)}
                    </span>
                    <span>
                        {t('diagnostics.storage')}：
                        {t(
                            status.storageProtection.rejectNewRecords
                                ? 'diagnostics.protection'
                                : 'diagnostics.protectionOk',
                        )}
                    </span>
                    <Link to="/system" className={linkStyle}>
                        {t('diagnostics.system')}
                        <ArrowUpRight className="size-3.5" aria-hidden="true" />
                    </Link>
                </div>
            )}
        </div>
    )
}
