import { useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, queryClient, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import { csvBase64, maxCsvBytes } from './csv-model'

export function PointCsvPanel({
    deviceId,
    version,
    blocked,
    canEdit,
}: {
    deviceId: number
    version: string
    blocked: boolean
    canEdit: boolean
}) {
    const { t } = useTranslation()
    const [file, setFile] = useState<File | null>(null)
    const [encoding, setEncoding] = useState<'utf8' | 'gbk'>('utf8')
    const [mode, setMode] = useState<'overwrite' | 'incremental'>('incremental')
    const [notice, setNotice] = useState('')
    const fileInput = useRef<HTMLInputElement>(null)
    const mutation = useMutation({
        retry: false,
        mutationFn: async (action: 'template' | 'export' | 'import') => {
            setNotice('')
            if (action !== 'import') {
                const { data } = await api.GET(
                    '/api/edge-admin/v1/devices/{deviceId}/points/csv',
                    {
                        params: {
                            path: { deviceId },
                            query: {
                                expectedVersion: version,
                                template: action === 'template',
                            },
                        },
                        parseAs: 'blob',
                    },
                )
                if (!data) throw new Error('missing-csv')
                const url = URL.createObjectURL(data)
                const link = document.createElement('a')
                link.href = url
                link.download =
                    action === 'template' ? 'point-template.csv' : 'points.csv'
                link.click()
                setTimeout(() => URL.revokeObjectURL(url), 1000)
                return
            }
            if (!file || file.size > maxCsvBytes) throw new Error('csv-size')
            const epoch = useSession.getState().epoch
            const content = csvBase64(new Uint8Array(await file.arrayBuffer()))
            if (useSession.getState().epoch !== epoch)
                throw new Error('stale-session-response')
            const { data } = await api.POST(
                '/api/edge-admin/v1/devices/{deviceId}/points/csv',
                {
                    params: { path: { deviceId } },
                    body: { expectedVersion: version, mode, encoding, content },
                },
            )
            if (!data) throw new Error('missing-import')
            setNotice(
                t('inputs.csv.imported', {
                    added: data.added,
                    updated: data.updated,
                    skipped: data.skipped,
                }),
            )
            setFile(null)
            if (fileInput.current) fileInput.current.value = ''
            await Promise.all([
                queryClient.invalidateQueries({
                    queryKey: ['input-drafts', deviceId],
                }),
                queryClient.invalidateQueries({ queryKey: ['device-drafts'] }),
                queryClient.invalidateQueries({
                    queryKey: ['connection-drafts'],
                }),
            ])
        },
    })
    return (
        <section
            className="space-y-3 rounded border p-3"
            aria-label={t('inputs.csv.title')}
        >
            <h2 className="font-semibold">{t('inputs.csv.title')}</h2>
            <p className="text-sm text-muted-foreground">
                {t('inputs.csv.help')}
            </p>
            {blocked && <p className="text-sm">{t('inputs.csv.blocked')}</p>}
            {mutation.error instanceof Error &&
            mutation.error.message === 'csv-size' ? (
                <p role="alert">{t('inputs.csv.size')}</p>
            ) : (
                <ErrorMessage error={mutation.error} />
            )}
            {notice && <p role="status">{notice}</p>}
            <fieldset
                disabled={blocked || mutation.isPending}
                className="space-y-3"
            >
                <div className="flex gap-2">
                    <Button
                        type="button"
                        variant="outline"
                        onClick={() => mutation.mutate('template')}
                    >
                        {t('inputs.csv.template')}
                    </Button>
                    <Button
                        type="button"
                        variant="outline"
                        onClick={() => mutation.mutate('export')}
                    >
                        {t('inputs.csv.export')}
                    </Button>
                </div>
                {canEdit && (
                    <>
                        <label className="block">
                            {t('inputs.csv.file')}
                            <input
                                ref={fileInput}
                                type="file"
                                accept=".csv,text/csv"
                                className="mt-1 block w-full"
                                onChange={(e) => {
                                    setFile(e.target.files?.[0] ?? null)
                                    mutation.reset()
                                    setNotice('')
                                }}
                            />
                        </label>
                        <div className="flex flex-wrap items-end gap-3">
                            <label>
                                {t('inputs.csv.encoding')}
                                <select
                                    className="ml-2 rounded border bg-background p-2"
                                    value={encoding}
                                    onChange={(e) =>
                                        setEncoding(
                                            e.target.value as 'utf8' | 'gbk',
                                        )
                                    }
                                >
                                    <option value="utf8">UTF-8</option>
                                    <option value="gbk">GBK</option>
                                </select>
                            </label>
                            <label>
                                {t('inputs.csv.mode')}
                                <select
                                    className="ml-2 rounded border bg-background p-2"
                                    value={mode}
                                    onChange={(e) =>
                                        setMode(
                                            e.target.value as
                                                'overwrite' | 'incremental',
                                        )
                                    }
                                >
                                    <option value="incremental">
                                        {t('inputs.csv.incremental')}
                                    </option>
                                    <option value="overwrite">
                                        {t('inputs.csv.overwrite')}
                                    </option>
                                </select>
                            </label>
                            <Button
                                type="button"
                                disabled={!file}
                                onClick={() => mutation.mutate('import')}
                            >
                                {t('inputs.csv.import')}
                            </Button>
                        </div>
                    </>
                )}
            </fieldset>
        </section>
    )
}
