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
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold">{t('diagnostics.title')}</h1>
            <DeviceSummary />
            <OutputSummary />
            <DiagnosticPanel title={t('diagnostics.units')} query={query}>
                {data && (
                    <>
                        <div className="overflow-x-auto">
                            <table className="w-full text-left text-sm">
                                <thead>
                                    <tr>
                                        {[
                                            'unit',
                                            'unitState',
                                            'restartCount',
                                            'lastError',
                                            'nextRestart',
                                        ].map((key) => (
                                            <th key={key} className="p-2">
                                                {t(`diagnostics.${key}`)}
                                            </th>
                                        ))}
                                    </tr>
                                </thead>
                                <tbody>
                                    {data.runtime.units.map((unit) => (
                                        <tr
                                            key={`${unit.id.kind}/${unit.id.key}`}
                                            className="border-t align-top"
                                        >
                                            <td className="p-2">
                                                {unit.id.kind}/{unit.id.key}
                                            </td>
                                            <td className="p-2">
                                                {t(
                                                    `diagnostics.unitStates.${unit.state}`,
                                                )}
                                            </td>
                                            <td className="p-2">
                                                {unit.restartCount}
                                            </td>
                                            <td className="max-w-lg p-2 break-words">
                                                {unit.lastError ?? '—'}
                                                <small className="block">
                                                    {dateText(unit.lastErrorAt)}
                                                </small>
                                            </td>
                                            <td className="p-2">
                                                {dateText(unit.nextRestartAt)}
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                        {data.runtime.units.length === 0 && (
                            <p>{t('diagnostics.empty')}</p>
                        )}
                        <h3 className="font-semibold">
                            {t('diagnostics.reads')}
                        </h3>
                        <div className="overflow-x-auto">
                            <table className="w-full text-left text-sm">
                                <thead>
                                    <tr>
                                        {[
                                            'device',
                                            'health',
                                            'lastSuccess',
                                            'readCount',
                                            'skipped',
                                            'lastError',
                                        ].map((key) => (
                                            <th key={key} className="p-2">
                                                {t(`diagnostics.${key}`)}
                                            </th>
                                        ))}
                                    </tr>
                                </thead>
                                <tbody>
                                    {data.runtime.units.map(
                                        (unit) =>
                                            unit.reads && (
                                                <tr
                                                    key={`${unit.id.kind}/${unit.id.key}`}
                                                    className="border-t align-top"
                                                >
                                                    <td className="p-2">
                                                        {unit.reads.deviceKey} /{' '}
                                                        {unit.reads.inputKey}
                                                    </td>
                                                    <td className="p-2">
                                                        {unit.id.kind ===
                                                        'input.mqtt'
                                                            ? t(
                                                                  'diagnostics.mqtt',
                                                              )
                                                            : t(
                                                                  `diagnostics.readHealth.${unit.reads.health}`,
                                                              )}
                                                    </td>
                                                    <td className="p-2">
                                                        {dateText(
                                                            unit.reads
                                                                .lastSuccessAt,
                                                        )}
                                                    </td>
                                                    <td className="p-2">
                                                        {
                                                            unit.reads
                                                                .readsCompleted
                                                        }{' '}
                                                        /{' '}
                                                        {unit.reads.readsFailed}
                                                    </td>
                                                    <td className="p-2">
                                                        {
                                                            unit.reads
                                                                .cyclesSkipped
                                                        }
                                                    </td>
                                                    <td className="max-w-lg p-2 break-words">
                                                        {unit.reads.lastError ??
                                                            '—'}
                                                        <small className="block">
                                                            {dateText(
                                                                unit.reads
                                                                    .lastFailureAt,
                                                            )}
                                                        </small>
                                                    </td>
                                                </tr>
                                            ),
                                    )}
                                </tbody>
                            </table>
                        </div>
                    </>
                )}
            </DiagnosticPanel>
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
                                <table className="w-full text-left text-sm">
                                    <thead>
                                        <tr>
                                            <th className="p-2">
                                                {t('diagnostics.counter')}
                                            </th>
                                            <th className="p-2">
                                                {t('diagnostics.value')}
                                            </th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {Object.entries(data[key]).map(
                                            ([name, value]) => (
                                                <tr
                                                    className="border-t"
                                                    key={name}
                                                >
                                                    <td className="p-2">
                                                        {name}
                                                    </td>
                                                    <td className="p-2">
                                                        {value}
                                                    </td>
                                                </tr>
                                            ),
                                        )}
                                    </tbody>
                                </table>
                            )}
                        </div>
                    ))}
                </section>
            )}
            <RuntimeTrends />
            <RuntimeEvents />
        </div>
    )
}
