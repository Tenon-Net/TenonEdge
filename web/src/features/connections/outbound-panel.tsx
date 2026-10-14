import { useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useBlocker } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { api, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { createSystemApi } from '../system/api'
import {
    createOutboundInspectionApi,
    outboundGrantRequest,
    type OutboundTarget,
} from './outbound-api'

const inspect = createOutboundInspectionApi(api)
const settings = createSystemApi(api)

export function ConnectionOutboundPanel({
    target,
    onBusy,
}: {
    target: OutboundTarget
    onBusy: (busy: boolean) => void
}) {
    const { t } = useTranslation()
    const administrator = useSession((s) => s.value?.role) === 'administrator'
    const query = useQuery({
        queryKey: ['connection-outbound', target.host, target.port],
        queryFn: ({ signal }) => inspect(target, signal),
        retry: false,
        gcTime: 0,
        staleTime: Infinity,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
    })
    const [confirmed, confirm] = useState(false)
    const [busy, setBusy] = useState(false)
    const [needsCheck, requireCheck] = useState(false)
    const [error, setError] = useState<unknown>(null)
    const [saved, setSaved] = useState(false)
    const sending = useRef(false)
    useBlocker({
        disabled: !busy,
        enableBeforeUnload: busy,
        shouldBlockFn: () => !window.confirm(t('connections.outbound.leaving')),
    })
    const view = query.error || query.isFetching ? null : query.data
    const grants =
        view?.addresses.filter(
            (row) => row.decision === 'publicGrantRequired' && row.grant,
        ) ?? []
    let request: ReturnType<typeof outboundGrantRequest> | undefined
    if (view && grants.length) {
        try {
            request = outboundGrantRequest(view)
        } catch {
            /* Explicit limit message below; no partial write. */
        }
    }
    async function refresh() {
        confirm(false)
        setError(null)
        setSaved(false)
        const read = await query.refetch()
        requireCheck(!!read.error)
    }
    async function grant() {
        if (
            !administrator ||
            !request ||
            !confirmed ||
            busy ||
            needsCheck ||
            query.isFetching ||
            sending.current
        )
            return
        sending.current = true
        setBusy(true)
        onBusy(true)
        confirm(false)
        setError(null)
        setSaved(false)
        try {
            await settings.writePolicy(request, true)
            setSaved(true)
            const read = await query.refetch()
            requireCheck(!!read.error)
        } catch (failure) {
            setError(failure)
            requireCheck(true) // Even a correctable server rejection needs fresh immutable advice; never replay.
        } finally {
            sending.current = false
            setBusy(false)
            onBusy(false)
        }
    }
    return (
        <section
            className="space-y-3 rounded-lg border bg-card p-4"
            aria-label={t('connections.outbound.title')}
        >
            <h2 className="font-semibold">{t('connections.outbound.title')}</h2>
            <p>{t('connections.outbound.target', target)}</p>
            <p className="text-sm text-muted-foreground">
                {t('connections.outbound.notice')}
            </p>
            <Button
                variant="outline"
                disabled={busy || query.isFetching}
                onClick={() => void refresh()}
            >
                {t('connections.outbound.refresh')}
            </Button>
            <Link className="ml-3 underline" to="/system">
                {t('connections.outbound.system')}
            </Link>
            {query.isFetching && (
                <p role="status">{t('connections.outbound.checking')}</p>
            )}
            <ErrorMessage error={query.error ?? error} />
            {needsCheck && (
                <p role="alert">{t('connections.outbound.unknown')}</p>
            )}
            {saved && <p role="status">{t('connections.outbound.saved')}</p>}
            {view && (
                <>
                    <ul className="space-y-1 text-sm">
                        {view.addresses.map((row) => (
                            <li key={row.address}>
                                <code>
                                    {row.address}:{String(view.port)}
                                </code>{' '}
                                —{' '}
                                {t(
                                    `connections.outbound.decisions.${row.decision}`,
                                )}
                                {row.grant && (
                                    <span>
                                        {' '}
                                        · {row.grant.cidr} /{' '}
                                        {String(row.grant.port)}
                                    </span>
                                )}
                            </li>
                        ))}
                    </ul>
                    {grants.length > 0 &&
                        (administrator ? (
                            <>
                                <p>{t('connections.outbound.risk')}</p>
                                {!request && (
                                    <p role="alert">
                                        {t('connections.outbound.limit')}
                                    </p>
                                )}
                                <label className="flex items-center gap-2 text-sm">
                                    <input
                                        type="checkbox"
                                        checked={confirmed}
                                        disabled={
                                            busy || needsCheck || !request
                                        }
                                        onChange={(event) =>
                                            confirm(event.target.checked)
                                        }
                                    />
                                    {t('connections.outbound.confirm')}
                                </label>
                                <Button
                                    disabled={
                                        busy ||
                                        needsCheck ||
                                        !confirmed ||
                                        !request
                                    }
                                    onClick={() => void grant()}
                                >
                                    {t('connections.outbound.grant')}
                                </Button>
                            </>
                        ) : (
                            <p role="alert">
                                {t('connections.outbound.contact')}
                            </p>
                        ))}
                </>
            )}
        </section>
    )
}
