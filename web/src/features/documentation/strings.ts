export const documentationStrings = {
    title: 'API 文档',
    notice: '从当前宿主实时读取 OpenAPI，不是构建时样本。所有已登录角色均可查看，调用仍受各接口的权限和 HTTPS/CSRF 约束。此页只读，不提供自动发送请求；不加载外部脚本，也不解析外部引用。',
    refresh: '重新读取 API 文档',
    search: '筛选方法、路径、说明或 operationId',
    count: '匹配 {{shown}} / {{total}} 个操作',
    empty: '没有匹配的操作。',
    loading: '正在读取当前 API 文档…',
    failure: '本次文档读取失败；未将旧文档作为当前结果。可重新读取。',
    complete: '完整 OpenAPI 文档（含模型和引用定义）',
    raw: '打开当前宿主原始 OpenAPI JSON',
}
