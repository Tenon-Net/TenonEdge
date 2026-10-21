import { featureTranslation } from '../../lib/i18n'
import { diagnosticsStrings } from './strings'
import { dashboardStrings } from './dashboard-strings'
import { currentValueStrings } from './current-value-strings'
import { outputStrings } from './output-strings'

export const useTranslation = featureTranslation({
    diagnostics: diagnosticsStrings,
    dashboard: dashboardStrings,
    currentValues: currentValueStrings,
    outputs: outputStrings,
})
