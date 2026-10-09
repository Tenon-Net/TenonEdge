import { useTranslation } from 'react-i18next'
import type { components } from '../../api/schema'

export function ConnectionTestSteps({
    steps,
    protocol,
}: {
    steps: components['schemas']['ConnectionTestStep'][]
    protocol: 'mqtt' | 'http'
}) {
    const { t } = useTranslation()
    return (
        <div className="overflow-x-auto">
            <table
                className="w-full text-left text-sm"
                aria-label={t(`connections.${protocol}Test.stepsTitle`)}
            >
                <thead>
                    <tr>
                        {['stage', 'status', 'elapsed', 'reason'].map(
                            (column) => (
                                <th className="border-b p-2" key={column}>
                                    {t(`connections.${protocol}Test.${column}`)}
                                </th>
                            ),
                        )}
                    </tr>
                </thead>
                <tbody>
                    {steps.map((step) => (
                        <tr key={step.stage}>
                            <th
                                scope="row"
                                className="border-b p-2 font-medium"
                            >
                                {t(
                                    `connections.${protocol}Test.stages.${step.stage}`,
                                )}
                            </th>
                            <td className="border-b p-2">
                                {t(
                                    `connections.${protocol}Test.statuses.${step.status}`,
                                )}
                            </td>
                            <td className="border-b p-2">
                                {step.elapsedMs == null
                                    ? '—'
                                    : `${Number(step.elapsedMs).toFixed(1)} ms`}
                            </td>
                            <td className="border-b p-2">
                                {step.code && step.code !== 'success'
                                    ? t(
                                          `connections.${protocol}Test.results.${step.code}`,
                                          {
                                              defaultValue: t(
                                                  `connections.${protocol}Test.failed`,
                                              ),
                                          },
                                      )
                                    : '—'}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    )
}
