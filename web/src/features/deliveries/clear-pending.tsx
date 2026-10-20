import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { createOutputControlsApi } from './controls-api'

const controls = createOutputControlsApi(api)
export function ClearPending({
    id,
    name,
    revision,
    count,
    unavailable,
    refresh,
}: {
    id: string
    name: string
    revision: string
    count: string
    unavailable: boolean
    refresh: () => Promise<void>
}) {
    const { t } = useTranslation()
    const role = useSession((s) => s.value?.role)
    const [proposal, setProposal] = useState<string>()
    const [confirmed, setConfirmed] = useState(false)
    const [uncertain, setUncertain] = useState(false)
    const command = useMutation({
        retry: false,
        mutationFn: (accepted: boolean) =>
            controls.clearPending(id, revision, accepted),
        onMutate: () => setConfirmed(false),
        onSuccess: () => setProposal(undefined),
        onError: () => {
            setUncertain(true)
            setProposal(undefined)
        },
    })
    const read = useMutation({
        retry: false,
        mutationFn: refresh,
        onSuccess: () => {
            setUncertain(false)
            setProposal(undefined)
            setConfirmed(false)
            command.reset()
        },
    })
    const busy = command.isPending || read.isPending
    if (role !== 'administrator') return null
    return (
        <section
            aria-label={t('outputControls.clear.title')}
            className="space-y-3 rounded border p-4"
        >
            <h3 className="font-semibold">{t('outputControls.clear.title')}</h3>
            <p>{t('outputControls.clear.boundary')}</p>
            <p className="text-sm text-muted-foreground">
                {t('outputControls.clear.safety')}
            </p>
            <ErrorMessage error={command.error || read.error} />
            {uncertain && (
                <p role="alert">{t('outputControls.clear.unknown')}</p>
            )}
            {command.isPending && (
                <p role="status">{t('outputControls.clear.waiting')}</p>
            )}
            {command.isSuccess && (
                <p role="status">
                    {t('outputControls.clear.done', { count: command.data })}
                </p>
            )}
            <div className="flex flex-wrap gap-3">
                <Button
                    variant="outline"
                    disabled={busy || unavailable || uncertain}
                    onClick={() => {
                        command.reset()
                        setConfirmed(false)
                        setProposal(count)
                    }}
                >
                    {t('outputControls.clear.prepare')}
                </Button>
                <Button
                    variant="outline"
                    disabled={busy}
                    onClick={() => read.mutate()}
                >
                    {t('outputControls.clear.refresh')}
                </Button>
            </div>
            {proposal !== undefined && (
                <div className="space-y-3 rounded border p-4">
                    <p className="font-semibold">
                        {t('outputControls.clear.target', {
                            name,
                            id,
                            count: proposal,
                            revision,
                        })}
                    </p>
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={confirmed}
                            disabled={busy || unavailable || uncertain}
                            onChange={(event) =>
                                setConfirmed(event.target.checked)
                            }
                        />
                        {t('outputControls.clear.confirmation')}
                    </label>
                    <div className="flex flex-wrap gap-3">
                        <Button
                            variant="destructive"
                            disabled={
                                !confirmed || busy || unavailable || uncertain
                            }
                            onClick={() => command.mutate(confirmed)}
                        >
                            {t('outputControls.clear.submit')}
                        </Button>
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
