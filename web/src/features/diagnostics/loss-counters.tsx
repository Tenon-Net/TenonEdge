import { useTranslation } from 'react-i18next'
import { useRuntimeStatus } from './queries'
import { DiagnosticPanel } from './view'
import { classifyCounters } from './model'

export function LossCounters() {
    const { t } = useTranslation()
    const query = useRuntimeStatus()
    const rows =
        query.data?.persistentCounters == null
            ? null
            : classifyCounters(query.data.persistentCounters)
    return (
        <DiagnosticPanel title={t('outputs.lossTitle')} query={query}>
            <p className="text-sm text-muted-foreground">
                {t('outputs.lossNotice')}
            </p>
            {rows === null ? (
                <p>{t('diagnostics.countersUnknown')}</p>
            ) : (
                ['policy', 'data', 'fault'].map((category) => (
                    <section
                        key={category}
                        aria-label={t(`outputs.loss.${category}`)}
                    >
                        <h3 className="font-semibold">
                            {t(`outputs.loss.${category}`)}
                        </h3>
                        <table className="w-full text-left text-sm">
                            <tbody>
                                {rows
                                    .filter((row) => row.category === category)
                                    .map((row) => (
                                        <tr key={row.key} className="border-t">
                                            <td className="p-2">
                                                {t(`outputs.${row.label}`)}
                                                <small className="block break-all text-muted-foreground">
                                                    {row.key}
                                                </small>
                                            </td>
                                            <td className="p-2">{row.value}</td>
                                        </tr>
                                    ))}
                            </tbody>
                        </table>
                        {!rows.some((row) => row.category === category) && (
                            <p>{t('outputs.noLossCounters')}</p>
                        )}
                    </section>
                ))
            )}
        </DiagnosticPanel>
    )
}
