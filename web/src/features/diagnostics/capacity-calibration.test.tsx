import { expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { CapacityCalibration } from './capacity-calibration'

it.each([3.1254, '3.1254'])(
    'shows the applicable empirical profile and effective coefficient %s without calling it a guarantee',
    (amplification) => {
        const html = renderToStaticMarkup(
            <CapacityCalibration
                calibration={{
                    status: 'applicable',
                    profile: 'linux-ext4-sqlite-3.53.3-queue10-v1',
                    amplification,
                }}
            />,
        )
        expect(html).toContain('经验估算，非保证')
        expect(html).toContain('linux-ext4-sqlite-3.53.3-queue10-v1')
        expect(html).toContain('3.125')
    },
)

it.each([
    ['environmentUnknown', '环境信息不完整'],
    ['unsupportedEnvironment', '当前环境未校准'],
    ['sampleUnknown', '缺少有效生成记录采样'],
    ['sampleOutsideProfile', '记录大小超出校准范围'],
    ['future-status', '校准状态未知'],
])(
    'explains %s without presenting a stray coefficient as calibrated',
    (status, message) => {
        const html = renderToStaticMarkup(
            <CapacityCalibration
                calibration={{ status, profile: null, amplification: 123.456 }}
            />,
        )
        expect(html).toContain(message)
        expect(html).toContain('不能采用磁盘模型')
        expect(html).not.toContain('123.456')
        expect(html).not.toContain('outputs.calibrationStatuses.')
    },
)

it.each([undefined, null])(
    'does not invent a calibration for an absent result',
    (calibration) => {
        const html = renderToStaticMarkup(
            <CapacityCalibration calibration={calibration} />,
        )
        expect(html).toContain('校准状态未知')
        expect(html).not.toContain('环境与采样范围匹配')
    },
)

it.each([NaN, Infinity, -1, 0, null])(
    'does not display an invalid effective coefficient %s',
    (amplification) => {
        const html = renderToStaticMarkup(
            <CapacityCalibration
                calibration={{
                    status: 'applicable',
                    profile: null,
                    amplification,
                }}
            />,
        )
        expect(html).toMatch(/当前等效 k<\/dt><dd[^>]*>未知<\/dd>/)
    },
)
