import { useEffect, useRef } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { exportErrorKey } from './export-error'

export function ConfigurationExportPanel() {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    const controller = useRef<AbortController | null>(null)
    useEffect(() => () => controller.current?.abort(), [])
    const download = useMutation({
        retry: false,
        mutationFn: async () => {
            const epoch = useSession.getState().epoch
            const abort = new AbortController()
            controller.current = abort
            try {
                const { data } = await api.GET(
                    '/api/edge-admin/v1/configuration/export',
                    {
                        parseAs: 'blob',
                        signal: abort.signal,
                    },
                )
                if (
                    !data ||
                    abort.signal.aborted ||
                    useSession.getState().epoch !== epoch
                )
                    throw new Error('stale-or-missing-configuration-export')
                // Download raw UTF-8 bytes; JSON.parse/stringify would round integers.
                const url = URL.createObjectURL(data)
                const link = document.createElement('a')
                link.href = url
                link.download = 'tenonedge-configuration.json'
                link.click()
                setTimeout(() => URL.revokeObjectURL(url), 1000)
            } finally {
                if (controller.current === abort) controller.current = null
            }
        },
    })
    const canExport = role === 'administrator' || role === 'configurator'
    const errorKey = exportErrorKey(download.error)
    return (
        <section
            className="space-y-3 rounded-lg border p-5"
            aria-label={t('configurationExport.title')}
        >
            <h2 className="text-lg font-semibold">
                {t('configurationExport.title')}
            </h2>
            <p>{t('configurationExport.notice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('configurationExport.scope')}
            </p>
            {canExport ? (
                <>
                    {errorKey ? (
                        <p role="alert" className="text-destructive">
                            {t(errorKey)}
                        </p>
                    ) : (
                        <ErrorMessage error={download.error} />
                    )}
                    <Button
                        disabled={download.isPending}
                        onClick={() => download.mutate()}
                    >
                        {t(
                            download.isPending
                                ? 'pending'
                                : 'configurationExport.download',
                        )}
                    </Button>
                    {download.isSuccess && (
                        <p role="status">{t('configurationExport.ready')}</p>
                    )}
                </>
            ) : (
                <p>{t('configurationExport.denied')}</p>
            )}
        </section>
    )
}
