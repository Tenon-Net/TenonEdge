import { featureTranslation } from '../../lib/i18n'
import { userStrings } from './strings'
import { connectionStrings } from '../connections/strings'
import { tokenStrings } from '../tokens/strings'

export const useTranslation = featureTranslation({
    users: userStrings,
    connections: connectionStrings,
    tokens: tokenStrings,
})
