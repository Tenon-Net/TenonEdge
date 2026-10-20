// Adapted from shadcn-admin e16c87f (MIT, Copyright (c) 2024 Sat Naing). See web/THIRD_PARTY.md.
import { createContext, useContext, useEffect, useState } from 'react'

type Theme = 'dark' | 'light' | 'system'
const storageKey = 'tenonedge-theme'
const ThemeContext = createContext<{
    theme: Theme
    resolvedTheme: 'dark' | 'light'
    setTheme: (theme: Theme) => void
} | null>(null)

export function ThemeProvider({ children }: { children: React.ReactNode }) {
    const [theme, updateTheme] = useState<Theme>(() => {
        try {
            const stored = localStorage.getItem(storageKey)
            if (stored === 'dark' || stored === 'light') return stored
        } catch {
            /* The interface still works when browser storage is disabled. */
        }
        return 'system'
    })
    const [systemDark, setSystemDark] = useState(
        () => window.matchMedia('(prefers-color-scheme: dark)').matches,
    )
    const resolvedTheme =
        theme === 'system' ? (systemDark ? 'dark' : 'light') : theme
    useEffect(() => {
        const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)')
        const handleChange = () => setSystemDark(mediaQuery.matches)
        handleChange()
        mediaQuery.addEventListener('change', handleChange)
        return () => mediaQuery.removeEventListener('change', handleChange)
    }, [])
    useEffect(() => {
        const root = document.documentElement
        root.classList.remove('light', 'dark')
        root.classList.add(resolvedTheme)
        root.style.colorScheme = resolvedTheme
    }, [resolvedTheme])
    const setTheme = (value: Theme) => {
        updateTheme(value)
        try {
            localStorage.setItem(storageKey, value)
        } catch {
            /* Optional preference only. */
        }
    }
    return (
        <ThemeContext value={{ theme, resolvedTheme, setTheme }}>
            {children}
        </ThemeContext>
    )
}

export function useTheme() {
    const context = useContext(ThemeContext)
    if (!context)
        throw new Error('useTheme must be used within a ThemeProvider')
    return context
}
