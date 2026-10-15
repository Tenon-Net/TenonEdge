import { useEffect, useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, useSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ChangeDetails, ConfigurationError } from './view'
import { type ImportReview } from './api'
import { useDraftSummary } from './queries'
import { createConfigurationImportApi, maximumImportBytes } from './import-api'

const imports = createConfigurationImportApi(api)
type Action = 'prepare' | 'current' | 'confirm' | 'cancel'

export function ConfigurationImportPanel() {
    const { t } = useTranslation()
    const role = useSession((state) => state.value?.role)
    return (
        <section
            className="space-y-3 rounded-lg border p-5"
            aria-label={t('configurationImport.title')}
        >
            <h2 className="text-lg font-semibold">
                {t('configurationImport.title')}
            </h2>
            <p>{t('configurationImport.notice')}</p>
            <p className="text-sm text-muted-foreground">
                {t('configurationImport.boundary')}
            </p>
            {role === 'administrator' || role === 'configurator' ? (
                <ImportControls />
            ) : (
                <p>{t('configurationImport.denied')}</p>
            )}
        </section>
    )
}

function ImportControls() {
    const { t } = useTranslation()
    const queryClient = useQueryClient()
    const summary = useDraftSummary()
    const [file, setFile] = useState<File | null>(null)
    const [mode, setMode] = useState<'merge' | 'replace'>('merge')
    const [review, setReview] = useState<ImportReview | null>(null)
    const [accepted, setAccepted] = useState(false)
    const [changePage, setChangePage] = useState(0)
    const [credentialPage, setCredentialPage] = useState(0)
    const [notice, setNotice] = useState('')
    const controller = useRef<AbortController | null>(null)
    const actionable = useRef<ImportReview | null>(null)
    const working = useRef(false)
    useEffect(
        () => () => {
            controller.current?.abort()
        },
        [],
    )
    function clear() {
        actionable.current = null
        setReview(null)
        setAccepted(false)
        setChangePage(0)
        setCredentialPage(0)
    }
    const operation = useMutation({
        retry: false,
        mutationFn: async ({
            action,
            selected,
        }: {
            action: Action
            selected: ImportReview | null
        }) => {
            const abort = new AbortController()
            controller.current = abort
            const epoch = useSession.getState().epoch
            try {
                if (action === 'confirm' || action === 'cancel') {
                    if (!selected)
                        throw new Error('configuration-import-review')
                    if (action === 'confirm') {
                        const saved = await imports.confirm(
                            selected.id,
                            abort.signal,
                        )
                        if (
                            abort.signal.aborted ||
                            useSession.getState().epoch !== epoch
                        )
                            return
                        setNotice(
                            t('configurationImport.success', {
                                version: saved.version,
                            }),
                        )
                    } else {
                        await imports.cancel(selected.id, abort.signal)
                        if (
                            abort.signal.aborted ||
                            useSession.getState().epoch !== epoch
                        )
                            return
                        setNotice(t('configurationImport.cancelled'))
                    }
                    await queryClient.invalidateQueries()
                } else {
                    const next =
                        action === 'prepare'
                            ? await imports.prepare(
                                  file!,
                                  mode,
                                  summary.data!.version,
                                  abort.signal,
                              )
                            : await imports.current(abort.signal)
                    if (
                        abort.signal.aborted ||
                        useSession.getState().epoch !== epoch
                    )
                        return
                    actionable.current = next
                    setReview(next)
                    setNotice(next ? '' : t('configurationImport.noCandidate'))
                }
            } finally {
                if (controller.current === abort) controller.current = null
                working.current = false
            }
        },
    })
    function run(action: Action) {
        if (working.current) return
        const selected = actionable.current
        if ((action === 'confirm' || action === 'cancel') && !selected) return
        if (action === 'confirm' && !accepted) return
        working.current = true
        clear() // Invalidate synchronously before the request, not after a render.
        setNotice(action === 'confirm' ? t('configurationImport.unknown') : '')
        operation.mutate({ action, selected })
    }
    const pending = operation.isPending
    const tooLarge = file !== null && file.size > maximumImportBytes
    return (
        <div className="space-y-3">
            <ConfigurationError error={operation.error ?? summary.error} />
            <p className="break-all text-sm">
                {t('configurationImport.draftVersion', {
                    version: summary.data?.version ?? '—',
                })}
            </p>
            <label className="block space-y-1">
                <span>{t('configurationImport.file')}</span>
                <input
                    className="block w-full rounded border p-2"
                    type="file"
                    accept="application/json,.json"
                    disabled={pending}
                    onChange={(event) => {
                        clear()
                        setNotice('')
                        setFile(event.target.files?.[0] ?? null)
                    }}
                />
            </label>
            <label className="block space-y-1">
                <span>{t('configurationImport.mode')}</span>
                <select
                    className="block w-full rounded border bg-background p-2"
                    value={mode}
                    disabled={pending}
                    onChange={(event) => {
                        clear()
                        setNotice('')
                        setMode(
                            event.target.value === 'replace'
                                ? 'replace'
                                : 'merge',
                        )
                    }}
                >
                    <option value="merge">
                        {t('configurationImport.merge')}
                    </option>
                    <option value="replace">
                        {t('configurationImport.replace')}
                    </option>
                </select>
            </label>
            {tooLarge && (
                <p role="alert">{t('configurationImport.tooLarge')}</p>
            )}
            <div className="flex flex-wrap gap-2">
                <Button
                    disabled={
                        pending ||
                        !file ||
                        tooLarge ||
                        !summary.data ||
                        summary.isError
                    }
                    onClick={() => run('prepare')}
                >
                    {t('configurationImport.prepare')}
                </Button>
                <Button
                    variant="outline"
                    disabled={pending}
                    onClick={() => run('current')}
                >
                    {t('configurationImport.current')}
                </Button>
                <Button
                    variant="outline"
                    disabled={pending || summary.isFetching}
                    onClick={() => {
                        clear()
                        void summary.refetch()
                    }}
                >
                    {t('configurationImport.readDraft')}
                </Button>
                {pending && (
                    <Button
                        variant="outline"
                        onClick={() => {
                            clear()
                            controller.current?.abort()
                        }}
                    >
                        {t('configurationImport.cancelRequest')}
                    </Button>
                )}
            </div>
            {notice && <p role="status">{notice}</p>}
            {review && (
                <div className="space-y-3 rounded border p-4">
                    <h3 className="break-all font-semibold">
                        {t('configurationImport.review', {
                            mode: t('configurationImport.' + review.mode),
                            version: review.version,
                        })}
                    </h3>
                    <p className="break-all text-sm">
                        {t('configurationImport.expires', {
                            id: review.id,
                            time: new Date(review.expiresAt).toLocaleString(
                                'zh-CN',
                            ),
                        })}
                    </p>
                    {review.changes.length === 0 && (
                        <p>{t('configurationImport.noChanges')}</p>
                    )}
                    {review.changes
                        .slice(changePage * 25, (changePage + 1) * 25)
                        .map((change) => (
                            <ChangeDetails
                                key={`${change.kind}/${change.id}`}
                                change={change}
                            />
                        ))}
                    <ImportPages
                        count={review.changes.length}
                        page={changePage}
                        change={setChangePage}
                        label={t('configuration.changes')}
                    />
                    <h4 className="font-semibold">
                        {t('configurationImport.credentials')}
                    </h4>
                    {review.credentials.length === 0 && (
                        <p>{t('configurationImport.none')}</p>
                    )}
                    {review.credentials
                        .slice(credentialPage * 25, (credentialPage + 1) * 25)
                        .map((value) => (
                            <p
                                key={String(value.connectionId)}
                                className="break-all"
                            >
                                {value.connectionKey} ·{' '}
                                {t(
                                    'configurationImport.actions.' +
                                        value.action,
                                    {
                                        defaultValue: value.action,
                                    },
                                )}{' '}
                                ·{' '}
                                {t(
                                    'configurationImport.reasons.' +
                                        value.reason,
                                    { defaultValue: value.reason },
                                )}
                            </p>
                        ))}
                    <ImportPages
                        count={review.credentials.length}
                        page={credentialPage}
                        change={setCredentialPage}
                        label={t('configurationImport.credentials')}
                    />
                    <label className="flex items-start gap-2">
                        <input
                            type="checkbox"
                            checked={accepted}
                            onChange={(event) =>
                                setAccepted(event.target.checked)
                            }
                        />
                        {t('configurationImport.accept')}
                    </label>
                    <div className="flex flex-wrap gap-2">
                        <Button
                            disabled={pending || !accepted}
                            onClick={() => run('confirm')}
                        >
                            {t('configurationImport.confirm')}
                        </Button>
                        <Button
                            variant="outline"
                            disabled={pending}
                            onClick={() => run('cancel')}
                        >
                            {t('configurationImport.cancel')}
                        </Button>
                    </div>
                </div>
            )}
        </div>
    )
}

function ImportPages({
    count,
    page,
    change,
    label,
}: {
    count: number
    page: number
    change: (page: number) => void
    label: string
}) {
    const { t } = useTranslation()
    if (count <= 25) return null
    return (
        <nav aria-label={label} className="flex flex-wrap items-center gap-2">
            <span>
                {t('configurationImport.page', {
                    page: page + 1,
                    total: Math.ceil(count / 25),
                    count,
                })}
            </span>
            <Button
                variant="outline"
                disabled={page === 0}
                onClick={() => change(page - 1)}
            >
                {t('configurationImport.previous')}
            </Button>
            <Button
                variant="outline"
                disabled={(page + 1) * 25 >= count}
                onClick={() => change(page + 1)}
            >
                {t('configurationImport.next')}
            </Button>
        </nav>
    )
}
