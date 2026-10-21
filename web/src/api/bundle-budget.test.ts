import { expect, it } from 'vitest'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { gzipSync } from 'node:zlib'
import { build } from 'vite'
import {
    bundleBudget,
    checkBundle,
    measureBundle,
    type BundleReport,
} from '../../bundle-budget'

function chunk(fileName: string, imports: string[] = [], entry = false) {
    return {
        fileName,
        code: 'export const label = "网关";',
        isEntry: entry,
        imports,
        dynamicImports: [] as string[],
        modules: {} as Record<string, unknown>,
    }
}

it('counts shared and cyclic static imports once, in UTF-8 bytes', () => {
    const files = [
        chunk('entry', ['a', 'b'], true),
        chunk('a', ['shared']),
        chunk('b', ['shared']),
        chunk('shared', ['a']),
    ]
    const report = measureBundle(files)
    expect(report.initial.files).toEqual(['entry', 'a', 'shared', 'b'])
    expect(report.initial.bytes).toBe(Buffer.byteLength(files[0].code) * 4)
    expect(report.initial.gzipBytes).toBeGreaterThan(0)
    expect(() => checkBundle(report)).not.toThrow()
})

it('keeps dynamic features out of initial bytes but records them for browser verification', () => {
    const entry = chunk('entry', [], true)
    entry.dynamicImports = ['canvas']
    const canvas = chunk('canvas')
    canvas.modules['/workspace/web/node_modules/@xyflow/react/index.js'] = {}
    const report = measureBundle([entry, canvas])
    expect(report.initial.files).toEqual(['entry'])
    expect(report.files[0].dynamicImports).toEqual(['canvas'])
    expect(() => checkBundle(report)).not.toThrow()
})

it('fails if a static dependency or the entry cannot be measured', () => {
    expect(() => measureBundle([chunk('entry', ['missing'], true)])).toThrow(
        'Unmeasured',
    )
    expect(() => measureBundle([chunk('unused')])).toThrow(
        'No application entry',
    )
})

it('enforces the cumulative budget even when each chunk is small', () => {
    const files = [chunk('entry', ['shared'], true), chunk('shared')]
    files.forEach((file) => {
        file.code = 'x'.repeat(260_000)
    })
    expect(() => checkBundle(measureBundle(files))).toThrow('exceeds budget')
})

it.each([
    'src/features/connections/strings.ts',
    'src/features/auth/auth-form.tsx',
    'src/features/auth/auth-layout.tsx',
    'src/components/layout/workspace.tsx',
    'node_modules/zod/v4/core.js',
    'node_modules/@xyflow/react/index.js',
])('rejects an eager feature dependency %s', (id) => {
    const entry = chunk('entry', [], true)
    entry.modules[`/workspace/web/${id}`] = {}
    expect(() => checkBundle(measureBundle([entry]))).toThrow('Lazy features')
})

it('reports the emitted Vite bytes, including final dynamic-import preload rewrites', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'tenonedge-bundle-'))
    try {
        await writeFile(
            join(directory, 'index.js'),
            `document.onclick = () => import('./feature.js').then(m => m.show())`,
        )
        await writeFile(
            join(directory, 'feature.js'),
            `import './feature.css'; export const show = () => document.title = '网关'`,
        )
        await writeFile(join(directory, 'feature.css'), 'body { color: red }')
        await build({
            configFile: false,
            root: directory,
            logLevel: 'silent',
            plugins: [bundleBudget()],
            build: { rollupOptions: { input: join(directory, 'index.js') } },
        })
        const report: BundleReport = JSON.parse(
            await readFile(join(directory, 'reports/bundle.json'), 'utf8'),
        )
        for (const file of report.files) {
            const bytes = await readFile(join(directory, 'dist', file.file))
            expect(file.bytes).toBe(bytes.length)
            expect(file.gzipBytes).toBe(gzipSync(bytes).length)
        }
        expect(report.initial.files.length).toBeGreaterThan(0)
        expect(report.files.some((file) => file.dynamicImports.length)).toBe(
            true,
        )

        await writeFile(
            join(directory, 'index.js'),
            `document.title = '${'x'.repeat(500_001)}'`,
        )
        await expect(
            build({
                configFile: false,
                root: directory,
                logLevel: 'silent',
                plugins: [bundleBudget()],
                build: {
                    write: false,
                    rollupOptions: { input: join(directory, 'index.js') },
                },
            }),
        ).rejects.toThrow('exceeds budget')
    } finally {
        await rm(directory, { recursive: true, force: true })
    }
})
