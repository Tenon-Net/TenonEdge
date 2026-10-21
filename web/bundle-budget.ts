import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { gzipSync } from 'node:zlib'
import type { Plugin } from 'vite'

type Chunk = {
    fileName: string
    code: string
    isEntry: boolean
    imports: string[]
    dynamicImports: string[]
    modules: Record<string, unknown>
}

export const initialBudget = { bytes: 500_000, gzipBytes: 165_000 }

/** Count the entire static import graph, so splitting one eager file into
 * several files cannot make the initial download appear smaller. */
export function measureBundle(chunks: Chunk[]) {
    const files = chunks.map((chunk) => ({
        file: chunk.fileName,
        entry: chunk.isEntry,
        bytes: Buffer.byteLength(chunk.code),
        gzipBytes: gzipSync(chunk.code).length,
        imports: chunk.imports,
        dynamicImports: chunk.dynamicImports,
        modules: Object.keys(chunk.modules).map((id) =>
            id.replaceAll('\\', '/').split('/web/').at(-1)!,
        ),
    }))
    const byName = new Map(files.map((file) => [file.file, file]))
    const initial = new Set<string>()
    function visit(name: string) {
        if (initial.has(name)) return
        const file = byName.get(name)
        if (!file) throw new Error(`Unmeasured static dependency: ${name}`)
        initial.add(name)
        file.imports.forEach(visit)
    }
    const entries = files.filter((file) => file.entry)
    if (!entries.length) throw new Error('No application entry in bundle')
    entries.forEach((file) => visit(file.file))
    const eager = files.filter((file) => initial.has(file.file))
    return {
        initial: {
            files: [...initial],
            bytes: eager.reduce((sum, file) => sum + file.bytes, 0),
            gzipBytes: eager.reduce((sum, file) => sum + file.gzipBytes, 0),
        },
        files,
    }
}

export type BundleReport = ReturnType<typeof measureBundle>

export function checkBundle(report: BundleReport) {
    if (
        report.initial.bytes > initialBudget.bytes ||
        report.initial.gzipBytes > initialBudget.gzipBytes
    )
        throw new Error(
            `Initial JavaScript exceeds budget: ${JSON.stringify(report.initial)}`,
        )
    const forbidden = report.files
        .filter((file) => report.initial.files.includes(file.file))
        .flatMap((file) => file.modules)
        .filter((id) =>
            /src\/features\/.*strings\.ts$|src\/features\/auth\/auth-form\.tsx$|src\/components\/layout\/workspace\.tsx$|node_modules\/(?:zod|react-hook-form|@xyflow|@tanstack\/react-(?:table|virtual))\//.test(
                id,
            ),
        )
    if (forbidden.length)
        throw new Error(
            `Lazy features entered the initial bundle: ${forbidden.join(', ')}`,
        )
}

export function bundleBudget(): Plugin {
    let directory = ''
    return {
        name: 'tenonedge-bundle-budget',
        apply: 'build',
        enforce: 'post',
        configResolved(config) {
            directory = resolve(config.root, 'reports')
        },
        generateBundle: {
            order: 'post',
            handler(_, bundle) {
                // Also enforce the budget for builds with write: false.
                checkBundle(
                    measureBundle(
                        Object.values(bundle).filter(
                            (file) => file.type === 'chunk',
                        ),
                    ),
                )
            },
        },
        async writeBundle(_, bundle) {
            // Vite can rewrite dynamic-import preloads in generateBundle. Report
            // the finalized output, including those bytes, rather than an earlier snapshot.
            const report = measureBundle(
                Object.values(bundle).filter((file) => file.type === 'chunk'),
            )
            checkBundle(report)
            await mkdir(directory, { recursive: true })
            await writeFile(
                resolve(directory, 'bundle.json'),
                JSON.stringify(report, null, 2) + '\n',
            )
        },
    }
}
