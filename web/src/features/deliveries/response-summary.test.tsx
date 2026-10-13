import { expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { ResponseSummary } from './response-summary'
import type { BodyPreview } from './api'

it('renders response text as escaped text without interpreting HTML or rounding embedded numbers', () => {
    const html = renderToStaticMarkup(
        <ResponseSummary
            summary={{
                statusCode: 422,
                state: 'captured',
                text: '<script>globalThis.injected=true</script> 9007199254740993',
                truncated: true,
            }}
        />,
    )
    expect(html).toContain('&lt;script&gt;')
    expect(html).not.toContain('<script>')
    expect(html).toContain('9007199254740993')
    expect(html).toContain('HTTP 422')
    expect(html).toContain('响应摘要已截断')
    expect(html).toContain('原接收方')
})
it.each([
    'empty',
    'tooLarge',
    'unsupportedEncoding',
    'unsupportedMediaType',
    'invalidBody',
    'redacted',
    'unavailable',
] as const)(
    'shows explicit %s state without rendering stray body text',
    (state) => {
        const html = renderToStaticMarkup(
            <ResponseSummary
                summary={{
                    statusCode: 422,
                    state,
                    text: 'must-not-render',
                    truncated: false,
                }}
            />,
        )
        expect(html).not.toContain('must-not-render')
        expect(html).not.toContain('<pre')
        expect(html).not.toContain(`deliveries.response.states.${state}`)
        expect(html).toContain('HTTP 422')
    },
)
it.each([null, undefined])(
    'does not pretend a missing summary is an empty response',
    (summary) => {
        const html = renderToStaticMarkup(<ResponseSummary summary={summary} />)
        expect(html).toContain('未保留响应摘要')
        expect(html).toContain('不表示对方返回空正文')
        expect(html).not.toContain('<pre')
    },
)
it('hides unknown response state rather than guessing it is safe text', () => {
    const summary = JSON.parse(
        '{"statusCode":422,"state":"future-state","text":"must-not-render"}',
    ) as BodyPreview['responseSummary']
    const html = renderToStaticMarkup(<ResponseSummary summary={summary} />)
    expect(html).toContain('未知摘要状态')
    expect(html).not.toContain('must-not-render')
})
