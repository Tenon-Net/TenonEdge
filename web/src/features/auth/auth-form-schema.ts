import { z } from 'zod'

export type AuthMode = 'login' | 'setup' | 'reset' | 'passwordChange'

// Confirmation is local typo protection, not a second credential sent to the API.
// The server remains authoritative for account/password strength and authorization.
export function authFormSchema(
    mode: AuthMode,
    required: string,
    mismatch: string,
) {
    const confirms = mode === 'setup' || mode === 'reset'
    return z
        .object({
            accountName:
                mode === 'passwordChange' || mode === 'reset'
                    ? z.string()
                    : z.string().min(1, required),
            password: z.string().min(1, required),
            confirmPassword: confirms
                ? z.string().min(1, required)
                : z.string(),
            newPassword:
                mode === 'passwordChange'
                    ? z.string().min(1, required)
                    : z.string(),
            gatewayName: z.string(),
        })
        .refine(
            (value) => !confirms || value.password === value.confirmPassword,
            {
                path: ['confirmPassword'],
                message: mismatch,
            },
        )
}
