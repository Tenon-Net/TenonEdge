export const clientCertificateStrings = {
    upload: '客户端身份材料',
    privateNotice:
        '仅用于客户端认证，不会信任服务器证书。私钥和容器口令不可读回，仅暂存当前表单；请使用 HTTPS 或隔离管理网。切换方式、离开页面或提交后清除本页材料。',
    format: '材料格式',
    pkcs12: 'PKCS#12 / PFX',
    pem: 'PEM 证书链与私钥',
    pkcs12File: 'PKCS#12 文件（最多 64 KiB）',
    certificatePemFile: 'PEM 证书链文件（最多 96 KiB，叶证书在前）',
    privateKeyPemFile: '未加密 PEM 私钥文件（最多 16 KiB）',
    password: 'PKCS#12 容器口令（选填）',
    loaded: '已载入（不回显）',
    reading: '正在读取本地文件…',
    discard: '清除本页证书材料',
    fileSize:
        '文件须非空，PKCS#12 最多 64 KiB、证书链最多 96 KiB、私钥最多 16 KiB。',
    fileEncoding: '无法读取文件，请使用完整的 UTF-8 PEM 文件。',
    tlsRequired: '客户端证书只用于 HTTPS / MQTT TLS；请先修改并保存连接草稿。',
    review: '核对客户端证书（不保存）',
    reviewTitle: '确认客户端证书身份',
    current: '原认证身份',
    proposed: '新客户端证书',
    recipient: '接收方',
    absent: '无已保存凭证',
    notConfigured: '原凭证已作废或未设置',
    hiddenAccount: '无可见账户标识',
    count: '核对时待发 {{pending}} 条、失败区 {{failed}} 条（{{time}}）。',
    liveNotice:
        '核对不会冻结队列，数量可能继续变化；提交时服务端重新检查版本、证书及积存声明。隔离记录不会因保存凭证自动迁移或恢复。',
    changedNotice:
        '接收方或可见账户身份将变化；与新身份不符的现有待发和失败记录将被隔离，不会用新账户发送。',
    sameNotice:
        '证书主题相同不代表背后的账户相同。有积存时必须明确声明，不能仅凭续签推断。',
    acknowledge:
        '我已核对原新接收方、证书主题和指纹，了解认证立即生效及积存处理规则',
    confirm: '确认保存客户端证书',
    cancel: '取消核对并清除材料',
    saved: '客户端证书已保存，后续连接使用当前身份；不自动应用草稿。',
    unknown:
        '本次保存未取得成功确认，请先重新读取凭证状态核对版本和审计。不会自动重试；本页材料已清除。',
    refresh: '重新读取凭证状态',
    kinds: {
        password: '账号密码',
        basic: 'Basic',
        bearer: 'Bearer',
        apiKeyHeader: '请求头 API Key',
        apiKeyQuery: '查询参数 API Key',
        clientCertificate: '客户端证书',
    },
}
