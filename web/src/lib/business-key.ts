// HTML pattern uses Unicode sets (v) in current browsers and u in older ones.
// Escape the literal hyphen so both enforce the backend business-key alphabet.
export const businessKeyPattern = String.raw`[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}`
