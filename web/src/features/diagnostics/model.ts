import type { Diagnostics, TrendPoint } from './api'

export function runtimeHealth(status: Diagnostics) {
    if (status.coordinator.state !== 'active') return status.coordinator.state
    return status.queue.storageFault ||
        status.storageProtection.rejectNewRecords ||
        status.runtime.units.some(
            (unit) =>
                unit.state !== 'running' ||
                (unit.id.kind !== 'input.mqtt' &&
                    unit.reads &&
                    ['partialFailure', 'failing', 'offline'].includes(
                        unit.reads.health,
                    )),
        )
        ? 'degraded'
        : 'active'
}

/** SVG geometry is approximate; exact counts remain in the accessible table. Never invent missing buckets. */
export function trendGeometry(points: TrendPoint[]) {
    const samples = points.map((point) => {
        const duration = Number(point.durationSeconds)
        const values = [
            Number(point.received),
            Number(point.sent),
            Number(point.readFailures) + Number(point.sendFailures),
        ]
        const uptime = Number(point.uptimeSeconds)
        if (
            !Number.isFinite(duration) ||
            duration <= 0 ||
            !Number.isFinite(uptime) ||
            values.some((value) => !Number.isFinite(value) || value < 0)
        )
            throw new Error('diagnostics-trends')
        return { uptime, values: values.map((value) => value / duration) }
    })
    const start = samples[0]?.uptime ?? 0
    const span = Math.max(1, (samples.at(-1)?.uptime ?? start) - start)
    const peak = Math.max(1, ...samples.flatMap((sample) => sample.values))
    return {
        peak,
        lines: [0, 1, 2].map((series) =>
            samples
                .map(
                    (sample) =>
                        `${20 + (760 * (sample.uptime - start)) / span},${180 - (160 * sample.values[series]) / peak}`,
                )
                .join(' '),
        ),
    }
}

export function eventTime(value: string): string | undefined {
    if (!value) return undefined
    const parsed = new Date(value)
    if (!Number.isFinite(parsed.getTime())) throw new Error('diagnostics-time')
    return parsed.toISOString()
}

// 只选互不重复的叶子口径，不相加为丢失总数。path 是连接汇总，route 为其重复的路径细分。
export function classifyCounters(
    counters: NonNullable<Diagnostics['persistentCounters']>,
) {
    const categories: Record<
        string,
        { category: 'policy' | 'data' | 'fault'; label: string }
    > = {
        manuallyClearedPendingRecords: {
            category: 'policy',
            label: 'lossManualPending',
        },
        manuallyClearedFailedRecords: {
            category: 'policy',
            label: 'lossManualFailed',
        },
        manuallyClearedQuarantinedRecords: {
            category: 'policy',
            label: 'lossManualQuarantined',
        },
        globalQuotaRejectedRecords: {
            category: 'policy',
            label: 'lossGlobalQuota',
        },
        storageProtectionRejectedRecords: {
            category: 'policy',
            label: 'lossStorageProtection',
        },
        dataRejectedRecords: { category: 'data', label: 'lossWriterData' },
        storageLostSourceMessages: {
            category: 'fault',
            label: 'lossSourceLost',
        },
        storageLostDeliveryRecords: {
            category: 'fault',
            label: 'lossDeliveriesLost',
        },
        storageFailedBatches: { category: 'fault', label: 'lossFailedBatches' },
        storageFailedRecordAttempts: {
            category: 'fault',
            label: 'lossFailedAttempts',
        },
        storageUnavailableAttempts: {
            category: 'fault',
            label: 'lossUnavailableAttempts',
        },
    }
    return Object.entries(counters).flatMap(([key, value]) => {
        if (key.startsWith('route/')) return []
        let info = categories[key]
        if (/^target\/\d+\/quotaRejectedRecords$/.test(key))
            info = { category: 'policy', label: 'lossTargetQuota' }
        if (/^input\/.+\/overruns$/.test(key))
            info = { category: 'policy', label: 'lossOverruns' }
        if (/^(input|path)\/.+\/dataRejected$/.test(key))
            info = {
                category: 'data',
                label: key.startsWith('path/')
                    ? 'lossPathData'
                    : 'lossInputData',
            }
        if (/^input\/.+\/readErrors$/.test(key))
            info = { category: 'fault', label: 'lossReadErrors' }
        if (/^input\/.+\/handoffFailed$/.test(key))
            info = { category: 'fault', label: 'lossHandoffFailed' }
        if (
            /^connection\/\d+\/mqtt\/(bufferDropped|retainedIgnored|unmatched)$/.test(
                key,
            )
        )
            info = { category: 'policy', label: 'lossMqttPolicy' }
        if (/^connection\/\d+\/mqtt\/(oversized|wireOversized)$/.test(key))
            info = { category: 'data', label: 'lossMqttOversized' }
        return info ? [{ key, value, ...info }] : []
    })
}
