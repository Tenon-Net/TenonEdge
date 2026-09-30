# <img src="assets/brand/tenonedge/icon-128.png" width="32" alt="TenonEdge"> TenonEdge

基于 .NET 的边缘接入与转发网关，面向独立部署和二次开发。

## 当前状态

项目处于设计阶段。本次初始化仅包含设计文档与开发 Agent 阅读入口，不包含已经实现的网关程序，也不代表已完成设备兼容验证或性能测试。

## 设计方案

- [接入、转发与平台联动设计方案 V1.6](docs/tenonedge-design.md)
- [功能需求](docs/functional-spec.md)
- [开发准备：结论、待确认事项与 T0 验证清单](docs/development-readiness.md)
- [架构评审记录（2026-09-28）](docs/reviews/2026-09-28-architecture-review.md)
- [开发 Agent 阅读要求](AGENTS.md)

## 首版范围

- 接入：Modbus TCP（含 RTU over TCP）、MQTT 订阅、HTTP 拉取。
- 处理：字段选择、重命名、JSON 提取和数值换算。
- 输出：MQTT、HTTP，以及可选的 TenonIoT 平台连接。
- 使用：产品内的可视化连线配置、数据预览和运行诊断。
- 部署：一个 .NET 服务，自带 Web 界面，本地配置和有限发送缓存。只支持 MQTT 的设备需要直连时，可在同一台机器上附带一个 Mosquitto。
- 管理：本地账号与三种角色（管理员、配置人员、只读），首次访问时设置管理员；不依赖 TenonAdmin。
- 界面：React + shadcn/ui + Tailwind + React Flow，需要 Chrome/Edge 111 及以上的浏览器。

LoRa／LoRaWAN 优先接入现有无线网关或网络服务器已经处理好的应用数据，不自研无线协议栈。Node-RED 官方嵌入仅记录为待评估选项，首版不实施。

开源版、企业扩展和工控整机交付的边界以设计文档为准。本次不创建企业版工程、授权系统或其他应用代码。

## 开发入口

先阅读 `AGENTS.md` 和 `docs/tenonedge-design.md`，再按照文档第 12 节的 T0—T5 执行。涉及第三方库与平台接口、真实设备能力时，先核实再实现。
