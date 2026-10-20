import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import type { DraftSummary, Operation } from './api'
import { ConfigurationRecovery } from './recovery'
import { configuration } from './queries'
import { ChangeDetails, ConfigurationError } from './view'

export function ConfigurationHistory({
    summary,
    canEdit,
    rollbackDisabled,
    onReview,
    recoveryDisabled,
    onRecoveryBusy,
    onRecoveryAccepted,
}: {
    summary?: DraftSummary
    canEdit: boolean
    rollbackDisabled: boolean
    onReview: (revision: string) => void
    recoveryDisabled: boolean
    onRecoveryBusy: (busy: boolean) => void
    onRecoveryAccepted: (operation: Operation) => void
}) {
    const { t } = useTranslation()
    const [cursors, setCursors] = useState<string[]>([])
    const [selected, setSelected] = useState('')
    const [other, setOther] = useState('draft')
    const cursor = cursors.at(-1)
    const versions = useQuery({
        queryKey: ['configuration-history', 'list', cursor],
        queryFn: ({ signal }) => configuration.versions(cursor, signal),
    })
    const detail = useQuery({
        queryKey: ['configuration-history', 'detail', selected],
        queryFn: ({ signal }) => configuration.version(selected, signal),
        enabled: !!selected,
        staleTime: Infinity,
    })
    const comparison = useQuery({
        queryKey: [
            'configuration-history',
            'comparison',
            selected,
            other,
            other === 'draft' ? summary?.version : null,
        ],
        queryFn: ({ signal }) =>
            other === 'draft'
                ? configuration.compareDraft(selected, summary!.version, signal)
                : configuration.compare(selected, other, signal),
        enabled:
            !!selected &&
            detail.isSuccess &&
            !detail.isFetching &&
            (other !== 'draft' || !!summary),
        // Revision contents and a versioned draft are immutable; a periodic summary
        // read must not refetch the same pair merely by re-enabling its observer.
        staleTime: Infinity,
    })
    return (
        <section
            className="product-panel space-y-4"
            aria-label={t('configuration.history')}
        >
            <h2 className="text-base font-semibold">
                {t('configuration.history')}
            </h2>
            <p className="text-sm text-muted-foreground">
                {t('configuration.historyHelp')}
            </p>
            <ConfigurationError error={versions.error} />
            {versions.isPending && (
                <p role="status">{t('configuration.loading')}</p>
            )}
            {!versions.isError &&
                versions.data?.map((version) => (
                    <article
                        key={String(version.revision)}
                        className="grid items-start gap-3 border-b py-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]"
                        data-version={String(version.revision)}
                    >
                        <div className="space-y-1">
                            <p>
                                r{version.revision} ·{' '}
                                {t(
                                    `configuration.versionStates.${version.state}`,
                                    {
                                        defaultValue: version.state,
                                    },
                                )}{' '}
                                ·{' '}
                                {t(`configuration.origins.${version.origin}`, {
                                    defaultValue: version.origin,
                                })}{' '}
                                · {version.createdBy} ·{' '}
                                {new Date(
                                    Number(version.createdAt),
                                ).toLocaleString('zh-CN')}
                            </p>
                            <p className="text-sm">
                                {t('configuration.appliedAt', {
                                    time:
                                        version.activatedAt == null
                                            ? t('configuration.none')
                                            : new Date(
                                                  Number(version.activatedAt),
                                              ).toLocaleString('zh-CN'),
                                })}
                            </p>
                        </div>
                        <div className="space-y-1">
                            <p>{version.changeDescription}</p>
                            {version.notEffectiveReason && (
                                <p>{version.notEffectiveReason}</p>
                            )}
                        </div>
                        <Button
                            variant="outline"
                            onClick={() =>
                                setSelected(String(version.revision))
                            }
                        >
                            {t('configuration.viewVersion', {
                                revision: version.revision,
                            })}
                        </Button>
                    </article>
                ))}
            {!versions.isError && versions.data?.length === 0 && (
                <p>{t('configuration.noVersions')}</p>
            )}
            <div className="flex gap-3">
                <Button
                    variant="outline"
                    disabled={!cursors.length || versions.isFetching}
                    onClick={() => setCursors((items) => items.slice(0, -1))}
                >
                    {t('configuration.previous')}
                </Button>
                <Button
                    variant="outline"
                    disabled={
                        versions.isError ||
                        versions.isFetching ||
                        versions.data?.length !== 20
                    }
                    onClick={() =>
                        setCursors((items) => [
                            ...items,
                            String(versions.data!.at(-1)!.revision),
                        ])
                    }
                >
                    {t('configuration.next')}
                </Button>
            </div>
            {selected && (
                <section
                    className="space-y-3"
                    aria-label={t('configuration.versionDetail', {
                        revision: selected,
                    })}
                >
                    <h3 className="font-semibold">
                        {t('configuration.versionDetail', {
                            revision: selected,
                        })}
                    </h3>
                    <ConfigurationError error={detail.error} />
                    {detail.isPending && (
                        <p role="status">{t('configuration.loading')}</p>
                    )}
                    {detail.data && !detail.isError && (
                        <>
                            {detail.data.warnings.map((warning) => (
                                <p key={warning}>
                                    {t(
                                        `configuration.warnings.${warning.replace('configuration.', '')}`,
                                        { defaultValue: warning },
                                    )}
                                </p>
                            ))}
                            <p className="break-all text-xs">
                                {t('configuration.hashes', {
                                    content: detail.data.contentHash,
                                    plan: detail.data.planHash,
                                })}
                            </p>
                            {(['draftText', 'planText'] as const).map((key) => (
                                <SnapshotContents
                                    key={key}
                                    label={t(`configuration.${key}`)}
                                    text={detail.data![key]}
                                />
                            ))}
                            <ConfigurationRecovery
                                key={selected}
                                detail={detail.data}
                                unavailable={
                                    recoveryDisabled || detail.isFetching
                                }
                                onBusy={onRecoveryBusy}
                                onAccepted={onRecoveryAccepted}
                            />
                            {canEdit && (
                                <Button
                                    disabled={
                                        rollbackDisabled ||
                                        detail.isFetching ||
                                        Number(
                                            detail.data.version.schemaVersion,
                                        ) !== 2 ||
                                        !['effective', 'history'].includes(
                                            detail.data.version.state,
                                        )
                                    }
                                    onClick={() => onReview(selected)}
                                >
                                    {t('configuration.reviewRollback', {
                                        revision: selected,
                                    })}
                                </Button>
                            )}
                        </>
                    )}
                    <label className="block">
                        {t('configuration.compareWith')}
                        <select
                            className="m-2 rounded border bg-background p-2"
                            value={other}
                            onChange={(event) => setOther(event.target.value)}
                        >
                            <option value="draft">
                                {t('configuration.currentDraft')}
                            </option>
                            {other !== 'draft' &&
                                !versions.data?.some(
                                    (item) => String(item.revision) === other,
                                ) && <option value={other}>r{other}</option>}
                            {versions.data?.map((item) => (
                                <option
                                    key={String(item.revision)}
                                    value={String(item.revision)}
                                >
                                    r{item.revision}
                                </option>
                            ))}
                        </select>
                    </label>
                    <ConfigurationError error={comparison.error} />
                    {comparison.isFetching && (
                        <p role="status">{t('configuration.loading')}</p>
                    )}
                    {other === 'draft' && !summary && (
                        <p>{t('configuration.unknownSummary')}</p>
                    )}
                    {!comparison.isError &&
                        !comparison.isFetching &&
                        comparison.data &&
                        (other !== 'draft' || summary) && (
                            <div
                                className="space-y-2"
                                aria-label={t('configuration.comparison')}
                            >
                                <p>
                                    {t('configuration.comparisonDirection', {
                                        before: selected,
                                        after:
                                            other === 'draft'
                                                ? t(
                                                      'configuration.currentDraft',
                                                  )
                                                : `r${other}`,
                                    })}
                                </p>
                                {comparison.data.changes.length === 0 && (
                                    <p>{t('configuration.sameContents')}</p>
                                )}
                                {comparison.data.points.map((point) => (
                                    <p
                                        key={String(point.deviceId)}
                                        className="text-sm"
                                    >
                                        {t('configuration.points', {
                                            device: point.deviceId,
                                            added: point.added,
                                            modified: point.modified,
                                            removed: point.removed,
                                        })}
                                    </p>
                                ))}
                                {comparison.data.changes.map((change) => (
                                    <ChangeDetails
                                        key={`${change.kind}/${change.id}`}
                                        change={change}
                                    />
                                ))}
                            </div>
                        )}
                </section>
            )}
        </section>
    )
}

function SnapshotContents({ label, text }: { label: string; text: string }) {
    const [open, setOpen] = useState(false)
    return (
        <details onToggle={(event) => setOpen(event.currentTarget.open)}>
            <summary className="cursor-pointer">{label}</summary>
            {open && (
                <pre className="max-h-80 overflow-auto whitespace-pre-wrap break-all text-xs">
                    {text}
                </pre>
            )}
        </details>
    )
}
