import { useTranslation } from 'react-i18next'
import type { RuntimeOutputInfo } from './api'
import { Fact } from './view'

export function CapacityCalibration({
    calibration,
}: {
    calibration: RuntimeOutputInfo['capacity']['calibration']
}) {
    const { t } = useTranslation()
    const amplification =
        calibration?.amplification == null
            ? null
            : Number(calibration.amplification)
    return (
        <dl className="grid gap-4 sm:grid-cols-3">
            <Fact
                label={t('outputs.calibration')}
                value={t(
                    `outputs.calibrationStatuses.${calibration?.status ?? 'unknown'}`,
                    { defaultValue: t('outputs.calibrationStatuses.unknown') },
                )}
            />
            <Fact
                label={t('outputs.calibrationProfile')}
                value={calibration?.profile ?? t('outputs.unknown')}
            />
            <Fact
                label={t('outputs.amplification')}
                value={
                    calibration?.status === 'applicable' &&
                    amplification != null &&
                    Number.isFinite(amplification) &&
                    amplification > 0
                        ? amplification.toFixed(3)
                        : t('outputs.unknown')
                }
            />
        </dl>
    )
}
