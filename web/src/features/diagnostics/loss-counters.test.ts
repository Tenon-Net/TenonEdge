import { expect, it } from 'vitest'
import { classifyCounters } from './model'

it('separates units and categories without double-counting path aliases or labeling retries as lost', () => {
    const rows = classifyCounters({
        'path/meter/main/1/dataRejected': '9',
        'route/meter/main/output.http/send/dataRejected': '9',
        'path/meter/main/1/requiredFieldFailures': '3',
        'input/meter/main/readsFailed': '5',
        'input/meter/main/readErrors': '2',
        'input/meter/main/dataRejected': '3',
        'target/1/quotaRejectedRecords': '4',
        globalQuotaRejectedRecords: '6',
        storageFailedRecordAttempts: '9223372036854775807',
        storageLostDeliveryRecords: '2',
    })
    expect(rows).toHaveLength(7)
    expect(
        rows.filter((row) => row.category === 'policy').map((row) => row.value),
    ).toEqual(['4', '6'])
    expect(
        rows.filter((row) => row.category === 'data').map((row) => row.value),
    ).toEqual(['9', '3'])
    expect(
        rows.find((row) => row.key === 'storageFailedRecordAttempts')?.value,
    ).toBe('9223372036854775807')
    expect(rows.some((row) => row.key.startsWith('route/'))).toBe(false)
})

it('shows durable global manual cleanup as policy loss without double-counting per-target breakdown', () => {
    const rows = classifyCounters({
        manuallyClearedFailedRecords: '9223372036854775807',
        manuallyClearedQuarantinedRecords: '3',
        'target/11/manuallyClearedFailedRecords': '9',
        'target/11/manuallyClearedQuarantinedRecords': '3',
        acknowledgedRecords: '7',
    })
    expect(rows).toEqual([
        {
            key: 'manuallyClearedFailedRecords',
            value: '9223372036854775807',
            category: 'policy',
            label: 'lossManualFailed',
        },
        {
            key: 'manuallyClearedQuarantinedRecords',
            value: '3',
            category: 'policy',
            label: 'lossManualQuarantined',
        },
    ])
})
