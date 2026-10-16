import type { components } from '../../api/schema'

export type ClientCertificateUpload =
    components['schemas']['ClientCertificateUpload']
export type CertificateFileKind = 'pkcs12' | 'certificatePem' | 'privateKeyPem'
const limits = {
    pkcs12: 64 * 1024,
    certificatePem: 96 * 1024,
    privateKeyPem: 16 * 1024,
}

/** Bound reads before allocating. X.509, key matching and purpose checks belong to the server. */
export async function readClientCertificateFile(
    file: File,
    kind: CertificateFileKind,
): Promise<string> {
    if (!file.size || file.size > limits[kind])
        throw new Error('clientCertificate.fileSize')
    const bytes = new Uint8Array(await file.arrayBuffer())
    if (!bytes.length || bytes.length > limits[kind])
        throw new Error('clientCertificate.fileSize')
    if (kind === 'pkcs12')
        return btoa(
            Array.from(bytes, (byte) => String.fromCharCode(byte)).join(''),
        )
    try {
        const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
        if (!text.trim()) throw new Error('empty')
        return text
    } catch {
        throw new Error('clientCertificate.fileEncoding')
    }
}
