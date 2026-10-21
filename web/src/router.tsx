import {
    createRootRoute,
    createRoute,
    createRouter,
    Link,
} from '@tanstack/react-router'
import { lazy, type ComponentType } from 'react'
import { useTranslation } from 'react-i18next'
import { SessionGate } from './features/auth/session-gate'
import { LazyAuthForm } from './features/auth/lazy-auth-form'
import { PageLoadError, PageLoading } from './components/page-boundary'

// Router component.preload runs before SessionGate can authenticate. React.lazy
// waits for the authorized Outlet to render and never reloads the document on failure.
function lazyPage<Name extends string>(
    load: () => Promise<Record<Name, ComponentType>>,
    name: Name,
) {
    return lazy(() => load().then((module) => ({ default: module[name] })))
}
function AccountPage() {
    const { t } = useTranslation()
    return (
        <div className="max-w-md space-y-6">
            <h1 className="text-2xl font-semibold">{t('passwordChange')}</h1>
            <LazyAuthForm mode="passwordChange" />
        </div>
    )
}
function NotFound() {
    const { t } = useTranslation()
    return (
        <div>
            <h1>{t('noPage')}</h1>
            <Link to="/">{t('backHome')}</Link>
        </div>
    )
}
const rootRoute = createRootRoute({
    component: SessionGate,
    notFoundComponent: NotFound,
})
const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: lazyPage(
        () => import('./features/diagnostics/overview'),
        'OverviewPage',
    ),
})
const usersRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/users',
    component: lazyPage(() => import('./features/users/page'), 'UsersPage'),
})
const documentationRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/api-docs',
    component: lazyPage(
        () => import('./features/documentation/page'),
        'DocumentationPage',
    ),
})
const systemRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/system',
    component: lazyPage(() => import('./features/system/page'), 'SystemPage'),
})
const auditRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/audit',
    component: lazyPage(() => import('./features/audit/page'), 'AuditPage'),
})
const tokensRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/tokens',
    component: lazyPage(() => import('./features/tokens/page'), 'TokensPage'),
})
const accountRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/account',
    component: AccountPage,
})
const pointsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/prototypes/points',
    component: lazyPage(
        () => import('./features/points/editor'),
        'PointEditor',
    ),
})
const canvasRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/prototypes/canvas',
    component: lazyPage(
        () => import('./features/canvas/editor'),
        'CanvasEditor',
    ),
})
const connectionsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/connections',
    component: lazyPage(
        () => import('./features/connections/page'),
        'ConnectionsPage',
    ),
})
const devicesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/devices',
    component: lazyPage(() => import('./features/devices/page'), 'DevicesPage'),
})
const inputsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/devices/$deviceId/inputs',
    component: lazyPage(() => import('./features/inputs/page'), 'InputsPage'),
})
const routesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/routes',
    component: lazyPage(() => import('./features/routes/page'), 'RoutesPage'),
})
const configurationRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/configuration',
    component: lazyPage(
        () => import('./features/configuration/page'),
        'ConfigurationPage',
    ),
})
function migrationSearch(search: Record<string, unknown>): {
    operationId?: string
    intentId?: string
    attemptId?: string
} {
    const id = (value: unknown) => {
        const text = String(value ?? '').toLowerCase()
        return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
            text,
        ) && text !== '00000000-0000-0000-0000-000000000000'
            ? text
            : undefined
    }
    return {
        operationId: id(search.operationId),
        intentId: id(search.intentId),
        attemptId: id(search.attemptId),
    }
}
const configurationMigrationRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/configuration/migrations',
    validateSearch: migrationSearch,
    component: lazyPage(
        () => import('./features/configuration/migration'),
        'ConfigurationMigrationPage',
    ),
})
const configurationMigrationIntentsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/configuration/migration-intents',
    validateSearch: migrationSearch,
    component: lazyPage(
        () => import('./features/configuration/migration-intents'),
        'ConfigurationMigrationIntentsPage',
    ),
})
const diagnosticsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/diagnostics',
    component: lazyPage(
        () => import('./features/diagnostics/page'),
        'DiagnosticsPage',
    ),
})
const runtimeMessagesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/runtime/messages',
    component: lazyPage(
        () => import('./features/diagnostics/messages'),
        'RuntimeMessagesPage',
    ),
})
const runtimeDevicesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/runtime/devices',
    component: lazyPage(
        () => import('./features/diagnostics/current-values'),
        'RuntimeDevicesPage',
    ),
})
const runtimeOutputsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/runtime/outputs',
    component: lazyPage(
        () => import('./features/diagnostics/outputs'),
        'RuntimeOutputsPage',
    ),
})
const deliveryRecordsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/deliveries',
    validateSearch: (
        search: Record<string, unknown>,
    ): { connectionId?: string } => {
        const value = String(search.connectionId ?? '')
        return {
            connectionId:
                /^[1-9][0-9]{0,15}$/.test(value) &&
                BigInt(value) <= 9007199254740991n
                    ? value
                    : undefined,
        }
    },
    component: lazyPage(
        () => import('./features/deliveries/page'),
        'DeliveryRecordsPage',
    ),
})
const deliveryMigrationRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/deliveries/migrate',
    validateSearch: (
        search: Record<string, unknown>,
    ): { connectionId?: string; operationId?: string } => {
        const id = String(search.connectionId ?? '')
        const operation = String(search.operationId ?? '').toLowerCase()
        return {
            connectionId:
                /^[1-9][0-9]{0,15}$/.test(id) && BigInt(id) <= 9007199254740991n
                    ? id
                    : undefined,
            operationId:
                /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
                    operation,
                ) && operation !== '00000000-0000-0000-0000-000000000000'
                    ? operation
                    : undefined,
        }
    },
    component: lazyPage(
        () => import('./features/deliveries/migration'),
        'DeliveryMigrationPage',
    ),
})
export const router = createRouter({
    routeTree: rootRoute.addChildren([
        indexRoute,
        accountRoute,
        usersRoute,
        systemRoute,
        documentationRoute,
        auditRoute,
        tokensRoute,
        configurationRoute,
        configurationMigrationRoute,
        configurationMigrationIntentsRoute,
        diagnosticsRoute,
        runtimeDevicesRoute,
        runtimeMessagesRoute,
        runtimeOutputsRoute,
        deliveryRecordsRoute,
        deliveryMigrationRoute,
        pointsRoute,
        canvasRoute,
        connectionsRoute,
        devicesRoute,
        inputsRoute,
        routesRoute,
    ]),
    defaultPreload: false,
    // Route-owned boundaries follow the committed match, so a failed page does
    // not trap the Outlet or require a document reload to open another page.
    defaultErrorComponent: PageLoadError,
    defaultPendingComponent: PageLoading,
})
declare module '@tanstack/react-router' {
    interface Register {
        router: typeof router
    }
}
