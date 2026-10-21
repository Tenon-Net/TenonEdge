import { featureTranslation } from '../../lib/i18n'
import { deviceStrings } from './strings'
import { currentValueStrings } from '../diagnostics/current-value-strings'
import { inputStrings } from '../inputs/strings'
import { parameterStrings } from '../parameters/strings'
import { routeStrings } from '../routes/strings'

export const useTranslation = featureTranslation({
    devices: deviceStrings,
    currentValues: currentValueStrings,
    inputs: inputStrings,
    parameters: parameterStrings,
    routes: routeStrings,
})
