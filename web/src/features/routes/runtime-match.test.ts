import { describe, expect, it } from 'vitest'
import { routeRuntimeMatch } from './runtime-match'

type Evidence = Parameters<typeof routeRuntimeMatch>[0]
function matched(): Evidence {
    return {
        form: { id: 7, version: 'draft-3' },
        dirty: false,
        failed: false,
        list: {
            version: 'draft-3',
            routes: [{ id: 7, enabled: true, effective: true }],
        },
        summary: {
            version: 'draft-3',
            effectiveRevision: '9007199254740993',
            pendingRevision: null,
            comparisonAvailable: true,
            changeCount: 0,
        },
        runtime: {
            coordinator: {
                state: 'active',
                activeRevision: '9007199254740993',
            },
            runtime: { activeRevision: '9007199254740993' },
        },
    }
}
describe('route editor effective configuration evidence', () => {
    it('requires all three version-bound observations, keeping exact revisions', () => {
        expect(routeRuntimeMatch(matched())).toBe('effective')
        const value = matched()
        value.runtime!.runtime.activeRevision = '9007199254740992'
        expect(routeRuntimeMatch(value)).toBe('matchUnknown')
    })
    it('never labels a local unsaved edit or pending node form as applied', () => {
        expect(routeRuntimeMatch({ ...matched(), dirty: true })).toBe(
            'localChanges',
        )
    })
    it('withdraws cached healthy evidence on any query failure', () => {
        expect(routeRuntimeMatch({ ...matched(), failed: true })).toBe(
            'matchUnknown',
        )
    })
    it.each(['list', 'summary', 'runtime'] as const)(
        'requires %s evidence',
        (key) => {
            const value = matched()
            delete value[key]
            expect(routeRuntimeMatch(value)).toBe('matchUnknown')
        },
    )
    it.each(['list', 'summary'] as const)(
        'rejects a stale %s version',
        (key) => {
            const value = matched()
            value[key]!.version = 'different'
            expect(routeRuntimeMatch(value)).toBe('matchUnknown')
        },
    )
    it('does not combine saved-route metadata from before an activation with a new runtime', () => {
        const value = matched()
        value.runtime!.coordinator.activeRevision = 9
        value.runtime!.runtime.activeRevision = 9
        expect(routeRuntimeMatch(value)).toBe('matchUnknown')
    })
    it('does not equate an unchanged graph with unchanged referenced configuration', () => {
        const value = matched()
        value.summary!.changeCount = '1'
        expect(routeRuntimeMatch(value)).toBe('draftChanged')
    })
    it('refuses unavailable legacy comparisons and in-flight commits', () => {
        const value = matched()
        value.summary!.comparisonAvailable = false
        expect(routeRuntimeMatch(value)).toBe('matchUnknown')
        value.summary!.comparisonAvailable = true
        value.summary!.pendingRevision = 2
        expect(routeRuntimeMatch(value)).toBe('matchUnknown')
        value.summary!.pendingRevision = null
        value.summary!.changeCount = null
        expect(routeRuntimeMatch(value)).toBe('matchUnknown')
    })
    it.each([
        'starting',
        'idle',
        'blocked',
        'runFailed',
        'stopping',
        'stopped',
    ] as const)(
        'does not advertise execution while coordinator is %s',
        (state) => {
            const value = matched()
            value.runtime!.coordinator.state = state
            expect(routeRuntimeMatch(value)).toBe('inactive')
        },
    )
    it('separates a new route, disabled route, changed saved route and missing row', () => {
        const value = matched()
        expect(
            routeRuntimeMatch({ ...value, form: { version: 'draft-3' } }),
        ).toBe('notEffective')
        value.list!.routes[0].enabled = false
        expect(routeRuntimeMatch(value)).toBe('inactive')
        value.list!.routes[0].enabled = true
        value.list!.routes[0].effective = false
        expect(routeRuntimeMatch(value)).toBe('notEffective')
        value.list!.routes = []
        expect(routeRuntimeMatch(value)).toBe('matchUnknown')
    })
    it('does not label a newly disabled draft as if the effective route were stopped', () => {
        const value = matched()
        value.list!.routes[0].enabled = false
        value.list!.routes[0].effective = false
        expect(routeRuntimeMatch(value)).toBe('notEffective')
    })
    it('reports no applied route when all three pointers agree no plan exists', () => {
        const value = matched()
        value.summary!.effectiveRevision = null
        value.runtime!.coordinator.activeRevision = null
        value.runtime!.runtime.activeRevision = null
        expect(routeRuntimeMatch(value)).toBe('notEffective')
    })
})
