import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import type { z } from 'zod'
import { authFormSchema, type AuthMode } from './auth-form-schema'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, replaceSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import { ErrorMessage } from './error-message'

export function AuthForm({
    mode,
    resetAccount,
}: {
    mode: AuthMode
    resetAccount?: string | null
}) {
    const { t } = useTranslation()
    const schema = authFormSchema(mode, t('required'), t('passwordMismatch'))
    const form = useForm({
        resolver: zodResolver(schema),
        defaultValues: {
            accountName: mode === 'setup' ? 'admin' : (resetAccount ?? ''),
            password: '',
            confirmPassword: '',
            newPassword: '',
            gatewayName: '',
        },
    })
    const mutation = useMutation({
        retry: false,
        mutationFn: async (values: z.infer<typeof schema>) => {
            const result =
                mode === 'login'
                    ? await api.POST('/api/edge-admin/v1/auth/login', {
                          body: {
                              accountName: values.accountName,
                              password: values.password,
                          },
                      })
                    : mode === 'passwordChange'
                      ? await api.POST('/api/edge-admin/v1/auth/password', {
                            body: {
                                currentPassword: values.password,
                                newPassword: values.newPassword,
                            },
                        })
                      : await api.POST('/api/edge-admin/v1/setup', {
                            body: {
                                accountName: resetAccount ?? values.accountName,
                                password: values.password,
                                gatewayName: values.gatewayName || null,
                            },
                        })
            if (!result.data) throw new Error('missing-session')
            return result.data
        },
        onSuccess: (session) => {
            form.reset()
            replaceSession(session)
        },
    })
    return (
        <form
            className="space-y-5"
            onSubmit={form.handleSubmit((values) => mutation.mutate(values))}
        >
            {mode !== 'passwordChange' && (
                <div className="space-y-2">
                    <Label htmlFor="accountName">{t('account')}</Label>
                    <Input
                        id="accountName"
                        autoComplete="username"
                        readOnly={mode === 'reset'}
                        {...form.register('accountName')}
                        aria-invalid={!!form.formState.errors.accountName}
                    />
                    {form.formState.errors.accountName && (
                        <p role="alert">
                            {form.formState.errors.accountName.message}
                        </p>
                    )}
                </div>
            )}
            <div className="space-y-2">
                <Label htmlFor="password">
                    {t(
                        mode === 'passwordChange'
                            ? 'currentPassword'
                            : 'password',
                    )}
                </Label>
                <Input
                    id="password"
                    type="password"
                    autoComplete={
                        mode === 'setup' || mode === 'reset'
                            ? 'new-password'
                            : 'current-password'
                    }
                    {...form.register('password')}
                    aria-invalid={!!form.formState.errors.password}
                />
                {form.formState.errors.password && (
                    <p role="alert">{form.formState.errors.password.message}</p>
                )}
            </div>
            {(mode === 'setup' || mode === 'reset') && (
                <div className="space-y-2">
                    <Label htmlFor="confirmPassword">
                        {t('confirmPassword')}
                    </Label>
                    <Input
                        id="confirmPassword"
                        type="password"
                        autoComplete="new-password"
                        {...form.register('confirmPassword')}
                        aria-invalid={!!form.formState.errors.confirmPassword}
                    />
                    {form.formState.errors.confirmPassword && (
                        <p role="alert">
                            {form.formState.errors.confirmPassword.message}
                        </p>
                    )}
                </div>
            )}
            {mode === 'passwordChange' && (
                <div className="space-y-2">
                    <Label htmlFor="newPassword">{t('newPassword')}</Label>
                    <Input
                        id="newPassword"
                        type="password"
                        autoComplete="new-password"
                        {...form.register('newPassword')}
                        aria-invalid={!!form.formState.errors.newPassword}
                    />
                    {form.formState.errors.newPassword && (
                        <p role="alert">
                            {form.formState.errors.newPassword.message}
                        </p>
                    )}
                </div>
            )}
            {mode === 'setup' && (
                <div className="space-y-2">
                    <Label htmlFor="gatewayName">{t('gatewayName')}</Label>
                    <Input id="gatewayName" {...form.register('gatewayName')} />
                </div>
            )}
            <ErrorMessage error={mutation.error} />
            {mode === 'passwordChange' && mutation.isSuccess && (
                <p role="status">{t('savedPassword')}</p>
            )}
            <Button
                className="w-full"
                type="submit"
                disabled={mutation.isPending}
            >
                {t(
                    mutation.isPending
                        ? 'pending'
                        : {
                              login: 'login',
                              setup: 'submitSetup',
                              reset: 'submitReset',
                              passwordChange: 'submitPassword',
                          }[mode],
                )}
            </Button>
        </form>
    )
}
