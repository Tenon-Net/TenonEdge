import { LogOut } from 'lucide-react'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, replaceSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from './error-message'

export function LogoutButton({ compact = false }: { compact?: boolean }) {
    const { t } = useTranslation()
    const logout = useMutation({
        retry: false,
        mutationFn: () => api.POST('/api/edge-admin/v1/auth/logout'),
        onSuccess: () => replaceSession(null),
    })
    return (
        <div className="space-y-2">
            <Button
                variant={compact ? 'ghost' : 'outline'}
                title={t('logout')}
                className={
                    compact
                        ? 'w-full justify-start gap-2 px-2 group-data-[collapsible=icon]:size-8'
                        : undefined
                }
                disabled={logout.isPending}
                onClick={() => logout.mutate()}
            >
                {compact && (
                    <LogOut aria-hidden="true" className="size-4 shrink-0" />
                )}
                <span
                    className={
                        compact
                            ? 'group-data-[collapsible=icon]:sr-only'
                            : undefined
                    }
                >
                    {t('logout')}
                </span>
            </Button>
            <ErrorMessage error={logout.error} />
        </div>
    )
}
