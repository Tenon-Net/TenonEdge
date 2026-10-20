import { useId, useRef, useState, type ReactNode } from 'react'
import { FolderOpen, Info } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '../../lib/utils'

/** Compact context, not a second dashboard hero. Actions stay with their page. */
export function PageHeading({
    title,
    description,
    children,
    level = 1,
}: {
    title: string
    level?: 1 | 2
    description?: ReactNode
    children?: ReactNode
}) {
    const Heading = level === 1 ? 'h1' : 'h2'
    return (
        <header className="product-heading">
            <Heading>{title}</Heading>
            <div className="product-heading-actions">
                {children}
                {description && <PageHelp>{description}</PageHelp>}
            </div>
        </header>
    )
}
export function PageHelp({ children }: { children: ReactNode }) {
    const { t } = useTranslation()
    return (
        <details className="product-help">
            <summary>
                <Info aria-hidden="true" className="size-4" />
                {t('pageLayout.help')}
            </summary>
            <div className="product-help-content">{children}</div>
        </details>
    )
}
export type PageTab = { id: string; label: string; content: ReactNode }
/** Panels stay mounted: moving between settings never discards local edits. */
export function PageTabs({
    label,
    tabs,
    initialTab,
}: {
    label: string
    tabs: PageTab[]
    initialTab?: string
}) {
    const prefix = useId()
    const [active, select] = useState(initialTab ?? tabs[0]?.id)
    const buttons = useRef<(HTMLButtonElement | null)[]>([])
    const selected = tabs.some((tab) => tab.id === active)
        ? active
        : tabs[0]?.id
    return (
        <div className="product-tabs">
            <div role="tablist" aria-label={label} className="product-tablist">
                {tabs.map((tab, index) => (
                    <button
                        key={tab.id}
                        ref={(node) => {
                            buttons.current[index] = node
                        }}
                        type="button"
                        role="tab"
                        id={`${prefix}-${tab.id}-tab`}
                        aria-controls={`${prefix}-${tab.id}-panel`}
                        aria-selected={tab.id === selected}
                        tabIndex={tab.id === selected ? 0 : -1}
                        onClick={() => select(tab.id)}
                        onKeyDown={(event) => {
                            let next = index
                            if (event.key === 'ArrowRight')
                                next = (index + 1) % tabs.length
                            else if (event.key === 'ArrowLeft')
                                next = (index - 1 + tabs.length) % tabs.length
                            else if (event.key === 'Home') next = 0
                            else if (event.key === 'End') next = tabs.length - 1
                            else return
                            event.preventDefault()
                            select(tabs[next].id)
                            buttons.current[next]?.focus({
                                preventScroll: true,
                            })
                        }}
                    >
                        {tab.label}
                    </button>
                ))}
            </div>
            {tabs.map((tab) => (
                <div
                    key={tab.id}
                    role="tabpanel"
                    id={`${prefix}-${tab.id}-panel`}
                    aria-labelledby={`${prefix}-${tab.id}-tab`}
                    hidden={tab.id !== selected}
                    className={cn(
                        'product-tabpanel',
                        tab.id === selected && 'is-active',
                    )}
                    tabIndex={0}
                >
                    {tab.content}
                </div>
            ))}
        </div>
    )
}

export function PageEmpty({ children }: { children: ReactNode }) {
    return (
        <div className="flex min-h-32 flex-col items-center justify-center gap-3 rounded-lg border border-dashed p-5 text-center text-sm text-muted-foreground">
            <FolderOpen aria-hidden="true" className="size-6" />
            <p>{children}</p>
        </div>
    )
}
