# TenonEdge Web（T3 实施中）

真实首次设置、会话登录、退出与密码修改，以及连接/设备/来源草稿、点表 CSV 和 Modbus/MQTT/HTTP 连接测试、Modbus 组/设备真实读取与字节序采用、HTTP 来源/设备真实采样、MQTT 单条抓取与保留消息标记、显式样本推断；**尚不能通过网页独立配置并启动采集路线**。当前交付范围及验证证据见[项目说明](../README.md)与[实施状态](../docs/implementation-status.md)。
`/prototypes/points` 提供仅页面内存的点表交互原型（编辑、TSV 粘贴/覆盖、校验、批量修改和虚拟化），不保存到网关、不读取设备。离开页面内容会丢失。`/prototypes/canvas` 仍是本地画布交互原型。`/routes` 则已接入真实路线草稿、静态编译和样本预览：手工 JSON、明确选中的完整运行缓存记录，或明确确认的 HTTP/Modbus 一次读取或 MQTT 单条抓取，展示映射字段与将要发送的报文但不访问输出、不入队。应用/回滚尚未完成，不能通过网页启动采集路线。

## 环境与命令

- Node.js 22（实际验证 22.23.2），npm **11.21.0**；可用 `npx --yes npm@11.21.0 <command>` 运行固定版本，不必修改系统 npm。
- `npm ci`：按唯一锁文件安装。
- `npm run build`：类型检查、生产依赖许可文本和静态构建，输出 `dist/`。
- `npm test`、`npm run lint`、`npm run format:check`：Vitest、oxlint、Prettier。
- 在仓库根运行 `dotnet build TenonEdge.sln -c Release --no-restore`，再回到 web 执行 `npm run test:e2e`。
  Playwright 启动真实 .NET Production Host，使用独立临时数据目录及 18082 端口，退出时清理；先执行 `npx playwright install chromium`。
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
