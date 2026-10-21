import { featureTranslation } from '../../lib/i18n'
import { routeStrings } from './strings'
import { canvasStrings } from '../canvas/strings'
import { testSendStrings } from './test-send-strings'

export const useTranslation = featureTranslation({
    routes: routeStrings,
    canvas: canvasStrings,
    testSend: testSendStrings,
})
