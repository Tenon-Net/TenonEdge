import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, replaceSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { ErrorMessage } from './error-message'

export function LogoutButton() {
    const { t } = useTranslation()
    const logout = useMutation({
        retry: false,
        mutationFn: () => api.POST('/api/edge-admin/v1/auth/logout'),
        onSuccess: () => replaceSession(null),
    })
    return (
        <div className="space-y-2">
            <Button
                variant="outline"
                disabled={logout.isPending}
                onClick={() => logout.mutate()}
            >
                {t('logout')}
            </Button>
            <ErrorMessage error={logout.error} />
        </div>
    )
}
