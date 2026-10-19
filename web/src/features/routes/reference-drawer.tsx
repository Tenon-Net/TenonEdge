import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'
import { ConnectionsPage } from '../connections/page'
import { DevicesPage } from '../devices/page'
import { DeviceInputsPage } from '../inputs/page'

/** Reuse the real editors, not a second CRUD path. Native modal keeps the canvas
 * mounted and inert; only the editor's guarded return action can dismiss it. */
export function ReferenceDrawer({
    kind,
    close,
}: {
    kind: 'connection' | 'device'
    close: () => void
}) {
    const { t } = useTranslation()
    const dialog = useRef<HTMLDialogElement>(null)
    const [deviceId, setDeviceId] = useState<number>()
    useEffect(() => {
        const element = dialog.current!
        element.showModal()
        return () => element.close()
    }, [])
    return createPortal(
        <dialog
            ref={dialog}
            aria-label={t('routes.inline.title')}
            className="fixed inset-y-0 right-0 left-auto m-0 h-dvh max-h-none w-full max-w-5xl overflow-y-auto border-l bg-background p-6 text-foreground shadow-xl backdrop:bg-black/40"
            onCancel={(event) => event.preventDefault()}
        >
            <p className="mb-4 text-sm text-muted-foreground">
                {t('routes.inline.notice')}
            </p>
            {kind === 'connection' ? (
                <ConnectionsPage onReturn={close} />
            ) : deviceId === undefined ? (
                <DevicesPage onReturn={close} onOpenInputs={setDeviceId} />
            ) : (
                <DeviceInputsPage
                    key={deviceId}
                    routeId={String(deviceId)}
                    onReturn={() => setDeviceId(undefined)}
                />
            )}
        </dialog>,
        document.body,
    )
}
