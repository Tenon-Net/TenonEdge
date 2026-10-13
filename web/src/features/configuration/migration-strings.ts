export const configurationMigrationStrings = {
    title: '同连接换地址并迁移',
    open: '核对同连接换地址迁移',
    back: '返回配置应用与历史',
    intents: '持久迁移意图',
    boundary:
        '在完整配置应用中显式选择换址连接。执行前静默原发送者，配置与迁移意图一起提交；旧记录先隔离，之后才迁移到新地址。未选阻断、删除连接、改变协议或夹带主题／路径变更仍会被拒绝。',
    safety: '保留投递编号、FIFO 位置、原报文及主题／路径。原失败记录迁移后仍在失败区，不解除手动暂停；迁移完成不等于已发送，仍可能重复送达。',
    lifetime:
        '每次最多 500 个连接；确认清单属于本登录会话，15 分钟后过期。刷新可恢复清单，但不会恢复勾选声明。取消准备不取消已由后台接管的应用或迁移。',
    readDraft: '读取换址变更清单',
    select: '迁移连接 {{id}} 的旧记录',
    selected: '已选 {{count}} 个连接',
    prepare: '准备所选连接的换址应用',
    prepared: '换址应用最终核对',
    blocked:
        '普通应用的目标阻断仍保留展示；以下确认仅允许服务器已核对的换址迁移，不放宽普通应用。',
    target: '{{name}}（{{key}}，连接 {{id}}）',
    count: '准备时涉及 {{count}} 条旧记录，并非冻结条数。确认前旧配置仍会采样，条数可能增加；已发送或过期会减少。范围以旧发送者静默后仍属原接收方的记录为准，执行时整批复查配额。',
    targetIdentity: '新接收方（这次准备的配置身份）',
    expires: '本清单到期：{{time}}',
    agree: '我已核对全部配置差异、迁移连接和配额，确认应用',
    owner: '我确认每个所选连接的新旧接收方属于同一数据所有者',
    confirm: '确认换址并应用配置',
    cancel: '取消换址应用准备',
    refresh: '刷新换址应用状态',
    operation: '配置应用操作编号：{{id}}',
    unknown:
        '未能取得应用结果。应用状态仅在当前进程保存，404 不证明未提交；请核对持久迁移意图和配置历史，不要重复确认。',
    next: '结束本次核对，读取新的草稿',
    nextNotice:
        '只清除页面尝试并取消仍未消费的准备，不取消后台任务。请保留操作编号并先核对持久意图与配置历史。',
    intentId: '意图编号：{{id}}',
    listBoundary:
        '只读取持久元数据，不读取队列正文。按版本／连接顺序分页，不是跨页一致快照。等待不表示可以重放；完成不表示已发送。',
    status: '筛选意图状态',
    connection: '筛选连接编号（选填）',
    filter: '应用意图筛选',
    all: '全部状态',
    invalid: '编号或筛选格式无效。',
    first: '首批意图',
    nextPage: '下一批意图',
    refreshList: '重新读取本页意图',
    empty: '本页没有迁移意图。',
    states: {
        waiting: '等待迁移',
        completed: '迁移已完成（不表示已发送）',
        rejected: '迁移已拒绝，记录保持隔离',
    },
    item: 'r{{revision}}（原 r{{source}}） · 连接 {{connection}} · 登记人 {{account}}',
    inspect: '核对意图 {{id}}',
    detail: '意图当前核对',
    approved: '上次确认的目标身份',
    current: '当前有效目标身份',
    source: '原始冻结身份',
    identityUnknown: '身份未记录或当前目标不可用，不用其他身份补填。',
    effective: '当前有效版本：{{revision}}；仍属原隔离意图的记录：{{count}}',
    matches: '当前目标与上次确认的完整身份一致：{{value}}',
    yes: '是',
    no: '否',
    quotaNow: '以下是当前配额缺口，不是历史拒绝时的配额。',
    refreshDetail: '重新核对当前身份与配额',
    prepareRetry: '准备重新审批此意图',
    retryReview: '重新审批确认清单',
    retryLifetime:
        '每次重新审批一个持久意图；清单属于本登录会话，15 分钟后过期。刷新恢复清单但不恢复声明，取消准备不取消已接管的后台迁移。',
    retryBoundary:
        '只允许明确拒绝或旧版未确认且从未开始的意图。新审批保留原登记人和历史尝试，实际重试管理员另记审计。零条原隔离记录可以完成，不扩展到后来同旧指纹记录。未知结果只能查询，不重放。',
    retryAgree: '我已核对原始、上次确认和当前身份及当前配额，确认重试',
    retryOwner: '我再次确认当前接收方与原接收方属于同一数据所有者',
    retryConfirm: '确认重新审批并重试',
    retryCancel: '取消意图重试准备',
    retryRefresh: '刷新本会话重试清单',
    retryNext: '结束本次重试核对',
    lastOperation: '最近一次持久尝试：{{id}}',
}

export const configurationMigrationErrors = {
    'configuration.migrationPreparationBusy':
        '另一登录会话正在准备迁移，请稍后再试。',
    'configuration.migrationPreparationNotFound':
        '准备已过期、已消费或不属于本登录会话，请先查询结果。',
    'configuration.migrationPreflightFailed':
        '配置不能按所选换址方式应用，请处理未选阻断、协议或主题／路径变更。',
    'configuration.migrationTargetUnavailable':
        '原目标或新目标的完整身份不可用，不能确认。',
    'configuration.migrationReviewChanged':
        '草稿、有效版本、意图或目标身份已变化，请重新核对。',
    'configuration.migrationIdentityLimit':
        '本次身份元数据超出预算，请缩小范围。',
    'configuration.migrationNotConfirmable':
        '当前清单不能确认，请重新核对条件与配额。',
    'configuration.migrationIntentNotFound':
        '未找到该持久意图，不能据此推定应用未提交。',
    'configuration.migrationAlreadyCompleted': '此意图已完成，不得再次执行。',
    'configuration.migrationNotRetryable':
        '此意图尚不允许显式重试；未知结果不得重放。',
    'configuration.migrationOutcomeUnreconciled':
        '已有队列结果尚未完成启动核对，不允许重新执行。',
    'configuration.migrationEarlierIntentWaiting':
        '同连接还有更早的等待意图，请先核对。',
}
