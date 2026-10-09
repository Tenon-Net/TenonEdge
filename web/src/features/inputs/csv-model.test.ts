import { expect, it } from 'vitest'
import { csvBase64, maxCsvBytes } from './csv-model'
it('transports UTF8 BOM and legacy encoding bytes without decoding or corrupting them', () => {
    const bytes = new Uint8Array([
        0xef, 0xbb, 0xbf, 0xce, 0xc2, 0xb6, 0xc8, 0x2c, 0x0d, 0x0a,
    ])
    expect(
        Uint8Array.from(atob(csvBase64(bytes)), (c) => c.charCodeAt(0)),
    ).toEqual(bytes)
})
it('bounds the upload before allocating base64 and handles chunk boundaries', () => {
    const bytes = new Uint8Array(16385).fill(255)
    expect(atob(csvBase64(bytes)).length).toBe(16385)
    expect(() => csvBase64(new Uint8Array(maxCsvBytes + 1))).toThrow('csv-size')
    expect(atob(csvBase64(new Uint8Array(maxCsvBytes))).length).toBe(
        maxCsvBytes,
    )
})
