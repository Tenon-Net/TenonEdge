# TenonEdge Web（T3 实施中）

真实首次设置、会话登录、退出与密码修改，以及连接/设备/来源草稿、点表 CSV 和 Modbus/MQTT/HTTP 连接测试、Modbus 组/设备真实读取与字节序采用、HTTP 来源/设备真实采样、MQTT 单条抓取与保留消息标记、显式样本推断；**已可在 `/configuration` 预检并确认应用已保存草稿；完整无代码配置与故障恢复流程仍待验收**。当前交付范围及验证证据见[项目说明](../README.md)与[实施状态](../docs/implementation-status.md)。
`/prototypes/points` 提供仅页面内存的点表交互原型（编辑、TSV 粘贴/覆盖、校验、批量修改和虚拟化），不保存到网关、不读取设备。离开页面内容会丢失。`/prototypes/canvas` 仍是本地画布交互原型。`/routes` 则已接入真实路线草稿、静态编译和样本预览：手工 JSON、明确选中的完整运行缓存记录，或明确确认的 HTTP/Modbus 一次读取或 MQTT 单条抓取，展示映射字段与将要发送的报文但不访问输出、不入队。页头显示全局未应用修改，`/configuration` 提供精确字段差异、应用确认/说明、真实后台操作进度和放弃草稿；请求不会自动重试，202 不等于配置已生效。持久历史可分页查看非秘密草稿和运行计划，比较两个版本或版本与当前草稿，并预检/确认回滚；回滚目标来自已返回的清单，浏览其他版本不会改写确认目标。旧快照/未生效版本不可正常回滚，有未应用修改或数量未知时也不可回滚。配置页还显示持久有效版本和运行失败原因，允许明确确认重试当前有效快照；不应用草稿、不生成版本，响应丢失只查询操作记录而不重放。损坏快照明确拒绝重试，不按空配置处理。专用损坏快照恢复尚未实现；最低浏览器和完整中断矩阵仍待验收。

主页已接入真实运行总览、系统信息和首次设置来源；`/diagnostics` 展示运行单元/读取状态、真实内存趋势、持久运行事件筛选与分页。登录后仍持续提示 HTTP 明文风险。`/runtime/devices` 提供设备状态分类和逐来源当前值（每页100项、每2秒刷新）；64位数按原文显示，过期灰显并保留最后成功时间，隐私未通过/超限/淘汰明确标记，重启或版本切换后重新采样。`/runtime/outputs` 已提供只读输出状态、实际待发/失败/隔离队列、最近结果、趋势、配额时长上界和持久分类计数；磁盘放大系数尚未校准，不将配额上界冒充完整容量。`/deliveries` 提供失败/隔离记录按目标、设备、原因和首次入队时间筛选及键集分页；管理员与配置人员显式查看最多 64 KiB 正文，只读角色不能查看，截断和非 UTF-8 明示。仅查看不重发，记录可能在翻页期间过期或被操作。完整调试/重试/清理/导出/迁移仍在补齐；不将运行单元数作为设备数。验证见实施状态 §2.193—§2.196。

## 环境与命令

- Node.js 22（实际验证 22.23.2），npm **11.21.0**；可用 `npx --yes npm@11.21.0 <command>` 运行固定版本，不必修改系统 npm。
- `npm ci`：按唯一锁文件安装。
- `npm run build`：类型检查、生产依赖许可文本和静态构建，输出 `dist/`。
- `npm test`、`npm run lint`、`npm run format:check`：Vitest、oxlint、Prettier。
- 在仓库根运行 `dotnet build TenonEdge.sln -c Release --no-restore`，再回到 web 执行 `npm run test:e2e`。
  Playwright 启动真实 .NET Production Host，使用独立临时数据目录及 18082 端口，退出时清理；正常重启场景另用系统分配的回环端口，在同端口重启独立临时 Host，并完整关闭/新建浏览器后重新登录。先执行 `npx playwright install chromium`。
- `TENONEDGE_TEST_URL=http://<本机网卡IP>:18082 npm run test:e2e`：经非 localhost 的 HTTP 运行相同场景。
  当前 Playwright 浏览器不是 Chrome/Edge 111 或 Safari 16.4，不能替代最低版本验收。

## 类型生成

用独立临时数据目录启动 Development Host（HTTP 127.0.0.1:18081，HTTPS 端口 0），执行 `npm run api:generate`。
脚本只读真实 `/api/edge-admin/v1/openapi/v1.json` 并保存 `openapi.json`，openapi-typescript 据此生成 `src/api/schema.d.ts`。
可设置 `TENONEDGE_API_URL`。不手写接口结构，不在脚本中写生产会话或密码。新增后端契约时重新生成并审查 diff。

## 同源部署

构建后 .NET Host 通过标准 `--webroot=<web/dist 的绝对路径>` 提供静态文件；HTTP/HTTPS 地址、端口与数据目录仍按后端部署参数。
只运行 .NET，不需要 Node 服务。正式发布集成/安装包属于后续任务，当前不会在 dotnet build 中静默联网安装 npm 包。
`npm run dev` 仅为开发便利，Vite 默认监听回环，将 `/api` 代理至本机测试 Host，**不是生产部署**。

构建目标显式设置 Chrome/Edge 111、Safari 16.4、Firefox 128；最低浏览器和离线现场验收仍待执行。
账户状态和防 CSRF 令牌只保留内存；会话 Cookie 由 .NET 管理，密码不写本地存储；所有写操作禁止自动重试。

来源、实际裁剪和许可见 [THIRD_PARTY.md](THIRD_PARTY.md)。验证证据统一见 `docs/implementation-status.md`。
