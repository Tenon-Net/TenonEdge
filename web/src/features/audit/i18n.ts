import { featureTranslation } from '../../lib/i18n'
import { auditStrings } from './strings'

export const useTranslation = featureTranslation({
    audit: auditStrings,
})
