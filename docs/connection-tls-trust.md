# 连接级 TLS 服务器信任（T3-04，后端子项）

当前交付为管理 API、持久存储及实际 HTTP/MQTT 传输接线。**网页一键信任、CA 文件上传表单及客户端证书认证尚未交付**；不是 T3-04 或 T3 整体完成。服务器信任证据见 [实施状态 §2.254](implementation-status.md#2254-t3-04-连接级-tls-服务器信任后端2026-10-15)。系统默认本地建链的真实 Host 验证见实施状态 §2.256。管理宿主自身的 HTTPS 证书替换仍属于 T5-01。

## 信任规则

- 未配置连接信任：使用本地系统根验证服务器证书；不能因为没有连接信任就跳过校验。
- `pinnedCertificate`：固定管理员核对的整张服务器公钥证书（SHA-256），不是只固定公钥。即使新证书会被系统信任，换证也返回 `certificate.changed`；须重新核对确认。
- `certificateAuthority`：只接受锚定到上传 CA 的有效服务器链。支持根 CA，以及显式上传并作为终点的中间 CA；正常续签无需更新信任。缺失必要中间链仍失败，不回退到其他系统根。
- 两种模式均保留原目标的主机名/IP、有效期、服务器用途、签名和链约束校验。不提供忽略证书错误选项，不修改操作系统证书库。
- 维护者于 2026-10-15 确认：系统默认信任和连接级信任均在本地建链，不下载 AIA 证书，不联网查询 CRL/OCSP；**不宣称支持在线吊销检查**。平台已经给出的 `Revoked` 状态仍拒绝。缺失必要中间证书时连接失败，要求对方正确交付证书链，不能绕过出站规则自动下载。
- 信任同时绑定内部连接 ID 和规范化接收方：HTTP 为含尾斜杠的完整基础地址，MQTT 为 `mqtts://主机:端口`。不同连接或不同接收方不共享它；改地址不会自动把该信任应用到新地址。

## API

以下均位于 `/api/edge-admin/v1`，Cookie 写操作须有 CSRF 请求头；Token 继续遵守 HTTPS、角色与审计规则。

| 方法与路径 | 权限 | 行为 |
|---|---|---|
| `POST /connections/test/http`、`POST /connections/test/mqtt` | 配置人员/管理员 | 既有显式连接测试新增 `certificate`：主题、颁发者、有效期、SHA-256 和有界 DER 公钥证书，不含私钥；失败不放行应用协议或凭证 |
| `GET /connections/{connectionId}/tls-trust` | 配置人员/管理员 | 当前模式、版本、可读取状态与证书元数据；未配置返回 404，不返回证书正文 |
| `PUT /connections/{connectionId}/tls-trust` | 管理员 | 精确核对草稿、接收方、原信任版本与指纹后保存；同事务审计及身份复核 |
| `POST /connections/{connectionId}/tls-trust/clear` | 管理员 | 明确确认后清除；被有效/上一/待提交运行版本引用时拒绝 |

MQTT 测试新增可选 `connectionId`，让无认证/临时认证的已有连接也使用本连接信任；它必须指向已有 MQTT 草稿。与 `credentialConnectionId` 同时提供时必须相同。未保存的新表单使用独立临时 ID，不会误用编号 1 的信任。

保存体：

```json
{
  "expectedDraftVersion": "当前全局草稿版本",
  "recipient": "https://example.invalid:443/base/",
  "mode": "pinnedCertificate",
  "certificateDerBase64": "完整单个 DER 公钥证书的 Base64",
  "confirmedFingerprint": "该证书的 SHA-256 大写十六进制指纹",
  "confirmed": true,
  "expectedVersion": null
}
```

首次保存 `expectedVersion` 为 null，替换时必须为刚读取的信任版本。证书最多 32 KiB，整个请求最多 64 KiB；拒绝 PFX、私钥容器、多个/尾随证书、无效模式、非 CA 证书冒充 CA，以及尚未生效/已过期的证书。当前 API 接受 **DER Base64**；PEM 文件解析及上传交互将随网页交付，不用手写 JSON 冒充网页验收。

清除体为 `expectedDraftVersion`、当前草稿 `recipient`、`expectedVersion` 和 `confirmed:true`。版本冲突返回 409，重新读取后再核对；不重放确认请求。草稿不存在、非 TLS 连接、接收方不一致或证书无效时均不保存。

## 持久化与运行边界

- 配置库迁移 31 新增 `connection_trust`。证书由同一 Data Protection 密钥环保护，保护用途同时绑定连接 ID、接收方与模式；密文损坏保留“已配置但不可用”，握手拒绝，不退回默认信任。
- 只保存当前信任；不把证书或信任版本写入运行快照、发送报文、接收方指纹或配置导出。信任变更不改变草稿版本，不自动应用配置，不重写 queue.db。
- 每次操作按当前绑定读取分离快照，读取后关闭数据库上下文。HTTP 在原共享串行租约内替换 handler；MQTT 在原连接拥有者的发送/维护路径重连。版本变化后的下一次操作不能沿用旧验证的连接；不撤销已经开始的请求或确认。
- 输出的凭证变更检测同时观察信任版本，接收方指纹不变；沿用原发送者的重新探测机制，不建立第二个发送者。
- MQTTnet 5.2 的选项不能禁用 AIA 下载，因此所有 MQTT TLS 路径使用自有、受原连接器约束的 BCL `SslStream` 通道；HTTP 共用 handler 同样显式指定本地链策略。MQTT 编解码、3.1.1、帧限长、接收预算、确认和会话管理保持原实现，没有复制参考仓库源码。
- 正常删除无引用且无积存的连接时同步删除信任；配置导入不携带信任，内部旧 ID 仍保留占用保护。

## 验证边界

已执行真实 TCP/TLS 双协议拒绝/接受矩阵、同一会话的信任轮换与复用阻断、损坏信任拒绝、HTTP 现场预览/完整样本，以及实际 Host 注册到 Mosquitto 的连接测试、QoS 1 测试发布和诊断保留消息订阅。API 授权和重启持久化使用真实 SQLite 与 TestServer；出站 TLS 使用真实网络，但不代表管理端真实 TLS Token 验证或浏览器验证。

网页、客户端证书、全协议压力及阶段无 JSON 配置闭环仍未完成；不以本子项替代 T3-11。
