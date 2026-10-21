import { afterEach, expect, it } from 'vitest'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { syncContract } from '../../scripts/export-openapi.mjs'

const directories = []
afterEach(async () => {
    await Promise.all(
        directories
            .splice(0)
            .map((path) => rm(path, { recursive: true, force: true })),
    )
})
async function fixture() {
    const directory = await mkdtemp(join(tmpdir(), 'tenonedge-contract-test-'))
    directories.push(directory)
    await mkdir(join(directory, 'src/api'), { recursive: true })
    const document = {
        openapi: '3.0.1',
        info: { title: 'Contract test', version: 'v1' },
        servers: [{ url: 'http://127.0.0.1:12345/' }],
        paths: {
            '/api/edge-admin/v1/auth/session': {
                get: { responses: { 200: { description: 'Session' } } },
            },
        },
        components: {
            schemas: {
                Summary: {
                    type: 'object',
                    required: ['id'],
                    properties: {
                        id: { type: 'integer' },
                        effective: { type: 'boolean', default: false },
                    },
                },
            },
        },
    }
    await syncContract(document, directory)
    return { document, directory }
}

it('ignores the Host port while retaining defaults and required fields', async () => {
    const { document, directory } = await fixture()
    const stored = JSON.parse(
        await readFile(join(directory, 'openapi.json'), 'utf8'),
    )
    expect(stored.servers).toEqual([{ url: '/' }])
    expect(stored.components).toEqual(document.components)
    document.servers = [{ url: 'http://127.0.0.1:54321/' }]
    await syncContract(document, directory, true)
})

it('rejects a backend contract change without overwriting the checked artifacts', async () => {
    const { document, directory } = await fixture()
    const jsonPath = join(directory, 'openapi.json')
    const typesPath = join(directory, 'src/api/schema.d.ts')
    const before = await readFile(jsonPath, 'utf8')
    const typesBefore = await readFile(typesPath, 'utf8')
    document.components.schemas.Summary.properties.effective.default = true
    await expect(syncContract(document, directory, true)).rejects.toThrow(
        'openapi.json, src/api/schema.d.ts',
    )
    expect(await readFile(jsonPath, 'utf8')).toBe(before)
    expect(await readFile(typesPath, 'utf8')).toBe(typesBefore)
    await syncContract(document, directory)
    await syncContract(document, directory, true)
})

it.each(['openapi.json', 'src/api/schema.d.ts'])(
    'rejects a missing or edited %s independently',
    async (name) => {
        const { document, directory } = await fixture()
        const path = join(directory, name)
        await rm(path)
        await expect(syncContract(document, directory, true)).rejects.toThrow(
            name,
        )
        await writeFile(path, 'stale')
        await expect(syncContract(document, directory, true)).rejects.toThrow(
            name,
        )
        expect(await readFile(path, 'utf8')).toBe('stale')
    },
)

it('rejects unrelated documents before changing existing files', async () => {
    const { directory } = await fixture()
    const path = join(directory, 'openapi.json')
    const before = await readFile(path, 'utf8')
    await expect(
        syncContract({ openapi: '3.0.1', paths: {} }, directory),
    ).rejects.toThrow('Not a TenonEdge')
    expect(await readFile(path, 'utf8')).toBe(before)
})
