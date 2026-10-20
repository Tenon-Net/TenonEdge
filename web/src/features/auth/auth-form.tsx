import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import type { z } from 'zod'
import { authFormSchema, type AuthMode } from './auth-form-schema'
import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, replaceSession } from '../../lib/session'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import {
    Form,
    FormField,
    FormItem,
    FormLabel,
    FormControl,
    FormMessage,
} from '../../components/ui/form'
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
    const field = (
        name: keyof z.infer<typeof schema>,
        label: string,
        input: {
            type?: string
            autoComplete?: string
            readOnly?: boolean
        } = {},
    ) => (
        <FormField
            control={form.control}
            name={name}
            render={({ field }) => (
                <FormItem>
                    <FormLabel>{label}</FormLabel>
                    <FormControl>
                        <Input {...input} {...field} />
                    </FormControl>
                    <FormMessage role="alert" />
                </FormItem>
            )}
        />
    )
    return (
        <Form {...form}>
            <form
                className="space-y-5"
                onSubmit={form.handleSubmit((values) =>
                    mutation.mutate(values),
                )}
            >
                {mode !== 'passwordChange' &&
                    field('accountName', t('account'), {
                        autoComplete: 'username',
                        readOnly: mode === 'reset',
                    })}
                {field(
                    'password',
                    t(
                        mode === 'passwordChange'
                            ? 'currentPassword'
                            : 'password',
                    ),
                    {
                        type: 'password',
                        autoComplete:
                            mode === 'setup' || mode === 'reset'
                                ? 'new-password'
                                : 'current-password',
                    },
                )}
                {(mode === 'setup' || mode === 'reset') &&
                    field('confirmPassword', t('confirmPassword'), {
                        type: 'password',
                        autoComplete: 'new-password',
                    })}
                {mode === 'passwordChange' &&
                    field('newPassword', t('newPassword'), {
                        type: 'password',
                        autoComplete: 'new-password',
                    })}
                {mode === 'setup' && field('gatewayName', t('gatewayName'))}
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
        </Form>
    )
}
