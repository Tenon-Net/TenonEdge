import { api } from '../../lib/session'
import { createConfigurationApi } from './api'
import { createConfigurationMigrationApi } from './migration-api'
export const configuration = createConfigurationApi(api)
export const configurationMigrations = createConfigurationMigrationApi(api)
