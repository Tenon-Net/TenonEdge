import { describe, expect, it } from 'vitest'
import {
    certificateDerBase64,
    maximumCertificateBytes,
    maximumTrustFileBytes,
} from './trust-file'

const encode = (text: string) => new TextEncoder().encode(text)
const pem = (text: string) =>
    `-----BEGIN CERTIFICATE-----\n${text}\n-----END CERTIFICATE-----`

describe('bounded public certificate file decoding', () => {
    it('preserves DER bytes without depending on secure-context crypto', () => {
        expect(certificateDerBase64(new Uint8Array([0x30, 0, 128, 255]))).toBe(
            'MACA/w==',
        )
    })
    it('decodes one PEM with CRLF or whitespace but does not claim X.509 validation', () => {
        expect(certificateDerBase64(encode(pem('MACA/w==')))).toBe('MACA/w==')
        expect(
            certificateDerBase64(
                encode(' \r\n' + pem('MACA\r\n/w==') + '\r\n'),
            ),
        ).toBe('MACA/w==')
    })
    it.each([
        pem('MACA/w==') + pem('MACA/w=='),
        pem('MACA/w==') + '\ntrailing',
        pem('MACA/w==').replace('BEGIN CERTIFICATE', 'BEGIN PRIVATE KEY'),
        '-----BEGIN PKCS7-----\nMACA/w==\n-----END PKCS7-----',
        pem('!!'),
        pem('A==='),
        pem('AB=='), // non-canonical pad bits
    ])('rejects ambiguous, private or malformed PEM: %s', (value) => {
        expect(() => certificateDerBase64(encode(value))).toThrow(
            'trust.fileFormat',
        )
    })
    it('bounds the raw file and decoded certificate separately', () => {
        expect(() => certificateDerBase64(new Uint8Array())).toThrow(
            'trust.fileSize',
        )
        expect(() =>
            certificateDerBase64(new Uint8Array(maximumTrustFileBytes + 1)),
        ).toThrow('trust.fileSize')
        expect(() =>
            certificateDerBase64(new Uint8Array(maximumCertificateBytes + 1)),
        ).toThrow('trust.fileSize')
        expect(() =>
            certificateDerBase64(
                encode(pem(btoa('x'.repeat(maximumCertificateBytes + 1)))),
            ),
        ).toThrow('trust.fileSize')
        expect(
            atob(certificateDerBase64(new Uint8Array(maximumCertificateBytes)))
                .length,
        ).toBe(maximumCertificateBytes)
    })
})
