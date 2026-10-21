import { featureTranslation } from '../../lib/i18n'
import { inputStrings } from './strings'
import { deviceStrings } from '../devices/strings'
import { parameterStrings } from '../parameters/strings'
import { routeStrings } from '../routes/strings'

export const useTranslation = featureTranslation({
    inputs: inputStrings,
    devices: deviceStrings,
    parameters: parameterStrings,
    routes: routeStrings,
})
