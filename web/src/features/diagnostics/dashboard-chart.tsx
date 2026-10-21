import { useEffect, useRef, useState, type PointerEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import {
    chartPaths,
    chartWindow,
    rateText,
    type DashboardSample,
} from './dashboard-model'

const timeText = (at: number) =>
    new Date(at).toLocaleTimeString('zh-CN', {
        hour12: false,
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
    })
const colors = ['var(--dashboard-received)', 'var(--dashboard-sent)']

export function DashboardChart({ samples }: { samples: DashboardSample[] }) {
    const { t } = useTranslation()
    const ref = useRef<HTMLDivElement>(null)
    const [width, setWidth] = useState(760)
    const [period, setPeriod] = useState(3600)
    const [selected, select] = useState<number>()
    useEffect(() => {
        const observer = new ResizeObserver(([entry]) =>
            setWidth(Math.max(200, entry.contentRect.width)),
        )
        if (ref.current) observer.observe(ref.current)
        return () => observer.disconnect()
    }, [])
    const window = chartWindow(samples, period)
    const geometry = chartPaths(window, width, 232)
    const currentIndex = Math.max(
        0,
        selected === undefined
            ? window.samples.length - 1
            : window.samples.findIndex((sample) => sample.uptime >= selected),
    )
    const current = window.samples[currentIndex]
    const latest = window.samples.at(-1)
    const selectAtPointer = (event: PointerEvent<SVGSVGElement>) => {
        const bounds = event.currentTarget.getBoundingClientRect()
        const at =
            window.start +
            ((event.clientX - bounds.left - 44) / (width - 56)) *
                (window.end - window.start)
        const closest = window.samples.reduce((best, sample) =>
            Math.abs(sample.uptime - at) < Math.abs(best.uptime - at)
                ? sample
                : best,
        )
        select(closest.uptime)
    }
    return (
        <>
            <div className="flex flex-col justify-between gap-3 sm:flex-row sm:items-center">
                <div>
                    <h2 className="font-semibold">
                        {t('dashboard.throughput')}
                    </h2>
                    <p className="mt-1 text-xs text-muted-foreground">
                        {t('dashboard.trendCaption')}
                    </p>
                </div>
                <div
                    className="flex w-fit rounded-lg bg-muted p-1"
                    role="group"
                    aria-label={t('dashboard.throughput')}
                >
                    {[900, 3600].map((value) => (
                        <Button
                            key={value}
                            size="sm"
                            variant={period === value ? 'secondary' : 'ghost'}
                            aria-pressed={period === value}
                            className={
                                period === value
                                    ? 'h-7 bg-background text-xs shadow-sm'
                                    : 'h-7 text-xs text-muted-foreground'
                            }
                            onClick={() => {
                                setPeriod(value)
                                select(undefined)
                            }}
                        >
                            {t(
                                value === 900
                                    ? 'dashboard.quarter'
                                    : 'dashboard.hour',
                            )}
                        </Button>
                    ))}
                </div>
            </div>
            <div className="mt-5 flex flex-wrap gap-x-5 gap-y-2 text-xs">
                {['received', 'sent'].map((key, index) => (
                    <span key={key} className="inline-flex items-center gap-2">
                        <span
                            className="h-0.5 w-4"
                            style={{ background: colors[index] }}
                        />
                        {t(`diagnostics.${key}`)}
                    </span>
                ))}
                <span className="ml-auto text-muted-foreground">
                    {t('dashboard.perSecond')}
                </span>
            </div>
            <div ref={ref} className="mt-3 min-w-0">
                {latest ? (
                    <svg
                        viewBox={`0 0 ${width} 232`}
                        className="block h-[232px] w-full rounded-sm text-muted-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        role="img"
                        tabIndex={0}
                        aria-label={t('diagnostics.trends')}
                        aria-describedby="dashboard-chart-keys dashboard-chart-sample"
                        onPointerMove={selectAtPointer}
                        onPointerDown={selectAtPointer}
                        onPointerLeave={(event) => {
                            if (event.pointerType === 'mouse') select(undefined)
                        }}
                        onKeyDown={(event) => {
                            if (
                                ![
                                    'ArrowLeft',
                                    'ArrowRight',
                                    'Home',
                                    'End',
                                ].includes(event.key)
                            )
                                return
                            event.preventDefault()
                            const index =
                                event.key === 'Home'
                                    ? 0
                                    : event.key === 'End'
                                      ? window.samples.length - 1
                                      : Math.max(
                                            0,
                                            Math.min(
                                                window.samples.length - 1,
                                                currentIndex +
                                                    (event.key === 'ArrowRight'
                                                        ? 1
                                                        : -1),
                                            ),
                                        )
                            select(window.samples[index].uptime)
                        }}
                    >
                        <title>{t('dashboard.sampleWindow')}</title>
                        {[0, 1, 2, 3, 4].map((tick) => {
                            const rate = (window.ceiling * tick) / 4
                            const y = geometry.y(rate)
                            return (
                                <g key={tick}>
                                    <line
                                        x1="44"
                                        x2={width - 12}
                                        y1={y}
                                        y2={y}
                                        stroke="var(--border)"
                                        strokeDasharray="3 4"
                                    />
                                    <text
                                        x="34"
                                        y={y + 4}
                                        textAnchor="end"
                                        fill="currentColor"
                                        fontSize="11"
                                    >
                                        {rateText(rate)}
                                    </text>
                                </g>
                            )
                        })}
                        {[0, 0.5, 1].map((step) => (
                            <text
                                key={step}
                                x={44 + (width - 56) * step}
                                y="226"
                                textAnchor={
                                    step === 0
                                        ? 'start'
                                        : step === 1
                                          ? 'end'
                                          : 'middle'
                                }
                                fill="currentColor"
                                fontSize="11"
                            >
                                {step === 1
                                    ? t('dashboard.latestSample')
                                    : (window.end - window.start) * (1 - step) <
                                        60
                                      ? t('dashboard.secondsAgo', {
                                            count: Math.round(
                                                (window.end - window.start) *
                                                    (1 - step),
                                            ),
                                        })
                                      : t('dashboard.minutesAgo', {
                                            count: Math.round(
                                                ((window.end - window.start) *
                                                    (1 - step)) /
                                                    60,
                                            ),
                                        })}
                            </text>
                        ))}
                        {geometry.paths.map((path, index) => (
                            <path
                                key={index}
                                d={path}
                                fill="none"
                                stroke={colors[index]}
                                strokeWidth="2"
                                strokeLinejoin="round"
                                vectorEffect="non-scaling-stroke"
                            />
                        ))}
                        {current && (
                            <g>
                                <line
                                    x1={geometry.x(current.uptime)}
                                    x2={geometry.x(current.uptime)}
                                    y1="16"
                                    y2={geometry.y(0)}
                                    stroke="var(--muted-foreground)"
                                    strokeDasharray="3 4"
                                />
                                {current.rates.map((rate, index) => (
                                    <circle
                                        key={index}
                                        cx={geometry.x(current.uptime)}
                                        cy={geometry.y(rate)}
                                        r="3.5"
                                        fill={colors[index]}
                                        stroke="var(--card)"
                                        strokeWidth="2"
                                    />
                                ))}
                            </g>
                        )}
                    </svg>
                ) : (
                    <div className="flex h-[232px] items-center justify-center text-sm text-muted-foreground">
                        {t('dashboard.waiting')}
                    </div>
                )}
            </div>
            <p id="dashboard-chart-keys" className="sr-only">
                {t('dashboard.chartKeys')}
            </p>
            <div
                className="mt-3 flex min-h-5 flex-wrap items-center gap-x-4 gap-y-1 text-xs tabular-nums"
                aria-live={selected === undefined ? 'off' : 'polite'}
            >
                <span className="text-muted-foreground">
                    {current
                        ? t('dashboard.sample', { time: timeText(current.at) })
                        : '—'}
                </span>
                {current?.rates.map((rate, index) => (
                    <span key={index}>
                        {t(`diagnostics.${index === 0 ? 'received' : 'sent'}`)}{' '}
                        <strong className="font-medium">
                            {rateText(rate)}
                        </strong>{' '}
                        /s
                    </span>
                ))}
            </div>
            {current && (
                <p id="dashboard-chart-sample" className="sr-only">
                    {t('dashboard.exactSample', {
                        received: current.point.received,
                        sent: current.point.sent,
                        seconds: current.point.durationSeconds,
                    })}
                </p>
            )}
            <p className="mt-3 border-t pt-3 text-[11px] leading-relaxed text-muted-foreground">
                {t('dashboard.sampleWindow')}
            </p>
        </>
    )
}
