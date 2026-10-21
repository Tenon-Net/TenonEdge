import {
    Table,
    TableHeader,
    TableBody,
    TableRow,
    TableHead,
    TableCell,
} from '../../components/ui/table'
import { Link } from '@tanstack/react-router'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import { draftTarget, type ConnectionRow } from './list-model'
import type { Connection } from './model'
import type { components } from '../../api/schema'

type User = components['schemas']['ConnectionListUser']
function Users({ users, draft = false }: { users: User[]; draft?: boolean }) {
    const { t } = useTranslation()
    return (
        <details>
            <summary className="cursor-pointer">
                {t(
                    draft
                        ? 'connections.runtime.draftUsers'
                        : 'connections.runtime.users',
                    { count: users.length },
                )}
            </summary>
            <ul className="space-y-1 py-2">
                {users.map((user) => (
                    <li key={`${user.kind}/${user.key}`}>
                        {t(`connections.runtime.userKinds.${user.kind}`)}:{' '}
                        {user.name}
                        <code className="block text-xs">{user.key}</code>
                        {!user.enabled
                            ? t('connections.runtime.userDisabled')
                            : !draft && !user.active
                              ? t('connections.runtime.userInactive')
                              : null}
                    </li>
                ))}
            </ul>
        </details>
    )
}
export function ConnectionTable({
    rows,
    current,
    canEdit,
    busy,
    onOpen,
    onDelete,
}: {
    rows: ConnectionRow[]
    current: boolean
    canEdit: boolean
    busy: boolean
    onOpen: (draft: Connection) => void
    onDelete: (draft: Connection) => void
}) {
    const { t } = useTranslation()
    const date = (value: string | null | undefined) =>
        value ? new Date(value).toLocaleString() : t('connections.runtime.none')
    return (
        <div className="overflow-x-auto">
            <Table
                className="w-full text-left text-sm"
                aria-label={t('connections.runtime.table')}
            >
                <TableHeader>
                    <TableRow>
                        {[
                            'definition',
                            'state',
                            'references',
                            'pending',
                            'lastError',
                            'actions',
                        ].map((field) => (
                            <TableHead key={field} className="border-b p-2">
                                {t(`connections.runtime.${field}`)}
                            </TableHead>
                        ))}
                    </TableRow>
                </TableHeader>
                <TableBody>
                    {rows.map(({ id, draft, runtime }) => {
                        const effective = runtime?.effective
                        const observed = current ? runtime : undefined
                        return (
                            <TableRow key={id}>
                                <TableCell className="min-w-64 space-y-2 border-b p-2">
                                    {effective && (
                                        <div>
                                            <p className="text-xs text-muted-foreground">
                                                {t(
                                                    current
                                                        ? 'connections.runtime.effective'
                                                        : 'connections.runtime.oldEffective',
                                                )}
                                            </p>
                                            <p className="font-medium">
                                                {effective.name} ·{' '}
                                                {t(
                                                    `connections.kinds.${effective.kind}`,
                                                    {
                                                        defaultValue:
                                                            effective.kind,
                                                    },
                                                )}
                                            </p>
                                            <code className="block text-xs">
                                                {effective.key ?? `#${id}`}
                                            </code>
                                            <p className="break-all">
                                                {effective.target}
                                            </p>
                                        </div>
                                    )}
                                    {draft && (
                                        <div>
                                            <p className="text-xs text-muted-foreground">
                                                {t('connections.runtime.draft')}
                                            </p>
                                            <p className="font-medium">
                                                {draft.name} ·{' '}
                                                {t(
                                                    `connections.kinds.${draft.kind}`,
                                                )}
                                                {draft.module &&
                                                    ` · ${draft.module.kind} v${draft.module.version}`}
                                            </p>
                                            <code className="block text-xs">
                                                {draft.key}
                                            </code>
                                            <p className="break-all">
                                                {draftTarget(draft)}
                                            </p>
                                            <p>
                                                {t(
                                                    draft.enabled
                                                        ? 'connections.enabled'
                                                        : 'connections.disabled',
                                                )}
                                            </p>
                                        </div>
                                    )}
                                    {observed?.draftChanged && (
                                        <p className="text-amber-700 dark:text-amber-400">
                                            {t('connections.runtime.changed')}
                                        </p>
                                    )}
                                    {observed &&
                                        effective &&
                                        observed.draftChanged == null && (
                                            <p>
                                                {t(
                                                    'connections.runtime.legacy',
                                                )}
                                            </p>
                                        )}
                                    {!draft && (
                                        <p>
                                            {t(
                                                'connections.runtime.effectiveOnly',
                                            )}
                                        </p>
                                    )}
                                </TableCell>
                                <TableCell className="whitespace-nowrap border-b p-2">
                                    <p>
                                        {t(
                                            `connections.runtime.states.${observed?.state ?? 'unavailable'}`,
                                        )}
                                    </p>
                                    {observed?.lastSuccessAt && (
                                        <p className="text-xs text-muted-foreground">
                                            {t(
                                                'connections.runtime.lastSuccess',
                                            )}
                                            : {date(observed.lastSuccessAt)}
                                        </p>
                                    )}
                                </TableCell>
                                <TableCell className="min-w-48 border-b p-2">
                                    {observed ? (
                                        <>
                                            <Users users={observed.users} />
                                            <Users
                                                users={observed.draftUsers}
                                                draft
                                            />
                                        </>
                                    ) : (
                                        t(
                                            'connections.runtime.states.unavailable',
                                        )
                                    )}
                                </TableCell>
                                <TableCell className="border-b p-2">
                                    {(effective?.kind ?? draft?.kind) ===
                                    'modbus' ? (
                                        t('connections.runtime.notOutput')
                                    ) : (
                                        <>
                                            <p>
                                                {observed?.pendingCount == null
                                                    ? t(
                                                          'connections.runtime.unknownCount',
                                                      )
                                                    : observed.pendingCount.toLocaleString()}
                                            </p>
                                            <Link
                                                className="underline"
                                                to="/runtime/outputs"
                                            >
                                                {t(
                                                    'connections.runtime.outputDetails',
                                                )}
                                            </Link>
                                        </>
                                    )}
                                </TableCell>
                                <TableCell className="min-w-48 border-b p-2">
                                    {!observed ? (
                                        t(
                                            'connections.runtime.states.unavailable',
                                        )
                                    ) : observed.lastErrorCode ? (
                                        <>
                                            <p>
                                                {t(
                                                    `connections.runtime.errors.${observed.lastErrorCode}`,
                                                    {
                                                        defaultValue: t(
                                                            'connections.runtime.otherError',
                                                        ),
                                                    },
                                                )}
                                            </p>
                                            <code className="block text-xs">
                                                {observed.lastErrorCode}
                                            </code>
                                            <time
                                                dateTime={
                                                    observed.lastErrorAt ??
                                                    undefined
                                                }
                                            >
                                                {date(observed.lastErrorAt)}
                                            </time>
                                        </>
                                    ) : (
                                        t('connections.runtime.none')
                                    )}
                                </TableCell>
                                <TableCell className="space-y-2 border-b p-2">
                                    <Button
                                        variant="outline"
                                        disabled={busy || !draft}
                                        onClick={() => draft && onOpen(draft)}
                                    >
                                        {t(
                                            canEdit
                                                ? 'connections.edit'
                                                : 'connections.view',
                                        )}
                                    </Button>
                                    {canEdit && (
                                        <Button
                                            variant="outline"
                                            disabled={busy || !draft}
                                            onClick={() =>
                                                draft && onDelete(draft)
                                            }
                                        >
                                            {t('connections.remove')}
                                        </Button>
                                    )}
                                </TableCell>
                            </TableRow>
                        )
                    })}
                </TableBody>
            </Table>
        </div>
    )
}
