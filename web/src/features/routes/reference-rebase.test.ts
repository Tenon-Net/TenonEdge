import { describe, expect, it } from 'vitest'
import { newRoute, type RouteForm } from './api'
import { rebaseReferences } from './reference-rebase'

function saved(): RouteForm {
    return {
        ...newRoute('v1'),
        id: 1,
        key: 'route',
        name: 'Original',
        document: {
            schemaVersion: 1,
            definition: {
                nodes: [
                    {
                        id: 'source',
                        kind: 'input.simulator',
                        name: 'source',
                        configText:
                            '{"sourceIds":[12],"precise":9007199254740993}',
                    },
                ],
                edges: [],
            },
            layout: { positions: { source: { x: 20, y: 30 } } },
        },
    }
}
describe('inline reference version review', () => {
    it('advances only the version and preserves unsaved properties and exact graph text', () => {
        const baseline = saved()
        const local = { ...baseline, name: 'Unsaved', description: 'local' }
        const result = rebaseReferences(local, baseline, {
            ...baseline,
            version: 'v2',
        })!
        expect(result.form).toEqual({ ...local, version: 'v2' })
        expect(result.form.document).toBe(local.document)
        expect(result.baseline).toEqual({ ...baseline, version: 'v2' })
        expect(result.form.document.definition.nodes[0].configText).toContain(
            '9007199254740993',
        )
        expect(baseline.version).toBe('v1')
    })
    it.each([
        'name',
        'description',
        'key',
        'enabled',
        'id',
        'document',
    ] as const)(
        'refuses a concurrent saved %s change without overwriting local edits',
        (field) => {
            const baseline = saved()
            const latest = {
                ...baseline,
                version: 'v2',
                [field]:
                    field === 'enabled'
                        ? true
                        : field === 'id'
                          ? 2
                          : field === 'document'
                            ? newRoute('v2').document
                            : 'changed',
            }
            expect(rebaseReferences(baseline, baseline, latest)).toBeNull()
        },
    )
    it('does not treat a directory version as proof an existing route is unchanged', () => {
        expect(rebaseReferences(saved(), saved(), 'v2')).toBeNull()
    })
    it('allows a still unsaved new route to adopt a fresh directory version without changing its graph', () => {
        const baseline = newRoute('v1')
        const local = { ...baseline, name: 'new', document: saved().document }
        const result = rebaseReferences(local, baseline, 'v2')!
        expect(result.form.document).toBe(local.document)
        expect(result.form.name).toBe('new')
        expect(result.baseline).toEqual(newRoute('v2'))
        expect(rebaseReferences(local, baseline, saved())).toBeNull()
        expect(rebaseReferences(saved(), baseline, 'v2')).toBeNull()
    })
})
