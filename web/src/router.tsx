import { PageHeading } from './components/layout/page'
import { Button } from './components/ui/button'
import { FileQuestion, ShieldCheck } from 'lucide-react'
import {
    createRootRoute,
    createRoute,
    createRouter,
    Link,
    lazyRouteComponent,
} from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { SessionGate } from './features/auth/session-gate'
import { AuthForm } from './features/auth/auth-form'
function AccountPage() {
    const { t } = useTranslation()
    return (
        <div className="product-page">
            <PageHeading title={t('passwordChange')} />
            <section className="product-panel max-w-lg space-y-5">
                <ShieldCheck
                    aria-hidden="true"
                    className="size-6 text-muted-foreground"
                />
                <AuthForm mode="passwordChange" />
            </section>
        </div>
    )
}
function NotFound() {
    const { t } = useTranslation()
    return (
        <div className="product-page">
            <PageHeading title={t('pageLayout.notFound')} />
            <section className="mx-auto flex max-w-md flex-col items-center gap-4 py-16 text-center">
                <FileQuestion
                    aria-hidden="true"
                    className="size-10 text-muted-foreground"
                />
                <p>{t('noPage')}</p>
                <p className="text-sm text-muted-foreground">
                    {t('pageLayout.notFoundHelp')}
                </p>
                <Button asChild>
                    <Link to="/">{t('backHome')}</Link>
                </Button>
            </section>
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
    component: lazyRouteComponent(
        () => import('./features/diagnostics/overview'),
        'OverviewPage',
    ),
})
const usersRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/users',
    component: lazyRouteComponent(
        () => import('./features/users/page'),
        'UsersPage',
    ),
})
const documentationRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/api-docs',
    component: lazyRouteComponent(
        () => import('./features/documentation/page'),
        'DocumentationPage',
    ),
})
const systemRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/system',
    component: lazyRouteComponent(
        () => import('./features/system/page'),
        'SystemPage',
    ),
})
const auditRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/audit',
    component: lazyRouteComponent(
        () => import('./features/audit/page'),
        'AuditPage',
    ),
})
const tokensRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/tokens',
    component: lazyRouteComponent(
        () => import('./features/tokens/page'),
        'TokensPage',
    ),
})
const accountRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/account',
    component: AccountPage,
})
const pointsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/prototypes/points',
    component: lazyRouteComponent(
        () => import('./features/points/editor'),
        'PointEditor',
    ),
})
const canvasRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/prototypes/canvas',
    component: lazyRouteComponent(
        () => import('./features/canvas/editor'),
        'CanvasEditor',
    ),
})
const connectionsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/connections',
    component: lazyRouteComponent(
        () => import('./features/connections/page'),
        'ConnectionsPage',
    ),
})
const devicesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/devices',
    component: lazyRouteComponent(
        () => import('./features/devices/page'),
        'DevicesPage',
    ),
})
const inputsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/devices/$deviceId/inputs',
    component: lazyRouteComponent(
        () => import('./features/inputs/page'),
        'InputsPage',
    ),
})
const routesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/routes',
    component: lazyRouteComponent(
        () => import('./features/routes/page'),
        'RoutesPage',
    ),
})
const configurationRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/configuration',
    component: lazyRouteComponent(
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
    component: lazyRouteComponent(
        () => import('./features/configuration/migration'),
        'ConfigurationMigrationPage',
    ),
})
const configurationMigrationIntentsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/configuration/migration-intents',
    validateSearch: migrationSearch,
    component: lazyRouteComponent(
        () => import('./features/configuration/migration-intents'),
        'ConfigurationMigrationIntentsPage',
    ),
})
const diagnosticsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/diagnostics',
    component: lazyRouteComponent(
        () => import('./features/diagnostics/page'),
        'DiagnosticsPage',
    ),
})
const runtimeMessagesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/runtime/messages',
    component: lazyRouteComponent(
        () => import('./features/diagnostics/messages'),
        'RuntimeMessagesPage',
    ),
})
const runtimeDevicesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/runtime/devices',
    component: lazyRouteComponent(
        () => import('./features/diagnostics/current-values'),
        'RuntimeDevicesPage',
    ),
})
const runtimeOutputsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/runtime/outputs',
    component: lazyRouteComponent(
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
    component: lazyRouteComponent(
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
    component: lazyRouteComponent(
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
})
declare module '@tanstack/react-router' {
    interface Register {
        router: typeof router
    }
}
