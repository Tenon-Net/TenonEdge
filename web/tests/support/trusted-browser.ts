import { chromium, expect } from '@playwright/test'
import { execFile } from 'node:child_process'
import { X509Certificate } from 'node:crypto'
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { promisify } from 'node:util'
import { startOwnedHost } from './owned-host'
const run = promisify(execFile)

// Linux Chromium trusts only this test's isolated NSS database. No ignoreHTTPSErrors,
// --ignore-certificate-errors, global certificate installation, or application bypass.
// certutil comes from libnss3-tools (or TENONEDGE_CERTUTIL for an extracted local package).
export async function startTrustedBrowser(httpAddress = '127.0.0.1') {
    const directory = await mkdtemp(join(tmpdir(), 'tenonedge-token-browser-'))
    const host = await startOwnedHost(directory, 0, [], httpAddress)
    try {
        if (!host.httpsUrl || !host.certificateFingerprint)
            throw new Error('missing-owned-host-certificate')
        const address = new URL(host.httpsUrl)
        // Fetch only the public certificate for inspection, before sending any HTTP request or secret.
        const pem = await new Promise<string>((resolve, reject) => {
            const child = execFile(
                'openssl',
                ['s_client', '-connect', address.host, '-showcerts'],
                { timeout: 10000 },
                (error, stdout) => {
                    if (error) {
                        reject(error)
                        return
                    }
                    const certificate = stdout.match(
                        /-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----/,
                    )?.[0]
                    if (!certificate)
                        reject(new Error('missing-peer-certificate'))
                    else resolve(certificate)
                },
            )
            child.stdin?.end()
        })
        const certificate = new X509Certificate(pem)
        expect(certificate.fingerprint256.replaceAll(':', '')).toBe(
            host.certificateFingerprint,
        )
        const file = join(directory, 'public-certificate.pem')
        await writeFile(file, pem)
        const home = join(directory, 'browser-home')
        // Chromium 146 moved the default location; set up both in this disposable HOME.
        for (const relative of ['.pki/nssdb', '.local/share/pki/nssdb']) {
            const database = join(home, relative)
            await mkdir(database, { recursive: true })
            const certutil = process.env.TENONEDGE_CERTUTIL ?? 'certutil'
            await run(certutil, [
                '-N',
                '-d',
                'sql:' + database,
                '--empty-password',
            ])
            await run(certutil, [
                '-A',
                '-d',
                'sql:' + database,
                '-n',
                'tenonedge-owned-test',
                '-t',
                'P,,',
                '-i',
                file,
            ])
        }
        const browser = await chromium.launch({
            executablePath: process.env.TENONEDGE_TEST_EXECUTABLE,
            env: { ...process.env, HOME: home },
        })
        const context = await browser.newContext({ ignoreHTTPSErrors: false })
        return {
            ...host,
            url: host.httpsUrl,
            httpUrl: host.url,
            context,
            browser,
            ca: await readFile(file),
            async close() {
                await browser.close()
                await host.stop()
                await rm(directory, { recursive: true, force: true })
            },
        }
    } catch (error) {
        await host.stop()
        await rm(directory, { recursive: true, force: true })
        throw error
    }
}
