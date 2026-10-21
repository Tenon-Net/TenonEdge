import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import type { ApiToken } from './api'
export function TokenTable({
    items,
    revoke,
    disabled = false,
}: {
    items: ApiToken[]
    revoke?: (token: ApiToken) => void
    disabled?: boolean
}) {
    const { t } = useTranslation()
    const time = (
        value: string | null | undefined,
        fallback = 'tokens.never',
    ) => (value ? new Date(value).toLocaleString() : t(fallback))
    return (
        <div className="max-h-96 overflow-auto rounded-md border">
            <table className="w-full text-left text-sm">
                <thead className="bg-muted">
                    <tr>
                        {[
                            'name',
                            'role',
                            'creator',
                            'created',
                            'expires',
                            'lastUsed',
                            'state',
                        ].map((key) => (
                            <th className="p-3" key={key}>
                                {t(`tokens.${key}`)}
                            </th>
                        ))}
                        {revoke && (
                            <th className="p-3">{t('tokens.revoke')}</th>
                        )}
                    </tr>
                </thead>
                <tbody>
                    {items.map((token) => (
                        <tr className="border-t align-top" key={token.id}>
                            <td className="p-3 font-medium">{token.name}</td>
                            <td className="p-3">{t(`roles.${token.role}`)}</td>
                            <td className="p-3">
                                {token.creatorAccountName}
                                {token.createdByTokenName && (
                                    <p>
                                        {t('tokens.via', {
                                            name: token.createdByTokenName,
                                        })}
                                    </p>
                                )}
                            </td>
                            <td className="p-3">{time(token.createdAt)}</td>
                            <td className="p-3">
                                {time(token.expiresAt, 'tokens.unlimited')}
                            </td>
                            <td className="p-3">{time(token.lastUsedAt)}</td>
                            <td className="p-3">
                                {t(
                                    token.revokedAt
                                        ? 'tokens.ended'
                                        : token.expiresAt &&
                                            Date.parse(token.expiresAt) <=
                                                Date.now()
                                          ? 'tokens.expired'
                                          : 'tokens.active',
                                )}
                            </td>
                            {revoke && (
                                <td className="p-3">
                                    <Button
                                        variant="outline"
                                        size="sm"
                                        disabled={disabled || !!token.revokedAt}
                                        onClick={() => revoke(token)}
                                    >
                                        {t('tokens.revoke')}
                                    </Button>
                                </td>
                            )}
                        </tr>
                    ))}
                </tbody>
            </table>
            {items.length === 0 && <p className="p-3">{t('tokens.empty')}</p>}
        </div>
    )
}
