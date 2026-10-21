import { featureTranslation } from '../../lib/i18n'
import { systemStrings } from './strings'
import { diagnosticsStrings } from '../diagnostics/strings'

export const useTranslation = featureTranslation({
    systemSettings: systemStrings,
    diagnostics: diagnosticsStrings,
})
