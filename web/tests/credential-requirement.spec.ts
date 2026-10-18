import { test, expect } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { startOwnedHost } from './support/owned-host'

const root = '/api/edge-admin/v1'
const password = 'Refill-browser-5938!'

test('persisted refill fixture blocks apply after restart; explicit secret save advances and refreshes the real draft version', async ({
    browser,
}) => {
    test.setTimeout(90_000)
    const data = await mkdtemp(join(tmpdir(), 'tenonedge-refill-browser-'))
    let host = await startOwnedHost(data)
    let context = await browser.newContext({ baseURL: host.url })
    try {
        let page = await context.newPage()
        await page.goto('/')
        await page.getByLabel('账号', { exact: true }).fill('refill-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByLabel('确认密码', { exact: true }).fill(password)
        await page
            .getByRole('button', { name: '创建管理员并登录', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '总览', exact: true }),
        ).toBeVisible()
        const session = await (
            await page.request.get(root + '/auth/session')
        ).json()
        const created = await page.request.post(root + '/connections', {
            headers: { 'X-CSRF-Token': session.csrfToken },
            data: {
                expectedVersion: '0',
                key: 'receiver',
                name: 'Receiver',
                kind: 'http',
                http: { baseAddress: 'https://example.com/base/' },
            },
        })
        expect(created.status(), await created.text()).toBe(200)
        const connection = (await created.json()).connection
        await context.close()
        expect(await host.stop()).toBe(0)
        // Explicit persisted-state fixture while the owned Host is stopped. This
        // tests the real guard/refill workflow, NOT a finished import API or UI.
        execFileSync('python3', [
            '-c',
            `
import sqlite3, sys, uuid, time
with sqlite3.connect(sys.argv[1]) as db:
 db.execute("INSERT INTO configuration_credential_requirement(connection_id,account_identity,reason) VALUES (?,?,?)", (int(sys.argv[2]), 'sha256:'+'b'*64, 'exportRequiresInput'))
 db.execute("UPDATE configuration_draft SET version=?, updated_at=? WHERE id=1", (uuid.uuid4().hex, int(time.time()*1000)))
`,
            join(data, 'config.db'),
            String(connection.id),
        ])
        host = await startOwnedHost(data)
        context = await browser.newContext({ baseURL: host.url })
        page = await context.newPage()
        await page.goto('/configuration')
        await page.getByLabel('账号', { exact: true }).fill('refill-admin')
        await page.getByLabel('密码', { exact: true }).fill(password)
        await page.getByRole('button', { name: '登录', exact: true }).click()
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await expect(
            page.getByText(
                '导入草稿仍有连接需要重新填写凭证。请在连接页面保存新的凭证后重新预检；保留或清除旧凭证不能解除此阻断。',
                { exact: true },
            ),
        ).toBeVisible()
        await expect(
            page.getByRole('button', { name: '确认应用配置', exact: true }),
        ).toHaveCount(0)
        await page.getByRole('link', { name: '连接', exact: true }).click()
        await page
            .getByRole('button', { name: '编辑连接', exact: true })
            .click()
        let panel = page.getByRole('region', {
            name: '连接认证凭证',
            exact: true,
        })
        await expect(panel.getByText('未设置', { exact: true })).toBeVisible()
        const before = await (
            await page.request.get(root + '/connections')
        ).json()
        await panel
            .getByRole('combobox', { name: '新认证方式', exact: true })
            .selectOption('bearer')
        await panel
            .getByLabel('新密码或秘密', { exact: true })
            .fill('not-a-real-refill-secret')
        await panel
            .getByRole('button', {
                name: '核对凭证变更（不保存）',
                exact: true,
            })
            .click()
        await panel
            .getByRole('checkbox', {
                name: '我已核对原新身份和积存，了解凭证立即生效及隔离规则',
            })
            .check()
        await panel
            .getByRole('button', { name: '立即保存凭证', exact: true })
            .click()
        // Updated global version invalidates this editor rather than silently
        // reusing an old token. The operator explicitly opens the fresh draft.
        await expect(
            page.getByText('先保存或重新载入连接草稿，再编辑它的凭证。', {
                exact: true,
            }),
        ).toBeVisible()
        const after = await (
            await page.request.get(root + '/connections')
        ).json()
        expect(after.version).not.toBe(before.version)
        expect(after.connections).toEqual(before.connections)
        await expect(
            page.getByText(`全局草稿版本：${after.version}`, { exact: true }),
        ).toBeVisible()
        await page
            .getByRole('button', { name: '编辑连接', exact: true })
            .click()
        panel = page.getByRole('region', { name: '连接认证凭证', exact: true })
        await expect(panel.getByText('已设置', { exact: true })).toBeVisible()
        await expect(
            panel.getByLabel('新密码或秘密', { exact: true }),
        ).toHaveValue('')
        await page.goto('/configuration')
        await page
            .getByRole('button', { name: '查看应用变更清单', exact: true })
            .click()
        await expect(
            page.getByRole('heading', { name: '应用前确认', exact: true }),
        ).toBeVisible()
        await expect(
            page.getByRole('button', { name: '确认应用配置', exact: true }),
        ).toBeDisabled()
        const state = await (
            await page.request.get(root + '/configuration/runtime')
        ).json()
        expect(state.effectiveRevision).toBeNull()
        await page.screenshot({
            path: 'test-results/credential-refill-guard.png',
            fullPage: true,
        })
    } finally {
        try {
            await context.close()
        } finally {
            try {
                await host.stop()
            } finally {
                await rm(data, { recursive: true, force: true })
            }
        }
    }
})
