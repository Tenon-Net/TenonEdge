import { useTranslation } from 'react-i18next'
import { ApiError, fieldErrors } from '../../api/errors'

export function ErrorMessage({ error }: { error: unknown }) {
    const { t } = useTranslation()
    if (!error) return null
    const key =
        error instanceof ApiError
            ? ({
                  401: 'unauthorized',
                  403: 'forbidden',
                  409: 'conflict',
                  429: 'rateLimited',
              }[error.status] ?? 'requestFailed')
            : 'timeoutNotice'
    const fields = error instanceof ApiError ? fieldErrors(error.problem) : {}
    return (
        <div
            role="alert"
            className="rounded-md border border-destructive/40 p-3 text-sm text-destructive"
        >
            <p>{t(key)}</p>
            {Object.entries(fields).map(([field, messages]) => (
                <p key={field}>
                    {t(field, { defaultValue: field })}: {messages.join(' ')}
                </p>
            ))}
        </div>
    )
}
