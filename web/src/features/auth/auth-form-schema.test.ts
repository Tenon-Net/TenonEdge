import { expect, it } from 'vitest'
import { authFormSchema } from './auth-form-schema'

const values = {
    accountName: 'admin',
    password: 'Browser-fixture-5938',
    confirmPassword: 'Browser-fixture-5938',
    newPassword: '',
    gatewayName: '',
}
it.each(['setup', 'reset'] as const)(
    '%s requires an exact repeated password',
    (mode) => {
        const schema = authFormSchema(mode, 'required', 'mismatch')
        expect(schema.safeParse(values).success).toBe(true)
        for (const confirmPassword of ['', 'other', values.password + ' ']) {
            const result = schema.safeParse({ ...values, confirmPassword })
            expect(result.success).toBe(false)
            expect(
                result.error?.issues.every(
                    (issue) => issue.path[0] === 'confirmPassword',
                ),
            ).toBe(true)
        }
    },
)
it('ordinary login does not require a second password; password change still requires its new password', () => {
    expect(
        authFormSchema('login', 'required', 'mismatch').safeParse({
            ...values,
            confirmPassword: '',
        }).success,
    ).toBe(true)
    const schema = authFormSchema('passwordChange', 'required', 'mismatch')
    expect(
        schema.safeParse({ ...values, accountName: '', confirmPassword: '' })
            .success,
    ).toBe(false)
    expect(
        schema.safeParse({
            ...values,
            accountName: '',
            confirmPassword: '',
            newPassword: 'Personal-fixture-5938',
        }).success,
    ).toBe(true)
})
