import { expect, it } from 'vitest'
import { readDocumentation } from './model'
it('shows actual operations and complete schemas without interpreting markup or external refs', () => {
    const input = {
        openapi: '3.1.1',
        info: { title: '<img src=x>', version: 'v1' },
        security: [{ local: [] }],
        paths: {
            '/api/edge-admin/v1/example': {
                parameters: [{ name: 'id', in: 'query' }],
                summary: 'not an operation',
                get: {
                    summary: '<script>bad()</script>',
                    operationId: 'actual',
                    responses: { 200: { $ref: 'https://external.invalid' } },
                },
                post: { security: [], requestBody: { required: true } },
            },
        },
        components: { schemas: { Real: { type: 'integer' } } },
    }
    const doc = readDocumentation(input)
    expect(doc.title).toBe('<img src=x>')
    expect(doc.operations).toHaveLength(2)
    expect(doc.operations[0].summary).toBe('<script>bad()</script>')
    expect(JSON.parse(doc.operations[0].detail).pathParameters).toEqual(
        input.paths['/api/edge-admin/v1/example'].parameters,
    )
    expect(JSON.parse(doc.operations[0].detail).security).toEqual(
        input.security,
    )
    expect(JSON.parse(doc.operations[1].detail).security).toEqual([])
    expect(JSON.parse(doc.complete)).toEqual(input)
})
it.each([
    null,
    [],
    {},
    { openapi: '2.0', info: {}, paths: {} },
    { openapi: '3.1.0', info: {}, paths: [] },
])('rejects non OpenAPI data %j', (value) => {
    expect(() => readDocumentation(value)).toThrow('invalid-openapi')
})
