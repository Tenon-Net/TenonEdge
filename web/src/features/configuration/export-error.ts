import { ApiError } from '../../api/errors'

// Render only known static messages. Never echo an arbitrary server detail or
// the configuration content into a generic export failure notice.
export function exportErrorKey(error: unknown): string | undefined {
    if (
        !(error instanceof ApiError) ||
        !error.problem ||
        typeof error.problem !== 'object' ||
        !('code' in error.problem)
    )
        return undefined
    switch (error.problem.code) {
        case 'configuration.exportReferenceMissing':
            return 'configurationExport.referenceMissing'
        case 'configuration.exportCredentialConflict':
            return 'configurationExport.credentialConflict'
        case 'configuration.exportTooLarge':
            return 'configurationExport.tooLarge'
        case 'configuration.exportInvalid':
            return 'configurationExport.invalid'
        default:
            return undefined
    }
}
