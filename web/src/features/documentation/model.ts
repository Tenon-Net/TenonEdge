const methods = new Set([
    'get',
    'put',
    'post',
    'delete',
    'options',
    'head',
    'patch',
    'trace',
])
function object(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === 'object' && !Array.isArray(value)
}
export function readDocumentation(value: unknown) {
    if (
        !object(value) ||
        typeof value.openapi !== 'string' ||
        !value.openapi.startsWith('3.') ||
        !object(value.info) ||
        !object(value.paths)
    )
        throw new Error('invalid-openapi-document')
    const operations = Object.entries(value.paths)
        .flatMap(([path, item]) => {
            if (!path.startsWith('/') || !object(item)) return []
            return Object.entries(item).flatMap(([method, operation]) => {
                if (!methods.has(method) || !object(operation)) return []
                return [
                    {
                        key: method + ' ' + path,
                        method: method.toUpperCase(),
                        path,
                        summary:
                            typeof operation.summary === 'string'
                                ? operation.summary
                                : '',
                        operationId:
                            typeof operation.operationId === 'string'
                                ? operation.operationId
                                : '',
                        // Include path-level metadata and root security; never resolve external refs,
                        // render descriptions as HTML or execute a server URL from the document.
                        detail: JSON.stringify(
                            {
                                pathParameters: item.parameters,
                                security: operation.security ?? value.security,
                                ...operation,
                            },
                            null,
                            2,
                        ),
                    },
                ]
            })
        })
        .sort(
            (a, b) =>
                a.path.localeCompare(b.path) ||
                a.method.localeCompare(b.method),
        )
    return {
        title: typeof value.info.title === 'string' ? value.info.title : '',
        version:
            typeof value.info.version === 'string' ? value.info.version : '',
        openapi: value.openapi,
        operations,
        complete: JSON.stringify(value, null, 2),
    }
}
