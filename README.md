# <img src="assets/brand/tenonedge/icon-128.png" width="32" alt="TenonEdge"> TenonEdge

基于 .NET 的边缘接入与转发网关，面向独立部署和二次开发。

## 当前状态

项目按设计分阶段实施。实际交付范围、运行方式与验证证据统一见 [实施状态](docs/implementation-status.md)；任务清单和首版范围不代表所有功能已实现，也不代表已完成设备兼容验证或性能测试。

## 设计方案

- [接入、转发与平台联动设计方案 V1.6](docs/tenonedge-design.md)
- [开发阶段与任务清单：T0—T5、依赖与验收](docs/development-plan.md)
- [后端工程实施规范：目录、Minimal API、生命周期与测试](docs/backend-implementation.md)
- [前端实施基线：shadcn-admin 裁剪复用](docs/frontend-design.md)
- [功能需求](docs/functional-spec.md)
- [网页受控升级规划：GW-27 / G1 优先项](docs/software-upgrade.md)
- [开发准备：结论、待确认事项与 T0 验证清单](docs/development-readiness.md)
- [架构评审记录（2026-09-28）](docs/reviews/2026-09-28-architecture-review.md)
- [开发 Agent 阅读要求](AGENTS.md)

后端工程细节由[后端工程实施规范](docs/backend-implementation.md)补充，保留四工程依赖方向和既有运行机制；其中第 7 节与开发准备清单共同用于 T0 验收，不新增进入 T0 的前置门槛。

前端选型以已确认的[前端实施基线](docs/frontend-design.md)为准；它替代 V1.6 中“新建精简前端、沿用 TenonAdmin 工具链”和 React Router 的旧约定。后端架构、功能范围、安全要求和 T0—T5 顺序不变。

## 首版范围

- 接入：Modbus TCP（含 RTU over TCP）、MQTT 订阅、HTTP 拉取。
- 处理：字段选择、重命名、JSON 提取和数值换算。
- 输出：MQTT、HTTP，以及可选的 TenonIoT 平台连接。
- 使用：产品内的可视化连线配置、数据预览和运行诊断。
- 部署：一个 .NET 服务，自带 Web 界面，本地配置和有限发送缓存。只支持 MQTT 的设备需要直连时，可在同一台机器上附带一个 Mosquitto。
- 后端：.NET 10、ASP.NET Core 分组 Minimal API；Host、Runtime、Adapters、Abstractions 四工程；SQLite 配置库与发送缓存分离，具体依赖仍须 T0 验证。
- 管理：本地账号与三种角色（管理员、配置人员、只读），首次访问时设置管理员；不依赖 TenonAdmin。
- 界面：裁剪复用 shadcn-admin，采用 React 19、shadcn/ui、Tailwind CSS 4、TanStack Router 与 TanStack Query；路线画布使用 React Flow。需要 Chrome/Edge 111 及以上的浏览器，其他浏览器边界见设计文档；实际兼容性在 T3 验证。

LoRa／LoRaWAN 优先接入现有无线网关或网络服务器已经处理好的应用数据，不自研无线协议栈。Node-RED 官方嵌入仅记录为待评估选项，首版不实施。

开源版、企业扩展和工控整机交付的边界以设计文档为准。不提前创建企业版工程或授权系统。

## 开发阶段与任务

[任务清单 PLAN-01](docs/development-plan.md)将首版拆为 54 个任务包，每项明确依赖、主要内容、交付物和验收；网页升级另列 7 项，仍属于 G1。实际进度见实施状态，任务数量不是工时承诺。

| 阶段 | 主要目标 | 任务数 |
|---|---|---|
| T0 | 工程、存储身份、认证、测试 CI 与依赖验证 | 13 |
| T1 | 模拟输入经持久化缓存到真实 MQTT 输出 | 8 |
| T2 | Modbus、MQTT 输入、HTTP、共享连接与映射 | 8 |
| T3 | 真实配置页面、点表、画布、应用与故障操作 | 11 |
| T4 | TenonIoT 契约、同步、上报与条件联调 | 6 |
| T5 | 备份恢复、部署、诊断、手动升级与首版交付 | 8 |

首次编码的默认授权范围为 **T0-A：T0-01—T0-03**，交付可构建的最小后端、真实测试与 CI、健康检查和 OpenAPI；验收后再进入 T0-B（存储、身份和认证）及 T0-C（契约、协议验证与独立发布）。任务证据在 T0-01 建立的 `docs/implementation-status.md` 统一维护，不把最小原型通过当作完整功能通过。

## 应用数据样例

[Modbus 后端接入与软件验证](docs/modbus-integration.md)：运行计划配置、TCP/RTU over TCP 到 HTTP 的产品 Host 软件链路；尚无配置网页，不代表实物兼容验收。

[已解码 LoRa 应用数据接入](docs/lora-integration.md)：提供两设备 MQTT 种子配置、人工报文和真实 Broker/Host 软件测试。不是无线实测或厂商兼容声明；不安装网络服务器或新增无线协议栈。

## 后续优先规划

**GW-27 网页受控升级（G1，尚未实现）**：管理员在“系统设置 → 软件升级”中上传离线升级包，或检查可信源并下载；完成预检、二次确认后执行，并查看重启结果与历史。优先验证 Linux 原生安装和整机交付，Docker 网页安装须有受限宿主机执行器，否则继续外部更新。不会自动安装新版本，也不开展平台批量升级。功能、失败恢复与验收见[升级规划](docs/software-upgrade.md)，实施拆分见[任务清单第 9 节](docs/development-plan.md#9-g1-优先任务gw-27-网页受控升级)。

该规划补充总设计 G1 清单，不改变功能需求 §10.5、§10.7 中首版采用脚本/镜像升级的范围，也不阻塞当前 T0。

## 开发入口

先按 `AGENTS.md` 阅读总设计、工程规范、功能需求、前端基线、升级规划和开发准备，再按 `docs/development-plan.md` 中获授权的任务 ID 执行。任务验收同时使用原 V/FS/AT 和 BE/FE 标准；T0-A 结束后提供实际证据，不自动执行后续批次。前端仍在 T3，网页升级仍在 G1；本次任务拆分不批准其他待确认决策。涉及第三方库与平台接口、真实设备能力时，先核实再实现。

### 前端开发入口（T3 实施中）

[Web 工程说明](web/README.md)提供固定 npm 安装、构建及 .NET 同源托管命令。当前已接入真实首次设置、登录、退出和密码修改；连接、设备及 Modbus、MQTT、HTTP、模拟来源草稿已保存到网关配置库，支持在页面内从 JSON 样本生成字段描述（样本不持久化）、批量复制停用的设备草稿，以及[设备点表 CSV](docs/point-csv.md) 导入导出。**画布仍是本地原型，编译、应用及回滚尚未完成，不能通过网页独立配置并启动采集路线**。浏览器验证范围和剩余项见实施状态 §2.100—§2.110。

### 出站访问部署参数

运行时默认仅允许非本机私网目标；公网需 CIDR 与端口白名单，本机服务需端口例外。MQTT 输出在每次建立 TCP 连接时检查解析后的实际 IP，不使用系统代理。例如主机管理员可配置：

```json
{
  "TenonEdge": {
    "Outbound": {
      "LocalPorts": [1883],
      "Networks": [{ "Cidr": "203.0.113.0/24", "Port": 8883 }]
    }
  }
}
```

示例公网网段仅为文档示例，部署时替换为实际授权范围。没有默认放行任意公网或所有本机端口；管理 HTTP/HTTPS 端口不可被例外放行。链路本地、云元数据、未指定及组播地址不会因宽泛 CIDR 白名单而放行。环境变量可用 `TenonEdge__Outbound__LocalPorts__0=1883` 等标准 .NET 配置形式。部署参数由主机管理员管理。管理员也可通过 `PUT /api/edge-admin/v1/system/outbound-policy` 保存审计化规则（需会话、CSRF 及当前 `expectedVersion`）；所有已登录角色可 GET 查看持久规则及版本。部署规则与持久规则取并集，API 不能撤销部署授权；变更在下一次连接检查时生效，不强制断开现有连接。管理网页尚未实现，测试证据见实施状态。
