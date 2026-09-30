# <img src="assets/brand/tenonedge/icon-128.png" width="32" alt="TenonEdge"> TenonEdge

基于 .NET 的边缘接入与转发网关，面向独立部署和二次开发。

## 当前状态

项目处于设计阶段，当前包含设计文档、开发准备和品牌资源，不包含已经实现的网关程序，也不代表已完成设备兼容验证或性能测试。

## 设计方案

- [接入、转发与平台联动设计方案 V1.6](docs/tenonedge-design.md)
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

开源版、企业扩展和工控整机交付的边界以设计文档为准。本次不创建企业版工程、授权系统或其他应用代码。

## 后续优先规划

**GW-27 网页受控升级（G1，尚未实现）**：管理员在“系统设置 → 软件升级”中上传离线升级包，或检查可信源并下载；完成预检、二次确认后执行，并查看重启结果与历史。优先验证 Linux 原生安装和整机交付，Docker 网页安装须有受限宿主机执行器，否则继续外部更新。不会自动安装新版本，也不开展平台批量升级。功能、失败恢复与验收见[升级规划](docs/software-upgrade.md)。

该规划补充总设计 G1 清单，不改变功能需求 §10.5、§10.7 中首版采用脚本/镜像升级的范围，也不阻塞当前 T0。

## 开发入口

先按 `AGENTS.md` 阅读总设计、后端工程规范、功能需求、前端实施基线和升级规划，再按照总设计第 12 节的 T0—T5 执行。T0 的工程验收同时参照 `docs/backend-implementation.md` 第 7 节与 `docs/development-readiness.md`。前端于 T3 创建 web/，T0—T2 不提前创建前端工程；GW-27 在 G1 实施。涉及第三方库与平台接口、真实设备能力时，先核实再实现。
