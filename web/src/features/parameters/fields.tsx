import { useId } from 'react'
import { useTranslation } from 'react-i18next'
import type {
    ParameterConnection,
    ParameterIssue,
    ParameterSchema,
    ParameterValues,
} from './model'

const fieldClass =
    'mt-1 w-full rounded-md border bg-background p-2 disabled:opacity-60'
/** Controlled scalar fields. Secrets use an isolated callback, never onChange/values. */
export function ParameterFields({
    schema,
    values,
    onChange,
    connections = [],
    configuredCredentials = new Set(),
    onCredentialChange,
    issues = [],
    hidden = [],
}: {
    schema: ParameterSchema
    values: ParameterValues
    onChange: (name: string, value: string | boolean) => void
    connections?: readonly ParameterConnection[]
    configuredCredentials?: ReadonlySet<string>
    onCredentialChange?: (name: string, secret: string) => void
    issues?: readonly ParameterIssue[]
    hidden?: readonly string[]
}) {
    const { t } = useTranslation()
    const prefix = useId()
    const groups = new Map<string, NonNullable<ParameterSchema['parameters']>>()
    for (const field of schema.parameters ?? []) {
        if (hidden.includes(field.name)) continue
        const group = field.group ?? ''
        groups.set(group, [...(groups.get(group) ?? []), field])
    }
    return (
        <>
            {Array.from(groups, ([group, fields]) => (
                <fieldset
                    key={group}
                    className="grid gap-4 rounded-md border p-3 md:col-span-2 md:grid-cols-2"
                >
                    {group && <legend className="px-1 text-sm">{group}</legend>}
                    {fields.map((field) => {
                        const id = `${prefix}-${field.name}`
                        const error = issues.find(
                            (issue) => issue.name === field.name,
                        )
                        const value = values[field.name]
                        const options =
                            field.type === 'connectionReference'
                                ? connections
                                      .filter((connection) =>
                                          field.connectionKinds?.includes(
                                              connection.kind,
                                          ),
                                      )
                                      .map((connection) => ({
                                          value: connection.id,
                                          displayName: connection.name,
                                      }))
                                : (field.options ?? [])
                        const common = {
                            id,
                            'aria-describedby': `${id}-help`,
                            'aria-invalid': !!error,
                        }
                        return (
                            <div key={field.name}>
                                <label htmlFor={id}>{field.displayName}</label>
                                {field.type === 'switch' ? (
                                    <input
                                        {...common}
                                        className="ml-2"
                                        type="checkbox"
                                        checked={value === true}
                                        onChange={(event) =>
                                            onChange(
                                                field.name,
                                                event.target.checked,
                                            )
                                        }
                                    />
                                ) : field.type === 'choice' ||
                                  field.type === 'connectionReference' ? (
                                    <select
                                        {...common}
                                        className={fieldClass}
                                        value={
                                            typeof value === 'string'
                                                ? value
                                                : ''
                                        }
                                        required={field.required}
                                        onChange={(event) =>
                                            onChange(
                                                field.name,
                                                event.target.value,
                                            )
                                        }
                                    >
                                        <option value="">
                                            {t('parameters.select')}
                                        </option>
                                        {value &&
                                            !options.some(
                                                (option) =>
                                                    option.value === value,
                                            ) && (
                                                <option
                                                    value={String(value)}
                                                    disabled
                                                >
                                                    {t('parameters.unknown')}
                                                </option>
                                            )}
                                        {options.map((option) => (
                                            <option
                                                key={option.value}
                                                value={option.value}
                                            >
                                                {option.displayName}
                                            </option>
                                        ))}
                                    </select>
                                ) : field.type === 'credential' ? (
                                    <input
                                        {...common}
                                        className={fieldClass}
                                        type="password"
                                        autoComplete="new-password"
                                        disabled={!onCredentialChange}
                                        onChange={(event) =>
                                            onCredentialChange?.(
                                                field.name,
                                                event.target.value,
                                            )
                                        }
                                    />
                                ) : (
                                    <input
                                        {...common}
                                        className={fieldClass}
                                        type="text"
                                        inputMode={
                                            field.type === 'number'
                                                ? 'decimal'
                                                : undefined
                                        }
                                        value={
                                            typeof value === 'string'
                                                ? value
                                                : ''
                                        }
                                        required={field.required}
                                        maxLength={
                                            field.type === 'number'
                                                ? 128
                                                : Number(field.maximumLength)
                                        }
                                        onChange={(event) =>
                                            onChange(
                                                field.name,
                                                event.target.value,
                                            )
                                        }
                                    />
                                )}
                                <div
                                    id={`${id}-help`}
                                    className="text-xs text-muted-foreground"
                                >
                                    {field.description && (
                                        <p>{field.description}</p>
                                    )}
                                    {field.type === 'number' &&
                                        (field.minimumJson != null ||
                                            field.maximumJson != null) && (
                                            <p>
                                                {t('parameters.range', {
                                                    min:
                                                        field.minimumJson ??
                                                        t(
                                                            'parameters.unbounded',
                                                        ),
                                                    max:
                                                        field.maximumJson ??
                                                        t(
                                                            'parameters.unbounded',
                                                        ),
                                                })}
                                            </p>
                                        )}
                                    {field.type === 'credential' && (
                                        <p>
                                            {t('parameters.credential')}{' '}
                                            {t(
                                                configuredCredentials.has(
                                                    field.name,
                                                )
                                                    ? 'parameters.configured'
                                                    : 'parameters.unconfigured',
                                            )}
                                        </p>
                                    )}
                                    {error && (
                                        <p role="alert">
                                            {t(
                                                `parameters.issues.${error.code}`,
                                            )}
                                        </p>
                                    )}
                                </div>
                            </div>
                        )
                    })}
                </fieldset>
            ))}
        </>
    )
}
