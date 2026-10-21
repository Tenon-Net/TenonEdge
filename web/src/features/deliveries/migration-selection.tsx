import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { api } from '../../lib/session'
import { diagnostics } from '../diagnostics/queries'
import { dateText } from '../diagnostics/view'
import {
    createDeliveryMigrationApi,
    migrationAreas,
    type MigrationArea,
} from './migration-api'
import type { DeliveryItem } from './api'

const migrations = createDeliveryMigrationApi(api)
export function MigrationSelection({
    source,
    disabled,
    prepare,
}: {
    source: string
    disabled: boolean
    prepare: (target: string, items: DeliveryItem[]) => void
}) {
    const { t } = useTranslation()
    const [area, setArea] = useState<MigrationArea>('pending')
    const [after, setAfter] = useState<string>()
    const [checked, check] = useState<DeliveryItem[]>([])
    const [target, setTarget] = useState('')
    const [cursor, setCursor] = useState<{ after: string; revision: string }>()
    const records = useQuery({
        queryKey: ['migration-records', source, area, after],
        queryFn: ({ signal }) =>
            migrations.records(source, area, after, signal),
        gcTime: 0,
    })
    const targets = useQuery({
        queryKey: ['migration-targets', cursor],
        queryFn: ({ signal }) =>
            diagnostics.outputs(cursor?.after, cursor?.revision, signal),
        gcTime: 0,
    })
    const choices = targets.data?.metadataAvailable
        ? (targets.data.items.filter(
              (item) =>
                  item.active &&
                  // This is the effective output unit kind, which may be a module,
                  // not its transport protocol. Preparation checks each frozen
                  // record against the target's real protocol and current identity.
                  !!item.kind &&
                  item.kind !== 'tenon' &&
                  String(item.connectionId) !== source,
          ) ?? [])
        : []
    const same = (a: DeliveryItem, b: DeliveryItem) =>
        String(a.sequence) === String(b.sequence) &&
        a.deliveryId === b.deliveryId
    const unavailable =
        disabled ||
        records.isFetching ||
        records.isError ||
        targets.isFetching ||
        targets.isError ||
        !choices.some((item) => String(item.connectionId) === target)
    return (
        <section className="space-y-4">
            <h2 className="text-lg font-semibold">
                {t('migration.source', { id: source })}
            </h2>
            <fieldset disabled={disabled} className="space-y-4">
                <label className="block space-y-1">
                    {t('migration.area')}
                    <select
                        aria-label={t('migration.area')}
                        className="block rounded border bg-background p-2"
                        value={area}
                        onChange={(e) => {
                            setArea(e.target.value as MigrationArea)
                            setAfter(undefined)
                        }}
                    >
                        {migrationAreas.map((value) => (
                            <option key={value} value={value}>
                                {t(`migration.areas.${value}`)}
                            </option>
                        ))}
                    </select>
                </label>
                <p>{t('migration.selected', { count: checked.length })}</p>
                <Button
                    variant="outline"
                    disabled={checked.length === 0}
                    onClick={() => check([])}
                >
                    {t('migration.clear')}
                </Button>
                <p className="text-sm text-muted-foreground">
                    {t('migration.pageNotice')}
                </p>
                {records.isPending && <p role="status">{t('loading')}</p>}
                {records.error && (
                    <p role="alert">{t('migration.readError')}</p>
                )}
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead>
                            <tr>
                                <th />
                                <th className="p-2">{t('migration.record')}</th>
                                <th className="p-2">{t('migration.device')}</th>
                                <th className="p-2">{t('migration.reason')}</th>
                            </tr>
                        </thead>
                        <tbody>
                            {records.data?.items.map((item) => {
                                const selected = checked.some((row) =>
                                    same(row, item),
                                )
                                return (
                                    <tr
                                        key={String(item.sequence)}
                                        className="border-t align-top"
                                    >
                                        <td className="p-2">
                                            <input
                                                type="checkbox"
                                                aria-label={t(
                                                    'migration.select',
                                                    { id: item.deliveryId },
                                                )}
                                                checked={selected}
                                                disabled={
                                                    records.isError ||
                                                    records.isFetching ||
                                                    (!selected &&
                                                        checked.length >= 1000)
                                                }
                                                onChange={(e) =>
                                                    check(
                                                        e.target.checked
                                                            ? [...checked, item]
                                                            : checked.filter(
                                                                  (row) =>
                                                                      !same(
                                                                          row,
                                                                          item,
                                                                      ),
                                                              ),
                                                    )
                                                }
                                            />
                                        </td>
                                        <td className="p-2">
                                            <code>{item.deliveryId}</code>
                                            <small className="block">
                                                seq {item.sequence}
                                            </small>
                                        </td>
                                        <td className="p-2">
                                            {item.deviceKey}
                                            <small className="block">
                                                {dateText(item.createdAt)}
                                            </small>
                                        </td>
                                        <td className="p-2">
                                            {item.quarantineReason ??
                                                item.lastError ??
                                                '—'}
                                        </td>
                                    </tr>
                                )
                            })}
                        </tbody>
                    </table>
                </div>
                {records.data?.items.length === 0 && !records.error && (
                    <p>{t('migration.empty')}</p>
                )}
                <div className="flex flex-wrap gap-2">
                    <Button
                        variant="outline"
                        disabled={!after || records.isFetching}
                        onClick={() => setAfter(undefined)}
                    >
                        {t('migration.first')}
                    </Button>
                    <Button
                        variant="outline"
                        disabled={
                            !records.data?.nextSequence ||
                            records.isFetching ||
                            records.isError
                        }
                        onClick={() =>
                            setAfter(String(records.data!.nextSequence))
                        }
                    >
                        {t('migration.next')}
                    </Button>
                    <Button
                        variant="outline"
                        disabled={records.isFetching}
                        onClick={() => void records.refetch()}
                    >
                        {t('migration.refreshRecords')}
                    </Button>
                </div>
                <h3 className="font-semibold">{t('migration.target')}</h3>
                <p className="text-sm text-muted-foreground">
                    {t('migration.targetNotice')}
                </p>
                {targets.error && (
                    <p role="alert">{t('migration.readError')}</p>
                )}
                <label className="block">
                    {t('migration.choose')}
                    <select
                        aria-label={t('migration.choose')}
                        className="block max-w-full rounded border bg-background p-2"
                        value={target}
                        disabled={targets.isFetching || targets.isError}
                        onChange={(e) => setTarget(e.target.value)}
                    >
                        <option value="">{t('migration.choose')}</option>
                        {choices.map((item) => (
                            <option
                                key={String(item.connectionId)}
                                value={String(item.connectionId)}
                            >
                                {item.name ?? item.key ?? item.connectionId} ·{' '}
                                {item.kind} · {item.connectionId}
                            </option>
                        ))}
                    </select>
                </label>
                <div className="flex flex-wrap gap-2">
                    <Button
                        variant="outline"
                        disabled={targets.isFetching}
                        onClick={() => {
                            setTarget('')
                            if (cursor) setCursor(undefined)
                            else void targets.refetch()
                        }}
                    >
                        {t('migration.firstTargets')}
                    </Button>
                    <Button
                        variant="outline"
                        disabled={
                            !targets.data?.nextConnectionId ||
                            !targets.data?.revision ||
                            targets.isError ||
                            targets.isFetching
                        }
                        onClick={() => {
                            setTarget('')
                            setCursor({
                                after: String(targets.data!.nextConnectionId),
                                revision: String(targets.data!.revision),
                            })
                        }}
                    >
                        {t('migration.nextTargets')}
                    </Button>
                </div>
                <Button
                    disabled={unavailable || checked.length === 0}
                    onClick={() => prepare(target, checked.slice())}
                >
                    {t('migration.prepare')}
                </Button>
            </fieldset>
        </section>
    )
}
