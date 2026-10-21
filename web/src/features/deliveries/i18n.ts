import { featureTranslation } from '../../lib/i18n'
import { deliveryStrings } from './strings'
import { migrationStrings } from './migration-strings'
import { outputControlStrings } from './control-strings'
import { diagnosticsStrings } from '../diagnostics/strings'

export const useTranslation = featureTranslation({
    deliveries: deliveryStrings,
    migration: migrationStrings,
    outputControls: outputControlStrings,
    diagnostics: diagnosticsStrings,
})
