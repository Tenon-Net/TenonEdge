import { useEffect, useRef, useState } from 'react'
import { useTranslation } from './i18n'
import { Button } from '../../components/ui/button'
import {
    readClientCertificateFile,
    type CertificateFileKind,
    type ClientCertificateUpload,
} from './client-certificate-file'

/** Secret material stays only in the owning form; never in Query variables, Zustand or storage. */
export function useClientCertificateUpload() {
    const [upload, setUpload] = useState<ClientCertificateUpload>({})
    const [format, setFormat] = useState<'pkcs12' | 'pem'>('pkcs12')
    const [error, setError] = useState('')
    const [reading, setReading] = useState(false)
    const generation = useRef(0)
    useEffect(
        () => () => {
            generation.current++
        },
        [],
    )
    function clear() {
        generation.current++
        setUpload({})
        setReading(false)
        setError('')
    }
    async function read(file: File | undefined, kind: CertificateFileKind) {
        const current = ++generation.current
        setError('')
        setUpload((value) => ({ ...value, [kind]: undefined }))
        if (!file) {
            setReading(false)
            return
        }
        setReading(true)
        try {
            const value = await readClientCertificateFile(file, kind)
            if (generation.current === current)
                setUpload((old) => ({ ...old, [kind]: value }))
        } catch (error) {
            if (generation.current === current)
                setError(
                    error instanceof Error
                        ? error.message
                        : 'clientCertificate.fileEncoding',
                )
        } finally {
            if (generation.current === current) setReading(false)
        }
    }
    return {
        upload,
        format,
        error,
        reading,
        clear,
        read,
        ready:
            !reading &&
            (format === 'pkcs12'
                ? !!upload.pkcs12
                : !!upload.certificatePem && !!upload.privateKeyPem),
        changeFormat(value: 'pkcs12' | 'pem') {
            clear()
            setFormat(value)
        },
        password(value: string) {
            setUpload((old) => ({ ...old, password: value }))
        },
    }
}

export function ClientCertificateFields({
    value,
    disabled = false,
}: {
    value: ReturnType<typeof useClientCertificateUpload>
    disabled?: boolean
}) {
    const { t } = useTranslation()
    return (
        <fieldset disabled={disabled} className="space-y-3 rounded border p-3">
            <legend>{t('clientCertificate.upload')}</legend>
            <p className="text-sm text-muted-foreground">
                {t('clientCertificate.privateNotice')}
            </p>
            <label className="block">
                {t('clientCertificate.format')}
                <select
                    className="ml-2 rounded border p-2"
                    value={value.format}
                    onChange={(event) =>
                        value.changeFormat(
                            event.target.value as 'pkcs12' | 'pem',
                        )
                    }
                >
                    <option value="pkcs12">
                        {t('clientCertificate.pkcs12')}
                    </option>
                    <option value="pem">{t('clientCertificate.pem')}</option>
                </select>
            </label>
            {(value.format === 'pkcs12'
                ? (['pkcs12'] as const)
                : (['certificatePem', 'privateKeyPem'] as const)
            ).map((kind) => (
                <label className="block" key={kind}>
                    {t(`clientCertificate.${kind}File`)}
                    <input
                        type="file"
                        className="ml-2 max-w-full rounded border p-2"
                        disabled={value.reading}
                        accept={
                            kind === 'pkcs12' ? '.p12,.pfx' : '.pem,.crt,.key'
                        }
                        onChange={(event) => {
                            const file = event.currentTarget.files?.[0]
                            event.currentTarget.value = ''
                            void value.read(file, kind)
                        }}
                    />
                    {value.upload[kind] && (
                        <span className="ml-2">
                            {t('clientCertificate.loaded')}
                        </span>
                    )}
                </label>
            ))}
            {value.format === 'pkcs12' && (
                <label className="block">
                    {t('clientCertificate.password')}
                    <input
                        className="ml-2 rounded border p-2"
                        type="password"
                        autoComplete="new-password"
                        maxLength={1024}
                        value={value.upload.password ?? ''}
                        onChange={(event) => value.password(event.target.value)}
                    />
                </label>
            )}
            {value.error && <p role="alert">{t(value.error)}</p>}
            {value.reading && (
                <p role="status">{t('clientCertificate.reading')}</p>
            )}
            <Button type="button" variant="outline" onClick={value.clear}>
                {t('clientCertificate.discard')}
            </Button>
        </fieldset>
    )
}
