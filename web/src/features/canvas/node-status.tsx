import { useTranslation } from 'react-i18next'
import type { NodeRuntime } from '../routes/node-runtime'

export function NodeStatus({
    runtime,
    invalid,
    persisted,
}: {
    runtime?: NodeRuntime
    invalid: boolean
    persisted: boolean
}) {
    const { t } = useTranslation()
    return (
        <div
            className="space-y-1 text-xs"
            data-node-runtime={runtime?.state ?? 'unverified'}
        >
            {invalid && (
                <p className="text-destructive" data-node-error>
                    {t('canvas.nodeStatus.invalid')}
                </p>
            )}
            {!runtime ? (
                <p className="text-muted-foreground">
                    {t(
                        persisted
                            ? 'canvas.nodeStatus.unverified'
                            : 'canvas.unbound',
                    )}
                </p>
            ) : (
                <>
                    <p>
                        {t(`canvas.nodeStatus.states.${runtime.state}`, {
                            defaultValue: t('canvas.nodeStatus.states.unknown'),
                        })}
                    </p>
                    <p>
                        {t('canvas.nodeStatus.inputs', {
                            running: runtime.runningInputs,
                            total: runtime.inputs,
                            failed: runtime.faultedInputs,
                        })}
                    </p>
                    {!runtime.kind.startsWith('input.') && (
                        <p>
                            {t('canvas.nodeStatus.paths', {
                                observed: runtime.observedPaths,
                                total: runtime.paths,
                                failed: runtime.failedPaths,
                                unknown: runtime.unknownPaths,
                            })}
                        </p>
                    )}
                    <p>
                        {t('canvas.nodeStatus.last', {
                            outcome: runtime.lastOutcome
                                ? t(
                                      `canvas.nodeStatus.outcomes.${runtime.lastOutcome}`,
                                      {
                                          defaultValue: t(
                                              'canvas.nodeStatus.states.unknown',
                                          ),
                                      },
                                  )
                                : t('canvas.nodeStatus.noSample'),
                        })}
                    </p>
                    {runtime.lastObservedAt && (
                        <time dateTime={runtime.lastObservedAt}>
                            {new Date(runtime.lastObservedAt).toLocaleString()}
                        </time>
                    )}
                </>
            )}
        </div>
    )
}

/** Local code-native icons; no font/CDN or new icon dependency. */
export function NodeTypeIcon({ kind }: { kind: string }) {
    return (
        <svg
            aria-hidden="true"
            className="h-4 w-4 shrink-0"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
        >
            {kind.startsWith('input.') ? (
                <path d="M3 12h14m-5-5 5 5-5 5M20 4v16" />
            ) : kind.startsWith('output.') ? (
                <path d="M4 4v16m3-8h14m-5-5 5 5-5 5" />
            ) : (
                <>
                    <path d="M3 7h18M3 17h18" />
                    <circle cx="8" cy="7" r="3" fill="var(--background)" />
                    <circle cx="16" cy="17" r="3" fill="var(--background)" />
                </>
            )}
        </svg>
    )
}
