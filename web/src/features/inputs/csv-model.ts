export const maxCsvBytes = 5 * 1024 * 1024
export function csvBase64(bytes: Uint8Array): string {
    if (bytes.length > maxCsvBytes) throw new Error('csv-size')
    const parts: string[] = []
    for (let offset = 0; offset < bytes.length; offset += 16384)
        parts.push(
            String.fromCharCode(...bytes.subarray(offset, offset + 16384)),
        )
    return btoa(parts.join(''))
}
