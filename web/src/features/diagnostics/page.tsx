import { PageHeading } from '../../components/layout/page'
import {
    Table,
    TableHeader,
    TableBody,
    TableRow,
    TableHead,
    TableCell,
} from '../../components/ui/table'
import { Link } from '@tanstack/react-router'
import { OutputSummary } from './outputs'
import { DeviceSummary } from './current-values'
import { useTranslation } from 'react-i18next'
import { useRuntimeStatus } from './queries'
import { DiagnosticPanel, dateText } from './view'
import { RuntimeEvents } from './events'
import { RuntimeTrends } from './trends'

export function DiagnosticsPage() {
    const { t } = useTranslation()
    const query = useRuntimeStatus()
    const data = query.data
    return (
        <div className="product-page">
            <PageHeading title={t('diagnostics.title')} />
            <Link className="underline" to="/runtime/messages">
                {t('messages.title')}
            </Link>
            <div className="product-diagnostic-grid product-detail">
                <DeviceSummary />
                <OutputSummary />
            </div>
            <DiagnosticPanel title={t('diagnostics.units')} query={query}>
                {data && (
                    <>
                        <div className="overflow-x-auto">
                            <Table className="w-full text-left text-sm">
                                <TableHeader>
                                    <TableRow>
                                        {[
                                            'unit',
                                            'unitState',
                                            'restartCount',
                                            'lastError',
                                            'nextRestart',
                                        ].map((key) => (
                                            <TableHead
                                                key={key}
                                                className="p-2"
                                            >
                                                {t(`diagnostics.${key}`)}
                                            </TableHead>
                                        ))}
                                    </TableRow>
                                </TableHeader>
                                <TableBody>
                                    {data.runtime.units.map((unit) => (
                                        <TableRow
                                            key={`${unit.id.kind}/${unit.id.key}`}
                                            className="border-t align-top"
                                        >
                                            <TableCell className="p-2">
                                                {unit.id.kind}/{unit.id.key}
                                            </TableCell>
                                            <TableCell className="p-2">
                                                {t(
                                                    `diagnostics.unitStates.${unit.state}`,
                                                )}
                                            </TableCell>
                                            <TableCell className="p-2">
                                                {unit.restartCount}
                                            </TableCell>
                                            <TableCell className="max-w-lg p-2 break-words">
                                                {unit.lastError ?? '—'}
                                                <small className="block">
                                                    {dateText(unit.lastErrorAt)}
                                                </small>
                                            </TableCell>
                                            <TableCell className="p-2">
                                                {dateText(unit.nextRestartAt)}
                                            </TableCell>
                                        </TableRow>
                                    ))}
                                </TableBody>
                            </Table>
                        </div>
                        {data.runtime.units.length === 0 && (
                            <p>{t('diagnostics.empty')}</p>
                        )}
                        <h3 className="font-semibold">
                            {t('diagnostics.reads')}
                        </h3>
                        <div className="overflow-x-auto">
                            <Table className="w-full text-left text-sm">
                                <TableHeader>
                                    <TableRow>
                                        {[
                                            'device',
                                            'health',
                                            'lastSuccess',
                                            'readCount',
                                            'skipped',
                                            'lastError',
                                        ].map((key) => (
                                            <TableHead
                                                key={key}
                                                className="p-2"
                                            >
                                                {t(`diagnostics.${key}`)}
                                            </TableHead>
                                        ))}
                                    </TableRow>
                                </TableHeader>
                                <TableBody>
                                    {data.runtime.units.map(
                                        (unit) =>
                                            unit.reads && (
                                                <TableRow
                                                    key={`${unit.id.kind}/${unit.id.key}`}
                                                    className="border-t align-top"
                                                >
                                                    <TableCell className="p-2">
                                                        {unit.reads.deviceKey} /{' '}
                                                        {unit.reads.inputKey}
                                                    </TableCell>
                                                    <TableCell className="p-2">
                                                        {unit.id.kind ===
                                                        'input.mqtt'
                                                            ? t(
                                                                  'diagnostics.mqtt',
                                                              )
                                                            : t(
                                                                  `diagnostics.readHealth.${unit.reads.health}`,
                                                              )}
                                                    </TableCell>
                                                    <TableCell className="p-2">
                                                        {dateText(
                                                            unit.reads
                                                                .lastSuccessAt,
                                                        )}
                                                    </TableCell>
                                                    <TableCell className="p-2">
                                                        {
                                                            unit.reads
                                                                .readsCompleted
                                                        }{' '}
                                                        /{' '}
                                                        {unit.reads.readsFailed}
                                                    </TableCell>
                                                    <TableCell className="p-2">
                                                        {
                                                            unit.reads
                                                                .cyclesSkipped
                                                        }
                                                    </TableCell>
                                                    <TableCell className="max-w-lg p-2 break-words">
                                                        {unit.reads.lastError ??
                                                            '—'}
                                                        <small className="block">
                                                            {dateText(
                                                                unit.reads
                                                                    .lastFailureAt,
                                                            )}
                                                        </small>
                                                    </TableCell>
                                                </TableRow>
                                            ),
                                    )}
                                </TableBody>
                            </Table>
                        </div>
                    </>
                )}
            </DiagnosticPanel>
            <RuntimeTrends />
            <RuntimeEvents />
            {data && (
                <section
                    className="space-y-4 rounded-lg border p-5"
                    aria-label={t('diagnostics.counters')}
                >
                    <h2 className="text-lg font-semibold">
                        {t('diagnostics.counters')}
                    </h2>
                    <p className="text-sm text-muted-foreground">
                        {t('diagnostics.counterNotice')}
                    </p>
                    {(
                        ['persistentCounters', 'unpersistedCounters'] as const
                    ).map((key) => (
                        <div key={key} className="space-y-2">
                            <h3 className="font-medium">
                                {t(
                                    key === 'persistentCounters'
                                        ? 'diagnostics.persistent'
                                        : 'diagnostics.unpersisted',
                                )}
                            </h3>
                            {data[key] === null ? (
                                <p role="status">
                                    {t('diagnostics.countersUnknown')}
                                </p>
                            ) : (
                                <Table className="w-full text-left text-sm">
                                    <TableHeader>
                                        <TableRow>
                                            <TableHead className="p-2">
                                                {t('diagnostics.counter')}
                                            </TableHead>
                                            <TableHead className="p-2">
                                                {t('diagnostics.value')}
                                            </TableHead>
                                        </TableRow>
                                    </TableHeader>
                                    <TableBody>
                                        {Object.entries(data[key]).map(
                                            ([name, value]) => (
                                                <TableRow
                                                    className="border-t"
                                                    key={name}
                                                >
                                                    <TableCell className="p-2">
                                                        {name}
                                                    </TableCell>
                                                    <TableCell className="p-2">
                                                        {value}
                                                    </TableCell>
                                                </TableRow>
                                            ),
                                        )}
                                    </TableBody>
                                </Table>
                            )}
                        </div>
                    ))}
                </section>
            )}
        </div>
    )
}
