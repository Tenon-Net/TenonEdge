import { describe, expect, it } from 'vitest'
import { emptyGraph } from '../canvas/model'
import { bindNodeRuntime, type NodeRuntimePage } from './node-runtime'

function fixture() {
    const document = emptyGraph()
    document.definition.nodes = [
        { id: 'in', kind: 'input.simulator', name: 'Input', configText: '{}' },
    ]
    const page: NodeRuntimePage = {
        draftVersion: 'draft-1',
        revision: '9007199254740993',
        applied: true,
        capturedAt: '2026-10-19T00:00:00Z',
        nodes: [
            {
                id: 'in',
                kind: 'input.simulator',
                version: 1,
                state: 'running',
                inputs: 1,
                runningInputs: 1,
                faultedInputs: 0,
                paths: 1,
                observedPaths: 0,
                failedPaths: 0,
                unknownPaths: 0,
                lastObservedAt: null,
                lastOutcome: null,
            },
        ],
    }
    return { document, page }
}
describe('canvas effective node metadata binding', () => {
    it('keeps exact revisions and never synthesizes a message or acknowledgement', () => {
        const { document, page } = fixture()
        const result = bindNodeRuntime(
            document,
            'draft-1',
            '9007199254740993',
            true,
            page,
        )
        expect(result.get('in')).toBe(page.nodes[0])
        expect(result.get('in')?.lastOutcome).toBeNull()
        expect(
            bindNodeRuntime(document, 'draft-1', '9007199254740992', true, page)
                .size,
        ).toBe(0)
    })
    it.each([
        'unavailable',
        'not-applied',
        'stale-draft',
        'missing-revision',
        'missing-page',
    ])('withdraws stale state when %s', (kind) => {
        const { document, page } = fixture()
        if (kind === 'not-applied') page.applied = false
        if (kind === 'stale-draft') page.draftVersion = 'draft-0'
        expect(
            bindNodeRuntime(
                document,
                'draft-1',
                kind === 'missing-revision' ? null : page.revision,
                kind !== 'unavailable',
                kind === 'missing-page' ? undefined : page,
            ).size,
        ).toBe(0)
    })
    it.each(['kind', 'version', 'id', 'duplicate'] as const)(
        'rejects %s identity mismatch',
        (key) => {
            const { document, page } = fixture()
            if (key === 'kind') page.nodes[0].kind = 'input.http'
            if (key === 'version') page.nodes[0].version = 2
            if (key === 'id') page.nodes[0].id = 'other'
            if (key === 'duplicate') page.nodes.push({ ...page.nodes[0] })
            expect(
                bindNodeRuntime(document, 'draft-1', page.revision, true, page)
                    .size,
            ).toBe(0)
        },
    )
})
