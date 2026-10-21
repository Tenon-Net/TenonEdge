import { useMemo, useState } from 'react'
import { useTranslation } from './i18n'
import { ApiError } from '../../api/errors'
import { ErrorMessage } from '../auth/error-message'
import {
    fieldChanges,
    operationOutcome,
    type Operation,
    type Review,
} from './api'

export function ConfigurationError({ error }: { error: unknown }) {
    const { t } = useTranslation()
    const problem = error instanceof ApiError ? error.problem : null
    const code =
        problem &&
        typeof problem === 'object' &&
        'code' in problem &&
        typeof problem.code === 'string' &&
        /^[a-zA-Z][a-zA-Z0-9.]{0,99}$/.test(problem.code)
            ? problem.code
            : null
    return (
        <>
            <ErrorMessage error={error} />
            {code && (
                <p role="alert">
                    {t(`configuration.errors.${code.replaceAll('.', '_')}`, {
                        defaultValue: code,
                    })}
                </p>
            )}
        </>
    )
}

export function ReviewContents({ review }: { review: Review }) {
    const { t } = useTranslation()
    return (
        <div className="space-y-4">
            <ul className="list-disc space-y-1 pl-6 text-sm">
                {review.warnings.map((warning) => (
                    <li key={warning}>
                        {t(
                            `configuration.warnings.${warning.replace('configuration.', '')}`,
                            { defaultValue: warning },
                        )}
                    </li>
                ))}
            </ul>
            {!review.preflightPassed && (
                <p role="alert" className="text-destructive">
                    {t('configuration.blocked')}
                </p>
            )}
            {review.routeIssues.map((issue, index) => (
                <p role="alert" key={index}>
                    {t('configuration.routeIssue', {
                        route: issue.routeKey,
                        node: issue.nodeId ?? '—',
                        code: issue.code,
                    })}
                </p>
            ))}
            {review.targetBlocks.map((block, index) => (
                <p role="alert" key={index}>
                    {t('configuration.targetBlock', {
                        connection: block.connectionId,
                        recipient: block.recipient,
                        address: block.address,
                        pending: String(block.hasPending),
                        failed: String(block.hasFailed),
                    })}
                </p>
            ))}
            {review.points.map((point) => (
                <p key={String(point.deviceId)} className="text-sm">
                    {t('configuration.points', {
                        device: point.deviceId,
                        added: point.added,
                        modified: point.modified,
                        removed: point.removed,
                    })}
                </p>
            ))}
            <h3 className="font-semibold">{t('configuration.changes')}</h3>
            {review.changes.length === 0 && review.comparisonAvailable && (
                <p>{t('configuration.noChanges')}</p>
            )}
            {review.changes.map((change) => (
                <ChangeDetails
                    key={`${change.kind}/${change.id}`}
                    change={change}
                />
            ))}
        </div>
    )
}

export function OperationCard({ operation }: { operation: Operation }) {
    const { t } = useTranslation()
    return (
        <article
            className="space-y-2 rounded border p-4"
            data-operation-id={operation.id}
        >
            <h3 className="break-all font-semibold">
                {t('configuration.operationId', {
                    id: operation.id,
                    account: operation.account,
                })}
            </h3>
            <p>
                {t(`configuration.stages.${operation.stage}`)} ·{' '}
                {new Date(operation.startedAt).toLocaleString('zh-CN')}
            </p>
            <p role="status">
                {t(`configuration.outcomes.${operationOutcome(operation)}`)}
            </p>
            {operation.revision != null && (
                <p>
                    {t('configuration.revision', {
                        revision: operation.revision,
                    })}
                </p>
            )}
            {(operation.errorCode || operation.result?.code) && (
                <p>
                    {t('configuration.errorCode', {
                        code: operation.errorCode || operation.result?.code,
                    })}
                </p>
            )}
            {operation.result?.recoveryPending && (
                <p className="text-destructive">
                    {t('configuration.recoveryPending')}
                </p>
            )}
            {operation.result?.draftReplaced === false && (
                <p>{t('configuration.draftKept')}</p>
            )}
        </article>
    )
}

export function ChangeDetails({
    change,
}: {
    change: Review['changes'][number]
}) {
    const { t } = useTranslation()
    const [open, setOpen] = useState(false)
    const fields = useMemo(
        () =>
            open && change.change === 'modified'
                ? fieldChanges(change.beforeText, change.afterText)
                : null,
        [open, change.change, change.beforeText, change.afterText],
    )
    return (
        <details
            className="rounded border p-3"
            onToggle={(event) => setOpen(event.currentTarget.open)}
        >
            <summary className="cursor-pointer break-all">
                {t(`configuration.changeKinds.${change.change}`, {
                    defaultValue: change.change,
                })}{' '}
                ·{' '}
                {t(`configuration.kinds.${change.kind}`, {
                    defaultValue: change.kind,
                })}{' '}
                · {change.key}
            </summary>
            {open && (
                <>
                    <p className="my-2 text-sm text-muted-foreground">
                        {t('configuration.editor', {
                            account:
                                change.updatedBy ?? t('configuration.unknown'),
                            time:
                                change.updatedAt == null
                                    ? t('configuration.unknown')
                                    : new Date(
                                          Number(change.updatedAt),
                                      ).toLocaleString('zh-CN'),
                        })}
                    </p>
                    {fields && (
                        <div className="my-3 space-y-2">
                            <h4 className="font-semibold">
                                {t('configuration.fields')}
                            </h4>
                            {fields.items.map((field) => (
                                <div
                                    key={field.path}
                                    className="rounded border p-2 text-xs"
                                >
                                    <code className="break-all">
                                        {field.path}
                                    </code>
                                    <pre className="overflow-auto whitespace-pre-wrap break-all">
                                        {field.before} → {field.after}
                                    </pre>
                                </div>
                            ))}
                            {fields.truncated && (
                                <p role="status">
                                    {t('configuration.fieldsTruncated')}
                                </p>
                            )}
                        </div>
                    )}
                    <div className="grid gap-3 lg:grid-cols-2">
                        <div>
                            <h4>{t('configuration.before')}</h4>
                            <pre className="max-h-80 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 text-xs">
                                {change.beforeText}
                            </pre>
                        </div>
                        <div>
                            <h4>{t('configuration.after')}</h4>
                            <pre className="max-h-80 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 text-xs">
                                {change.afterText}
                            </pre>
                        </div>
                    </div>
                </>
            )}
        </details>
    )
}
