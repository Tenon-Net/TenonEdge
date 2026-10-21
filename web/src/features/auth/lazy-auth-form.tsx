import { lazy, type ComponentProps } from 'react'
import { PageBoundary } from '../../components/page-boundary'
import type { AuthForm } from './auth-form'

const Form = lazy(() =>
    import('./auth-form').then(({ AuthForm }) => ({ default: AuthForm })),
)

export function LazyAuthForm(props: ComponentProps<typeof AuthForm>) {
    return (
        <PageBoundary>
            <Form {...props} />
        </PageBoundary>
    )
}
