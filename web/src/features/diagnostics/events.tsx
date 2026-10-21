import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import type { EventFilter } from './api'
import { eventTime } from './model'
import { diagnostics } from './queries'
import { DiagnosticPanel, dateText } from './view'

export function RuntimeEvents() {
    const { t } = useTranslation()
    const [draft, setDraft] = useState({
        from: '',
        to: '',
        level: '',
        type: '',
        target: '',
    })
    const [filter, setFilter] = useState<EventFilter>({})
    const [cursors, setCursors] = useState<string[]>([])
    const [invalid, setInvalid] = useState(false)
    const before = cursors.at(-1)
    const query = useQuery({
        queryKey: ['runtime-events', filter, before],
        queryFn: ({ signal }) => diagnostics.events(filter, before, signal),
        refetchInterval: before === undefined ? 10_000 : false,
        gcTime: 0,
    })
    return (
        <DiagnosticPanel title={t('diagnostics.events')} query={query}>
            <p className="text-sm text-muted-foreground">
                {t('diagnostics.eventNotice')}
            </p>
            <form
                className="flex flex-wrap items-end gap-3"
                onSubmit={(event) => {
                    event.preventDefault()
                    try {
                        const from = eventTime(draft.from),
                            to = eventTime(draft.to)
                        if (from && to && from >= to) throw new Error('range')
                        if (
                            !['', 'info', 'warning', 'error'].includes(
                                draft.level,
                            )
                        )
                            throw new Error('level')
                        setFilter({
                            from,
                            to,
                            level: draft.level as EventFilter['level'],
                            type: draft.type.trim() || undefined,
                            target: draft.target.trim() || undefined,
                        })
                        setCursors([])
                        setInvalid(false)
                    } catch {
                        setInvalid(true)
                    }
                }}
            >
                {(['from', 'to', 'type', 'target'] as const).map((key) => (
                    <label key={key} className="space-y-1 text-sm">
                        {t(`diagnostics.${key}`)}
                        <Input
                            type={
                                key === 'from' || key === 'to'
                                    ? 'datetime-local'
                                    : 'text'
                            }
                            maxLength={256}
                            value={draft[key]}
                            onChange={(event) =>
                                setDraft({
                                    ...draft,
                                    [key]: event.target.value,
                                })
                            }
                        />
                    </label>
                ))}
                <div className="space-y-1 text-sm">
                    <label htmlFor="runtime-event-level">
                        {t('diagnostics.level')}
                    </label>
                    <select
                        id="runtime-event-level"
                        className="block h-9 rounded-md border bg-background px-3"
                        value={draft.level}
                        onChange={(event) =>
                            setDraft({ ...draft, level: event.target.value })
                        }
                    >
                        <option value="">{t('diagnostics.all')}</option>
                        {(['info', 'warning', 'error'] as const).map(
                            (level) => (
                                <option key={level} value={level}>
                                    {t(`diagnostics.levels.${level}`)}
                                </option>
                            ),
                        )}
                    </select>
                </div>
                <Button type="submit">{t('diagnostics.filter')}</Button>
            </form>
            {invalid && <p role="alert">{t('diagnostics.invalidTime')}</p>}
            {query.data && (
                <>
                    <div className="overflow-x-auto">
                        <table className="w-full text-left text-sm">
                            <thead>
                                <tr>
                                    {[
                                        'first',
                                        'last',
                                        'level',
                                        'type',
                                        'target',
                                        'message',
                                        'occurrences',
                                    ].map((key) => (
                                        <th key={key} className="p-2">
                                            {t(`diagnostics.${key}`)}
                                        </th>
                                    ))}
                                </tr>
                            </thead>
                            <tbody>
                                {query.data.items.map((item) => (
                                    <tr
                                        key={String(item.id)}
                                        className="border-t align-top"
                                    >
                                        <td className="p-2 whitespace-nowrap">
                                            {dateText(item.firstOccurredAt)}
                                        </td>
                                        <td className="p-2 whitespace-nowrap">
                                            {dateText(item.lastOccurredAt)}
                                        </td>
                                        <td className="p-2">
                                            {t(
                                                `diagnostics.levels.${item.level}`,
                                            )}
                                        </td>
                                        <td className="p-2">{item.type}</td>
                                        <td className="p-2">
                                            {item.target || '—'}
                                        </td>
                                        <td className="max-w-lg p-2 break-words whitespace-pre-wrap">
                                            {item.message}
                                        </td>
                                        <td className="p-2">
                                            {item.occurrenceCount}
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                    {query.data.items.length === 0 && (
                        <p>{t('diagnostics.empty')}</p>
                    )}
                    <div className="flex items-center gap-3">
                        <Button
                            variant="outline"
                            disabled={!cursors.length || query.isFetching}
                            onClick={() => setCursors(cursors.slice(0, -1))}
                        >
                            {t('diagnostics.previous')}
                        </Button>
                        <span>
                            {t('diagnostics.page', {
                                page: cursors.length + 1,
                            })}
                        </span>
                        <Button
                            variant="outline"
                            disabled={
                                query.data.nextCursor === null ||
                                query.isFetching ||
                                !!query.error
                            }
                            onClick={() => {
                                if (query.data?.nextCursor != null)
                                    setCursors([
                                        ...cursors,
                                        String(query.data.nextCursor),
                                    ])
                            }}
                        >
                            {t('diagnostics.next')}
                        </Button>
                    </div>
                </>
            )}
        </DiagnosticPanel>
    )
}
