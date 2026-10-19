import { expect, it } from 'vitest'
import type { components } from '../../api/schema'
import { emptyGraph } from '../canvas/model'
import { localNodeErrors, previewErrorNodes } from './node-errors'

const document = emptyGraph()
document.definition.nodes = [
    {
        id: 'in',
        kind: 'input.simulator',
        name: 'Input',
        configText: '{"sourceIds":[1]}',
    },
    {
        id: 'map',
        kind: 'transform.map',
        name: 'Map',
        configText: '{"rules":[]}',
    },
    {
        id: 'out',
        kind: 'output.http',
        name: 'Output',
        configText: '{"connectionId":1}',
    },
]
document.definition.edges = [
    { id: 'a', source: 'in', target: 'map' },
    { id: 'b', source: 'map', target: 'out' },
]
it('marks missing references and topology without parsing constants through JavaScript numbers', () => {
    expect([...localNodeErrors(document)]).toEqual([])
    const copy = structuredClone(document)
    copy.definition.nodes[0].configText = '{"sourceIds":[]}'
    copy.definition.nodes[2].configText = '{"connectionId":9007199254740993}'
    expect([...localNodeErrors(copy)].sort()).toEqual(['in', 'out'])
    copy.definition.edges = []
    expect(localNodeErrors(copy).size).toBe(3)
})
it('only returns bounded known-node ids from failed preview stages and issues, never message data', () => {
    const response: components['schemas']['RoutePreviewResponse'] = {
        version: '1',
        previewed: true,
        invalidUpstreamTime: false,
        issues: [],
        result: {
            version: '1',
            sourceId: 1,
            sourceMessageId: 'id',
            collectedAt: '2026-10-19T00:00:00Z',
            timeSource: 'gateway',
            totalPaths: 1,
            succeededPaths: 0,
            truncated: false,
            source: null,
            paths: [
                {
                    nodes: ['in', 'map', 'out'],
                    succeeded: false,
                    output: null,
                    issues: [
                        {
                            code: 'output.invalid',
                            routeKey: 'route',
                            nodeId: 'out',
                            fieldKey: null,
                        },
                        {
                            code: 'unknown',
                            routeKey: 'route',
                            nodeId: 'foreign',
                            fieldKey: 'private-data',
                        },
                    ],
                    stages: [
                        {
                            nodeId: 'map',
                            succeeded: false,
                            fields: [],
                            truncated: false,
                            issues: [
                                {
                                    field: 'private-data',
                                    required: true,
                                    reason: 'conversionFailed',
                                },
                            ],
                        },
                    ],
                },
            ],
        },
    }
    expect(previewErrorNodes(document, response)).toEqual(['map', 'out'])
    response.result!.paths[0].stages[0].succeeded = true
    response.result!.paths[0].issues = []
    expect(previewErrorNodes(document, response)).toEqual([])
})
