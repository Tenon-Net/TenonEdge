import { useState } from 'react'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { Identity, MigrationQuotaPanel } from '../deliveries/migration-review'
import type { MigrationIdentity } from '../deliveries/migration-api'
import type { IntentInspection } from './migration-api'

export function Recipient({
    title,
    value,
}: {
    title: string
    value: MigrationIdentity | null
}) {
    const { t } = useTranslation()
    return (
        <section className="space-y-3 rounded border p-3">
            <h3 className="font-semibold">{title}</h3>
            {value ? (
                <Identity identity={value} />
            ) : (
                <p>{t('configurationMigration.identityUnknown')}</p>
            )}
        </section>
    )
}
export function IntentInspectionPanel({
    value,
    showStatus = true,
}: {
    value: IntentInspection
    showStatus?: boolean
}) {
    const { t } = useTranslation()
    return (
        <div className="space-y-4">
            {showStatus && (
                <>
                    <p>
                        {t('configurationMigration.intentId', {
                            id: value.intentId,
                        })}
                    </p>
                    <p>{t(`configurationMigration.states.${value.status}`)}</p>
                </>
            )}
            <p>
                {t('configurationMigration.effective', {
                    revision:
                        value.effectiveRevision == null
                            ? '—'
                            : `r${value.effectiveRevision}`,
                    count: value.remainingRecords ?? '—',
                })}
            </p>
            <div className="grid gap-3 lg:grid-cols-3">
                <Recipient
                    title={t('configurationMigration.source')}
                    value={value.source}
                />
                <Recipient
                    title={t('configurationMigration.approved')}
                    value={value.approvedTarget}
                />
                <Recipient
                    title={t('configurationMigration.current')}
                    value={value.currentTarget}
                />
            </div>
            <p>
                {t('configurationMigration.matches', {
                    value: t(
                        value.targetMatchesConfirmation
                            ? 'configurationMigration.yes'
                            : 'configurationMigration.no',
                    ),
                })}
            </p>
            <p>{t('configurationMigration.quotaNow')}</p>
            {value.currentQuota && (
                <MigrationQuotaPanel quota={value.currentQuota} />
            )}
            {[value.problem, value.retryProblem]
                .filter(Boolean)
                .map((code, i) => (
                    <p role="alert" key={i}>
                        {t(`migration.errors.${code}`, { defaultValue: code! })}
                    </p>
                ))}
        </div>
    )
}
// Remount for a new preparation or explicit refresh. Declarations are never persisted.
export function MigrationDeclarations({
    disabled,
    retry = false,
    onConfirm,
}: {
    disabled: boolean
    retry?: boolean
    onConfirm: () => void
}) {
    const { t } = useTranslation()
    const [agree, setAgree] = useState(false)
    const [owner, setOwner] = useState(false)
    return (
        <div className="space-y-3">
            <p>{t('migration.manual')}</p>
            <label className="flex items-start gap-2">
                <input
                    type="checkbox"
                    checked={agree}
                    disabled={disabled}
                    onChange={(e) => setAgree(e.target.checked)}
                />
                {t(
                    retry
                        ? 'configurationMigration.retryAgree'
                        : 'configurationMigration.agree',
                )}
            </label>
            <label className="flex items-start gap-2">
                <input
                    type="checkbox"
                    checked={owner}
                    disabled={disabled}
                    onChange={(e) => setOwner(e.target.checked)}
                />
                {t(
                    retry
                        ? 'configurationMigration.retryOwner'
                        : 'configurationMigration.owner',
                )}
            </label>
            <Button disabled={disabled || !agree || !owner} onClick={onConfirm}>
                {t(
                    retry
                        ? 'configurationMigration.retryConfirm'
                        : 'configurationMigration.confirm',
                )}
            </Button>
        </div>
    )
}
