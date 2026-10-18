import { expect, it } from 'vitest'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { decodeGraph, encodeGraph } from './codec'
import { createGraphEditor } from './store'
import {
    transformConfig,
    transformDefaults,
    transformValues,
} from './transform-model'
import { TransformEditor } from './transform-editor'
import { parameterIssues, type ParameterSchema } from '../parameters/model'

const schema: ParameterSchema = {
    id: 'example',
    scope: 'transform',
    parameters: [
        {
            name: 'value',
            displayName: '精确数字',
            type: 'number',
            required: true,
            wholeNumber: true,
            defaultJson: '9007199254740993',
            minimumJson: '0',
            maximumJson: '9223372036854775807',
        },
        { name: 'title', displayName: '文本', type: 'text', maximumLength: 8 },
        {
            name: 'enabled',
            displayName: '开关',
            type: 'switch',
            defaultJson: 'false',
        },
        {
            name: 'mode',
            displayName: '方式',
            type: 'choice',
            options: [{ value: 'a', displayName: '选项甲' }],
        },
    ],
}
it('adds exact defaults as one undo step and retains version through copy and codec even without metadata', () => {
    const store = createGraphEditor()
    const configText = transformDefaults(schema)
    expect(configText).toBe('{"value":9007199254740993,"enabled":false}')
    const id = store
        .getState()
        .add(
            'transform.example',
            '例子',
            { x: 1, y: 2 },
            { version: 2, configText },
        )
    expect(store.getState().past).toHaveLength(1)
    store.getState().undo()
    expect(store.getState().document.definition.nodes).toHaveLength(0)
    store.getState().redo()
    store.getState().select([id], [])
    store.getState().copy()
    store.getState().paste()
    const decoded = decodeGraph(encodeGraph(store.getState().document))
    expect(decoded.definition.nodes).toHaveLength(2)
    for (const node of decoded.definition.nodes)
        expect(node).toMatchObject({
            kind: 'transform.example',
            version: 2,
            configText,
        })
})
it('preserves untouched invalid scalar types, nulls, exact lexemes and unknown fields rather than resetting defaults', () => {
    const original =
        '{"value":1.000,"title":false,"mode":null,"extra":9223372036854775807}'
    expect(transformConfig(original, schema, {})).toBe(original)
    expect(
        transformConfig(original, schema, { value: '9007199254740993' }),
    ).toBe(
        '{"value":9007199254740993,"title":false,"mode":null,"extra":9223372036854775807}',
    )
    expect(parameterIssues(schema, transformValues(original))).toContainEqual({
        name: '',
        code: 'unknown',
    })
})
it('keeps incomplete edits saveable without rounding or pretending they are valid', () => {
    expect(transformConfig('{}', schema, { value: 'unfinished' })).toBe(
        '{"value":"unfinished"}',
    )
    expect(transformConfig('{"value":42}', schema, { value: '' })).toBe('{}')
    expect(parameterIssues(schema, { value: 'unfinished' })).toContainEqual({
        name: 'value',
        code: 'number',
    })
    expect(
        parameterIssues(schema, { value: '9223372036854775808' }),
    ).toContainEqual({ name: 'value', code: 'range' })
    expect(() => transformValues('{"value":[]}')).toThrow()
    expect(() => transformConfig('{}', schema, { unknown: 'no' })).toThrow()
})
it('renders installed scalar declaration with labels, defaults, choices and required errors', () => {
    const html = renderToStaticMarkup(
        createElement(TransformEditor, {
            schema,
            configText: '{}',
            disabled: false,
            dirty: () => {},
            accept: () => true,
        }),
    )
    expect(html).toContain('精确数字')
    expect(html).toContain('此参数必填')
    expect(html).toContain('选项甲')
    expect(html.toLowerCase()).toContain('novalidate')
})
