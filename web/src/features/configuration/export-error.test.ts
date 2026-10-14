import { describe, expect, it } from 'vitest'
import { ApiError } from '../../api/errors'
import { exportErrorKey } from './export-error'

describe('configuration export errors', () => {
    it.each([
        ['ReferenceMissing', 'referenceMissing'],
        ['CredentialConflict', 'credentialConflict'],
        ['TooLarge', 'tooLarge'],
        ['Invalid', 'invalid'],
    ])('maps the known %s code without server content', (code, key) => {
        expect(
            exportErrorKey(
                new ApiError(409, {
                    code: `configuration.export${code}`,
                    detail: 'do-not-render-this-server-content',
                }),
            ),
        ).toBe(`configurationExport.${key}`)
    })
    it.each([
        null,
        new Error('offline'),
        new ApiError(409, { code: 'unknown' }),
        new ApiError(413, 'untrusted'),
    ])('leaves unknown failures to the shared safe error display', (error) =>
        expect(exportErrorKey(error)).toBeUndefined(),
    )
})
