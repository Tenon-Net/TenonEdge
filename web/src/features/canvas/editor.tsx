import { ReferencePicker } from '../routes/reference-picker'
import { useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { useStore } from 'zustand'
import { useTranslation } from 'react-i18next'
import {
    Background,
    Controls,
    Handle,
    MiniMap,
    Position as HandlePosition,
    ReactFlow,
    ReactFlowProvider,
    useReactFlow,
    type Node,
    type NodeProps,
    type NodeChange,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { Button } from '../../components/ui/button'
import {
    GraphEditError,
    nodeKinds,
    topologyHints,
    type NodeKind,
    type Position,
} from './model'
import { createGraphEditor } from './store'

type CanvasNode = Node<{ name: string; kind: NodeKind }, 'gateway'>
function GatewayNode({ data, selected }: NodeProps<CanvasNode>) {
    const { t } = useTranslation()
    return (
        <div
            className={`w-48 rounded-md border bg-background p-3 shadow-sm ${selected ? 'ring-2 ring-primary' : ''}`}
        >
            {!data.kind.startsWith('input.') && (
                <Handle type="target" position={HandlePosition.Left} />
            )}
            <div className="truncate font-medium">{data.name}</div>
            <div className="text-xs text-muted-foreground">
                {t(`canvas.kinds.${data.kind}`)}
            </div>
            <div className="text-xs text-muted-foreground">
                {t('canvas.unbound')}
            </div>
            {!data.kind.startsWith('output.') && (
                <Handle type="source" position={HandlePosition.Right} />
            )}
        </div>
    )
}
const nodeTypes = { gateway: GatewayNode }
type CanvasProps = {
    editor?: ReturnType<typeof createGraphEditor>
    readOnly?: boolean
    persisted?: boolean
    onPendingChange?: (pending: boolean) => void
}
function CanvasWorkspace({
    editor,
    readOnly = false,
    persisted = false,
    onPendingChange,
}: CanvasProps) {
    const { t } = useTranslation()
    const [store] = useState(() => editor ?? createGraphEditor())
    const state = useStore(store)
    const { document: doc } = state
    const flow = useReactFlow<CanvasNode>()
    const [transient, setTransient] = useState<Record<string, Position>>({})
    const [dimensions, setDimensions] = useState<
        Record<string, { width: number; height: number }>
    >({})
    const dragging = useRef(false)
    const [error, setError] = useState('')
    const [pendingConfig, setPendingConfig] = useState(false)
    const pending = (value: boolean) => {
        setPendingConfig(value)
        onPendingChange?.(value)
    }
    const selected = doc.definition.nodes.filter((n) =>
        state.selectedNodes.includes(n.id),
    )
    const nodes = useMemo<CanvasNode[]>(
        () =>
            doc.definition.nodes.map((n) => ({
                id: n.id,
                type: 'gateway',
                data: { name: n.name, kind: n.kind },
                measured: dimensions[n.id],
                position: {
                    ...(transient[n.id] ?? doc.layout.positions[n.id]),
                },
                selected: state.selectedNodes.includes(n.id),
            })),
        [doc, transient, dimensions, state.selectedNodes],
    )
    const edges = useMemo(
        () =>
            doc.definition.edges.map((e) => ({
                ...e,
                selected: state.selectedEdges.includes(e.id),
            })),
        [doc, state.selectedEdges],
    )
    const attempt = (action: () => void, accepting = false) => {
        if (readOnly || (pendingConfig && !accepting)) return false
        try {
            action()
            setError('')
            return true
        } catch (failure) {
            setError(
                failure instanceof GraphEditError
                    ? t(`canvas.errors.${failure.code}`)
                    : t('routes.configError'),
            )
            return false
        }
    }
    const add = (kind: NodeKind, position: Position) =>
        attempt(() => {
            state.add(kind, t(`canvas.kinds.${kind}`), position)
        })
    const changes = (items: NodeChange<CanvasNode>[]) => {
        const sizes: Record<string, { width: number; height: number }> = {}
        for (const change of items)
            if (change.type === 'dimensions' && change.dimensions)
                sizes[change.id] = { ...change.dimensions }
        if (Object.keys(sizes).length)
            setDimensions((previous) =>
                Object.fromEntries(
                    Object.entries({ ...previous, ...sizes }).filter(([id]) =>
                        store
                            .getState()
                            .document.definition.nodes.some(
                                (node) => node.id === id,
                            ),
                    ),
                ),
            )
        const current = store.getState()
        const selected = new Set(current.selectedNodes)
        for (const change of items)
            if (change.type === 'select') {
                if (change.selected) selected.add(change.id)
                else selected.delete(change.id)
            }
        if (!pendingConfig && items.some((change) => change.type === 'select'))
            current.select([...selected], current.selectedEdges)
        const moved: Record<string, Position> = {}
        for (const change of items)
            if (change.type === 'position' && change.position)
                moved[change.id] = { ...change.position }
        if (readOnly || pendingConfig || !Object.keys(moved).length) return
        if (dragging.current)
            setTransient((previous) => ({ ...previous, ...moved }))
        else state.move(moved)
    }
    const stopMove = (_event: unknown, moved: CanvasNode[]) => {
        if (readOnly || pendingConfig) return
        state.move(
            Object.fromEntries(moved.map((n) => [n.id, { ...n.position }])),
        )
        dragging.current = false
        setTransient({})
    }
    const keyboard = (event: KeyboardEvent) => {
        if (readOnly || pendingConfig) return
        if (
            (event.target as HTMLElement).closest(
                'input,textarea,select,[contenteditable="true"]',
            )
        )
            return
        const key = event.key.toLowerCase()
        const command = event.ctrlKey || event.metaKey
        let action: (() => void) | undefined
        if (command && key === 'z')
            action = event.shiftKey ? state.redo : state.undo
        if (command && key === 'y') action = state.redo
        if (command && key === 'c') action = state.copy
        if (command && key === 'v') action = state.paste
        if (key === 'delete' || key === 'backspace') action = state.remove
        if (action && !dragging.current) {
            event.preventDefault()
            attempt(action)
        }
    }
    const dangling = topologyHints(doc)
    return (
        <section className="space-y-3" onKeyDown={keyboard}>
            <h1 className="text-2xl font-semibold">
                {t(persisted ? 'routes.canvas' : 'canvas.title')}
            </h1>
            <p className="text-sm text-muted-foreground">
                {t(persisted ? 'routes.canvasNotice' : 'canvas.notice')}
            </p>
            <p className="text-sm">{t('canvas.help')}</p>
            <div className="flex flex-wrap gap-2">
                <Button
                    variant="outline"
                    disabled={readOnly || pendingConfig || !state.past.length}
                    onClick={state.undo}
                >
                    {t('canvas.undo')}
                </Button>
                <Button
                    variant="outline"
                    disabled={readOnly || pendingConfig || !state.future.length}
                    onClick={state.redo}
                >
                    {t('canvas.redo')}
                </Button>
                <Button
                    variant="outline"
                    disabled={!selected.length}
                    onClick={state.copy}
                >
                    {t('canvas.copy')}
                </Button>
                <Button
                    variant="outline"
                    disabled={readOnly || pendingConfig || !state.clipboard}
                    onClick={() => attempt(state.paste)}
                >
                    {t('canvas.paste')}
                </Button>
                <Button
                    variant="outline"
                    disabled={
                        readOnly ||
                        pendingConfig ||
                        (!selected.length && !state.selectedEdges.length)
                    }
                    onClick={state.remove}
                >
                    {t('canvas.remove')}
                </Button>
                <span role="status">
                    {t('canvas.summary', {
                        nodes: nodes.length,
                        edges: edges.length,
                        steps: state.past.length,
                    })}
                </span>
            </div>
            <div className="grid gap-3 lg:grid-cols-[200px_minmax(0,1fr)_220px]">
                <aside
                    className="space-y-3 rounded-md border p-3"
                    aria-label={t('canvas.palette')}
                >
                    {(['input', 'transform', 'output'] as const).map(
                        (group) => (
                            <div key={group} className="space-y-2">
                                <h2 className="font-medium">
                                    {t(`canvas.groups.${group}`)}
                                </h2>
                                {nodeKinds
                                    .filter((kind) =>
                                        kind.startsWith(`${group}.`),
                                    )
                                    .map((kind) => (
                                        <button
                                            key={kind}
                                            type="button"
                                            draggable={!readOnly}
                                            disabled={readOnly}
                                            className="block w-full rounded border p-2 text-left text-sm"
                                            onDragStart={(event) => {
                                                event.dataTransfer.setData(
                                                    'application/x-tenon-node',
                                                    kind,
                                                )
                                                event.dataTransfer.effectAllowed =
                                                    'copy'
                                            }}
                                            onClick={() =>
                                                add(
                                                    kind,
                                                    flow.screenToFlowPosition({
                                                        x:
                                                            window.innerWidth /
                                                                2 +
                                                            (nodes.length % 5) *
                                                                24,
                                                        y:
                                                            360 +
                                                            (nodes.length % 5) *
                                                                24,
                                                    }),
                                                )
                                            }
                                        >
                                            {t(`canvas.kinds.${kind}`)}
                                        </button>
                                    ))}
                            </div>
                        ),
                    )}
                </aside>
                <div
                    className="h-[600px] min-w-0 rounded-md border"
                    aria-label={t('canvas.graph')}
                    tabIndex={0}
                    onDragOver={(event) => {
                        event.preventDefault()
                        event.dataTransfer.dropEffect = 'copy'
                    }}
                    onDrop={(event) => {
                        event.preventDefault()
                        const kind = event.dataTransfer.getData(
                            'application/x-tenon-node',
                        )
                        if (nodeKinds.includes(kind as NodeKind))
                            add(
                                kind as NodeKind,
                                flow.screenToFlowPosition({
                                    x: event.clientX,
                                    y: event.clientY,
                                }),
                            )
                    }}
                >
                    <ReactFlow<CanvasNode>
                        nodesDraggable={!readOnly && !pendingConfig}
                        nodesConnectable={!readOnly && !pendingConfig}
                        nodes={nodes}
                        edges={edges}
                        nodeTypes={nodeTypes}
                        onNodesChange={changes}
                        onEdgesChange={(items) => {
                            const current = store.getState()
                            const ids = new Set(current.selectedEdges)
                            for (const change of items)
                                if (change.type === 'select') {
                                    if (change.selected) ids.add(change.id)
                                    else ids.delete(change.id)
                                }
                            if (
                                items.some((change) => change.type === 'select')
                            )
                                current.select(current.selectedNodes, [...ids])
                        }}
                        onNodeDragStart={() => {
                            dragging.current = true
                        }}
                        onNodeDragStop={(event, _node, moved) =>
                            stopMove(event, moved)
                        }
                        onSelectionDragStart={() => {
                            dragging.current = true
                        }}
                        onSelectionDragStop={stopMove}
                        onConnect={(connection) =>
                            attempt(() =>
                                state.connect(
                                    connection.source,
                                    connection.target,
                                ),
                            )
                        }
                        onConnectEnd={(_event, result) => {
                            if (!result.isValid)
                                setError(t('canvas.errors.invalid'))
                        }}
                        deleteKeyCode={null}
                        selectionKeyCode="Shift"
                        multiSelectionKeyCode="Shift"
                        minZoom={0.2}
                        maxZoom={2}
                        ariaLabelConfig={{
                            'node.a11yDescription.default':
                                t('canvas.a11y.node'),
                            'edge.a11yDescription.default':
                                t('canvas.a11y.edge'),
                            'node.a11yDescription.ariaLiveMessage': ({
                                direction,
                                x,
                                y,
                            }) =>
                                t('canvas.a11y.moved', {
                                    direction: t(
                                        `canvas.a11y.directions.${direction}`,
                                    ),
                                    x,
                                    y,
                                }),
                            'controls.ariaLabel': t('canvas.a11y.controls'),
                            'controls.zoomIn.ariaLabel':
                                t('canvas.a11y.zoomIn'),
                            'controls.zoomOut.ariaLabel': t(
                                'canvas.a11y.zoomOut',
                            ),
                            'controls.fitView.ariaLabel': t('canvas.a11y.fit'),
                            'controls.interactive.ariaLabel': t(
                                'canvas.a11y.interactive',
                            ),
                            'minimap.ariaLabel': t('canvas.a11y.minimap'),
                            'handle.ariaLabel': t('canvas.a11y.handle'),
                        }}
                    >
                        <Background />
                        <Controls showInteractive={false} />
                        <MiniMap pannable zoomable />
                    </ReactFlow>
                </div>
                <aside
                    className="space-y-3 rounded-md border p-3"
                    aria-label={t('canvas.properties')}
                >
                    <h2 className="font-medium">{t('canvas.properties')}</h2>
                    {selected.length === 1 ? (
                        <form
                            key={
                                persisted
                                    ? selected[0].id
                                    : `${selected[0].id}:${selected[0].name}`
                            }
                            className="space-y-3"
                            onSubmit={(event) => {
                                event.preventDefault()
                                if (readOnly || pendingConfig) return
                                state.rename(
                                    selected[0].id,
                                    String(
                                        new FormData(event.currentTarget).get(
                                            'name',
                                        ),
                                    ).trim() || selected[0].name,
                                )
                            }}
                        >
                            <label className="block">
                                {t('canvas.name')}
                                <input
                                    className="mt-2 w-full rounded border p-2"
                                    name="name"
                                    defaultValue={
                                        persisted ? undefined : selected[0].name
                                    }
                                    value={
                                        persisted ? selected[0].name : undefined
                                    }
                                    onChange={
                                        persisted
                                            ? (event) =>
                                                  state.rename(
                                                      selected[0].id,
                                                      event.target.value,
                                                  )
                                            : undefined
                                    }
                                    maxLength={64}
                                    disabled={readOnly}
                                />
                            </label>
                            <Button type="submit" disabled={readOnly}>
                                {t('canvas.applyName')}
                            </Button>
                            <p className="text-sm">{t('canvas.unbound')}</p>
                        </form>
                    ) : (
                        <p>{t('canvas.selectOne')}</p>
                    )}
                    {persisted &&
                        selected.length === 1 &&
                        (selected[0].kind.startsWith('input.') ||
                            ['output.http', 'output.mqtt'].includes(
                                selected[0].kind,
                            )) && (
                            <ReferencePicker
                                key={selected[0].id}
                                node={selected[0]}
                                disabled={readOnly || pendingConfig}
                                change={(text) =>
                                    attempt(() =>
                                        state.configure(selected[0].id, text),
                                    )
                                }
                            />
                        )}
                    {persisted && selected.length === 1 && (
                        <form
                            key={`${selected[0].id}:${selected[0].configText}`}
                            onSubmit={(event) => {
                                event.preventDefault()
                                const text = String(
                                    new FormData(event.currentTarget).get(
                                        'config',
                                    ),
                                )
                                if (
                                    attempt(
                                        () =>
                                            state.configure(
                                                selected[0].id,
                                                text,
                                            ),
                                        true,
                                    )
                                )
                                    pending(false)
                            }}
                            className="space-y-2"
                        >
                            <label className="block">
                                {t('routes.config')}
                                <textarea
                                    name="config"
                                    aria-label={t('routes.config')}
                                    className="w-full rounded border p-2 font-mono text-xs"
                                    rows={12}
                                    defaultValue={selected[0].configText}
                                    disabled={readOnly}
                                    onChange={() => pending(true)}
                                />
                            </label>
                            <p className="text-xs">
                                {t('routes.configNotice')}
                            </p>
                            {pendingConfig && (
                                <p role="status">{t('routes.configPending')}</p>
                            )}
                            <Button type="submit" disabled={readOnly}>
                                {t('routes.configAccept')}
                            </Button>
                            <Button
                                type="button"
                                variant="outline"
                                disabled={readOnly || !pendingConfig}
                                onClick={(event) => {
                                    event.currentTarget.form?.reset()
                                    pending(false)
                                }}
                            >
                                {t('routes.configDiscard')}
                            </Button>
                        </form>
                    )}
                </aside>
            </div>
            <section
                className="space-y-2 rounded-md border p-3"
                aria-label={t('canvas.hints')}
            >
                <h2 className="font-medium">{t('canvas.hints')}</h2>
                {error && (
                    <p role="alert" className="text-destructive">
                        {error}
                    </p>
                )}
                {!nodes.length && <p>{t('canvas.empty')}</p>}
                {!!dangling.length && (
                    <p>
                        {t('canvas.dangling', {
                            names: doc.definition.nodes
                                .filter((n) => dangling.includes(n.id))
                                .map((n) => n.name)
                                .join('、'),
                        })}
                    </p>
                )}
                <p className="text-sm text-muted-foreground">
                    {t('canvas.topologyOnly')}
                </p>
                <details>
                    <summary>{t('canvas.json')}</summary>
                    <pre
                        data-testid="graph-document"
                        className="max-h-96 overflow-auto text-xs"
                    >
                        {JSON.stringify(doc, null, 2)}
                    </pre>
                </details>
            </section>
        </section>
    )
}
export function CanvasEditor(props: CanvasProps) {
    return (
        <ReactFlowProvider>
            <CanvasWorkspace {...props} />
        </ReactFlowProvider>
    )
}
