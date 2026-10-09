# 路线草稿 API 与图格式

T3-05 当前实现边界：路线可在后端保存、读取、复制、删除，并显式静态编译为运行计划；保存不编译、不启动采集、不更改运行版本、不入发送队列。`/routes` 页面已接入列表、元数据、画布保存/复制/删除及静态编译；节点配置暂采用保精度 JSON 编辑入口，专用属性表单、样本预览与应用/回滚仍在实施。静态编译成功不等于凭证准备、运行激活或外部设备验证成功。验证证据见 [实施状态 §2.125/126](implementation-status.md)。

## API

前缀 `/api/edge-admin/v1`。所有请求使用产品本地会话；写操作还须 CSRF 和配置人员/管理员角色。只读角色可以读取非秘密草稿。

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/routes` | `{ version, routes }`；仅摘要（id/key/name/description/enabled/nodeCount/updatedBy/updatedAt），不是运行状态或“编译有效”声明 |
| GET | `/routes/{routeId}` | `{ version, route }`；含完整图 |
| POST | `/routes` | 新建，返回 `{ version, route }` |
| PUT | `/routes/{routeId}` | 更新，key 不可变；启停也是草稿修改 |
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

这是本网关的图定义与独立布局，不保存 React Flow 的内部 node/edge 对象。前端原有本地画布原型的 `definition.nodes[].kind` / `layout.positions` **不能原样提交**，后续接入须显式转换。

## 配置形状

`config` 只接受相应节点的已知属性，未知属性（含 host/password/script 等旁路）拒绝保存。下面列的是草稿字段；可空或未完成的字段不代表可执行。编译阶段还须检查值、引用、类型、字段传播和输出格式。

- `input.modbus` / `input.mqtt` / `input.http` / `input.simulator`：`{ sourceIds: number[] }`，最多 500 项，可为空。每项引用已保存来源的 ID，不是设备 ID。
- `transform.map`：`{ rules: [...] }`，最多 1000 项，可为空。规则字段为 `target`、`type`、`sourceField`、`sourcePath`、`constant`、`unit`、`required`、`scale`、`offset`、`decimalPlaces`。语义同运行时固定规则，草稿允许尚未填完；不支持表达式或脚本。
- `output.mqtt`：`connectionId`、`topic`、`qos`、`payload`。
- `output.http`：`connectionId`、`path`、`headers`、`successPath`、`successValue`、`payload`。认证使用连接凭证；Authorization、Proxy-Authorization、Cookie、API Key/token/password 等秘密头不能保存进图，路径也不能保存用户名密码或秘密查询参数。
- `output.tenon`：只记录 `connectionId`。此类型的持久化不代表平台同步/上报或 T4 已实现。
- `payload`：`{ mode, fields? }`，字段组装项为 `target`、`sourceField`、`placeholder`、`constant`、`required`。正式语义仍为 envelope/values/custom，草稿保存不执行组装。

JSON 常量按原文精度存储，不经 double 中转。映射的数值常量也可用十进制字符串配合声明类型（如 integer）表达。**浏览器不得用普通 JSON.parse 再 JSON.stringify 往返大整数/高精度常量**；画布接入时必须采取保精度读取/编辑策略，尤其不能把自定义输出的数字常量改成字符串而改变输出类型。后端与浏览器编辑状态的格式转换已完成保精度单元验证（实施状态 §2.127）；API 与真实浏览器的保存/重读/更新保精度往返已按 §2.129 验证；专用表单与完整网页验收仍未完成。

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
