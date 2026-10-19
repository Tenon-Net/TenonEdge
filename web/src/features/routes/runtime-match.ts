import type { components } from '../../api/schema'
import type { RouteForm } from './api'

type Schema = components['schemas']
type RouteList = Pick<Schema['RouteDraftList'], 'version'> & {
    routes: Pick<Schema['RouteDraftSummary'], 'id' | 'effective' | 'enabled'>[]
}
type Summary = Pick<
    Schema['ConfigurationDraftSummary'],
    | 'version'
    | 'effectiveRevision'
    | 'pendingRevision'
    | 'comparisonAvailable'
    | 'changeCount'
>
type Runtime = {
    coordinator: Pick<
        Schema['RuntimeCoordinatorStatus'],
        'state' | 'activeRevision'
    >
    runtime: Pick<Schema['RuntimeStatusReport'], 'activeRevision'>
}

/** A saved-route match alone says nothing about local edits, changed source or
 * connection parameters, or the currently activated plan. Only comparable,
 * version-bound observations may label the editor as matching live execution. */
export function routeRuntimeMatch({
    form,
    dirty,
    list,
    summary,
    runtime,
    failed,
}: {
    form: Pick<RouteForm, 'id' | 'version'>
    dirty: boolean
    list?: RouteList
    summary?: Summary
    runtime?: Runtime
    failed: boolean
}):
    | 'localChanges'
    | 'notEffective'
    | 'matchUnknown'
    | 'inactive'
    | 'draftChanged'
    | 'effective' {
    if (dirty) return 'localChanges'
    if (form.id === undefined) return 'notEffective'
    if (
        failed ||
        !list ||
        !summary ||
        !runtime ||
        list.version !== form.version ||
        summary.version !== form.version
    )
        return 'matchUnknown'
    const { coordinator } = runtime
    const revision = summary.effectiveRevision
    if (
        String(revision) !== String(coordinator.activeRevision) ||
        String(revision) !== String(runtime.runtime.activeRevision) ||
        summary.pendingRevision !== null
    )
        return 'matchUnknown'
    if (revision === null) return 'notEffective'
    const route = list.routes.find(
        (item) => String(item.id) === String(form.id),
    )
    if (!route) return 'matchUnknown'
    if (!route.effective) return 'notEffective'
    if (coordinator.state !== 'active' || !route.enabled) return 'inactive'
    if (!summary.comparisonAvailable || summary.changeCount === null)
        return 'matchUnknown'
    // Conservative for dependency changes: the graph can be unchanged while a
    // referenced device/connection differs. Do not invent a route-level match.
    if (String(summary.changeCount) !== '0') return 'draftChanged'
    return 'effective'
}
