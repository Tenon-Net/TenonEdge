import { useSession } from '../../lib/session'
import { OutputControls } from '../deliveries/controls'
import { ClearPending } from '../deliveries/clear-pending'
import { LossCounters } from './loss-counters'
import { useState } from 'react'
import { Link } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { diagnostics, useRuntimeOutputs } from './queries'
import { DiagnosticPanel, Fact, dateText } from './view'
import type { RuntimeOutputInfo, RuntimeOutputPage } from './api'

const states = [
    'normal',
    'backoff',
    'automaticallyPaused',
    'manuallyPaused',
    'notRunning',
]
const usageKeys = ['pendingCount', 'failedCount', 'quarantinedCount'] as const
const secondsText = (value: number | string | null | undefined) =>
    value == null
        ? null
        : Number(value).toLocaleString('zh-CN', { maximumFractionDigits: 1 })

export function OutputSummary() {
    const { t } = useTranslation()
    const query = useRuntimeOutputs()
    return (
        <DiagnosticPanel title={t('outputs.summary')} query={query}>
            {query.data && (
                <>
                    <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                        {states.map((state) => (
                            <Fact
                                key={state}
                                label={t(`outputs.states.${state}`)}
                                value={query.data.activeStates[state] ?? 0}
                            />
                        ))}
                        {usageKeys.map((key) => (
                            <Fact
                                key={key}
                                label={t(`outputs.${key}`)}
                                value={query.data.usage[key]}
                            />
                        ))}
                    </dl>
                    <p className="text-sm text-muted-foreground">
                        {t('outputs.stateNotice')}
                    </p>
                    <QueuePolicies counters={query.data.counters} />
                </>
            )}
            <Link className="underline" to="/runtime/outputs">
                {t('outputs.title')}
            </Link>
        </DiagnosticPanel>
    )
}

export function RuntimeOutputsPage() {
    const { t } = useTranslation()
    const [cursor, setCursor] = useState<{ after: string; revision: string }>()
    const [selected, select] = useState<string>()
    const query = useRuntimeOutputs(cursor?.after, cursor?.revision)
    const page = query.data
    const revision = String(page?.revision ?? 0)
    return (
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold">{t('outputs.title')}</h1>
            <p className="text-sm text-muted-foreground">
                {t('outputs.notice')}
            </p>
            <DiagnosticPanel title={t('outputs.summary')} query={query}>
                <p>
                    {t('outputs.revision', { revision: page?.revision ?? '—' })}
                </p>
                {page && (
                    <>
                        {!page.metadataAvailable && (
                            <p role="alert">{t('outputs.missingMetadata')}</p>
                        )}
                        {page.storageProtection.rejectNewRecords && (
                            <p role="alert">
                                {t(
                                    page.storageProtection.spaceUnknown
                                        ? 'outputs.diskUnknown'
                                        : 'outputs.protection',
                                )}
                            </p>
                        )}
                        <dl className="grid gap-4 sm:grid-cols-3">
                            {usageKeys.map((key) => (
                                <Fact
                                    key={key}
                                    label={t(`outputs.${key}`)}
                                    value={page.usage[key]}
                                />
                            ))}
                        </dl>
                        <p className="text-sm text-muted-foreground">
                            {t('outputs.stateNotice')}
                        </p>
                        <div className="overflow-x-auto">
                            <table className="w-full text-left text-sm">
                                <thead>
                                    <tr>
                                        {[
                                            'target',
                                            'state',
                                            ...usageKeys,
                                            'capacity',
                                        ].map((key) => (
                                            <th className="p-2" key={key}>
                                                {t(`outputs.${key}`)}
                                            </th>
                                        ))}
                                        <th />
                                    </tr>
                                </thead>
                                <tbody>
                                    {page.items.map((item) => (
                                        <tr
                                            className="border-t"
                                            key={item.connectionId}
                                        >
                                            <td className="p-2">
                                                {item.name ??
                                                    t('outputs.noName')}
                                                <small className="block">
                                                    {item.key ??
                                                        item.connectionId}{' '}
                                                    · {item.kind ?? '—'}
                                                </small>
                                                {!item.active && (
                                                    <small className="block">
                                                        {t('outputs.orphan')}
                                                    </small>
                                                )}
                                            </td>
                                            <td className="p-2">
                                                {t(
                                                    `outputs.states.${item.state}`,
                                                    {
                                                        defaultValue:
                                                            item.state,
                                                    },
                                                )}
                                            </td>
                                            {usageKeys.map((key) => (
                                                <td className="p-2" key={key}>
                                                    {item.queue.usage[key]}
                                                </td>
                                            ))}
                                            <td className="max-w-xs p-2">
                                                <CapacityBrief
                                                    capacity={item.capacity}
                                                />
                                            </td>
                                            <td className="p-2">
                                                <Button
                                                    variant="outline"
                                                    onClick={() =>
                                                        select(
                                                            String(
                                                                item.connectionId,
                                                            ),
                                                        )
                                                    }
                                                >
                                                    {t('outputs.view', {
                                                        id: item.connectionId,
                                                    })}
                                                </Button>
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                        {page.items.length === 0 && <p>{t('outputs.empty')}</p>}
                    </>
                )}
                <div className="flex gap-3">
                    <Button
                        variant="outline"
                        disabled={!cursor}
                        onClick={() => setCursor(undefined)}
                    >
                        {t('outputs.first')}
                    </Button>
                    <Button
                        variant="outline"
                        disabled={
                            !page?.nextConnectionId ||
                            !!query.error ||
                            query.isFetching
                        }
                        onClick={() => {
                            if (page?.nextConnectionId)
                                setCursor({
                                    after: String(page.nextConnectionId),
                                    revision,
                                })
                        }}
                    >
                        {t('outputs.next')}
                    </Button>
                </div>
                {page && (
                    <>
                        <QueuePolicies counters={page.counters} />
                        <h3 className="font-semibold">{t('outputs.files')}</h3>
                        <dl className="grid gap-4 sm:grid-cols-3">
                            {(
                                [
                                    'databaseBytes',
                                    'walBytes',
                                    'sharedMemoryBytes',
                                ] as const
                            ).map((key) => (
                                <Fact
                                    key={key}
                                    label={t(`outputs.${key}`)}
                                    value={page.files?.[key]}
                                />
                            ))}
                        </dl>
                        <p className="text-sm text-muted-foreground">
                            {t('outputs.fileNotice')}
                        </p>
                    </>
                )}
            </DiagnosticPanel>
            <LossCounters />
            {selected && page && (
                <OutputDetail
                    key={`${revision}/${selected}`}
                    id={selected}
                    revision={revision}
                />
            )}
        </div>
    )
}
function CapacityBrief({
    capacity,
}: {
    capacity: RuntimeOutputInfo['capacity']
}) {
    const { t } = useTranslation()
    const complete = capacity.seconds != null
    const seconds = secondsText(
        complete ? capacity.seconds : capacity.quotaUpperBoundSeconds,
    )
    const kind = complete
        ? capacity.limitingConstraint
        : capacity.quotaLimitingConstraint
    return (
        <div>
            <p>{t(complete ? 'outputs.complete' : 'outputs.quotaBound')}</p>
            <p>
                {seconds == null
                    ? t('outputs.unknown')
                    : t('outputs.seconds', { value: seconds })}
                {kind && ` · ${t(`outputs.kinds.${kind}`)}`}
            </p>
        </div>
    )
}
function QueuePolicies({
    counters,
}: {
    counters: RuntimeOutputPage['counters']
}) {
    const { t } = useTranslation()
    return (
        <section className="space-y-3" aria-label={t('outputs.policies')}>
            <h3 className="font-semibold">{t('outputs.policies')}</h3>
            <p className="text-sm text-muted-foreground">
                {t('outputs.counterNotice')}
            </p>
            <dl className="grid gap-4 sm:grid-cols-3">
                {Object.entries(counters).map(([key, value]) => (
                    <Fact
                        key={key}
                        label={t(`outputs.${key}`, { defaultValue: key })}
                        value={value}
                    />
                ))}
            </dl>
        </section>
    )
}
function OutputDetail({ id, revision }: { id: string; revision: string }) {
    const administrator = useSession((s) => s.value?.role === 'administrator')
    const { t } = useTranslation()
    const query = useQuery({
        queryKey: ['runtime-output', id, revision],
        queryFn: ({ signal }) => diagnostics.output(id, revision, signal),
        refetchInterval: 2000,
        gcTime: 0,
    })
    const item = query.data?.items[0]
    const seconds = (value: number | string | null | undefined) =>
        value == null
            ? '—'
            : t('outputs.seconds', { value: secondsText(value) })
    return (
        <DiagnosticPanel title={t('outputs.detail')} query={query}>
            {item && (
                <>
                    <h3 className="text-lg font-semibold">
                        {item.name ?? item.connectionId} ·{' '}
                        {t(`outputs.states.${item.state}`)}
                    </h3>
                    {administrator && (
                        <Button variant="outline" asChild>
                            <Link
                                to="/deliveries/migrate"
                                search={{ connectionId: id }}
                            >
                                {t('migration.open')}
                            </Link>
                        </Button>
                    )}
                    <OutputControls
                        id={id}
                        name={item.name ?? id}
                        revision={revision}
                        state={item.state}
                        backoffVersion={item.sender?.backoffVersion}
                        unavailable={query.isFetching || query.isError}
                        refresh={async () => {
                            const result = await query.refetch()
                            if (result.error) throw result.error
                        }}
                    />
                    {item.active && (
                        <ClearPending
                            key={`${id}/${revision}`}
                            id={id}
                            name={item.name ?? id}
                            revision={revision}
                            count={String(item.queue.usage.pendingCount)}
                            unavailable={query.isFetching || query.isError}
                            refresh={async () => {
                                const result = await query.refetch()
                                if (result.error) throw result.error
                            }}
                        />
                    )}
                    <dl className="grid gap-4 sm:grid-cols-3">
                        <Fact
                            label={t('outputs.lastResult')}
                            value={
                                item.sender?.lastResult
                                    ? t(
                                          `outputs.results.${item.sender.lastResult}`,
                                      )
                                    : '—'
                            }
                        />
                        <Fact
                            label={t('outputs.lastAt')}
                            value={dateText(item.sender?.lastResultAt)}
                        />
                        <Fact
                            label={t('outputs.error')}
                            value={item.sender?.lastError}
                        />
                        <Fact
                            label={t('outputs.pause')}
                            value={
                                item.sender?.pauseReason ??
                                (item.state === 'manuallyPaused'
                                    ? t('outputs.states.manuallyPaused')
                                    : null)
                            }
                        />
                        <Fact
                            label={t('outputs.retryAt')}
                            value={dateText(item.sender?.retryAt)}
                        />
                        <Fact
                            label={t('outputs.headWait')}
                            value={seconds(item.headWaitingSeconds)}
                        />
                        <Fact
                            label={t('outputs.headBlock')}
                            value={seconds(item.headBlockedSeconds)}
                        />
                        {Object.entries(item.queue.usage).map(
                            ([key, value]) => (
                                <Fact
                                    key={key}
                                    label={t(`outputs.${key}`, {
                                        defaultValue: key,
                                    })}
                                    value={value}
                                />
                            ),
                        )}
                    </dl>
                    <p className="text-sm text-muted-foreground">
                        {t('outputs.headNotice')}
                    </p>
                    <section
                        aria-label={t('outputs.capacity')}
                        className="space-y-3"
                    >
                        <h3 className="font-semibold">
                            {t('outputs.capacity')}
                        </h3>
                        <CapacityBrief capacity={item.capacity} />
                        <p className="text-sm text-muted-foreground">
                            {t('outputs.capacityNotice')}
                        </p>
                        <dl className="grid gap-4 sm:grid-cols-3">
                            {(
                                [
                                    'recordsPerSecond',
                                    'bytesPerSecond',
                                    'windowSeconds',
                                ] as const
                            ).map((key, i) => (
                                <Fact
                                    key={key}
                                    label={t(
                                        `outputs.${['rate', 'byteRate', 'window'][i]}`,
                                    )}
                                    value={
                                        secondsText(item.traffic[key]) ??
                                        t('outputs.unknown')
                                    }
                                />
                            ))}
                        </dl>
                        <table className="w-full text-left text-sm">
                            <thead>
                                <tr>
                                    <th className="p-2">
                                        {t('outputs.constraint')}
                                    </th>
                                    <th className="p-2">
                                        {t('outputs.duration')}
                                    </th>
                                </tr>
                            </thead>
                            <tbody>
                                {item.capacity.constraints.map((constraint) => (
                                    <tr
                                        key={constraint.kind}
                                        className="border-t"
                                    >
                                        <td className="p-2">
                                            {t(
                                                `outputs.kinds.${constraint.kind}`,
                                            )}
                                        </td>
                                        <td className="p-2">
                                            {constraint.seconds == null
                                                ? t(
                                                      `outputs.statuses.${constraint.status}`,
                                                  )
                                                : `${seconds(constraint.seconds)} · ${t(`outputs.statuses.${constraint.status}`)}`}
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </section>
                    <Link
                        className="underline"
                        to="/deliveries"
                        search={{ connectionId: id }}
                    >
                        {t('deliveries.inspectTarget')}
                    </Link>
                    <QueuePolicies counters={item.queue.counters} />
                    <OutputTrend traffic={item.traffic} />
                </>
            )}
        </DiagnosticPanel>
    )
}
function OutputTrend({ traffic }: { traffic: RuntimeOutputInfo['traffic'] }) {
    const { t } = useTranslation()
    const keys = ['generated', 'sent', 'sendFailures'] as const
    const points = traffic.points
    const elapsed = points.map((point, index) =>
        points
            .slice(0, index + 1)
            .reduce((sum, part) => sum + Number(part.durationSeconds), 0),
    )
    const span = Math.max(1, elapsed.at(-1) ?? 1)
    const peak = Math.max(
        1,
        ...points.flatMap((point) =>
            keys.map(
                (key) => Number(point[key]) / Number(point.durationSeconds),
            ),
        ),
    )
    return (
        <section className="space-y-3" aria-label={t('outputs.trend')}>
            <h3 className="font-semibold">{t('outputs.trend')}</h3>
            <p className="text-sm text-muted-foreground">
                {t('outputs.trendNotice')}
            </p>
            {points.length === 0 ? (
                <p>{t('outputs.noTrend')}</p>
            ) : (
                <>
                    <div className="flex gap-5 text-sm">
                        {keys.map((key, i) => (
                            <span
                                key={key}
                                style={{
                                    color: ['#2563eb', '#15803d', '#dc2626'][i],
                                }}
                            >
                                {t(`outputs.${key}`)}
                            </span>
                        ))}
                    </div>
                    <svg
                        viewBox="0 0 800 200"
                        role="img"
                        aria-label={t('outputs.trend')}
                        className="w-full rounded border bg-background"
                    >
                        {keys.map((key, i) => (
                            <g key={key}>
                                <polyline
                                    fill="none"
                                    stroke={
                                        ['#2563eb', '#15803d', '#dc2626'][i]
                                    }
                                    strokeWidth="2"
                                    points={points
                                        .map(
                                            (point, index) =>
                                                `${20 + (760 * elapsed[index]) / span},${180 - (160 * Number(point[key])) / Number(point.durationSeconds) / peak}`,
                                        )
                                        .join(' ')}
                                />
                                <circle
                                    cx="780"
                                    cy={
                                        180 -
                                        (160 *
                                            Number(
                                                points[points.length - 1][key],
                                            )) /
                                            Number(
                                                points[points.length - 1]
                                                    .durationSeconds,
                                            ) /
                                            peak
                                    }
                                    r="3"
                                    fill={['#2563eb', '#15803d', '#dc2626'][i]}
                                />
                            </g>
                        ))}
                    </svg>
                    <details>
                        <summary className="cursor-pointer">
                            {t('diagnostics.trendDetails')}
                        </summary>
                        <div className="max-h-80 overflow-auto">
                            <table className="w-full text-left text-sm">
                                <thead>
                                    <tr>
                                        {['at', 'durationSeconds', ...keys].map(
                                            (key) => (
                                                <th className="p-2" key={key}>
                                                    {t(`outputs.${key}`)}
                                                </th>
                                            ),
                                        )}
                                    </tr>
                                </thead>
                                <tbody>
                                    {points.map((point, index) => (
                                        <tr key={index} className="border-t">
                                            <td className="p-2">
                                                {dateText(point.at)}
                                            </td>
                                            <td className="p-2">
                                                {point.durationSeconds}
                                            </td>
                                            {keys.map((key) => (
                                                <td className="p-2" key={key}>
                                                    {point[key]}
                                                </td>
                                            ))}
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    </details>
                </>
            )}
        </section>
    )
}
