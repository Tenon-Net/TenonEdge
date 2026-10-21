import { featureTranslation } from '../../lib/i18n'
import { connectionStrings, credentialStrings } from './strings'
import { moduleConnectionStrings } from './module-strings'
import { clientCertificateStrings } from './client-certificate-strings'
import { trustStrings } from './trust-strings'
import { parameterStrings } from '../parameters/strings'
import { routeStrings } from '../routes/strings'

export const useTranslation = featureTranslation({
    connections: connectionStrings,
    moduleConnections: moduleConnectionStrings,
    credentials: credentialStrings,
    clientCertificate: clientCertificateStrings,
    trust: trustStrings,
    parameters: parameterStrings,
    routes: routeStrings,
})
