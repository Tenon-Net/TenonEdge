import { dashboardStrings } from './dashboard-strings'
import { featureTranslation } from '../../lib/i18n'
import { diagnosticsStrings } from './strings'
import { currentValueStrings } from './current-value-strings'
import { outputStrings } from './output-strings'
import { messageStrings } from './message-strings'
import { deliveryStrings } from '../deliveries/strings'
import { migrationStrings } from '../deliveries/migration-strings'

export const useTranslation = featureTranslation({
    diagnostics: diagnosticsStrings,
    dashboard: dashboardStrings,
    currentValues: currentValueStrings,
    outputs: outputStrings,
    messages: messageStrings,
    deliveries: deliveryStrings,
    migration: migrationStrings,
})
