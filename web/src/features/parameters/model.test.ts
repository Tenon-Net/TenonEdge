import { expect, it } from 'vitest'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import '../../lib/i18n'
import { ParameterFields } from './fields'
import {
    wholeNumberText,
    parameterDefaults,
    parameterIssues,
    type ParameterSchema,
} from './model'

const schema: ParameterSchema = {
    id: 'example',
    scope: 'connection',
    parameters: [
        {
            name: 'title',
            displayName: '标题',
            type: 'text',
            required: true,
            maximumLength: 8,
            group: '基本',
        },
        {
            name: 'number',
            displayName: '数字',
            type: 'number',
            required: true,
            defaultJson: '9007199254740993',
            minimumJson: '9007199254740993',
            maximumJson: '9007199254740993',
        },
        {
            name: 'enabled',
            displayName: '启用',
            type: 'switch',
            required: true,
            defaultJson: 'false',
        },
        {
            name: 'mode',
            displayName: '方式',
            type: 'choice',
            options: [{ value: 'one', displayName: '方式一' }],
        },
        {
            name: 'password',
            displayName: '密码',
            type: 'credential',
            required: true,
        },
        {
            name: 'connection',
            displayName: '引用',
            type: 'connectionReference',
            required: true,
            connectionKinds: ['mqtt'],
        },
    ],
}
const values = {
    title: '设备',
    number: '9007199254740993',
    enabled: false,
    mode: 'one',
    connection: '1',
}
const connections = [{ id: '1', kind: 'mqtt', name: 'Broker' }]
it('supports all six scalars, with false and configured secrets satisfying required declarations', () => {
    expect(
        parameterIssues(schema, values, connections, new Set(['password'])),
    ).toEqual([])
    expect(parameterDefaults(schema)).toEqual({
        number: '9007199254740993',
        enabled: false,
    })
    expect(
        parameterIssues(
            schema,
            { ...values, password: 'secret', unknownSecret: 'secret' },
            connections,
        ),
    ).toEqual([
        { name: '', code: 'unknown' },
        { name: 'password', code: 'credentialSeparate' },
    ])
})
it.each([
    ['9007199254740992', '9007199254740993', undefined, 'range'],
    ['9007199254740994', undefined, '9007199254740993', 'range'],
    ['0.10000000000000000000000000001', undefined, '0.1', 'range'],
    ['1e-1000', '0', '0', 'range'],
    ['-1e-1000', '0', undefined, 'range'],
    ['-0', '0', '0.0', undefined],
    ['1.00e2', '100', '100', undefined],
    ['-1.01', '-2', '-1', undefined],
    ['-1.01', '-1', '0', 'range'],
    ['18446744073709551615', '0', '18446744073709551615', undefined],
    ['1e1000001', undefined, undefined, 'number'],
    ['1'.repeat(129), undefined, undefined, 'number'],
    ['01', undefined, undefined, 'number'],
    ['NaN', undefined, undefined, 'number'],
])(
    'compares numeric lexeme %s exactly',
    (number, minimumJson, maximumJson, code) => {
        const declaration: ParameterSchema = {
            id: 'number',
            scope: 'input',
            parameters: [
                {
                    name: 'number',
                    displayName: 'Number',
                    type: 'number',
                    minimumJson,
                    maximumJson,
                },
            ],
        }
        expect(parameterIssues(declaration, { number })).toEqual(
            code ? [{ name: 'number', code }] : [],
        )
    },
)
it.each([
    ['1.0', true],
    ['1e2', true],
    ['-0.0', true],
    ['1.1', false],
    ['1e-1000', false],
])('checks integral value %s', (number, valid) => {
    expect(
        parameterIssues(
            {
                id: 'whole',
                scope: 'input',
                parameters: [
                    {
                        name: 'number',
                        displayName: 'Number',
                        type: 'number',
                        wholeNumber: true,
                    },
                ],
            },
            { number },
        ).length,
    ).toBe(valid ? 0 : 1)
})
it.each(['01', '9007199254740992', '2', ''])(
    'rejects noncanonical, unsafe or missing reference %s',
    (connection) => {
        expect(
            parameterIssues(
                schema,
                { ...values, connection },
                connections,
                new Set(['password']),
            ),
        ).toHaveLength(1)
    },
)
it('rejects type, length, options and unresolved protocol without echoing the input', () => {
    expect(
        parameterIssues(
            schema,
            {
                ...values,
                title: 'x'.repeat(9),
                enabled: 'false',
                mode: 'wrong',
            },
            [{ id: '1', kind: 'http', name: 'HTTP' }],
            new Set(['password']),
        ).map((issue) => issue.code),
    ).toEqual(['length', 'type', 'choice', 'connection'])
})
it('renders exact editable text, groups, descriptions and isolated password controls without exposing secrets', () => {
    const html = renderToStaticMarkup(
        createElement(ParameterFields, {
            schema,
            values: { ...values, password: 'must-not-render' },
            connections: [
                ...connections,
                { id: '2', kind: 'http', name: 'wrong-protocol' },
            ],
            onChange: () => {},
        }),
    )
    expect(html).toContain('value="9007199254740993"')
    expect(html).toContain('type="text"')
    expect(html).toContain('type="checkbox"')
    expect(html).toContain('type="password"')
    expect(html).toContain('Broker')
    expect(html).toContain('基本')
    expect(html).not.toContain('must-not-render')
    expect(html).not.toContain('wrong-protocol')
    expect(values.number).toBe('9007199254740993')
})
it('does not merge defaults into controlled values or hide a missing selected connection', () => {
    const edited = { ...values, number: '42', connection: 'missing' }
    const html = renderToStaticMarkup(
        createElement(ParameterFields, {
            schema,
            values: edited,
            onChange: () => {},
        }),
    )
    expect(html).toContain('value="42"')
    expect(html).toContain('当前选择不可用')
    expect(edited.number).toBe('42')
})

it.each([
    ['1e3', '1000'],
    ['1.50e2', '150'],
    ['-0.0', '0'],
    ['9007199254740993.0', '9007199254740993'],
    ['1e-1000', '1e-1000'],
    ['1e1000000', '1e1000000'],
    ['invalid', 'invalid'],
])(
    'normalizes only bounded integral text %s for typed DTOs',
    (value, expected) => {
        expect(wholeNumberText(value)).toBe(expected)
    },
)
