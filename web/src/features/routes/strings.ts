export const routeStrings = {
    title: '路线草稿',
    notice: '保存、复制、删除和启停仅修改草稿，不改变运行配置、不启动采集或发送。应用与回滚尚未开放。',
    new: '新建路线',
    open: '打开',
    save: '保存路线草稿',
    remove: '删除路线草稿',
    copy: '复制已保存路线',
    key: '路线标识',
    name: '路线名称',
    description: '路线说明',
    enabled: '参与静态编译（草稿启用）',
    copyKey: '副本标识',
    copyName: '副本名称',
    saved: '路线草稿已保存，未应用。',
    copied: '已创建停用的独立副本，未应用。',
    deleted: '路线草稿已删除；旧运行配置及投递未撤回。',
    discard: '放弃当前未保存的路线修改？',
    confirmDelete:
        '删除当前已保存路线？此操作不撤回已发送数据，也不改变运行配置。',
    conflict:
        '草稿版本已变化。本地修改仍保留；重新查询列表不会自动覆盖编辑，请重新打开路线后核对修改。',
    compile: '静态编译已保存的启用路线',
    compileNotice:
        '编译仅核对响应中的草稿版本，不验证凭证、设备在线或运行激活；不会联网或入队。未保存修改不参与。',
    compiled:
        '静态编译通过：版本 {{version}}，{{sources}} 个来源，{{paths}} 条路径，{{units}} 个单元。未应用。',
    invalid: '静态编译未通过（版本 {{version}}）',
    empty: '尚无路线草稿。',
    dirty: '有未保存的路线修改',
    canvas: '路线画布',
    canvasNotice:
        '编辑后须点击“保存路线草稿”。节点来源、连接与规则尚采用临时配置编辑入口；保存成功不代表可运行。',
    config: '节点配置 JSON（临时编辑入口）',
    configAccept: '确认节点配置',
    configNotice:
        '保留数字原文精度。不要填写密码、令牌或脚本；只能使用路线草稿文档规定的配置字段。专用属性表单仍在实施。',
    configPending: '节点配置尚未确认；请先确认或放弃，再切换节点或保存路线。',
    configDiscard: '放弃节点配置修改',
    configError: '配置 JSON、图大小或格式无效，未改变本地草稿。',
    readOnly: '只读账号：可以查看草稿，不能保存、复制、删除或编译。',
}
