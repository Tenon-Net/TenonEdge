import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from './i18n'
import { api } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { GraphNode } from '../canvas/model'
import { selectedReferences, withReferences } from './references'

export function ReferencePicker({
    node,
    version,
    disabled,
    change,
}: {
    node: GraphNode
    version?: string
    disabled: boolean
    change: (text: string) => void
}) {
    const { t } = useTranslation()
    const [cursors, setCursors] = useState([0])
    const [search, setSearch] = useState('')
    const [error, setError] = useState(false)
    const cursor = cursors[cursors.length - 1]
    const query = useQuery({
        queryKey: ['route-catalog', node.kind, node.version ?? 1, cursor],
        queryFn: async ({ signal }) => {
            const { data } = await api.GET(
                '/api/edge-admin/v1/routes/catalog',
                {
                    params: {
                        query: {
                            kind: node.kind,
                            moduleVersion: node.version ?? 1,
                            afterId: cursor,
                            limit: 100,
                        },
                    },
                    signal,
                },
            )
            if (!data) throw new Error('catalog')
            return data
        },
    })
    let invalid = false
    let selected: number[]
    try {
        selected = selectedReferences(node.kind, node.configText)
    } catch {
        invalid = true
        selected = []
    }
    function apply(ids: number[]) {
        try {
            change(withReferences(node.kind, node.configText, ids))
            setError(false)
        } catch {
            setError(true)
        }
    }
    const input = node.kind.startsWith('input.')
    const coherent =
        !query.error && !query.isFetching && query.data?.version === version
    return (
        <section className="space-y-2" aria-label={t('routes.references')}>
            <h3 className="font-medium">{t('routes.references')}</h3>
            <p className="text-xs">
                {t('routes.catalogNotice', {
                    version: query.data?.version ?? '—',
                })}
            </p>
            <ErrorMessage error={query.error} />
            {query.data && query.data.version !== version && (
                <p role="alert">{t('routes.inline.versionChanged')}</p>
            )}
            {(error || invalid) && (
                <p role="alert">{t('routes.configError')}</p>
            )}
            <Button
                type="button"
                variant="outline"
                disabled={query.isFetching}
                onClick={() => void query.refetch()}
            >
                {t('routes.catalogRefresh')}
            </Button>
            <label>
                {t('routes.catalogSearch')}
                <input
                    className="w-full rounded border p-1"
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                />
            </label>
            <div className="max-h-64 space-y-1 overflow-auto">
                {query.data?.items
                    .filter((item) =>
                        `${item.key} ${item.name} ${item.deviceKey ?? ''} ${item.deviceName ?? ''}`
                            .toLowerCase()
                            .includes(search.toLowerCase()),
                    )
                    .map((item) => {
                        const id = Number(item.id)
                        const checked = selected.includes(id)
                        return (
                            <label key={id} className="block text-xs">
                                <input
                                    type={input ? 'checkbox' : 'radio'}
                                    name={`reference-${node.id}`}
                                    checked={checked}
                                    disabled={
                                        disabled ||
                                        !coherent ||
                                        invalid ||
                                        (!item.enabled && !checked) ||
                                        !Number.isSafeInteger(id)
                                    }
                                    onChange={(event) =>
                                        apply(
                                            input
                                                ? event.target.checked
                                                    ? [...selected, id]
                                                    : selected.filter(
                                                          (value) =>
                                                              value !== id,
                                                      )
                                                : [id],
                                        )
                                    }
                                />{' '}
                                {item.deviceName ? `${item.deviceName} / ` : ''}
                                {item.name} ({item.key}){' '}
                                {!item.enabled && t('routes.catalogDisabled')}
                            </label>
                        )
                    })}
            </div>
            <div className="flex gap-2">
                <Button
                    type="button"
                    variant="outline"
                    disabled={cursors.length < 2 || query.isFetching}
                    onClick={() => setCursors((values) => values.slice(0, -1))}
                >
                    {t('routes.catalogPrevious')}
                </Button>
                <Button
                    type="button"
                    variant="outline"
                    disabled={!query.data?.nextAfterId || query.isFetching}
                    onClick={() =>
                        query.data?.nextAfterId &&
                        setCursors((values) => [
                            ...values,
                            Number(query.data!.nextAfterId),
                        ])
                    }
                >
                    {t('routes.catalogNext')}
                </Button>
            </div>
            <p className="text-xs">
                {t('routes.catalogSelected', { count: selected.length })}
            </p>
            <ul className="max-h-32 overflow-auto text-xs">
                {selected.map((id) => (
                    <li key={id}>
                        #{id}{' '}
                        <Button
                            type="button"
                            variant="outline"
                            disabled={disabled}
                            onClick={() =>
                                apply(selected.filter((value) => value !== id))
                            }
                        >
                            {t('routes.catalogRemove')}
                        </Button>
                    </li>
                ))}
            </ul>
        </section>
    )
}
