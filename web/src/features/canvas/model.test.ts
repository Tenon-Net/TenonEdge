import { describe, expect, it } from 'vitest'
import {
    connectionError,
    emptyGraph,
    GraphEditError,
    topologyHints,
    type NodeKind,
} from './model'
import { createGraphEditor } from './store'
const fixture = () => {
    const store = createGraphEditor()
    const add = (kind: NodeKind = 'transform.map') =>
        store.getState().add(kind, kind, { x: 10, y: 20 })
    return { store, add, state: () => store.getState() }
}
describe('local graph document and history', () => {
    it('keeps semantics and layout separate without library private state', () => {
        const { state, add } = fixture()
        const id = add('input.modbus')
        expect(state().document.definition.nodes).toEqual([
            { id, kind: 'input.modbus', name: 'input.modbus' },
        ])
        expect(state().document.layout.positions).toEqual({
            [id]: { x: 10, y: 20 },
        })
        expect(Object.keys(state().document)).toEqual([
            'schemaVersion',
            'definition',
            'layout',
        ])
    })
    it('supports 50 undo and redo steps and discards older history', () => {
        const { state, add } = fixture()
        const id = add()
        for (let i = 0; i < 60; i++) state().rename(id, `name${i}`)
        expect(state().past).toHaveLength(50)
        for (let i = 0; i < 50; i++) state().undo()
        expect(state().document.definition.nodes[0].name).toBe('name9')
        const first = state().document
        state().undo()
        expect(state().document).toBe(first)
        for (let i = 0; i < 50; i++) state().redo()
        expect(state().document.definition.nodes[0].name).toBe('name59')
        const last = state().document
        state().redo()
        expect(state().document).toBe(last)
    })
    it('does not record selection, copy or no-op edits and clears redo on edits', () => {
        const { state, add } = fixture()
        const id = add()
        state().select([id], [])
        state().copy()
        state().move({ [id]: { x: 10, y: 20 } })
        state().rename(id, 'transform.map')
        expect(state().past).toHaveLength(1)
        state().rename(id, 'new')
        state().undo()
        state().rename(id, 'branch')
        expect(state().future).toHaveLength(0)
    })
    it('treats group movement as one history entry and copies positions defensively', () => {
        const { state, add } = fixture()
        const a = add(),
            b = add()
        const old = state().document
        const p = { x: 50, y: 70 }
        state().move({ [a]: p, [b]: p })
        p.x = 999
        expect(state().document.layout.positions[a].x).toBe(50)
        expect(old.layout.positions[a].x).toBe(10)
        state().undo()
        expect(state().document).toBe(old)
        state().redo()
        expect(state().document.layout.positions[b].y).toBe(70)
    })
    it('removes incident and selected edges atomically and restores everything', () => {
        const { state, add } = fixture()
        const a = add('input.mqtt'),
            b = add(),
            c = add('output.http')
        state().connect(a, b)
        state().connect(b, c)
        const original = state().document
        state().select([b], [])
        state().remove()
        expect(state().document.definition.edges).toHaveLength(0)
        expect(state().document.layout.positions[b]).toBeUndefined()
        state().undo()
        expect(state().document).toBe(original)
        state().select([], [original.definition.edges[0].id])
        state().remove()
        expect(state().document.definition.nodes).toHaveLength(3)
        expect(state().document.definition.edges).toHaveLength(1)
    })
    it('copies induced subgraph with fresh IDs and without external links', () => {
        const { state, add } = fixture()
        const a = add('input.modbus'),
            b = add(),
            c = add('output.mqtt')
        state().connect(a, b)
        state().connect(b, c)
        state().select([a, b], [])
        state().copy()
        state().rename(a, 'changed')
        state().paste()
        const doc = state().document
        const copies = doc.definition.nodes.slice(3)
        expect(copies.map((n) => n.name)).toEqual([
            'input.modbus',
            'transform.map',
        ])
        expect(doc.definition.edges[2]).toMatchObject({
            source: copies[0].id,
            target: copies[1].id,
        })
        expect(doc.layout.positions[copies[0].id]).toEqual({ x: 42, y: 52 })
        state().paste()
        const allIds = [
            ...state().document.definition.nodes,
            ...state().document.definition.edges,
        ].map((n) => n.id)
        expect(new Set(allIds).size).toBe(allIds.length)
        state().undo()
        expect(state().document).toBe(doc)
    })
    it('enforces the 50-node ceiling for both add and paste atomically', () => {
        const { state, add } = fixture()
        const id = add()
        state().select([id], [])
        state().copy()
        for (let i = 1; i < 50; i++) add()
        const before = state().document
        expect(() => add()).toThrow('limit')
        expect(() => state().paste()).toThrow('limit')
        expect(state().document).toBe(before)
    })
    it.each([NaN, Infinity, -Infinity])(
        'rejects invalid coordinates %s without changing history',
        (value) => {
            const { state, add } = fixture()
            const id = add()
            const doc = state().document
            expect(() => state().move({ [id]: { x: value, y: 1 } })).toThrow(
                GraphEditError,
            )
            expect(() =>
                state().add('input.http', 'x', { x: 1, y: value }),
            ).toThrow(GraphEditError)
            expect(state().document).toBe(doc)
        },
    )
    it('has no cross-instance clipboard or history', () => {
        const a = fixture()
        a.add()
        a.state().copy()
        expect(createGraphEditor().getState().document).toEqual(emptyGraph())
        expect(createGraphEditor().getState().clipboard).toBeNull()
    })
})
describe('local topology guard, not executable-plan validation', () => {
    it('allows independent fan-in and fan-out and reports dangling nodes', () => {
        const { state, add } = fixture()
        const a = add('input.http'),
            b = add('input.mqtt'),
            m = add(),
            o = add('output.mqtt'),
            p = add('output.http')
        expect(topologyHints(state().document)).toHaveLength(5)
        state().connect(a, m)
        state().connect(b, m)
        state().connect(m, o)
        state().connect(m, p)
        expect(topologyHints(state().document)).toEqual([])
    })
    it('rejects missing endpoints, wrong direction, duplicates, self and longer cycles', () => {
        const { state, add } = fixture()
        const i = add('input.simulator'),
            a = add(),
            b = add(),
            c = add(),
            o = add('output.http')
        state().connect(i, a)
        state().connect(a, b)
        state().connect(b, c)
        state().connect(c, o)
        for (const [source, target, code] of [
            [i, 'absent', 'missing'],
            [o, a, 'direction'],
            [a, i, 'direction'],
            [a, a, 'cycle'],
            [c, a, 'cycle'],
            [a, b, 'duplicate'],
        ]) {
            const doc = state().document
            expect(connectionError(doc, source, target)).toBe(code)
            expect(() => state().connect(source, target)).toThrow(code)
            expect(state().document).toBe(doc)
        }
    })
})
