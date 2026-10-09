import { writeFile } from 'node:fs/promises'
// Read from a running Development Host; never store production cookies or credentials here.
const base = process.env.TENONEDGE_API_URL ?? 'http://127.0.0.1:18081'
const response = await fetch(
    new URL('/api/edge-admin/v1/openapi/v1.json', base),
)
if (!response.ok) throw new Error(`OpenAPI HTTP ${response.status}`)
const document = await response.json()
if (
    !document.openapi?.startsWith('3.') ||
    !document.paths?.['/api/edge-admin/v1/auth/session']
) {
    throw new Error('Not a TenonEdge OpenAPI document')
}
await writeFile(
    new URL('../openapi.json', import.meta.url),
    JSON.stringify(document, null, 2) + '\n',
)
