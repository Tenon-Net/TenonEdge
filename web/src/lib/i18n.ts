import { documentationStrings } from '../features/documentation/strings'
import { systemStrings } from '../features/system/strings'
import { auditStrings } from '../features/audit/strings'
import { userStrings } from '../features/users/strings'
import { tokenStrings } from '../features/tokens/strings'
import { configurationMigrationStrings } from '../features/configuration/migration-strings'
import { migrationStrings } from '../features/deliveries/migration-strings'
import { outputControlStrings } from '../features/deliveries/control-strings'
import { deliveryStrings } from '../features/deliveries/strings'
import { outputStrings } from '../features/diagnostics/output-strings'
import { currentValueStrings } from '../features/diagnostics/current-value-strings'
import { configurationStrings } from '../features/configuration/strings'
import { diagnosticsStrings } from '../features/diagnostics/strings'
import { routeStrings } from '../features/routes/strings'
import { inputStrings } from '../features/inputs/strings'
import { deviceStrings } from '../features/devices/strings'
import {
    connectionStrings,
    credentialStrings,
} from '../features/connections/strings'
import { canvasStrings } from '../features/canvas/strings'
import { pointStrings } from '../features/points/strings'
import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
void i18n.use(initReactI18next).init({
    lng: 'zh-CN',
    fallbackLng: 'zh-CN',
    interpolation: { escapeValue: false },
    resources: {
        'zh-CN': {
            translation: {
                systemSettings: systemStrings,
                documentation: documentationStrings,
                audit: auditStrings,
                users: userStrings,
                tokens: tokenStrings,
                previousLogin: '上次登录：{{time}}；来源 IP：{{ip}}',
                noPreviousLogin: '上次登录：无记录',
                migration: migrationStrings,
                configurationMigration: configurationMigrationStrings,
                configuration: configurationStrings,
                diagnostics: diagnosticsStrings,
                currentValues: currentValueStrings,
                outputs: outputStrings,
                deliveries: deliveryStrings,
                outputControls: outputControlStrings,
                routes: routeStrings,
                devices: deviceStrings,
                inputs: inputStrings,
                points: pointStrings,
                canvas: canvasStrings,
                connections: connectionStrings,
                credentials: credentialStrings,
                product: 'TenonEdge',
                subtitle: '边缘接入与转发网关',
                loading: '正在读取网关状态…',
                retry: '重新查询',
                lastSessionRefresh: '会话状态上次成功刷新：{{time}}',
                neverRefreshed: '尚未成功刷新',
                requestFailed: '请求失败，请检查网关连接后重试。',
                timeoutNotice:
                    '请求失败或超时；操作不会自动重试，请先核对当前状态。',
                unauthorized: '账号或密码错误，或会话已失效。',
                forbidden: '权限不足或防 CSRF 校验失败，请重新登录。',
                rateLimited: '尝试次数过多，请稍后重试。',
                conflict: '状态已经变化，请刷新核对后再操作。',
                login: '登录',
                setup: '首次设置',
                reset: '重置管理员密码',
                passwordChange: '修改密码',
                logout: '退出登录',
                accountName: '账号',
                account: '账号',
                password: '密码',
                currentPassword: '原密码',
                newPassword: '新密码',
                gatewayName: '网关名称（可选）',
                submitSetup: '创建管理员并登录',
                submitReset: '设置密码并登录',
                submitPassword: '保存新密码',
                pending: '正在提交…',
                required: '请填写此项。',
                setupClosed: '设置窗口已关闭，请在主机上重启服务后再试。',
                windowCloses: '设置窗口关闭时间：{{time}}',
                httpWarning:
                    '当前使用 HTTP，密码会明文传输。请仅在可信管理网络使用，建议通过 HTTPS 访问。',
                httpsInstructions:
                    '密码、会话和凭证可能被同网段设备截获。请使用管理员提供的 HTTPS 地址（默认端口 8443），核对证书后重新登录；两种协议的登录不通用。',
                temporaryPassword: '当前账号必须先修改临时密码，才能继续操作。',
                workspace: '总览',
                authenticated: '已连接到真实网关',
                accountDetail: '当前账号：{{name}}',
                roles: {
                    administrator: '管理员',
                    configurator: '配置人员',
                    viewer: '只读',
                },
                noPage: '此页面不存在。',
                backHome: '返回总览',
                security: '账号安全',
                savedPassword: '密码已修改。',
                showNavigation: '展开导航',
                hideNavigation: '收起导航',
                navigation: '主导航',
            },
        },
    },
})
export default i18n
