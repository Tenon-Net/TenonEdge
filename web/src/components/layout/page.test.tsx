import { expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { PageHeading, PageTabs } from './page'

it('compact heading has one semantic H1 and puts guidance in an optional disclosure', () => {
    const html = renderToStaticMarkup(
        <PageHeading title="连接 <现场>" description="本地说明" />,
    )
    expect(html.match(/<h1/g)).toHaveLength(1)
    expect(html).toContain('连接 &lt;现场&gt;')
    expect(html).toContain('<details')
    expect(html).not.toContain('<details open')
    expect(html).toContain('本地说明')
})
it('settings keep all keyed panels mounted and hide only inactive content', () => {
    const html = renderToStaticMarkup(
        <PageTabs
            label="设置分类"
            initialTab="second"
            tabs={[
                {
                    id: 'first',
                    label: '第一组',
                    content: <input defaultValue="未保存 9007199254740993" />,
                },
                { id: 'second', label: '第二组', content: <p>当前内容</p> },
            ]}
        />,
    )
    expect(html.match(/role="tabpanel"/g)).toHaveLength(2)
    expect(html.match(/hidden=""/g)).toHaveLength(1)
    expect(html).toContain('未保存 9007199254740993')
    expect(html.match(/aria-selected="true"/g)).toHaveLength(1)
    expect(html.match(/tabindex="-1"/g)).toHaveLength(1)
    const panelIds = [...html.matchAll(/role="tabpanel" id="([^"]+)"/g)].map(
        (m) => m[1],
    )
    for (const id of panelIds) expect(html).toContain(`aria-controls="${id}"`)
})
it('unknown category safely uses the first real panel, without inventing an empty page', () => {
    const html = renderToStaticMarkup(
        <PageTabs
            label="设置分类"
            initialTab="unknown"
            tabs={[{ id: 'basic', label: '基本信息', content: '真实信息' }]}
        />,
    )
    expect(html).toContain('aria-selected="true"')
    expect(html).not.toContain('hidden=""')
    expect(html).toContain('真实信息')
})
