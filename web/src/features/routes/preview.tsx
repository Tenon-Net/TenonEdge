import { useEffect, useMemo, useRef, useState } from 'react'
import { useStore } from 'zustand'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/session'
import { ApiError } from '../../api/errors'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from '../auth/error-message'
import type { GraphDocument } from '../canvas/model'
import { createRouteApi, newRoute } from './api'
import {
    createPreviewDraft,
    previewSources,
    type PreviewDraft,
} from './preview-model'

const routes = createRouteApi(api)
type Response = Awaited<ReturnType<typeof routes.preview>>
type Result = {
    document: GraphDocument
    version: string
    source: string
    sample: string
    signal: AbortSignal
    pending: boolean
    data?: Response
    error?: unknown
}
type Props = {
    document: GraphDocument
    version: string
    disabled: boolean
    draft?: PreviewDraft
    locate: (id: string) => void
}
export function RoutePreview({
    document,
    version,
    disabled,
    draft,
    locate,
}: Props) {
    const { t } = useTranslation()
    const [store] = useState(() => draft ?? createPreviewDraft())
    const { source, sample } = useStore(store)
    const options = useMemo(() => previewSources(document), [document])
    const selected = options.sources.find((item) => item.key === source)
    // One bounded metadata lookup for the selected id; never adopt its version as the editing version.
    const metadata = useQuery({
        queryKey: ['route-preview-source', selected?.kind, selected?.id],
        enabled: !!selected,
        queryFn: async ({ signal }) => {
            if (!selected) throw new Error('source')
            const { data } = await api.GET(
                '/api/edge-admin/v1/routes/catalog',
                {
                    params: {
                        query: {
                            kind: selected.kind,
                            afterId: selected.id - 1,
                            limit: 1,
                        },
                    },
                    signal,
                },
            )
            if (!data) throw new Error('catalog')
            return (
                data.items.find((item) => Number(item.id) === selected.id) ??
                null
            )
        },
    })
    const [result, setResult] = useState<Result>()
    const request = useRef<AbortController | null>(null)
    useEffect(
        () => () => request.current?.abort(),
        [document, version, source, sample, disabled],
    )
    const current =
        !disabled &&
        result?.document === document &&
        result.version === version &&
        result.source === source &&
        result.sample === sample &&
        !result.signal.aborted
            ? result
            : undefined
    const bytes = useMemo(
        () => new TextEncoder().encode(sample).length,
        [sample],
    )
    const invalid =
        bytes > 65536 || !sample.trim() || !selected || options.overLimit
    async function preview() {
        if (disabled || invalid) return
        request.current?.abort()
        const controller = new AbortController()
        request.current = controller
        const captured = {
            document,
            version,
            source,
            sample,
            signal: controller.signal,
        }
        setResult({ ...captured, pending: true })
        try {
            const data = await routes.preview(
                { ...newRoute(version), document },
                selected!.id,
                sample,
                controller.signal,
            )
            if (!controller.signal.aborted)
                setResult({ ...captured, pending: false, data })
        } catch (error) {
            if (!controller.signal.aborted)
                setResult({ ...captured, pending: false, error })
        }
    }
    const nodeName = (id: string) =>
        document.definition.nodes.find((node) => node.id === id)?.name || id
    const nodeButton = (id: string) => (
        <Button
            type="button"
            variant="outline"
            disabled={disabled}
            onClick={() => locate(id)}
        >
            {t('routes.preview.locate', { name: nodeName(id) })}
        </Button>
    )
    const data = current?.data
    return (
        <section
            className="space-y-3 rounded-md border p-3"
            aria-label={t('routes.preview.title')}
        >
            <h2 className="font-medium">{t('routes.preview.title')}</h2>
            <p className="text-sm">{t('routes.preview.notice')}</p>
            <div className="grid gap-3 lg:grid-cols-2">
                <div className="space-y-2">
                    <label className="block">
                        {t('routes.preview.source')}
                        <select
                            aria-label={t('routes.preview.source')}
                            className="block w-full rounded border bg-background p-2"
                            value={selected ? source : ''}
                            disabled={disabled}
                            onChange={(event) =>
                                store.setState({ source: event.target.value })
                            }
                        >
                            <option value="">
                                {t('routes.preview.choose')}
                            </option>
                            {options.sources.map((item) => (
                                <option key={item.key} value={item.key}>
                                    {item.nodeName} · #{item.id} ·{' '}
                                    {t(`canvas.kinds.${item.kind}`)}
                                    {item.nodeCount > 1
                                        ? t('routes.preview.shared', {
                                              count: item.nodeCount,
                                          })
                                        : ''}
                                </option>
                            ))}
                        </select>
                    </label>
                    {metadata.data && (
                        <p>
                            {metadata.data.deviceName} / {metadata.data.name} (
                            {metadata.data.key})
                        </p>
                    )}
                    <ErrorMessage error={metadata.error} />
                    {selected && (
                        <p className="text-sm">
                            {t(
                                selected.kind === 'input.http' ||
                                    selected.kind === 'input.mqtt'
                                    ? 'routes.preview.rawHelp'
                                    : 'routes.preview.fieldsHelp',
                            )}
                        </p>
                    )}
                    {!!options.invalidNodes.length && (
                        <div role="alert">
                            <p>{t('routes.preview.invalidReferences')}</p>
                            {options.invalidNodes.map((id) => (
                                <span key={id}>{nodeButton(id)}</span>
                            ))}
                        </div>
                    )}
                    {options.overLimit && (
                        <p role="alert">{t('routes.preview.sourceLimit')}</p>
                    )}
                    {!options.sources.length && (
                        <p>{t('routes.preview.noSources')}</p>
                    )}
                </div>
                <label className="block">
                    {t('routes.preview.sample')}
                    <textarea
                        aria-label={t('routes.preview.sample')}
                        className="block w-full rounded border p-2 font-mono text-sm"
                        rows={8}
                        value={sample}
                        disabled={disabled}
                        maxLength={65536}
                        spellCheck={false}
                        onChange={(event) =>
                            store.setState({ sample: event.target.value })
                        }
                    />
                    <span
                        className={
                            bytes > 65536
                                ? 'text-sm text-destructive'
                                : 'text-sm'
                        }
                    >
                        {t('routes.preview.bytes', { bytes })}
                    </span>
                </label>
            </div>
            <div className="flex flex-wrap gap-2">
                <Button
                    type="button"
                    disabled={disabled || invalid || current?.pending}
                    onClick={() => void preview()}
                >
                    {t('routes.preview.run')}
                </Button>
                {current?.pending && (
                    <Button
                        type="button"
                        variant="outline"
                        onClick={() => {
                            request.current?.abort()
                            setResult(undefined)
                        }}
                    >
                        {t('routes.preview.cancel')}
                    </Button>
                )}
                <Button
                    type="button"
                    variant="outline"
                    disabled={disabled}
                    onClick={() => {
                        request.current?.abort()
                        setResult(undefined)
                        store.setState({ sample: '' })
                    }}
                >
                    {t('routes.preview.clear')}
                </Button>
            </div>
            {current?.pending && (
                <p role="status">{t('routes.preview.pending')}</p>
            )}
            {!current && (
                <p className="text-sm">{t('routes.preview.refresh')}</p>
            )}
            {current?.error instanceof ApiError &&
            current.error.status === 409 ? (
                <p role="alert">{t('routes.conflict')}</p>
            ) : (
                <ErrorMessage error={current?.error} />
            )}
            {data && (
                <div
                    className="space-y-3"
                    aria-label={t('routes.preview.results')}
                >
                    {!data.previewed && (
                        <p role="alert">{t('routes.preview.invalid')}</p>
                    )}
                    {data.issues.map((issue, index) => (
                        <p key={index}>
                            {issue.nodeId && nodeButton(issue.nodeId)}{' '}
                            {issue.code} {issue.fieldKey}
                        </p>
                    ))}
                    {data.invalidUpstreamTime && (
                        <p role="alert">{t('routes.preview.invalidTime')}</p>
                    )}
                    {data.result && (
                        <>
                            <p role="status">
                                {t('routes.preview.summary', {
                                    total: data.result.totalPaths,
                                    succeeded: data.result.succeededPaths,
                                    version: data.version,
                                })}
                            </p>
                            <p className="text-sm">
                                {t('routes.preview.time', {
                                    time: data.result.collectedAt,
                                    source: data.result.timeSource,
                                })}
                            </p>
                            {data.result.truncated && (
                                <p role="alert">
                                    {t('routes.preview.truncated')}
                                </p>
                            )}
                            {data.result.source && (
                                <PreviewFields
                                    stage={data.result.source}
                                    title={t('routes.preview.input')}
                                />
                            )}
                            {data.result.paths.map((path, index) => (
                                <section
                                    key={index}
                                    className="space-y-2 rounded border p-3"
                                    aria-label={t('routes.preview.path', {
                                        index: index + 1,
                                    })}
                                >
                                    <h3 className="font-medium">
                                        {t('routes.preview.path', {
                                            index: index + 1,
                                        })}{' '}
                                        ·{' '}
                                        {t(
                                            path.succeeded
                                                ? 'routes.preview.success'
                                                : 'routes.preview.failure',
                                        )}
                                    </h3>
                                    <p className="text-sm break-words">
                                        {path.nodes.map(nodeName).join(' → ')}
                                    </p>
                                    {path.issues.map((issue, i) => (
                                        <p key={i} role="alert">
                                            {issue.nodeId &&
                                                nodeButton(issue.nodeId)}{' '}
                                            {issue.code} {issue.fieldKey}
                                        </p>
                                    ))}
                                    {path.stages.map((stage, i) => (
                                        <div key={i}>
                                            {nodeButton(stage.nodeId)}
                                            <PreviewFields
                                                stage={stage}
                                                title={nodeName(stage.nodeId)}
                                            />
                                        </div>
                                    ))}
                                    {path.output && (
                                        <div className="space-y-2">
                                            {nodeButton(path.output.nodeId)}
                                            <p className="break-all">
                                                {path.output.kind} ·{' '}
                                                {path.output.address}{' '}
                                                {path.output.qos !== null &&
                                                    `QoS ${path.output.qos}`}
                                            </p>
                                            <p className="break-all text-xs">
                                                {t('routes.preview.delivery', {
                                                    id: path.output.deliveryId,
                                                })}
                                            </p>
                                            {!!Object.keys(path.output.headers)
                                                .length && (
                                                <pre className="max-h-48 overflow-auto whitespace-pre-wrap text-xs">
                                                    {Object.entries(
                                                        path.output.headers,
                                                    )
                                                        .map(
                                                            ([key, value]) =>
                                                                `${key}: ${value}`,
                                                        )
                                                        .join('\n')}
                                                </pre>
                                            )}
                                            <p>
                                                {t('routes.preview.bodyBytes', {
                                                    bytes: path.output
                                                        .bodyBytes,
                                                })}
                                            </p>
                                            {path.output.truncated && (
                                                <p role="alert">
                                                    {t(
                                                        'routes.preview.bodyTruncated',
                                                    )}
                                                </p>
                                            )}
                                            <pre
                                                aria-label={t(
                                                    'routes.preview.body',
                                                )}
                                                className="max-h-96 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2 text-sm"
                                            >
                                                {path.output.body}
                                            </pre>
                                        </div>
                                    )}
                                </section>
                            ))}
                        </>
                    )}
                </div>
            )}
        </section>
    )
}

type Stage = NonNullable<Response['result']>['source']
function PreviewFields({
    stage,
    title,
}: {
    stage: NonNullable<Stage>
    title: string
}) {
    const { t } = useTranslation()
    return (
        <details className="space-y-2" open>
            <summary>{title}</summary>
            {stage.truncated && (
                <p role="alert">{t('routes.preview.fieldsTruncated')}</p>
            )}
            {stage.issues.map((issue, index) => (
                <p key={index}>
                    {issue.field}: {issue.reason ?? t('routes.preview.missing')}{' '}
                    {issue.required && t('routes.preview.required')}
                </p>
            ))}
            <div className="max-h-64 overflow-auto">
                <table className="w-full text-sm">
                    <thead>
                        <tr>
                            {(
                                [
                                    'field',
                                    'type',
                                    'unit',
                                    'value',
                                    'quality',
                                ] as const
                            ).map((key) => (
                                <th
                                    key={key}
                                    scope="col"
                                    className="p-1 text-left"
                                >
                                    {t(`routes.preview.${key}`)}
                                </th>
                            ))}
                        </tr>
                    </thead>
                    <tbody>
                        {stage.fields.map((field) => (
                            <tr key={field.key}>
                                <td className="p-1">{field.key}</td>
                                <td className="p-1">{field.type}</td>
                                <td className="p-1">{field.unit ?? '—'}</td>
                                <td className="max-w-xl break-all p-1 font-mono">
                                    {field.text ?? '—'}
                                </td>
                                <td className="p-1">
                                    {field.quality} {field.reason ?? ''}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
        </details>
    )
}
