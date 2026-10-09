import { expect, it } from 'vitest'
import { emptyGraph, type GraphNode } from '../canvas/model'
import { createPreviewDraft, previewSources } from './preview-model'

const input = (id: string, configText: string): GraphNode => ({
    id,
    name: id,
    kind: 'input.simulator',
    configText,
})
it('deduplicates references per node and counts shared input nodes only', () => {
    const document = emptyGraph()
    document.definition.nodes = [
        input('first', '{"sourceIds":[1,1,2]}'),
        input('second', '{"sourceIds":[1]}'),
        { ...input('output', '{"connectionId":3}'), kind: 'output.http' },
    ]
    expect(previewSources(document)).toEqual({
        sources: [
            {
                key: 'input.simulator:1',
                id: 1,
                kind: 'input.simulator',
                nodeId: 'first',
                nodeName: 'first',
                nodeCount: 2,
            },
            {
                key: 'input.simulator:2',
                id: 2,
                kind: 'input.simulator',
                nodeId: 'first',
                nodeName: 'first',
                nodeCount: 1,
            },
        ],
        invalidNodes: [],
        overLimit: false,
    })
})
it('retains valid nodes while rejecting unsafe and malformed references without modifying exact constants', () => {
    const document = emptyGraph()
    document.definition.nodes = [
        input('unsafe', '{"sourceIds":[9007199254740993]}'),
        input('broken', '{'),
        input('valid', '{"sourceIds":[2],"constant":9223372036854775807}'),
    ]
    const before = JSON.stringify(document)
    const result = previewSources(document)
    expect(result.invalidNodes).toEqual(['unsafe', 'broken'])
    expect(result.sources.map((item) => item.id)).toEqual([2])
    expect(JSON.stringify(document)).toBe(before)
})
it('caps graph-wide options and still counts existing references', () => {
    const document = emptyGraph()
    document.definition.nodes = Array.from({ length: 5 }, (_, node) =>
        input(
            String(node),
            JSON.stringify({
                sourceIds: Array.from(
                    { length: 500 },
                    (_, i) => node * 500 + i + 1,
                ),
            }),
        ),
    )
    document.definition.nodes.push(input('shared', '{"sourceIds":[1]}'))
    const result = previewSources(document)
    expect(result.sources).toHaveLength(2000)
    expect(result.overLimit).toBe(true)
    expect(result.sources[0].nodeCount).toBe(2)
})
it('keeps manual samples isolated between editors and outside persisted graph state', () => {
    const first = createPreviewDraft()
    const second = createPreviewDraft()
    first.setState({
        source: 'input.simulator:1',
        sample: '{"value":9223372036854775807}',
    })
    expect(second.getState()).toEqual({ source: '', sample: '' })
    first.setState({ sample: '' })
    expect(first.getState()).toEqual({
        source: 'input.simulator:1',
        sample: '',
    })
})
