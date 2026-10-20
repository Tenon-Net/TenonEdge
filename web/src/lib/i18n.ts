import { moduleConnectionStrings } from '../features/connections/module-strings'
import { parameterStrings } from '../features/parameters/strings'
import { clientCertificateStrings } from '../features/connections/client-certificate-strings'
import { messageStrings } from '../features/diagnostics/message-strings'
import { testSendStrings } from '../features/routes/test-send-strings'
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
import { configurationImportStrings } from '../features/configuration/import-strings'
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
import { trustStrings } from '../features/connections/trust-strings'
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
                parameters: parameterStrings,
                testSend: testSendStrings,
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
                configurationImport: configurationImportStrings,
                configurationExport: {
                    title: '配置草稿导出',
                    notice: '下载网关当前已保存的完整草稿（最多 16 MiB），不是当前运行版本。未保存的页面编辑不在文件中；导出不会应用或修改配置。',
                    scope: '包含连接、设备、来源/点位和路线，以业务 key 关联。凭证仅标注需重新填写，不含秘密或受信任证书；不含用户、令牌、网关身份、系统参数、队列、审计或版本历史。配置可能含现场地址与业务样本，请妥善保管。',
                    download: '下载已保存草稿 JSON',
                    ready: '文件已交给浏览器下载；不会自动重试。',
                    denied: '仅配置人员和管理员可以导出配置。',
                    referenceMissing:
                        '草稿含不存在的设备、来源或输出连接引用。请在配置页面修正后重新导出；未生成部分文件。',
                    credentialConflict:
                        '连接凭证元数据冲突，无法安全导出。请联系管理员核对；未生成文件。',
                    tooLarge:
                        '完整导出文件超过 16 MiB，未生成部分文件。请减少草稿内容后重新导出。',
                    invalid:
                        '草稿格式或版本参数无效，未生成文件。请核对配置后重新导出。',
                },
                diagnostics: diagnosticsStrings,
                messages: messageStrings,
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
                moduleConnections: moduleConnectionStrings,
                credentials: credentialStrings,
                clientCertificate: clientCertificateStrings,
                trust: trustStrings,
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
                confirmPassword: '确认密码',
                passwordMismatch: '两次输入的密码不一致。',
                existingLogin: '使用其他已有账号登录',
                backToReset: '返回管理员重置',
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
                httpsHelp: '如何使用 HTTPS',
                httpsInstructions:
                    '密码、会话和凭证可能被同网段设备截获。请使用管理员提供的 HTTPS 地址（默认端口 8443），核对证书后重新登录；两种协议的登录不通用。',
                temporaryPassword: '当前账号必须先修改临时密码，才能继续操作。',
                workspace: '总览',
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
                navigationDescription: '选择要查看的网关功能。',
                navigationGroups: {
                    configuration: '接入配置',
                    operations: '运行监控',
                    management: '系统管理',
                },
                close: '关闭',
                skipToContent: '跳至主要内容',
                theme: {
                    title: '切换主题',
                    light: '浅色',
                    dark: '深色',
                    system: '跟随系统',
                },
            },
        },
    },
})
export default i18n
