export const parameterStrings = {
    transformTitle: '转换参数',
    transformNotice:
        '参数采用后可保存未完成草稿；必填、类型或范围错误必须修正后才能应用。',
    transformUnsupported: '当前配置不能由标量表单编辑，原配置已保留。',
    modulesRefresh: '重新读取转换模块',
    modulesUnavailable:
        '转换模块目录不可用；扩展节点暂不可添加或编辑，当前图与编辑内容保留。',
    moduleMissing: '此类型或版本未安装可用表单；原配置保留，不会自动替换版本。',

    refresh: '重新读取参数声明',
    unavailable: '参数声明不可用，不能保存。重新读取不会覆盖尚未保存的编辑。',
    loading: '正在读取参数声明…',
    select: '请选择',
    unknown: '当前选择不可用',
    credential: '凭证独立保存和核对，不属于普通配置。',
    configured: '已设置（不回显）',
    unconfigured: '尚未设置',
    issues: {
        required: '此参数必填。',
        number: '请输入有效 JSON 数字（最多 128 字符，指数绝对值不超过 1000000）。',
        integer: '必须是整数。',
        range: '数字不在声明范围内。',
        length: '文字超过声明长度。',
        type: '参数类型不正确。',
        choice: '请选择声明中的选项。',
        connection: '连接不存在或协议不匹配，请重新选择。',
        unknown: '配置含未声明字段。',
        credentialSeparate: '凭证不能写入普通配置。',
    },
    range: '范围：{{min}} — {{max}}',
    unbounded: '不限',
}
