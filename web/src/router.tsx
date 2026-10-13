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
        <div className="max-w-md space-y-6">
            <h1 className="text-2xl font-semibold">{t('passwordChange')}</h1>
            <AuthForm mode="passwordChange" />
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
    component: lazyRouteComponent(
        () => import('./features/diagnostics/overview'),
        'OverviewPage',
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
const diagnosticsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/diagnostics',
    component: lazyRouteComponent(
        () => import('./features/diagnostics/page'),
        'DiagnosticsPage',
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
export const router = createRouter({
    routeTree: rootRoute.addChildren([
        indexRoute,
        accountRoute,
        configurationRoute,
        diagnosticsRoute,
        runtimeDevicesRoute,
        runtimeOutputsRoute,
        deliveryRecordsRoute,
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
