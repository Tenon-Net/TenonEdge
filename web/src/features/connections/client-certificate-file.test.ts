import { describe, expect, it, vi } from 'vitest'
import { readClientCertificateFile } from './client-certificate-file'

describe('bounded client certificate file input', () => {
    it('encodes PKCS#12 bytes without secure-context APIs or decoding as text', async () => {
        expect(
            await readClientCertificateFile(
                new File([new Uint8Array([0x30, 0, 128, 255])], 'client.pfx'),
                'pkcs12',
            ),
        ).toBe('MACA/w==')
    })
    it.each(['certificatePem', 'privateKeyPem'] as const)(
        'preserves %s verbatim; does not invent browser X.509 validation',
        async (kind) => {
            const text =
                '\r\n-----BEGIN CERTIFICATE-----\r\nfixture\r\n-----END CERTIFICATE-----\r\n'
            expect(
                await readClientCertificateFile(
                    new File([text], 'client.pem'),
                    kind,
                ),
            ).toBe(text)
        },
    )
    it.each([
        ['pkcs12', 64 * 1024],
        ['certificatePem', 96 * 1024],
        ['privateKeyPem', 16 * 1024],
    ] as const)(
        'rejects %s oversized files before reading',
        async (kind, limit) => {
            const file = new File([new Uint8Array(limit + 1)], 'large')
            const read = vi.spyOn(file, 'arrayBuffer')
            await expect(readClientCertificateFile(file, kind)).rejects.toThrow(
                'clientCertificate.fileSize',
            )
            expect(read).not.toHaveBeenCalled()
            await expect(
                readClientCertificateFile(new File([], 'empty'), kind),
            ).rejects.toThrow('clientCertificate.fileSize')
        },
    )
    it('rejects invalid UTF-8 and blank PEM', async () => {
        await expect(
            readClientCertificateFile(
                new File([new Uint8Array([255])], 'invalid'),
                'privateKeyPem',
            ),
        ).rejects.toThrow('clientCertificate.fileEncoding')
        await expect(
            readClientCertificateFile(
                new File([' \n'], 'blank'),
                'certificatePem',
            ),
        ).rejects.toThrow('clientCertificate.fileEncoding')
    })
    it('accepts the PKCS#12 byte ceiling and checks actual allocated content too', async () => {
        const maximum = new File([new Uint8Array(64 * 1024)], 'maximum')
        expect(
            atob(await readClientCertificateFile(maximum, 'pkcs12')),
        ).toHaveLength(64 * 1024)
        const file = new File(['small'], 'misreported')
        vi.spyOn(file, 'arrayBuffer').mockResolvedValue(
            new ArrayBuffer(64 * 1024 + 1),
        )
        await expect(readClientCertificateFile(file, 'pkcs12')).rejects.toThrow(
            'clientCertificate.fileSize',
        )
    })
})
