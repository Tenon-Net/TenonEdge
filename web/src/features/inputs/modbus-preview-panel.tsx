import { useEffect, useRef } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import type { components } from '../../api/schema'
import { api } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'

type Source = components['schemas']['ModbusSourceDraft']
export function ModbusPreviewPanel({
    deviceId,
    version,
    inputId,
    source,
    onOrder,
    blocked = false,
}: {
    deviceId: number
    version: string
    inputId?: number
    source?: () => Source
    onOrder?: (key: string, order: string) => void
    blocked?: boolean
}) {
    const { t } = useTranslation()
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const read = useMutation({
        retry: false,
        gcTime: 0,
        mutationFn: async () => {
            let current: Source | undefined
            try {
                current = source?.()
            } catch {
                throw new ApiError(400, {
                    errors: { preview: [t('inputs.invalid')] },
                })
            }
            controller.current = new AbortController()
            const { data } = await api.POST(
                '/api/edge-admin/v1/devices/{deviceId}/preview/modbus',
                {
                    params: { path: { deviceId } },
                    signal: controller.current.signal,
                    body: {
                        expectedVersion: version,
                        inputId: inputId ?? null,
                        source: current ?? null,
                    },
                },
            )
            if (!data) throw new Error('missing-preview')
            return { result: data, source: JSON.stringify(current) }
        },
    })
    let unchanged = false
    try {
        if (read.data)
            unchanged = read.data.source === JSON.stringify(source?.())
    } catch {
        /* Invalid edits cannot receive old sample choices. */
    }
    const result = read.data?.result
    return (
        <section
            className="space-y-3 rounded border p-4"
            aria-label={t(
                source
                    ? 'inputs.preview.groupTitle'
                    : 'inputs.preview.deviceTitle',
            )}
        >
            <h2 className="font-semibold">
                {t(
                    source
                        ? 'inputs.preview.groupTitle'
                        : 'inputs.preview.deviceTitle',
                )}
            </h2>
            <p className="text-sm text-muted-foreground">
                {t(
                    source
                        ? 'inputs.preview.groupNotice'
                        : 'inputs.preview.deviceNotice',
                )}
            </p>
            <Button
                type="button"
                disabled={blocked || read.isPending}
                onClick={() => read.mutate()}
            >
                {t(
                    read.isPending
                        ? 'inputs.preview.running'
                        : 'inputs.preview.run',
                )}
            </Button>
            <ErrorMessage error={read.error} />
            {result && (
                <>
                    <p role="status">
                        {t('inputs.preview.completed', {
                            count: result.groups.length,
                            total: result.totalGroups,
                        })}{' '}
                        · {Number(result.elapsedMs).toFixed(0)} ms
                    </p>
                    {(result.truncated ||
                        result.deadlineExceeded ||
                        result.groups.length !==
                            Number(result.totalGroups)) && (
                        <p role="alert">{t('inputs.preview.partial')}</p>
                    )}
                    {result.groups.map((group) => (
                        <div className="space-y-2" key={group.key}>
                            <h3 className="font-medium">{group.key}</h3>
                            <p className="text-xs">
                                UnitId {group.preview.unitId} ·{' '}
                                {group.preview.points.length}/
                                {group.preview.totalPoints}
                            </p>
                            <div className="overflow-x-auto">
                                <table
                                    className="w-full text-left text-sm"
                                    aria-label={t('inputs.preview.table', {
                                        key: group.key,
                                    })}
                                >
                                    <thead>
                                        <tr>
                                            {[
                                                'key',
                                                'address',
                                                'raw',
                                                'value',
                                                'quality',
                                                'time',
                                                'orders',
                                            ].map((column) => (
                                                <th
                                                    className="border-b p-2"
                                                    key={column}
                                                >
                                                    {t(
                                                        `inputs.preview.columns.${column}`,
                                                    )}
                                                </th>
                                            ))}
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {group.preview.points.map((point) => (
                                            <tr key={point.key}>
                                                <th
                                                    scope="row"
                                                    className="border-b p-2"
                                                >
                                                    {point.key}
                                                </th>
                                                <td className="border-b p-2">
                                                    FC {point.functionCode} ·{' '}
                                                    {point.offset} /{' '}
                                                    {point.registerNumber} /{' '}
                                                    {point.manualReference}
                                                </td>
                                                <td className="border-b p-2 font-mono">
                                                    {point.rawHex.join(' ') ||
                                                        '—'}
                                                    {point.binary && (
                                                        <div>
                                                            {point.binary}
                                                        </div>
                                                    )}
                                                </td>
                                                <td className="border-b p-2 font-mono">
                                                    {point.value.text ?? '—'}
                                                    <div>
                                                        {point.value.type}
                                                    </div>
                                                    {point.value
                                                        .unsafeJavaScriptInteger && (
                                                        <p>
                                                            {t(
                                                                'inputs.preview.precision',
                                                            )}
                                                        </p>
                                                    )}
                                                </td>
                                                <td className="border-b p-2">
                                                    {point.value.quality}
                                                    {point.value.reason && (
                                                        <div>
                                                            {t(
                                                                `inputs.preview.reasons.${point.value.reason}`,
                                                                {
                                                                    defaultValue:
                                                                        point
                                                                            .value
                                                                            .reason,
                                                                },
                                                            )}
                                                        </div>
                                                    )}
                                                </td>
                                                <td className="border-b p-2">
                                                    {group.preview.blocks[
                                                        Number(point.block)
                                                    ]?.readAt ?? '—'}
                                                </td>
                                                <td className="border-b p-2">
                                                    {point.orders.map(
                                                        (order) => (
                                                            <div
                                                                key={
                                                                    order.byteOrder
                                                                }
                                                                className="my-1 space-x-2"
                                                            >
                                                                <span className="font-mono">
                                                                    {
                                                                        order.byteOrder
                                                                    }
                                                                    :{' '}
                                                                    {order.value
                                                                        .text ??
                                                                        order
                                                                            .value
                                                                            .reason}
                                                                </span>
                                                                {onOrder && (
                                                                    <Button
                                                                        type="button"
                                                                        variant="outline"
                                                                        disabled={
                                                                            blocked ||
                                                                            read.isPending ||
                                                                            !unchanged
                                                                        }
                                                                        onClick={() =>
                                                                            onOrder(
                                                                                point.key,
                                                                                order.byteOrder,
                                                                            )
                                                                        }
                                                                    >
                                                                        {t(
                                                                            'inputs.preview.apply',
                                                                            {
                                                                                order: order.byteOrder,
                                                                                key: point.key,
                                                                            },
                                                                        )}
                                                                    </Button>
                                                                )}
                                                            </div>
                                                        ),
                                                    )}
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                            <ul className="text-sm">
                                {group.preview.blocks.map((block, index) =>
                                    block.outcome === 'success' ? null : (
                                        <li key={index}>
                                            FC {block.functionCode} ·{' '}
                                            {block.offset}+{block.count}:{' '}
                                            {t(
                                                `inputs.preview.reasons.${block.outcome}`,
                                                { defaultValue: block.outcome },
                                            )}
                                            {block.requested === false &&
                                                ` · ${t('inputs.preview.notRequested')}`}
                                            {block.exceptionCode != null &&
                                                ` · ${t('inputs.preview.exception')} ${block.exceptionCode.toString(16).padStart(2, '0')} (${t(`inputs.preview.exceptions.${block.exceptionCode}`, { defaultValue: t('inputs.preview.unknownException') })})`}
                                        </li>
                                    ),
                                )}
                            </ul>
                        </div>
                    ))}
                </>
            )}
        </section>
    )
}
