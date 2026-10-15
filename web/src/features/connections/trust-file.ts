export const maximumTrustFileBytes = 48 * 1024
export const maximumCertificateBytes = 32 * 1024

/** Decode only a single public PEM certificate or a DER file. X.509 validity,
 * CA constraints, metadata and fingerprint are checked by the product API. */
export function certificateDerBase64(bytes: Uint8Array): string {
    if (!bytes.length || bytes.length > maximumTrustFileBytes)
        throw new Error('trust.fileSize')
    const text = new TextDecoder().decode(bytes).trim()
    if (text.startsWith('-----')) {
        const match = text.match(
            /^-----BEGIN CERTIFICATE-----\s+([A-Za-z0-9+/=\s]+)\s+-----END CERTIFICATE-----$/,
        )
        if (!match) throw new Error('trust.fileFormat')
        const encoded = match[1].replace(/\s/g, '')
        let decoded: string
        try {
            decoded = atob(encoded)
        } catch {
            throw new Error('trust.fileFormat')
        }
        if (btoa(decoded) !== encoded) throw new Error('trust.fileFormat')
        if (!decoded.length || decoded.length > maximumCertificateBytes)
            throw new Error('trust.fileSize')
        return encoded
    }
    if (bytes.length > maximumCertificateBytes)
        throw new Error('trust.fileSize')
    return btoa(Array.from(bytes, (byte) => String.fromCharCode(byte)).join(''))
}
