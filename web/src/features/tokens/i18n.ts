import { featureTranslation } from '../../lib/i18n'
import { tokenStrings } from './strings'

export const useTranslation = featureTranslation({
    tokens: tokenStrings,
})
