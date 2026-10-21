import { featureTranslation } from '../../lib/i18n'
import { configurationStrings } from './strings'
import { configurationExportStrings } from './export-strings'
import { configurationImportStrings } from './import-strings'
import { configurationMigrationStrings } from './migration-strings'
import { migrationStrings } from '../deliveries/migration-strings'

export const useTranslation = featureTranslation({
    configuration: configurationStrings,
    configurationExport: configurationExportStrings,
    configurationImport: configurationImportStrings,
    configurationMigration: configurationMigrationStrings,
    migration: migrationStrings,
})
