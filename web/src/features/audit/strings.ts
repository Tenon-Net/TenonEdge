export const auditStrings = {
    title: '审计记录',
    adminOnly: '仅管理员可查询和导出审计记录。',
    notice: '记录只读，不能人工修改或删除。默认保留 180 天，过期自动清理；本页不是运行日志。',
    from: '开始时间（含，本地时间）',
    to: '结束时间（不含，本地时间）',
    account: '账号（精确匹配）',
    action: '操作代码（精确匹配）',
    target: '对象（精确匹配）',
    apply: '应用筛选',
    refresh: '刷新当前页',
    previous: '上一页',
    next: '下一页',
    loading: '正在读取审计记录…',
    readFailed: '读取失败，不能把旧数据或空白当成当前结果。请重试。',
    empty: '当前范围没有审计记录。',
    invalid: '请检查时间先后及筛选字段长度。',
    dirty: '筛选框有未应用的修改；列表仍使用上次已应用条件，请先应用再导出。',
    columns: {
        id: '编号',
        occurredAt: '时间',
        accountName: '账号',
        tokenName: 'API Token',
        sourceIp: '来源 IP',
        action: '操作',
        target: '对象',
        result: '结果',
        detail: '详情',
    },
    exportTitle: '导出审计 CSV',
    exportNotice:
        '导出整个已应用筛选范围，不只是当前页。开始生成时冻结编号上界，后续新增记录和本次导出审计不在文件内。UTF-8 BOM；危险公式开头加单引号，其余原文保留。',
    lifetime:
        '同一网关同时一份，仅创建它的会话可下载；15 分钟内完成生成和一次下载，取消、到期或下载结束会删除暂存文件。不要自动重试结果不明的请求。',
    scope: '范围：账号 {{account}}；操作 {{action}}；对象 {{target}}；时间 {{from}} 至 {{to}}',
    all: '不限',
    confirm: '我已核对整个筛选范围，确认导出审计记录',
    start: '确认生成审计 CSV',
    cancel: '取消导出',
    readExport: '重新读取导出状态',
    download: '下载完整审计 CSV',
    noExport: '本会话暂无导出。',
    frozen: '本次导出的固定筛选：',
    summary: '{{count}} 条记录；{{bytes}} 字节；有效至 {{expires}}',
    noReplay:
        '请求结果未确认，未自动重放。请重新读取导出状态后再决定；重新登录或 Host 重启后不能恢复旧的下载。',
    response:
        'HTTP 写出完成不等于文件已保存，请核对浏览器下载结果。文件只允许下载一次，失败时重新明确生成。',
    exportReadFailed: '无法读取导出状态，暂不可执行操作。',
    genericError: '导出失败，不提供不完整文件。请重新读取并核对后再试。',
    states: {
        generating: '正在生成完整 CSV',
        ready: '文件已生成并记审计，可以下载',
        downloading: '正在写出下载响应',
        responseWritten: 'HTTP 响应已写出',
        failed: '导出失败',
        cancelled: '已取消',
        expired: '已过期',
    },
    errors: {
        'audit.exportCleanupFailed':
            '暂存文件清理失败，后续导出已禁用。请由主机管理员检查文件权限和磁盘，处理后重启。',
        'audit.exportInsufficientSpace':
            '磁盘可用空间不足或无法确认，已保留网关存储安全余量，不生成截断文件。',
        'audit.exportRetentionBusy':
            '审计保留期清理正在执行或等待重试，请稍后重新明确生成。',
        'audit.exportGenerationFailed':
            '文件生成或导出审计持久化失败，不允许下载。',
        'audit.exportDownloadFailed':
            '下载响应未完整写出。请检查浏览器，不要把部分文件当完整结果。',
    },
}
