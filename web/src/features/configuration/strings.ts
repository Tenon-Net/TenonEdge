export const configurationStrings = {
    title: '配置应用',
    summary: '有 {{count}} 项未应用的对象修改',
    unknownSummary: '未应用修改数量未知，不能按零项处理',
    effective: '有效配置：{{revision}}',
    none: '尚无有效版本',
    pending: '已有配置冻结待提交，请先查看操作进度。',
    applicationBy: '正在由 {{account}} 应用配置，操作 {{id}}。',
    lastKnown: '以下为上次成功查询的状态，不代表当前实时状态。',
    refresh: '刷新配置状态',
    review: '查看应用变更清单',
    reviewTitle: '应用前确认',
    description: '变更说明（选填，最多 1000 字符）',
    confirm: '我已检查变更及影响，确认应用这份草稿',
    apply: '确认应用配置',
    discard: '放弃全部未应用修改',
    discardConfirm:
        '放弃当前全部未应用修改，恢复有效配置的草稿？当前凭证不会恢复，运行配置与队列不会改变。',
    discarded:
        '未应用修改已放弃。当前凭证未恢复；其他打开的编辑页面须刷新，旧版本保存会被拒绝。',
    accepted:
        '请求已接管，不等于已生效。关闭页面不会取消操作，请查看下方操作状态。',
    uncertain:
        '请求失败或响应丢失，结果可能未知。禁止自动重试；先核对操作记录和有效版本，再重新读取清单。',
    stale: '草稿或有效版本已变化，请重新读取清单后确认。',
    blocked: '预检未通过，不能应用。请处理以下错误或阻断项。',
    unknown: '未知',
    before: '修改前（数字按原文显示）',
    after: '修改后（数字按原文显示）',
    changes: '对象变更',
    fields: '变化字段（JSON 路径）',
    fieldsTruncated:
        '变化字段超过 200 项；此处仅列前 200 项，完整内容见下方修改前/后。',
    noChanges: '没有对象内容差异；仍会生成新配置版本并暂停输入。',
    editor: '最后修改：{{account}} · {{time}}',
    points: '设备 {{device}} 点位：新增 {{added}}，修改 {{modified}}，删除 {{removed}}',
    routeIssue: '路线 {{route}} / 节点 {{node}}：{{code}}',
    targetBlock:
        '连接 {{connection}} → {{recipient}}{{address}}：待发 {{pending}}，失败区 {{failed}}；须先排空、清理或由管理员迁移。',
    operations: '本进程最近操作（最多 20 项）',
    operationId: '操作 {{id}} · {{account}}',
    noOperations:
        '本进程尚无操作。重启后操作列表会清空，不代表没有持久配置版本。',
    unavailableOperation:
        '无法查询该操作；可能服务已重启或记录已过期。不要再次提交，请核对有效版本与持久历史。',
    currentOperation: '本页最近一次已接管操作',
    revision: '配置版本 {{revision}}',
    errorCode: '结果代码：{{code}}',
    historyBoundary:
        '应用、放弃及历史回滚均操作真实配置。操作记录仅保留于本进程，持久版本请查看下方历史。',
    history: '持久配置历史',
    appliedAt: '提交生效时间：{{time}}',
    versionStates: {
        effective: '生效中',
        history: '历史',
        pending: '待提交',
        notEffective: '未生效',
    },
    origins: {
        apply: '人工应用',
        rollback: '人工回滚',
        autoRollback: '启动保护自动回滚',
        seed: '种子导入',
    },
    errors: {
        configuration_rollbackCredentialMissing:
            '目标版本需要的当前凭证不存在；不能恢复历史秘密，请先配置凭证。',
        configuration_rollbackConnectionMissing:
            '目标版本引用的连接已不存在，不能回滚。',
        configuration_rollbackTargetNotEffective:
            '目标版本从未生效，不能作为回滚目标。',
        configuration_unappliedChanges: '请先应用或放弃未应用修改，再回滚。',
        configuration_unsupportedSnapshot:
            '旧版快照缺少完整草稿，不能进行对象比较或正常回滚。',
        configuration_invalidSnapshot:
            '配置快照无效，不能安全读取、比较或确认回滚。',
        configuration_versionNotFound: '版本不存在或已清理，请刷新历史。',
        configuration_reviewChanged: '确认清单已变化，请重新预检。',
    },
    historyHelp:
        '按版本倒序，每页 20 项。回滚须先处理未应用修改；失败版本可查看，但不能作为回滚目标。',
    loading: '正在读取…',
    viewVersion: '查看 r{{revision}}',
    versionDetail: '版本 r{{revision}}',
    noVersions: '尚无持久版本',
    previous: '上一页',
    next: '下一页',
    hashes: '完整检查点 {{content}} · 编译计划 {{plan}}',
    draftText: '完整草稿（精确 JSON）',
    planText: '运行计划（精确 JSON）',
    reviewRollback: '检查回滚到 r{{revision}}',
    rollbackTitle: '回滚到 r{{revision}} 前确认',
    rollbackConfirm: '我已检查目标版本及影响，确认回滚并创建新版本',
    rollback: '确认回滚配置',
    compareWith: '比较到',
    currentDraft: '当前草稿',
    comparison: '版本差异',
    comparisonDirection: 'r{{before}} → {{after}}（仅比较，不执行预检或应用）',
    sameContents: '没有对象内容差异。',
    kinds: { connection: '连接', device: '设备', input: '来源', route: '路线' },
    changeKinds: { added: '新增', modified: '修改', removed: '删除' },
    stages: {
        validating: '校验',
        preparing: '准备',
        quiescing: '停止输入',
        committing: '提交',
        activating: '启动新配置',
        restoring: '恢复原配置',
        completed: '已结束',
        failed: '失败',
    },
    outcomes: {
        running: '操作进行中，请勿重复提交。',
        unknown: '提交结果未知，须核对持久配置版本，不要重试。',
        successful: '配置已提交并激活。后续运行健康情况仍须查看诊断。',
        committedFault:
            '新版本已提交，但运行激活存在故障；不能当作未生效，也不会自动撤销提交。',
        notCommitted:
            '本次配置未提交。原配置恢复情况请结合下方结果与运行诊断核对。',
    },
    recoveryPending: '原配置仍有单元等待恢复，不代表采集已全部恢复。',
    draftKept: '操作期间草稿被其他人修改，已保留这些修改。',
    warnings: {
        allInputsPause: '应用期间全部输入会暂停。',
        mqttQos1RequiresPersistentBrokerSession:
            'MQTT QoS 1 补收依赖持久会话及 Broker 持久化前提，不能保证 Broker 已丢失的数据可恢复。',
        qos0AndPollingGap: 'QoS 0 输入及轮询输入会缺少暂停期间的数据。',
        queuedMessagesKeepOriginalContent: '已生成的待发记录按原内容继续发送。',
        legacyComparisonUnavailable:
            '旧快照缺少完整草稿，无法安全比较或确认应用。',
        targetChangeRequiresDrainClearOrMigration:
            '目标变更受到积压记录保护，须先处理阻断。',
        rollbackDoesNotRetractSentData: '回滚不会撤回已发送数据。',
        rollbackKeepsConcurrentDraftEdits: '回滚期间发生的新草稿修改会被保留。',
        rollbackUsesCurrentCredentials: '回滚使用当前凭证，不恢复历史秘密。',
    },
}
