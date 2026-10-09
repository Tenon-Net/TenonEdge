import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import type { JsonField } from './model'
import {
    inferSample,
    maxSampleBytes,
    selectedFields,
    type SampleField,
} from './sample'

export function SampleEditor({
    fields,
    append,
}: {
    fields: JsonField[]
    append: (fields: JsonField[]) => void
}) {
    const { t } = useTranslation()
    // Intentionally not in query cache, draft DTO, browser storage or logging.
    const [sample, setSample] = useState('')
    const [candidates, setCandidates] = useState<SampleField[]>([])
    const [selected, setSelected] = useState<Set<string>>(new Set())
    const [error, setError] = useState('')
    const clear = () => {
        setSample('')
        setCandidates([])
        setSelected(new Set())
        setError('')
    }
    const fail = (error: unknown) =>
        setError(
            error instanceof Error &&
                ['json', 'limit', 'browser', 'duplicate'].includes(
                    error.message,
                )
                ? error.message
                : 'json',
        )
    return (
        <section
            className="space-y-3 rounded border p-3"
            aria-label={t('inputs.sample.title')}
        >
            <label className="block">
                {t('inputs.sample.text')}
                <textarea
                    className="mt-1 h-28 w-full rounded border bg-background p-2 font-mono"
                    value={sample}
                    maxLength={maxSampleBytes}
                    autoComplete="off"
                    spellCheck={false}
                    onChange={(e) => {
                        setSample(e.target.value)
                        setCandidates([])
                        setSelected(new Set())
                        setError('')
                    }}
                />
            </label>
            <p className="text-sm text-muted-foreground">
                {t('inputs.sample.help')}
            </p>
            <div className="flex flex-wrap gap-2">
                <Button
                    type="button"
                    variant="outline"
                    onClick={() => {
                        try {
                            setCandidates(inferSample(sample))
                            setSelected(new Set())
                            setError('')
                        } catch (error) {
                            setCandidates([])
                            setSelected(new Set())
                            fail(error)
                        }
                    }}
                >
                    {t('inputs.sample.infer')}
                </Button>
                <Button type="button" variant="outline" onClick={clear}>
                    {t('inputs.sample.clear')}
                </Button>
                <Button
                    type="button"
                    disabled={selected.size === 0}
                    onClick={() => {
                        try {
                            append(selectedFields(candidates, selected, fields))
                            clear()
                        } catch (error) {
                            fail(error)
                        }
                    }}
                >
                    {t('inputs.sample.add')}
                </Button>
            </div>
            {error && <p role="alert">{t('inputs.sample.errors.' + error)}</p>}
            {candidates.length > 0 && (
                <div className="max-h-96 overflow-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr>
                                {['select', 'path', 'type', 'value'].map(
                                    (column) => (
                                        <th
                                            className="p-2 text-left"
                                            key={column}
                                        >
                                            {t(
                                                'inputs.sample.columns.' +
                                                    column,
                                            )}
                                        </th>
                                    ),
                                )}
                            </tr>
                        </thead>
                        <tbody>
                            {candidates.map((field) => (
                                <tr key={field.path}>
                                    <td className="p-2">
                                        <input
                                            type="checkbox"
                                            disabled={field.type === null}
                                            checked={selected.has(field.path)}
                                            aria-label={t(
                                                'inputs.sample.select',
                                                {
                                                    path:
                                                        field.path || '(root)',
                                                },
                                            )}
                                            onChange={(e) =>
                                                setSelected((current) => {
                                                    const next = new Set(
                                                        current,
                                                    )
                                                    if (e.target.checked)
                                                        next.add(field.path)
                                                    else next.delete(field.path)
                                                    return next
                                                })
                                            }
                                        />
                                    </td>
                                    <td className="max-w-64 break-all p-2 font-mono">
                                        {field.path || '(root)'}
                                    </td>
                                    <td className="p-2">
                                        {field.type ?? t('inputs.sample.null')}
                                    </td>
                                    <td className="max-w-80 break-all p-2 font-mono">
                                        {field.preview.length > 256
                                            ? field.preview.slice(0, 256) + '…'
                                            : field.preview}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
        </section>
    )
}
