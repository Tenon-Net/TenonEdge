import createClient from 'openapi-fetch'
import type { paths } from './schema'
import { ApiError } from './errors'

// No AbortSignal.any/timeout: Chrome 111 and ordinary HTTP must work too.
export async function fetchWithDeadline(
    request: Request,
    fetcher: typeof fetch = fetch,
    milliseconds = 15000,
) {
    const controller = new AbortController()
    const abort = () => controller.abort(request.signal.reason)
    if (request.signal.aborted) abort()
    else request.signal.addEventListener('abort', abort, { once: true })
    const timer = setTimeout(() => controller.abort(), milliseconds)
    try {
        const response = await fetcher(
            new Request(request, { signal: controller.signal }),
        )
        // Keep the same deadline until the entire management response is received, not just its headers.
        const body =
            [204, 205, 304].includes(response.status) ||
            request.method === 'HEAD'
                ? null
                : await response.arrayBuffer()
        return new Response(body, {
            status: response.status,
            statusText: response.statusText,
            headers: response.headers,
        })
    } finally {
        clearTimeout(timer)
        request.signal.removeEventListener('abort', abort)
    }
}

export function createGatewayClient(options: {
    baseUrl: string
    csrf: () => string | undefined
    epoch: () => number
    unauthorized: () => void
    fetcher?: typeof fetch
}) {
    const generations = new WeakMap<Request, number>()
    const client = createClient<paths>({
        baseUrl: options.baseUrl,
        credentials: 'same-origin',
        fetch: (request) => fetchWithDeadline(request, options.fetcher),
    })
    client.use({
        onRequest({ request }) {
            generations.set(request, options.epoch())
            request.headers.set('Accept', 'application/json')
            if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method)) {
                const csrf = options.csrf()
                if (csrf) request.headers.set('X-CSRF-Token', csrf)
            }
            return request
        },
        async onResponse({ request, response }) {
            // A late response from an old account must never repopulate or invalidate a new session.
            if (generations.get(request) !== options.epoch())
                throw new Error('stale-session-response')
            if (response.status === 401 && !request.url.endsWith('/auth/login'))
                options.unauthorized()
            if (!response.ok) {
                let problem: unknown
                try {
                    problem = await response.clone().json()
                } catch {
                    problem = undefined
                }
                throw new ApiError(response.status, problem)
            }
            return response
        },
    })
    return client
}
