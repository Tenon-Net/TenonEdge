import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { createOutputControlsApi } from './controls-api'

const controls = createOutputControlsApi(api)
type Proposal = { paused: boolean; expectedVersion: string | null }

export function OutputControls({
    id,
    name,
    revision,
    state,
    backoffVersion,
    unavailable,
    refresh,
}: {
    id: string
    name: string
    revision: string
    state: string
    backoffVersion?: string | null
    unavailable: boolean
    refresh: () => Promise<void>
}) {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    const admin = role === 'administrator'
    const editor = admin || role === 'configurator'
    const [proposal, setProposal] = useState<Proposal>()
    const [confirmed, setConfirmed] = useState(false)
    const [uncertain, setUncertain] = useState(false)
    const [attempted, setAttempted] = useState<string>()
    const [notice, setNotice] = useState<string>()
    const invalidate = () => {
        void queryClient.invalidateQueries({ queryKey: ['runtime-output', id] })
        void queryClient.invalidateQueries({ queryKey: ['runtime-outputs'] })
    }
    const write = useMutation({
        retry: false,
        mutationFn: (snapshot: Proposal) =>
            controls.write(
                id,
                snapshot.paused,
                snapshot.expectedVersion,
                confirmed,
            ),
        onSuccess: (_, snapshot) => {
            setProposal(undefined)
            setConfirmed(false)
            setNotice(snapshot.paused ? 'pauseSaved' : 'resumeSaved')
        },
        onError: () => {
            setConfirmed(false)
            setUncertain(true)
        },
        onSettled: invalidate,
    })
    const check = useMutation({
        retry: false,
        mutationFn: async (paused: boolean) => ({
            paused,
            expectedVersion: (await controls.read(id)).resumeVersion,
        }),
        onMutate: () => {
            setProposal(undefined)
            setConfirmed(false)
            setNotice(undefined)
            write.reset()
        },
        onSuccess: (next) => {
            setProposal(next)
            setUncertain(false)
        },
    })
    const retry = useMutation({
        retry: false,
        mutationFn: (window: string) => controls.retry(id, revision, window),
        onMutate: (window) => {
            setAttempted(window)
            setNotice(undefined)
        },
        onSuccess: () => setNotice('retryAccepted'),
        onSettled: invalidate,
    })
    const readAgain = useMutation({
        retry: false,
        mutationFn: refresh,
        onSuccess: () => {
            setAttempted(undefined)
            retry.reset()
            setNotice(undefined)
            setProposal(undefined)
            setConfirmed(false)
        },
    })
    const busy =
        write.isPending ||
        check.isPending ||
        retry.isPending ||
        readAgain.isPending
    if (!editor) return null
    return (
        <section
            aria-label={t('outputControls.title')}
            className="space-y-3 rounded border p-4"
        >
            <h3 className="font-semibold">{t('outputControls.title')}</h3>
            <p className="text-sm text-muted-foreground">
                {t('outputControls.notice')}
            </p>
            <ErrorMessage
                error={
                    write.error || check.error || retry.error || readAgain.error
                }
            />
            {notice && <p role="status">{t(`outputControls.${notice}`)}</p>}
            {retry.isError && <p>{t('outputControls.retryUncertain')}</p>}
            <div className="flex flex-wrap gap-3">
                <Button
                    variant="outline"
                    disabled={
                        busy ||
                        unavailable ||
                        state !== 'backoff' ||
                        !backoffVersion ||
                        attempted === backoffVersion
                    }
                    onClick={() => {
                        if (backoffVersion) retry.mutate(backoffVersion)
                    }}
                >
                    {t('outputControls.retryNow')}
                </Button>
                <Button
                    variant="outline"
                    disabled={busy}
                    onClick={() => readAgain.mutate()}
                >
                    {t('outputControls.refresh')}
                </Button>
                {admin && (
                    <>
                        <Button
                            variant="outline"
                            disabled={
                                busy ||
                                unavailable ||
                                state === 'manuallyPaused'
                            }
                            onClick={() => check.mutate(true)}
                        >
                            {t('outputControls.pause')}
                        </Button>
                        <Button
                            variant="outline"
                            disabled={
                                busy ||
                                unavailable ||
                                ![
                                    'manuallyPaused',
                                    'automaticallyPaused',
                                ].includes(state)
                            }
                            onClick={() => check.mutate(false)}
                        >
                            {t('outputControls.resume')}
                        </Button>
                    </>
                )}
            </div>
            {!admin && (
                <p className="text-sm">{t('outputControls.adminOnly')}</p>
            )}
            {admin && proposal && (
                <div
                    className="space-y-3 rounded border p-4"
                    aria-label={t('outputControls.confirmation')}
                >
                    <p className="font-semibold">
                        {t(
                            proposal.paused
                                ? 'outputControls.pauseTarget'
                                : 'outputControls.resumeTarget',
                            { name, id },
                        )}
                    </p>
                    <p>
                        {t(
                            proposal.paused
                                ? 'outputControls.pauseBoundary'
                                : 'outputControls.resumeBoundary',
                        )}
                    </p>
                    {uncertain && (
                        <p role="alert">{t('outputControls.uncertain')}</p>
                    )}
                    <label className="flex items-center gap-2">
                        <input
                            type="checkbox"
                            checked={confirmed}
                            disabled={busy || unavailable || uncertain}
                            onChange={(event) =>
                                setConfirmed(event.target.checked)
                            }
                        />
                        {t('outputControls.confirm')}
                    </label>
                    <div className="flex flex-wrap gap-3">
                        <Button
                            disabled={
                                !confirmed || busy || unavailable || uncertain
                            }
                            onClick={() => write.mutate(proposal)}
                        >
                            {t('outputControls.submit')}
                        </Button>
                        {uncertain && (
                            <Button
                                variant="outline"
                                disabled={busy || unavailable}
                                onClick={() => check.mutate(proposal.paused)}
                            >
                                {t('outputControls.recheck')}
                            </Button>
                        )}
                        <Button
                            variant="outline"
                            disabled={busy}
                            onClick={() => {
                                setProposal(undefined)
                                setConfirmed(false)
                            }}
                        >
                            {t('outputControls.cancel')}
                        </Button>
                    </div>
                </div>
            )}
        </section>
    )
}
