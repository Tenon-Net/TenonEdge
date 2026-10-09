export class ApiError extends Error {
    constructor(
        public readonly status: number,
        public readonly problem: unknown,
    ) {
        super(`HTTP ${status}`)
        this.name = 'ApiError'
    }
}

export function fieldErrors(problem: unknown): Record<string, string[]> {
    if (!problem || typeof problem !== 'object' || !('errors' in problem))
        return {}
    const errors = problem.errors
    if (!errors || typeof errors !== 'object') return {}
    return Object.fromEntries(
        Object.entries(errors).filter(
            ([, value]) =>
                Array.isArray(value) &&
                value.every((item) => typeof item === 'string'),
        ),
    )
}
