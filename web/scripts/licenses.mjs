import { readFile, readdir, writeFile } from 'node:fs/promises'
const lock = JSON.parse(
    await readFile(new URL('../package-lock.json', import.meta.url), 'utf8'),
)
const sections = []
for (const [path, entry] of Object.entries(lock.packages)) {
    if (!path || entry.dev) continue
    const directory = new URL(`../${path}/`, import.meta.url)
    const metadata = JSON.parse(
        await readFile(new URL('package.json', directory), 'utf8'),
    )
    const files = (await readdir(directory))
        .filter((file) => /^(licen[cs]e|copying|notice)(\.|$)/i.test(file))
        .sort()
    if (!files.length)
        throw new Error(`No license text found: ${metadata.name}`)
    const texts = await Promise.all(
        files.map((file) => readFile(new URL(file, directory), 'utf8')),
    )
    sections.push(
        `${metadata.name}@${entry.version} (${entry.license ?? metadata.license ?? 'review required'})\n${files.map((file, index) => `${file}\n${texts[index]}`).join('\n')}`,
    )
}
await writeFile(
    new URL('../public/licenses/npm-notices.txt', import.meta.url),
    sections
        .join('\n\n' + '='.repeat(72) + '\n\n')
        .replace(/\r\n/g, '\n')
        .replace(/[ \t]+$/gm, ''),
)
console.log(`Recorded ${sections.length} production dependency license texts`)
