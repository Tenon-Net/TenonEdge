import { describe, expect, it } from 'vitest'
import { decodeGraph, encodeGraph, normalizeConfigText } from './codec'
import {
    readRouteJson,
    writeRouteJson,
    RouteNumber,
    maxGraphBytes,
} from './json'
import { createGraphEditor } from './store'
const constants =
    '9223372036854775807,0.1234567890123456789012345678,-0,1.2300e+20'
const fixture = (config = '{"rules":[]}') =>
    `{"schemaVersion":1,"nodes":[{"id":"g1","type":"transform.map","version":1,"name":"Map","config":${config}}],"edges":[],"layout":{"nodes":{"g1":{"x":1,"y":2}}}}`

describe('lossless route JSON', () => {
    it('retains number lexemes and types, including overflow and negative zero', () => {
        const text = `[${constants},1e9999,"9223372036854775807",true,false,null]`
        expect(writeRouteJson(readRouteJson(text))).toBe(text)
        expect(readRouteJson('9223372036854775807')).toBeInstanceOf(RouteNumber)
    })
    it('handles escaped keys, numeric-looking text, and prototype keys safely', () => {
        const text =
            '{"__proto__":{"x":1},"quote\\\"":"1e9999, [ \\\"x\\\" ]","unicode":"中😀","constructor":null}'
        expect(JSON.parse(writeRouteJson(readRouteJson(text)))).toEqual(
            JSON.parse(text),
        )
        expect(Object.prototype).not.toHaveProperty('x')
    })
    it.each([
        '{"a":1,"a":2}',
        '{"a":1,"\\u0061":2}',
        '{"nested":{"x":0,"x":1}}',
    ])('rejects ambiguous members %s', (text) =>
        expect(() => readRouteJson(text)).toThrow('duplicate'),
    )
    it.each([
        '[1,]',
        '01',
        'NaN',
        'undefined',
        '{"a":}',
        'true false',
        '"unterminated',
    ])('rejects invalid syntax %s', (text) => {
        expect(() => readRouteJson(text)).toThrow()
    })
    it('enforces UTF-8 size and nesting bounds', () => {
        expect(() =>
            readRouteJson('"' + '中'.repeat(maxGraphBytes / 3) + '"'),
        ).toThrow('limit')
        expect(() =>
            readRouteJson('['.repeat(18) + '0' + ']'.repeat(18)),
        ).toThrow('limit')
        expect(() => writeRouteJson(Infinity)).toThrow('json')
        expect(() => new RouteNumber('1,2')).toThrow('json')
    })
    it('does not treat numeric-looking string constants as numbers', () => {
        expect(
            normalizeConfigText(
                '{ "constant":"9223372036854775807", "other":9223372036854775807 }',
            ),
        ).toBe('{"constant":"9223372036854775807","other":9223372036854775807}')
        expect(() => normalizeConfigText('[1]')).toThrow('graph')
    })
})

describe('persisted graph to editor boundary', () => {
    it('preserves constants through loading, moving, copying and undo/redo', () => {
        const config = `{"rules":[${constants
            .split(',')
            .map(
                (constant, index) =>
                    `{"target":"f${index}","constant":${constant}}`,
            )
            .join(',')}]}`
        const doc = decodeGraph(fixture(config))
        const store = createGraphEditor()
        store.getState().load(doc)
        doc.definition.nodes[0].configText = '{}'
        const state = () => store.getState()
        state().rename('g1', 'renamed')
        state().move({ g1: { x: 10, y: 20 } })
        state().select(['g1'], [])
        state().copy()
        state().paste()
        expect(
            state().document.definition.nodes.map((node) => node.configText),
        ).toEqual([config, config])
        expect(
            new Set(state().document.definition.nodes.map((node) => node.id))
                .size,
        ).toBe(2)
        const saved = encodeGraph(state().document)
        state().undo()
        state().redo()
        expect(encodeGraph(state().document)).toBe(saved)
        expect(decodeGraph(saved).definition.nodes[0].configText).toBe(config)
        expect(saved).not.toContain('configText')
        expect(saved).not.toContain('definition')
    })
    it('edits configuration atomically and preserves history on invalid JSON', () => {
        const store = createGraphEditor()
        store.getState().load(decodeGraph(fixture()))
        store.getState().configure('g1', '{"constant":9223372036854775807}')
        const before = store.getState().document
        expect(() =>
            store.getState().configure('g1', '{"a":1,"a":2}'),
        ).toThrow()
        expect(store.getState().document).toBe(before)
        store.getState().undo()
        expect(store.getState().document.definition.nodes[0].configText).toBe(
            '{"rules":[]}',
        )
        store.getState().redo()
        expect(store.getState().document).toBe(before)
    })
    it('loads a new route without old selection, history or clipboard', () => {
        const store = createGraphEditor()
        store.getState().add('input.simulator', 'test', { x: 0, y: 0 })
        store.getState().copy()
        store.getState().load(decodeGraph(fixture()))
        expect(store.getState()).toMatchObject({
            past: [],
            future: [],
            selectedNodes: [],
            selectedEdges: [],
            clipboard: null,
        })
        const added = store.getState().add('output.http', 'out', { x: 0, y: 0 })
        expect(added).not.toBe('g1')
    })
    it('keeps incomplete topology, duplicates and cycles for server compilation', () => {
        const text = fixture().replace(
            '"edges":[]',
            '"edges":[{"source":"g1","target":"g1"},{"source":"g1","target":"missing"},{"source":"g1","target":"missing"}]',
        )
        expect(
            decodeGraph(encodeGraph(decodeGraph(text))).definition.edges,
        ).toHaveLength(3)
    })
    it.each([
        ['"schemaVersion":1', '"schemaVersion":2'],
        ['"version":1', '"version":2'],
        ['"type":"transform.map"', '"type":"script"'],
        ['"x":1', '"x":10000001'],
        ['"x":1', '"x":"1"'],
        ['"config":', '"unknown":1,"config":'],
    ])('rejects unsupported or unsafe structure %s', (from, to) => {
        expect(() => decodeGraph(fixture().replace(from, to))).toThrow()
    })
    it('refuses oversized copy and off-canvas paste without changing the document', () => {
        const store = createGraphEditor()
        store.getState().load(decodeGraph(fixture()))
        store
            .getState()
            .configure('g1', JSON.stringify({ constant: 'x'.repeat(600000) }))
        store.getState().select(['g1'], [])
        store.getState().copy()
        const before = store.getState().document
        expect(() => store.getState().paste()).toThrow('limit')
        expect(store.getState().document).toBe(before)
        store.getState().configure('g1', '{}')
        store.getState().move({ g1: { x: 10000000, y: 0 } })
        store.getState().copy()
        const edge = store.getState().document
        expect(() => store.getState().paste()).toThrow('position')
        expect(store.getState().document).toBe(edge)
    })
    it('bounds generated JSON before joining all serialized chunks', () => {
        expect(() =>
            writeRouteJson(Array(20).fill('x'.repeat(100000))),
        ).toThrow('limit')
    })
    it('supplies deterministic layout only when absent', () => {
        const text = fixture().replace('"g1":{"x":1,"y":2}', '')
        expect(decodeGraph(text).layout.positions.g1).toEqual({ x: 80, y: 80 })
    })
})
