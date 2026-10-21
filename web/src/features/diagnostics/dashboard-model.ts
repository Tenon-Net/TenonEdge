import type { EventPage, RuntimeDeviceList, TrendPoint } from './api'

export function dashboardSamples(points: TrendPoint[]) {
    let previous = -Infinity
    return points.map((point) => {
        const duration = Number(point.durationSeconds)
        const uptime = Number(point.uptimeSeconds)
        const at = Date.parse(point.at)
        const counts = [
            point.received,
            point.sent,
            point.readFailures,
            point.sendFailures,
        ].map(Number)
        if (
            !Number.isFinite(duration) ||
            duration <= 0 ||
            !Number.isFinite(uptime) ||
            uptime < 0 ||
            uptime <= previous ||
            !Number.isFinite(at) ||
            counts.some((count) => !Number.isFinite(count) || count < 0)
        )
            throw new Error('diagnostics-trends')
        previous = uptime
        return {
            point,
            uptime,
            duration,
            at,
            rates: [counts[0] / duration, counts[1] / duration],
        }
    })
}
export type DashboardSample = ReturnType<typeof dashboardSamples>[number]

export function chartWindow(samples: DashboardSample[], seconds: number) {
    const end = samples.at(-1)?.uptime ?? 0
    const start = Math.max(0, end - seconds)
    const visible = samples.filter((sample) => sample.uptime >= start)
    const peak = Math.max(1, ...visible.flatMap((sample) => sample.rates))
    const magnitude = 10 ** Math.floor(Math.log10(peak))
    const ceiling = [1, 2, 5, 10]
        .map((step) => step * magnitude)
        .find((step) => step >= peak)!
    return { start, end: Math.max(end, start + 1), ceiling, samples: visible }
}

/** A gap is missing data, not a zero or a continuous interpolation. Coordinates alone are approximate. */
export function chartPaths(
    window: ReturnType<typeof chartWindow>,
    width: number,
    height: number,
) {
    const x = (uptime: number) =>
        44 +
        ((width - 56) * (uptime - window.start)) / (window.end - window.start)
    const y = (rate: number) => 16 + (height - 44) * (1 - rate / window.ceiling)
    const paths = [0, 1].map((series) =>
        window.samples
            .map((sample, index) => {
                const previous = window.samples[index - 1]
                const gap =
                    !previous ||
                    sample.uptime - previous.uptime > sample.duration + 2
                return `${gap ? 'M' : 'L'}${x(sample.uptime)},${y(sample.rates[series])}`
            })
            .join(' '),
    )
    return { x, y, paths }
}

export const deviceGroups = [
    'normal',
    'attention',
    'push',
    'disabled',
    'other',
] as const
export function dashboardDeviceCounts(devices: RuntimeDeviceList['devices']) {
    return deviceGroups.map((key) => ({
        key,
        count: devices.filter((device) => {
            if (key === 'attention')
                return ['partialFailure', 'offline'].includes(device.health)
            if (key === 'other')
                return ![
                    'normal',
                    'partialFailure',
                    'offline',
                    'push',
                    'disabled',
                ].includes(device.health)
            return device.health === key
        }).length,
    }))
}

/** Never pass Int64 counters through Number before summing or calculating quota ratios. */
export function queuePressure(
    pending: number | string,
    quarantined: number | string,
    limit: number | string,
) {
    const used = BigInt(pending) + BigInt(quarantined)
    const maximum = BigInt(limit)
    return {
        used: used.toString(),
        percent:
            maximum > 0n ? Number((used * 1000n) / maximum) / 10 : undefined,
    }
}
export function countText(value: number | string | undefined) {
    return value === undefined ? '—' : BigInt(value).toLocaleString('zh-CN')
}
export function rateText(rate: number | undefined) {
    if (rate !== undefined && rate > 0 && rate < 0.01) return '<0.01'
    return rate === undefined
        ? '—'
        : rate.toLocaleString('zh-CN', {
              maximumFractionDigits: 2,
              notation: 'compact',
          })
}
export function bytesText(value: number | string | undefined) {
    if (value === undefined) return '—'
    const bytes = Number(value)
    const unit = Math.min(
        4,
        Math.max(0, Math.floor(Math.log2(Math.max(1, bytes)) / 10)),
    )
    return `${(bytes / 1024 ** unit).toLocaleString('zh-CN', { maximumFractionDigits: 1 })} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}`
}
export function recentExceptions(pages: EventPage[]) {
    return pages
        .flatMap((page) => page.items)
        .filter((item) => item.level === 'warning' || item.level === 'error')
        .sort((a, b) => {
            const time =
                Date.parse(b.lastOccurredAt) - Date.parse(a.lastOccurredAt)
            return (
                time ||
                (BigInt(b.id) > BigInt(a.id)
                    ? 1
                    : BigInt(b.id) < BigInt(a.id)
                      ? -1
                      : 0)
            )
        })
        .slice(0, 5)
}
