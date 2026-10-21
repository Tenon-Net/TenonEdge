import { featureTranslation } from '../../lib/i18n'
import { canvasStrings } from './strings'
import { inputStrings } from '../inputs/strings'
import { parameterStrings } from '../parameters/strings'
import { routeStrings } from '../routes/strings'

export const useTranslation = featureTranslation({
    canvas: canvasStrings,
    inputs: inputStrings,
    parameters: parameterStrings,
    routes: routeStrings,
})
