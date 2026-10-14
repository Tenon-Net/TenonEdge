import { useTranslation } from 'react-i18next'
import { useSystemInfo } from '../diagnostics/queries'
import { DiagnosticPanel, Fact, dateText } from '../diagnostics/view'
import { OutboundPolicyPanel } from './policy'
import { GatewayNameEditor } from './name'
import { RuntimeParameterPanel } from './parameters'
export function SystemPage() {
    const { t } = useTranslation()
    const query = useSystemInfo()
    const info = query.error ? undefined : query.data
    return (
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold">
                {t('systemSettings.title')}
            </h1>
            <DiagnosticPanel title={t('systemSettings.info')} query={query}>
                {info && (
                    <>
                        <dl className="grid gap-5 sm:grid-cols-2">
                            <Fact
                                label={t('systemSettings.gateway')}
                                value={
                                    info.gatewayName ||
                                    t('systemSettings.unnamed')
                                }
                            />
                            <Fact
                                label={t('systemSettings.version')}
                                value={info.version}
                            />
                            <Fact
                                label={t('systemSettings.node')}
                                value={info.nodeId}
                            />
                            <Fact
                                label={t('systemSettings.clock')}
                                value={t(
                                    `diagnostics.clocks.${info.clockSyncState}`,
                                )}
                            />
                            <Fact
                                label={t('systemSettings.directory')}
                                value={info.dataDirectory}
                            />
                            <Fact
                                label={t('systemSettings.total')}
                                value={
                                    info.dataDirectoryDisk?.totalBytes ??
                                    t('systemSettings.diskUnknown')
                                }
                            />
                            <Fact
                                label={t('systemSettings.free')}
                                value={
                                    info.dataDirectoryDisk?.availableBytes ??
                                    t('systemSettings.diskUnknown')
                                }
                            />
                            <Fact
                                label={t('systemSettings.fingerprint')}
                                value={
                                    <code className="break-all">
                                        {
                                            info.httpsCertificate
                                                .sha256Fingerprint
                                        }
                                    </code>
                                }
                            />
                            <Fact
                                label={t('systemSettings.expires')}
                                value={dateText(info.httpsCertificate.notAfter)}
                            />
                            <Fact
                                label={t('systemSettings.schema')}
                                value={info.configStore.schemaVersion}
                            />
                            <Fact
                                label={t('systemSettings.sqlite')}
                                value={info.configStore.sqliteVersion}
                            />
                            <Fact
                                label={t('systemSettings.setupAt')}
                                value={dateText(info.firstSetup?.completedAt)}
                            />
                            <Fact
                                label={t('systemSettings.setupIp')}
                                value={info.firstSetup?.sourceIp}
                            />
                        </dl>
                        <p className="text-sm text-muted-foreground">
                            {t('systemSettings.certificateNotice')}
                        </p>
                        <details>
                            <summary className="cursor-pointer">
                                {t('systemSettings.compile')}
                            </summary>
                            <pre className="max-h-64 overflow-auto whitespace-pre-wrap text-xs">
                                {info.configStore.sqliteCompileOptions.join(
                                    '\n',
                                )}
                            </pre>
                        </details>
                        {info.runtimeStartBlockers.map((block) => (
                            <p role="alert" key={block.code}>
                                {block.reason}
                            </p>
                        ))}
                    </>
                )}
            </DiagnosticPanel>
            <GatewayNameEditor />
            <OutboundPolicyPanel />
            <RuntimeParameterPanel />
        </div>
    )
}
