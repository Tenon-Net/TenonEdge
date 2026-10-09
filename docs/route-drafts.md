# 路线草稿 API 与图格式

T3-05 当前实现边界：路线可在后端保存、读取、复制和删除；保存不编译、不启动采集、不更改运行版本、不入发送队列。画布尚未接入这些端点，完整图编译、样本预览与应用/回滚仍在实施。验证证据见 [实施状态 §2.125](implementation-status.md)。

## API

前缀 `/api/edge-admin/v1`。所有请求使用产品本地会话；写操作还须 CSRF 和配置人员/管理员角色。只读角色可以读取非秘密草稿。

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/routes` | `{ version, routes }`；仅摘要（id/key/name/description/enabled/nodeCount/updatedBy/updatedAt），不是运行状态或“编译有效”声明 |
| GET | `/routes/{routeId}` | `{ version, route }`；含完整图 |
| POST | `/routes` | 新建，返回 `{ version, route }` |
| PUT | `/routes/{routeId}` | 更新，key 不可变；启停也是草稿修改 |
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
- `output.http`：`connectionId`、`path`、`headers`、`successPath`、`successValue`、`payload`。认证使用连接凭证；Authorization、Proxy-Authorization、Cookie、API Key/token/password 等秘密头不能保存进图。
- `output.tenon`：只记录 `connectionId`。此类型的持久化不代表平台同步/上报或 T4 已实现。
- `payload`：`{ mode, fields? }`，字段组装项为 `target`、`sourceField`、`placeholder`、`constant`、`required`。正式语义仍为 envelope/values/custom，草稿保存不执行组装。

JSON 常量按原文精度存储，不经 double 中转。映射的数值常量也可用十进制字符串配合声明类型（如 integer）表达。**浏览器不得用普通 JSON.parse 再 JSON.stringify 往返大整数/高精度常量**；画布接入时必须采取保精度读取/编辑策略，尤其不能把自定义输出的数字常量改成字符串而改变输出类型。当前仅完成后端保精度验证。

## 保存与应用的边界

- 请求体/图文档限制 1 MiB；节点最多 50、连线最多 2450；规则和自定义组装各最多 1000 项。布局只含本图节点，坐标须有限且绝对值不超过 10,000,000。这些是保存防护，不是性能承诺。
- schemaVersion/node version 当前均为 1，未知版本拒绝保存；节点 ID 必须有效且不重复。只接受已知配置字段及有界标量常量。
- 悬空节点、环、重复连线、空来源/空规则、未选连接或引用暂不存在的实体可以作为待修复草稿保存。保存成功**不代表图校验通过**；已有纯拓扑编译器尚未绑定到此 API。
- 全局草稿版本、路线、引用索引与审计在同一短事务提交。版本冲突返回 409，不覆盖其他编辑；不存在返回 404。审计只记 key、版本与复制来源，不记录整个图或常量正文。
- 复制/停用仍保留引用。来源、所属设备和输出连接被任意路线草稿引用时禁止删除；先移除图中引用或删除路线，再重新读取版本操作。该保护与原有运行快照/投递保护并存。
- 图内数值 ID 是本地实体引用；本节不是可跨网关移植的导入/导出格式承诺，跨网关按业务 key 重绑定属后续配置导入/导出任务。
