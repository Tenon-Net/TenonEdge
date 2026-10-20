import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { dateText } from '../diagnostics/view'
import type {
    MigrationIdentity,
    MigrationReview,
    MigrationQuota,
} from './migration-api'

export function Identity({ identity }: { identity: MigrationIdentity }) {
    const { t } = useTranslation()
    const known = (recorded: boolean, value: string | null) =>
        !recorded ? t('migration.unknown') : (value ?? t('migration.none'))
    return (
        <dl className="space-y-2 break-words text-sm">
            {(
                [
                    ['protocol', identity.protocol],
                    ['address', identity.address],
                    ['authentication', identity.authentication],
                    [
                        'account',
                        known(identity.accountRecorded, identity.account),
                    ],
                    [
                        'certificate',
                        known(
                            identity.clientCertificateRecorded,
                            identity.clientCertificateSubject,
                        ),
                    ],
                ] as const
            ).map(([key, value]) => (
                <div key={key}>
                    <dt className="text-muted-foreground">
                        {t(`migration.${key}`)}
                    </dt>
                    <dd className="whitespace-pre-wrap">{value}</dd>
                </div>
            ))}
        </dl>
    )
}
export function MigrationReviewPanel({ review }: { review: MigrationReview }) {
    const { t } = useTranslation()
    const [page, setPage] = useState(0)
    const pages = Math.ceil(review.sources.length / 20)
    return (
        <section
            aria-label={t('migration.review')}
            className="product-panel space-y-4"
        >
            <h2 className="text-lg font-semibold">{t('migration.review')}</h2>
            <p>
                {t('migration.scope', {
                    source: review.sourceConnectionId,
                    target: review.targetConnectionId,
                    name:
                        review.targetName ??
                        review.targetKey ??
                        review.targetConnectionId,
                    revision: review.revision,
                })}
            </p>
            <p>
                {t('migration.summary', {
                    count: review.count,
                    pending: review.pending,
                    failed: review.failed,
                    quarantined: review.quarantined,
                })}
            </p>
            <p className="text-sm">
                {t('migration.dates', {
                    first: dateText(review.firstCreatedAt),
                    last: dateText(review.lastCreatedAt),
                    expires: dateText(review.expiresAt),
                })}
            </p>
            <div className="grid gap-4 md:grid-cols-2">
                <div className="space-y-4 rounded border p-3">
                    <h3 className="font-semibold">{t('migration.original')}</h3>
                    <p>
                        {t('migration.identityPage', {
                            page: page + 1,
                            pages,
                            count: review.sources.length,
                        })}
                    </p>
                    {review.sources
                        .slice(page * 20, (page + 1) * 20)
                        .map((source, i) => (
                            <div
                                key={page * 20 + i}
                                className="space-y-2 border-t pt-3"
                            >
                                <p className="font-semibold">
                                    {t('migration.identityCount', {
                                        count: source.count,
                                    })}
                                </p>
                                <Identity identity={source.recipient} />
                            </div>
                        ))}
                    {pages > 1 && (
                        <div className="flex flex-wrap gap-2">
                            <Button
                                variant="outline"
                                disabled={page === 0}
                                onClick={() => setPage(page - 1)}
                            >
                                {t('migration.previousIdentities')}
                            </Button>
                            <Button
                                variant="outline"
                                disabled={page + 1 >= pages}
                                onClick={() => setPage(page + 1)}
                            >
                                {t('migration.nextIdentities')}
                            </Button>
                        </div>
                    )}
                </div>
                <div className="space-y-4 rounded border p-3">
                    <h3 className="font-semibold">
                        {t('migration.destination')}
                    </h3>
                    <Identity identity={review.target} />
                </div>
            </div>
            <MigrationQuotaPanel quota={review.quota} />
            {!review.canConfirm && (
                <p role="alert">{t('migration.cannotConfirm')}</p>
            )}
            <p>{t('migration.manual')}</p>
        </section>
    )
}
export function MigrationQuotaPanel({ quota }: { quota: MigrationQuota }) {
    const { t } = useTranslation()
    return (
        <section className="space-y-3" aria-label={t('migration.quota')}>
            <h3 className="font-semibold">{t('migration.quota')}</h3>
            <dl className="grid gap-3 sm:grid-cols-2">
                {(
                    [
                        'pendingRecords',
                        'pendingBytes',
                        'globalBytes',
                        'failedRecords',
                    ] as const
                ).map((key) => (
                    <div key={key}>
                        <dt>{t(`migration.${key}`)}</dt>
                        <dd>{quota[key]}</dd>
                    </div>
                ))}
            </dl>
        </section>
    )
}
