import { expect, it } from 'vitest'
import i18n from './i18n'

it('loads only shell text initially, then registers direct/inline feature text without losing other resources', async () => {
    expect(i18n.t('shell.connections')).toBe('连接')
    expect(i18n.t('noPreviousLogin')).toBe('上次登录：无记录')
    expect(i18n.exists('connections.deletion.prompt')).toBe(false)
    expect(i18n.exists('configurationImport.title')).toBe(false)

    // The module is also used by embedded editors; loading it does not require a route navigation.
    await import('../features/connections/i18n')
    expect(i18n.t('connections.deletion.prompt', { name: 'PLC' })).toBe(
        '确认删除连接草稿 PLC？服务端将再次核对全部条件。',
    )
    expect(i18n.exists('routes.inline.return')).toBe(true)
    expect(i18n.exists('parameters.unavailable')).toBe(true)
    expect(i18n.exists('configurationImport.title')).toBe(false)

    await import('../features/configuration/i18n')
    expect(i18n.exists('configurationImport.title')).toBe(true)
    expect(i18n.t('connections.title')).toBe('连接')
    expect(i18n.t('configuration.summary', { count: 2 })).toBe(
        i18n.t('shell.summary', { count: 2 }),
    )
    expect(i18n.t('configuration.title')).toBe(i18n.t('shell.configuration'))
})
