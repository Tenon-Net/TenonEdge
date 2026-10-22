import { expect, type Frame, type Page } from '@playwright/test'
import { createHash } from 'node:crypto'

const hash = (value: string) => createHash('sha256').update(value).digest('hex')
export const rows = Array.from({ length: 100 }, (_, index) => [
    index === 50 ? 'bad/key' : `excel_${index}`,
    `点位${index + 1}`,
    '3',
    index === 75 ? '65536' : String(index),
    'uint16',
    '',
    '',
    'kPa',
    'true',
])
export const replacement = [
    'excel_replacement',
    '替换点位',
    '3',
    '150',
    'uint16',
    '',
    '',
    'MPa',
    'true',
]
export const second = [
    'excel_replacement_two',
    '替换点位二',
    '3',
    '151',
    'uint16',
    '',
    '',
    'MPa',
    'true',
]
const expectedText = (values: string[][]) =>
    values.map((row) => row.join('\t')).join('\n')
const normalized = (value: string) =>
    value.replaceAll('\r\n', '\n').replace(/\n$/, '')

// Dedicated prepared artificial workbook only. No test code writes source
// cells, clipboard data or an application store.
export async function prepareExcel(page: Page, link: string) {
    let stage = 'open dedicated shared workbook'
    try {
        await page.goto(link, {
            waitUntil: 'domcontentloaded',
            timeout: 45_000,
        })
        await expect
            .poll(
                () =>
                    page.frames().some((frame) => {
                        try {
                            return (
                                new URL(frame.url()).hostname ===
                                'excel.officeapps.live.com'
                            )
                        } catch {
                            return false
                        }
                    }),
                { timeout: 45_000 },
            )
            .toBe(true)
        const sheet = page.frames().find((frame) => {
            try {
                return (
                    new URL(frame.url()).hostname ===
                    'excel.officeapps.live.com'
                )
            } catch {
                return false
            }
        })!
        stage = 'verify editable Excel grid'
        await expect(
            sheet.getByRole('button', { name: /Mode Menu;Editing Selected/ }),
        ).toBeVisible({ timeout: 45_000 })
        const readyBefore = await sheet.evaluate(() => document.readyState)
        await sheet.waitForLoadState('load', { timeout: 45_000 })
        await page.waitForLoadState('load', { timeout: 45_000 })
        await sheet.locator('html').evaluate((element, before) => {
            element.setAttribute('data-acceptance-load-before', before)
        }, readyBefore)
        return sheet
    } catch (error) {
        const reason = String(error)
            .split('\n')[0]
            .replace(/https?:\/\/[^\s"'<>]+/g, '[shared-url-redacted]')
        // A shared URL is an access capability. Do not leak it via Playwright's
        // goto call log, frame dump, page snapshot, error.stack or HAR.
        throw new Error(`Excel source failed at: ${stage}; ${reason}`)
    }
}

async function focusCell(page: Page, sheet: Frame, row: number) {
    const canvas = sheet.locator('canvas.ewr-sheettable')
    const index = await canvas.evaluateAll((elements) =>
        elements.findIndex((element) => {
            const box = element.getBoundingClientRect()
            return (
                box.width > 500 &&
                box.height > 100 &&
                box.top < innerHeight &&
                box.bottom > 100
            )
        }),
    )
    expect(index).toBeGreaterThanOrEqual(0)
    const grid = canvas.nth(index)
    const box = (await grid.boundingBox())!
    // Excel paints to canvas; a transparent grid layer receives real clicks.
    await page.mouse.click(Math.max(100, box.x + 30), Math.max(200, box.y + 10))
    await page.keyboard.press('Control+Home')
    for (let index = 0; index < row; index++)
        await page.keyboard.press('ArrowDown')
    return grid
}

export async function copyExcel(
    page: Page,
    sheet: Frame,
    row: number,
    values: string[][],
    proofPath: string,
) {
    let stage = 'focus'
    let latest = ''
    try {
        await page.bringToFront()
        const grid = await focusCell(page, sheet, row)
        stage = 'selected-cell-ready'
        await expect(
            sheet.locator('#formulaBarTextDivId_textElement'),
        ).toHaveText(values[0][0])
        await expect
            .poll(() =>
                sheet.evaluate(
                    () =>
                        document.hasFocus() &&
                        document.activeElement?.id ===
                            'gridKeyboardContentEditable_textElement',
                ),
            )
            .toBe(true)
        for (let index = 0; index < 8; index++)
            await page.keyboard.press('Shift+ArrowRight')
        for (let index = 1; index < values.length; index++)
            await page.keyboard.press('Shift+ArrowDown')
        // Crop to Excel's cell canvas, never the account/document chrome.
        await grid.screenshot({ path: proofPath })
        // Observe completion without changing arguments, returned promises or
        // clipboard data. Wait for the real browser API completion before reading,
        // and never repeat the copy action to obtain a green result.
        await sheet.evaluate(() => {
            const clipboard = navigator.clipboard
            const html = document.documentElement
            html.dataset.acceptanceWrite = 'idle'
            if (html.dataset.acceptanceWriteObserved) return
            html.dataset.acceptanceWriteObserved = 'true'
            for (const method of ['write', 'writeText'] as const) {
                const original = clipboard[method].bind(clipboard)
                Object.defineProperty(clipboard, method, {
                    configurable: true,
                    value: (...args: unknown[]) => {
                        html.dataset.acceptanceWrite = 'started:' + method
                        const result = Reflect.apply(
                            original,
                            clipboard,
                            args,
                        ) as Promise<void>
                        void result.then(
                            () => {
                                html.dataset.acceptanceWrite =
                                    'completed:' + method
                            },
                            (error) => {
                                html.dataset.acceptanceWrite =
                                    'failed:' + error.name
                            },
                        )
                        return result
                    },
                })
            }
        })
        await sheet
            .getByRole('button', { name: 'Clipboard', exact: true })
            .click()
        const copy = sheet.getByRole('menuitem', { name: /^Copy/ })
        await copy.evaluate((element) => {
            delete document.documentElement.dataset.acceptanceCopyTrusted
            element.addEventListener(
                'click',
                (event) => {
                    document.documentElement.dataset.acceptanceCopyTrusted =
                        String(event.isTrusted)
                },
                { once: true, capture: true },
            )
        })
        stage = 'ribbon-copy'
        await copy.click()
        stage = 'native-write-completion'
        await expect(sheet.locator('html')).toHaveAttribute(
            'data-acceptance-write',
            /^completed:/,
            { timeout: 10_000 },
        )
        // Poll asynchronous clipboard completion, not the copy action. No retry.
        await expect
            .poll(
                async () => {
                    latest = await page.evaluate(() =>
                        navigator.clipboard.readText(),
                    )
                    return normalized(latest)
                },
                { timeout: 10_000 },
            )
            .toBe(expectedText(values))
        stage = 'trusted-event'
        expect(
            await sheet
                .locator('html')
                .getAttribute('data-acceptance-copy-trusted'),
        ).toBe('true')
        const text = await page.evaluate(() => navigator.clipboard.readText())
        return {
            text,
            characters: text.length,
            sha256: hash(text),
            copyAction: 'excel-ribbon-copy',
            trustedClick: true,
            range: `Sheet1!A${row + 1}:I${row + values.length}`,
        }
    } catch {
        const focus = await sheet.evaluate(() => ({
            id: document.activeElement?.id,
            nameBox: (
                document.querySelector(
                    '#FormulaBar-NameBox-input',
                ) as HTMLInputElement
            )?.value,
            trusted: document.documentElement.dataset.acceptanceCopyTrusted,
            nativeWrite: document.documentElement.dataset.acceptanceWrite,
            readyState: document.readyState,
            loadBefore: document.documentElement.dataset.acceptanceLoadBefore,
        }))
        throw new Error(
            `Excel ribbon copy/range assertion failed at row ${row + 1}; shared URL redacted; ` +
                JSON.stringify({
                    stage,
                    characters: latest.length,
                    sha256: hash(latest),
                    lines: normalized(latest).split('\n').length,
                    focus,
                }),
        )
    }
}
