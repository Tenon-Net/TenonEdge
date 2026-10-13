import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { diagnostics } from './queries'
import { trendGeometry } from './model'
import { DiagnosticPanel, dateText } from './view'

export function RuntimeTrends() {
    const { t } = useTranslation()
    const query = useQuery({
        queryKey: ['runtime-trends'],
        queryFn: async ({ signal }) => {
            const points = await diagnostics.trends(signal)
            return { points, geometry: trendGeometry(points) }
        },
        refetchInterval: 10_000,
        gcTime: 0,
    })
    const colors = ['#2563eb', '#15803d', '#b91c1c']
    return (
        <DiagnosticPanel title={t('diagnostics.trends')} query={query}>
            <p className="text-sm text-muted-foreground">
                {t('diagnostics.trendNotice')}
            </p>
            {query.data &&
                (query.data.points.length === 0 ? (
                    <p>{t('diagnostics.empty')}</p>
                ) : (
                    <>
                        <ul className="flex flex-wrap gap-5 text-sm">
                            {['received', 'sent', 'failures'].map(
                                (key, index) => (
                                    <li
                                        key={key}
                                        style={{ color: colors[index] }}
                                    >
                                        {t(`diagnostics.${key}`)}
                                    </li>
                                ),
                            )}
                        </ul>
                        <svg
                            viewBox="0 0 800 200"
                            className="w-full"
                            role="img"
                            aria-label={t('diagnostics.trends')}
                        >
                            <title>{t('diagnostics.trendNotice')}</title>
                            <path
                                d="M20 20 V180 H780"
                                fill="none"
                                stroke="currentColor"
                                opacity="0.3"
                            />
                            {query.data.geometry.lines.map((line, index) => (
                                <g key={index}>
                                    <polyline
                                        points={line}
                                        fill="none"
                                        stroke={colors[index]}
                                        strokeWidth="2"
                                        strokeDasharray={
                                            index === 2 ? '5 3' : undefined
                                        }
                                    />
                                    {line && (
                                        <circle
                                            cx={
                                                line
                                                    .split(' ')
                                                    .at(-1)!
                                                    .split(',')[0]
                                            }
                                            cy={
                                                line
                                                    .split(' ')
                                                    .at(-1)!
                                                    .split(',')[1]
                                            }
                                            r="3"
                                            fill={colors[index]}
                                        />
                                    )}
                                </g>
                            ))}
                            <text
                                x="24"
                                y="16"
                                fill="currentColor"
                                fontSize="12"
                            >
                                {query.data.geometry.peak.toPrecision(3)}/s
                            </text>
                        </svg>
                        <details>
                            <summary className="cursor-pointer">
                                {t('diagnostics.trendDetails')}
                            </summary>
                            <div className="max-h-96 overflow-auto">
                                <table className="w-full text-left text-sm">
                                    <thead>
                                        <tr>
                                            {[
                                                'at',
                                                'duration',
                                                'received',
                                                'sent',
                                                'readFailures',
                                                'sendFailures',
                                                'rejectedPaths',
                                                'storageFailed',
                                            ].map((key) => (
                                                <th key={key} className="p-2">
                                                    {t(`diagnostics.${key}`)}
                                                </th>
                                            ))}
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {query.data.points.map((point) => (
                                            <tr
                                                key={String(
                                                    point.uptimeSeconds,
                                                )}
                                                className="border-t"
                                            >
                                                <td className="p-2 whitespace-nowrap">
                                                    {dateText(point.at)}
                                                </td>
                                                {[
                                                    point.durationSeconds,
                                                    point.received,
                                                    point.sent,
                                                    point.readFailures,
                                                    point.sendFailures,
                                                    point.rejectedPaths,
                                                    point.storageFailedRecordAttempts,
                                                ].map((value, index) => (
                                                    <td
                                                        className="p-2"
                                                        key={index}
                                                    >
                                                        {value}
                                                    </td>
                                                ))}
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        </details>
                    </>
                ))}
        </DiagnosticPanel>
    )
}
