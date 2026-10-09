# 路线草稿 API 与图格式

T3-05 当前实现边界：路线可在后端保存、读取、复制、删除，并显式静态编译为运行计划；保存不编译、不启动采集、不更改运行版本、不入发送队列。`/routes` 页面已接入列表、元数据、画布保存/复制/删除及静态编译；来源与 HTTP/MQTT 输出连接已提供分页选择，通用输出参数和报文组装已有专用表单；映射规则也有保精度分页表，上游字段已有真实分析和分页建议。手工样本预览 API 已接通，画布预览入口、最近消息/现场采样接入与应用/回滚仍在实施。静态编译成功不等于凭证准备、运行激活或外部设备验证成功。验证证据见 [实施状态 §2.125—§2.136](implementation-status.md)。

## API

前缀 `/api/edge-admin/v1`。所有请求使用产品本地会话；写操作还须 CSRF 和配置人员/管理员角色。只读角色可以读取非秘密草稿。

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/routes` | `{ version, routes }`；仅摘要（id/key/name/description/enabled/nodeCount/updatedBy/updatedAt），不是运行状态或“编译有效”声明 |
| GET | `/routes/catalog?kind=input.simulator&afterId=0&limit=100` | 同版本的来源/输出连接摘要，kind 支持 input.modbus/mqtt/http/simulator、output.mqtt/http；最多 100 项，nextAfterId 为空表示末页；不含点表、样本或凭证 |
| GET | `/routes/{routeId}` | `{ version, route }`；含完整图 |
| POST | `/routes` | 新建，返回 `{ version, route }` |
| PUT | `/routes/{routeId}` | 更新，key 不可变；启停也是草稿修改 |
| POST | `/routes/fields` | `{ expectedVersion, graph, nodeId, afterKey?, search?, limit? }`，分析当前未保存图所选节点的上游描述；返回分页共同字段及有界差异/错误，不保存、不执行样本 |
| POST | `/routes/preview` | `{ expectedVersion, graph, sourceId, sampleJson }`，以所选来源解码手工 JSON 并逐路径预览；不采集、不发送、不保存、不入队 |
| POST | `/routes/compile` | `{ expectedVersion }`，静态编译该版本全部启用路线；返回计数、内容哈希或结构化错误，不分配 revision |
| POST | `/routes/{routeId}/copy` | 请求 `{ expectedVersion, key, name }`；新 ID/key，图及引用独立保存，默认停用 |
| DELETE | `/routes/{routeId}?expectedVersion=...` | 删除草稿及引用索引，返回 `{ version }`；不撤回旧投递 |

POST/PUT 请求示例（未选择来源和输出连接，**可以保存但不能应用**）：

```json
{
  "expectedVersion": "读取到的全局草稿版本",
  "key": "meter-route",
  "name": "电表转发",
  "description": "待选择来源与连接",
  "enabled": false,
  "graph": {
    "schemaVersion": 1,
    "nodes": [
      { "id": "in", "type": "input.modbus", "version": 1, "name": "电表", "config": { "sourceIds": [] } },
      { "id": "out", "type": "output.mqtt", "version": 1, "name": "发布", "config": { "connectionId": null, "topic": "tenonedge/{deviceKey}/data", "qos": 1 } }
    ],
    "edges": [{ "source": "in", "target": "out" }],
    "layout": { "nodes": { "in": { "x": 80, "y": 120 }, "out": { "x": 380, "y": 120 } } }
  }
}
```

这是本网关的图定义与独立布局，不保存 React Flow 的内部 node/edge 对象。前端编辑状态的 `definition.nodes[].kind` / `layout.positions` **不能原样提交**，已通过 `canvas/codec.ts` 显式转换。

## 配置形状

`config` 只接受相应节点的已知属性，未知属性（含 host/password/script 等旁路）拒绝保存。下面列的是草稿字段；可空或未完成的字段不代表可执行。编译阶段还须检查值、引用、类型、字段传播和输出格式。

- `input.modbus` / `input.mqtt` / `input.http` / `input.simulator`：`{ sourceIds: number[] }`，最多 500 项，可为空。每项引用已保存来源的 ID，不是设备 ID。
- `transform.map`：`{ rules: [...] }`，最多 1000 项，可为空。规则字段为 `target`、`type`、`sourceField`、`sourcePath`、`constant`、`unit`、`required`、`scale`、`offset`、`decimalPlaces`。语义同运行时固定规则，草稿允许尚未填完；不支持表达式或脚本。
- `output.mqtt`：`connectionId`、`topic`、`qos`、`payload`。
- `output.http`：`connectionId`、`path`、`headers`、`successPath`、`successValue`、`payload`。认证使用连接凭证；Authorization、Proxy-Authorization、Cookie、API Key/token/password 等秘密头不能保存进图，路径也不能保存用户名密码或秘密查询参数。
- `output.tenon`：只记录 `connectionId`。此类型的持久化不代表平台同步/上报或 T4 已实现。
- `payload`：`{ mode, fields? }`，字段组装项为 `target`、`sourceField`、`placeholder`、`constant`、`required`。正式语义仍为 envelope/values/custom，草稿保存不执行组装。

JSON 常量按原文精度存储，不经 double 中转。映射的数值常量也可用十进制字符串配合声明类型（如 integer）表达。**浏览器不得用普通 JSON.parse 再 JSON.stringify 往返大整数/高精度常量**；画布接入时必须采取保精度读取/编辑策略，尤其不能把自定义输出的数字常量改成字符串而改变输出类型。后端与浏览器编辑状态的格式转换已完成保精度单元验证（实施状态 §2.127）；API 与真实浏览器的保存/重读/更新保精度往返已按 §2.129 验证；映射及通用输出专用表单见实施状态 §2.131—§2.134，完整网页验收仍未完成。

## 保存与应用的边界

- 请求体/图文档限制 1 MiB；节点最多 50、连线最多 2450；规则和自定义组装各最多 1000 项。布局只含本图节点，坐标须有限且绝对值不超过 10,000,000。这些是保存防护，不是性能承诺。
- schemaVersion/node version 当前均为 1，未知版本拒绝保存；节点 ID 必须有效且不重复。只接受已知配置字段及有界标量常量。
- 悬空节点、环、重复连线、空来源/空规则、未选连接或引用暂不存在的实体可以作为待修复草稿保存。保存成功**不代表图校验通过**；须另行调用静态编译 API；保存端点仍不以编译成功为前提。
- 全局草稿版本、路线、引用索引与审计在同一短事务提交。版本冲突返回 409，不覆盖其他编辑；不存在返回 404。审计只记 key、版本与复制来源，不记录整个图或常量正文。
- 复制/停用仍保留引用。来源、所属设备和输出连接被任意路线草稿引用时禁止删除；先移除图中引用或删除路线，再重新读取版本操作。该保护与原有运行快照/投递保护并存。
- 图内数值 ID 是本地实体引用；本节不是可跨网关移植的导入/导出格式承诺，跨网关按业务 key 重绑定属后续配置导入/导出任务。

## 静态编译

`POST /routes/compile` 需要配置人员/管理员角色及 CSRF，4 KiB 请求体限制，并共用诊断全局 4 / 每会话 2 的准入。请求版本过期返回 409；图错误返回 200、`compiled: false` 与 `issues`（code/routeKey/nodeId/sourceId/edgeIndex/fieldKey），成功返回 `compiled: true`、sources/paths/units/contentHash。结果只对应响应中的 version，不是当前运行状态，也不保证响应时草稿尚未继续编辑。

- 在同一短事务中读取启用路线及其实际引用的来源、设备、连接和 MQTT 凭证存在标志；离开事务后才编译。未引用草稿不参加编译，全部路线停用得到空计划。
- 四种输入均转换成现有适配器可解析的非秘密配置；同一来源仅生成一个输入单元。Modbus 字段描述来自启用点位（最多 10,000），JSON/模拟来源沿用各自 1000 字段上限。
- 拓扑通过有界计数后才展开。逐路径检查每个映射来源字段在相应上游存在，按顺序生成 `mappingStages`；检查输出连接/类型/启用状态、非秘密头、报文组装引用及现有 MQTT 反馈闭环/共享参数规则。JSON 定位只能做语法检查，实际样本中的存在性留给预览/执行。
- 同一图节点链上的多个来源共用一个输出定义；分叉后重合的不同链仍生成不同目标，绝不合并投递。标识由路线 key 与完整节点链哈希决定，移动布局、修改路线/节点显示名或打乱节点/连线顺序不改变运行计划。
- 快照涉及的图与配置正文先在 SQL 中按 UTF-8 字节总量限制为 16 MiB；生成的运行配置按单元逐步累计保守 16 MiB 预算，再经现有运行快照解析器校验。路径数合法不代表可以无限复制巨大规则配置。这是编译资源边界，不是 CLR 堆或性能承诺。
- 不连接目标、不构造/启动运行单元、不冻结配置版本、不解密凭证、不提交发送记录。MQTT 配置中只携带是否要求凭证的标志，既有凭证记录即使失效也不降级为匿名配置。凭证可用性、目标访问策略、静默/提交/激活仍须走后续应用流程。
- `output.tenon` 目前返回 `output.unavailable`，不以通用 HTTP/MQTT 代替平台适配。平台同名字段 type/unit 一致性和同步仍随 T4 的正式适配执行。

## 上游字段分析边界

字段分析用于填写映射与自定义组装，不要求当前选中节点或下游配置已经可运行。输入节点检查本节点各来源；其他节点沿祖先路径复用来源描述、拓扑校验与连续映射，所选节点自身规则不参与。只提供各上游共同存在且类型一致的字段；单位不同时返回空单位并提示，不推断换算。分页最多 100 字段，afterKey 按 ordinal 顺序，search 匹配字段 key；excludedFields 最多 100 条，超出用 differencesTruncated 明示。响应中的 commonCount 是未按 search 过滤的共同字段总数，upstreamCount 是去除重复来源/末映射后的字段集合数量，不是投递路径数。

角色、CSRF、准入沿用静态编译。版本冲突 409，图/来源/映射语义错误返回 analyzed:false 与 issues，成功也只说明所选局部的字段可分析；下游、其他路线、全局路径累计与输出准备仍须完整编译及应用流程。字段描述不包含样本值。接口/客户端验证见实施状态 §2.133；画布分析面板与表单字段建议见 §2.134。

在当前节点点击“分析 / 返回第一页”，可按字段键搜索和翻页；映射 field 模式与输出自定义 field 模式的输入框提供原生 datalist 建议，仍允许手工输入，当前值不在本页也不被替换。仅保留当前分析的一页结果，不把图存入查询缓存。图文档、所选节点或编辑版本变化后取消旧请求，并同步隐藏旧结果；再次分析仍使用原编辑版本，409 不自动刷新版本或覆盖本地修改。未确认的属性表单先确认或放弃，再发起新分析。该功能只提供描述，不是样本预览，也不证明下游可执行。


## 手工样本预览 API

`POST /routes/preview` 使用当前未保存 graph 与原编辑 expectedVersion，sourceId 须引用该图里的真实来源。样本作为 **sampleJson 字符串** 传入，不先经过 JavaScript 数字解析；整个请求最多 1 MiB，样本自身最多 64 KiB UTF-8、深度 64。角色/CSRF 和全局 4、会话 2 的准入与字段分析一致；操作含 30 秒截止时间。

- MQTT/HTTP：粘贴协议原始 JSON，按已保存来源的 Pointer、固定类型与上游时间规则使用正式解码器。允许保留原始 JSON 供映射的显式 Pointer 使用；未选择的字段不会自动变成输出值。上游时间无效时按正式规则回退接收时间，响应 `invalidUpstreamTime: true`，不改变运行时计数。
- 模拟/Modbus：粘贴已解码字段对象，如 `{"value":9223372036854775807}`，按来源描述的字段类型读取。Modbus uint64 点位可使用完整 uint64 数字或十进制数字字符串。这里不仿真寄存器位宽、字节序、通信故障或模拟器故障比例；这不是现场读取验证。与正式 Modbus/模拟消息相同，不凭手工 JSON 创造 RawJson，因此仅支持 JSON 协议原始正文的 Pointer 规则不会在该预览里假装可用。
- 来源身份、schema 与连接元数据来自同版本草稿快照，样本不能改写 deviceKey/inputKey 或输出地址。手工样本不持久化、不复制到运行时、无凭证解析/网络/队列操作。
- 成功进入路径计算返回 `{ version, previewed:true, invalidUpstreamTime, result, issues:[] }`。**previewed 不等于所有路径成功**：检查 result.totalPaths/succeededPaths、各路径 succeeded/issues。result.source 和各路径 stages 提供字段 type/unit/text/quality/reason，text 是精确值文本；输出 body 是正式组装的 JSON 文本，不是供 JavaScript 隐式转成数字的对象。输出含节点、Topic/相对路径、QoS、非秘密请求头和临时 deliveryId；它不是持久化投递号。
- 图/来源拓扑错误返回 `previewed:false`、result:null 与结构化 issues；routeKey 是本次局部图的内部标签，不是已保存路线 key。样本/图格式错误 400，陈旧版本 409，Kestrel 请求体超限 413，并发超限 429，截止时间 504。客户端不可通过重取版本静默重试覆盖本地编辑。
- 整个响应最多 64 KiB（执行核心为外层响应预留 1 KiB）；result.truncated、各阶段/输出 truncated 明示显示截断，output.bodyBytes 是完整正式报文字节数。显示截断不停止其他路径计算，但后续显示细节可能被省略；正式报文仍受 256 KiB 上限，超限路径失败。截断文本不是完整 JSON，不得用于测试发送。

当前已提供 API、OpenAPI 客户端和真实 Host 验证；尚无画布预览表单。最近消息、现场显式采样仍须分别接入，不能将粘贴样本当作设备能力验证；成功预览也不替代全局路线编译、凭证准备、目标访问校验或应用。
