import type { components } from '../../api/schema'
import {
    changeAddressMode,
    validateRows,
    type AddressMode,
    type PointRow,
} from '../points/model'

export type Input = components['schemas']['InputDraft']
export type InputRequest = components['schemas']['InputDraftRequest']
export type InputForm = {
    id?: number
    version: string
    key: string
    name: string
    enabled: boolean
    period: number
}
export function inputForm(version: string, input?: Input): InputForm {
    return {
        id: input ? Number(input.id) : undefined,
        version,
        key: input?.key ?? '',
        name: input?.name ?? '',
        enabled: input?.enabled ?? true,
        period: Number(input?.modbus?.periodMs ?? 5000),
    }
}
export function inputRows(
    input?: Input,
    mode: AddressMode = 'offset',
): PointRow[] {
    const rows = (input?.modbus?.points ?? []).map((p, index) => ({
        id: String(index + 1),
        key: p.key ?? '',
        name: p.name ?? '',
        functionCode: String(p.functionCode),
        address: String(p.address ?? ''),
        type: p.type ?? '',
        bit: p.bit == null ? '' : String(p.bit),
        byteOrder: p.byteOrder ?? '',
        unit: p.unit ?? '',
        enabled: String(p.enabled ?? true),
    }))
    return changeAddressMode(rows, 'offset', mode)
}
export function inputRequest(
    form: InputForm,
    rows: PointRow[],
    mode: AddressMode,
): InputRequest {
    const results = validateRows(rows, mode)
    if (results.some((r) => r.errors.length))
        throw new Error('invalid-point-table')
    return {
        expectedVersion: form.version,
        key: form.key,
        name: form.name,
        enabled: form.enabled,
        modbus: {
            periodMs: form.period,
            points: rows.map((r, index) => ({
                key: r.key,
                name: r.name || null,
                functionCode: results[index].address!.functionCode,
                address: results[index].address!.offset,
                type: r.type,
                bit: r.bit === '' ? null : Number(r.bit),
                byteOrder: r.byteOrder || null,
                unit: r.unit || null,
                enabled: r.enabled === 'true',
            })),
        },
    }
}
