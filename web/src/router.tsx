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
import { Card, CardContent, CardHeader, CardTitle } from './components/ui/card'

function WorkspacePage() {
    const { t } = useTranslation()
    return (
        <div className="mx-auto max-w-4xl space-y-6">
            <h1 className="text-2xl font-semibold">{t('workspace')}</h1>
            <Card>
                <CardHeader>
                    <CardTitle>{t('authenticated')}</CardTitle>
                </CardHeader>
                <CardContent>
                    <p className="leading-7 text-muted-foreground">
                        {t('foundationNotice')}
                    </p>
                </CardContent>
            </Card>
        </div>
    )
}
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
    component: WorkspacePage,
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
export const router = createRouter({
    routeTree: rootRoute.addChildren([
        indexRoute,
        accountRoute,
        configurationRoute,
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
