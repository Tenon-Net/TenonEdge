import { LossCounters } from './loss-counters'
import { OutputSummary } from './outputs'
import { DeviceSummary } from './current-values'
import { useState } from 'react'
import { Link } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { useSession } from '../../lib/session'
import { runtimeHealth } from './model'
import { useRuntimeStatus, useSystemInfo } from './queries'
import { DiagnosticPanel, Fact, dateText } from './view'

export function OverviewPage() {
    const { t } = useTranslation()
    const runtime = useRuntimeStatus()
    const info = useSystemInfo()
    const session = useSession((state) => state.value)
    const [dismissed, dismiss] = useState(false)
    const status = runtime.data
    return (
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold">
                {t('diagnostics.overview')}
            </h1>
            <p className="text-muted-foreground">{t('authenticated')}</p>
            <DeviceSummary />
            <OutputSummary />
            <LossCounters />
            <DiagnosticPanel title={t('diagnostics.state')} query={runtime}>
                {status && (
                    <dl className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
                        <Fact
                            label={t('diagnostics.state')}
                            value={t(
                                `diagnostics.states.${runtimeHealth(status)}`,
                            )}
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
                            label={t('diagnostics.clock')}
                            value={t(
                                `diagnostics.clocks.${status.clockSyncState}`,
                            )}
                        />
                        <Fact
                            label={t('diagnostics.storage')}
                            value={t(
                                status.storageProtection.rejectNewRecords
                                    ? 'diagnostics.protection'
                                    : 'diagnostics.protectionOk',
                            )}
                        />
                        <Fact
                            label={t('diagnostics.freeDisk')}
                            value={
                                status.storageProtection.space
                                    ?.availableBytes ??
                                t('diagnostics.diskUnknown')
                            }
                        />
                        <Fact
                            label={t('diagnostics.inFlight')}
                            value={`${status.inFlight.reservedBytes} / ${status.inFlight.capacityBytes}`}
                        />
                    </dl>
                )}
                {status?.coordinator.reason && (
                    <p role="alert">{status.coordinator.reason}</p>
                )}
                {status?.queue.storageFault && (
                    <p role="alert">
                        {t('diagnostics.queueFault')}: {status.queue.lastError}
                    </p>
                )}
                <Link to="/diagnostics" className="underline">
                    {t('diagnostics.title')}
                </Link>
            </DiagnosticPanel>
            {status?.coordinator.state === 'idle' &&
                !dismissed &&
                session?.role !== 'viewer' && (
                    <section
                        className="space-y-3 rounded-lg border p-5"
                        aria-label={t('diagnostics.start')}
                    >
                        <h2 className="text-lg font-semibold">
                            {t('diagnostics.start')}
                        </h2>
                        <p>{t('diagnostics.startNotice')}</p>
                        <ol className="list-inside list-decimal space-y-2">
                            <li>
                                <Link to="/connections" className="underline">
                                    {t('diagnostics.connection')}
                                </Link>
                            </li>
                            <li>
                                <Link to="/devices" className="underline">
                                    {t('diagnostics.deviceStep')}
                                </Link>
                            </li>
                            <li>
                                <Link to="/routes" className="underline">
                                    {t('diagnostics.route')}
                                </Link>
                            </li>
                            <li>
                                <Link to="/configuration" className="underline">
                                    {t('diagnostics.apply')}
                                </Link>
                            </li>
                        </ol>
                        <Button variant="outline" onClick={() => dismiss(true)}>
                            {t('diagnostics.dismiss')}
                        </Button>
                    </section>
                )}
            <DiagnosticPanel title={t('diagnostics.system')} query={info}>
                {info.data && (
                    <>
                        <dl className="grid gap-5 sm:grid-cols-2">
                            <Fact
                                label={t('gatewayName')}
                                value={info.data.gatewayName}
                            />
                            <Fact
                                label={t('diagnostics.version')}
                                value={info.data.version}
                            />
                            <Fact
                                label={t('diagnostics.node')}
                                value={info.data.nodeId}
                            />
                            <Fact
                                label={t('diagnostics.directory')}
                                value={info.data.dataDirectory}
                            />
                            <Fact
                                label={t('diagnostics.fingerprint')}
                                value={
                                    info.data.httpsCertificate.sha256Fingerprint
                                }
                            />
                            <Fact
                                label={t('diagnostics.certificateExpiry')}
                                value={dateText(
                                    info.data.httpsCertificate.notAfter,
                                )}
                            />
                        </dl>
                        <h3 className="font-semibold">
                            {t('diagnostics.firstSetup')}
                        </h3>
                        <dl className="grid gap-5 sm:grid-cols-2">
                            <Fact
                                label={t('diagnostics.setupAt')}
                                value={dateText(
                                    info.data.firstSetup?.completedAt,
                                )}
                            />
                            <Fact
                                label={t('diagnostics.setupIp')}
                                value={info.data.firstSetup?.sourceIp}
                            />
                        </dl>
                        <p className="text-sm text-muted-foreground">
                            {t('diagnostics.setupNotice')}
                        </p>
                        {info.data.runtimeStartBlockers.map((block) => (
                            <p role="alert" key={block.code}>
                                {block.reason}
                            </p>
                        ))}
                    </>
                )}
            </DiagnosticPanel>
            <p className="text-sm text-muted-foreground">
                {t('diagnostics.partialNotice')}
            </p>
        </div>
    )
}
