# TenonEdge Web（T3 实施中）

真实首次设置、会话登录、退出与密码修改，以及连接/设备/来源草稿、点表 CSV 和 Modbus/MQTT/HTTP 连接测试、Modbus 组/设备真实读取与字节序采用、HTTP 来源/设备真实采样、MQTT 单条抓取与保留消息标记、显式样本推断；**已可在 `/configuration` 预检并确认应用已保存草稿；完整无代码配置与故障恢复流程仍待验收**。当前交付范围及验证证据见[项目说明](../README.md)与[实施状态](../docs/implementation-status.md)。
界面布局、侧栏、页头、主题及公共表单/列表按固定提交的 shadcn-admin 裁剪复用，来源与改造见 [THIRD_PARTY.md](THIRD_PARTY.md)。产品导航不展示独立交互原型或开发进度说明。

`/prototypes/points` 提供仅页面内存的点表交互原型（编辑、TSV 粘贴/覆盖、校验、批量修改和虚拟化），不保存到网关、不读取设备。离开页面内容会丢失。`/prototypes/canvas` 仍是本地画布交互原型。`/routes` 则已接入真实路线草稿、静态编译和样本预览：手工 JSON、明确选中的完整运行缓存记录，或明确确认的 HTTP/Modbus 一次读取或 MQTT 单条抓取，展示映射字段与将要发送的报文但不访问输出、不入队。页头显示全局未应用修改，`/configuration` 提供精确字段差异、应用确认/说明、真实后台操作进度和放弃草稿；请求不会自动重试，202 不等于配置已生效。持久历史可分页查看非秘密草稿和运行计划，比较两个版本或版本与当前草稿，并预检/确认回滚；回滚目标来自已返回的清单，浏览其他版本不会改写确认目标。旧快照/未生效版本不可正常回滚，有未应用修改或数量未知时也不可回滚。配置页还显示持久有效版本和运行失败原因，允许明确确认重试当前有效快照；不应用草稿、不生成版本，响应丢失只查询操作记录而不重放。损坏快照明确拒绝重试，不按空配置处理。管理员受限恢复已接入完整历史预检、两项确认及新版本审计；持久 COMMIT 前后强杀/正常放行软件证据见实施状态 §2.238—§2.239。最低实际浏览器和 T3 整体验收仍未完成。

主页已接入真实运行总览、系统信息和首次设置来源；`/diagnostics` 展示运行单元/读取状态、真实内存趋势、持久运行事件筛选与分页。登录后仍持续提示 HTTP 明文风险。`/runtime/devices` 提供设备状态分类和逐来源当前值（每页100项、每2秒刷新）；64位数按原文显示，过期灰显并保留最后成功时间，隐私未通过/超限/淘汰明确标记，重启或版本切换后重新采样。`/runtime/outputs` 已提供输出状态、实际待发/失败/隔离队列、最近结果、趋势、配额时长上界和持久分类计数；磁盘估算仅在环境及记录范围匹配已校准档案时提供，否则明确说明未知原因；经验模型不是安全上界，也不将配额上界冒充完整容量（§2.303）。`/deliveries` 提供失败/隔离记录按目标、设备、原因和首次入队时间筛选及键集分页；管理员与配置人员显式查看最多 64 KiB 正文，只读角色不能查看，截断和非 UTF-8 明示。该正文面板还显示上次记录的 HTTP 对方错误摘要：完整有界响应经过本次凭证回显检查后，最多展示1024 UTF-8字节；隐藏/超限/不可用明示，不执行HTML，不进入列表、日志或JSON Lines导出，旧记录缺失摘要不表示空响应（§2.218）。仅查看不重发，记录可能在翻页期间过期或被操作。目标详情另提供管理员二次确认暂停／恢复，持久控制版本冲突或响应丢失须重新读取与确认；配置人员／管理员可绑定运行版本和当前退避窗口立即重试，不解除暂停、不重新生成报文、不自动重放。失败记录支持单条勾选及按已应用筛选准备最多 1000 条，核对冻结清单并确认后原样移回队尾；可能重复，保留编号，不解除暂停，冲突或响应丢失后只能重新读取并选择。管理员可按勾选或已应用筛选确认清理失败/隔离记录，绑定区域、编号、seq、目标及迁移代次，永久删除与人工丢弃计数同事务提交，不影响确认后新增记录，响应丢失不自动重放。待发清空及完整导出续见实施状态 §2.201—§2.204。管理员现可从目标详情进入 `/deliveries/migrate`，跨待发/失败/隔离区勾选最多1000条，与另一个已生效HTTP/MQTT目标并排核对冻结原身份、配额及同一所有者声明后确认；准备可恢复/取消，响应丢失和刷新只按操作编号查询，不自动重放，不解除暂停。形式 A 换址应用、持久意图及重新审批页面亦已接入，真实 HTTP/MQTT 强杀与正常放行的原子迁移/审计证据见 §2.211—§2.217；不等同实物断电或 T3 整体验收，不将运行单元数作为设备数。

管理员 `/users` 页面使用真实本地账号 API：创建与重置临时密码、编辑角色/显示名/启停、解锁、强制下线、删除及分页信息；现有账号操作须核对并二次确认，冲突/丢回复先清除表单并重新读取，不自动重放。临时密码用户必须改密；页头显示本会话的上次登录时间/IP，其他会话的新登录不会覆盖。`/tokens` 已接入 HTTPS 一次性创建、元数据分页和确认吊销；停用/删除用户时列出尚未吊销令牌，须明确选择保留或一并吊销，绑定清单版本。`/audit` 提供管理员审计筛选/分页和整个已应用范围的 CSV 导出，固定范围、明确确认、后台生成并持久审计、一次原生下载及丢回复后只读恢复；说明见[审计导出](../docs/audit-export.md)。`/system` 已提供全角色系统信息、页头/标签页已有网关名称和管理员出站规则编辑；分开展示只读部署/可编辑持久规则，显式确认、原版本绑定、冲突/丢回复只读核对。管理员还可确认修改/清空网关显示名称，绑定开始编辑时的原名称，名称和审计同事务；保存后刷新页头/标签页，不改变NodeId、草稿或运行版本。冲突/丢回复不重放，读取失败禁止编辑。连接页成功保存三协议草稿后会仅解析/检查目标，管理员可核对精确公网IP与端口后确认追加授权，原规则/本机例外保留，不自动授权/建连/应用；冲突或丢回复须重新检查，不重放。配置人员提示联系管理员，只读不能解析。说明见[系统设置](../docs/system-settings.md)，系统管理剩余项未完成，验证证据见实施状态 §2.219、§2.221—§2.226。

HTTP / MQTT 的[客户端证书表单](../docs/client-certificates.md)支持有界 PKCS#12/PEM 上传、公开身份与动态积存核对、账户声明和确认保存；材料只留当前表单，不进 Query 操作参数或本地持久存储。取消、切换、提交及离开后释放材料，冲突或丢回复后须显式读取状态，不重放保存。临时连接测试支持证书但不保存；服务器信任仍单独核对。证据与边界见实施状态 §2.262。

密码、Token 和 API Key 也已接入[公开身份与动态积存核对](../docs/connection-credentials.md)：核对时不发送秘密，有积存且账户相同/不可见时无默认声明，最终保存仍由原写入端复核。冲突或丢回复清除秘密并要求重新读取，不自动重放；证据见实施状态 §2.263。

## 环境与命令

- Node.js 22（实际验证 22.23.2），npm **11.21.0**；可用 `npx --yes npm@11.21.0 <command>` 运行固定版本，不必修改系统 npm。
- `npm ci`：按唯一锁文件安装。
- `npm run build`：类型检查、生产依赖许可文本和静态构建，输出 `dist/`。
- `npm test`、`npm run lint`、`npm run format:check`：Vitest、oxlint、Prettier。
- 在仓库根运行 `dotnet build TenonEdge.sln -c Release --no-restore`，再回到 web 执行 `npm run test:e2e`。
  Playwright 启动真实 .NET Production Host，使用独立临时数据目录及 18082 端口，退出时清理；正常重启场景另用系统分配的端口（多数仍为回环；客户端证书和凭证核对场景跟随 `TENONEDGE_TEST_URL` 的主机地址），在同端口重启独立临时 Host，并完整关闭/新建浏览器后重新登录。先执行 `npx playwright install chromium`。
- HTTPS Token 浏览器用例还需 Linux `openssl` 与 `libnss3-tools` 的 `certutil`（可用 `TENONEDGE_CERTUTIL` 指向在临时目录解包的工具）。用例只信任已与自有 Host 控制台指纹核对的公开证书，在隔离 HOME 中建立 NSS 信任库；不修改开发机信任库、不关闭证书校验，不保留令牌响应的浏览器 trace。
- `TENONEDGE_TEST_URL=http://<本机网卡IP>:18082 npm run test:e2e`：经非 localhost 的 HTTP 运行相同场景。
  当前 Playwright 浏览器不是 Chrome/Edge 111 或 Safari 16.4，不能替代最低版本验收。

### 最低版本浏览器复验

人工执行入口见[最低浏览器与 Excel 外部验收步骤](../docs/t3-browser-acceptance.md)，包含独立 Host、HTTP/HTTPS、真实 Excel 复制和证据要求。步骤文档不代表验证通过；实际状态统一见实施状态 §1.1。

默认回归仍使用锁定 Playwright 随附的 Chromium。可显式指定本机已核对来源的归档浏览器，无需修改前端依赖或替换系统浏览器：

```sh
TENONEDGE_TEST_URL=http://<本机网卡IP>:18082 \
TENONEDGE_TEST_BROWSER=chromium \
TENONEDGE_TEST_EXECUTABLE=/absolute/path/to/chrome \
npx --yes npm@11.21.0 run test:e2e -- tests/auth.spec.ts \
  --grep 'real setup|failed login|1000-row paste|10000-row virtualization|canvas drag/connect|canvas keeps'
```

`TENONEDGE_TEST_BROWSER` 只接受 `chromium`、`firefox`、`webkit`；省略可执行文件时使用 Playwright 默认安装。Firefox归档须支持Playwright的驱动协议；128与当前驱动不兼容，不能通过修改驱动或浏览器隐藏错误。每次从全新临时Host/浏览器上下文开始，记录浏览器实际版本、归档来源/哈希、HTTP网卡地址、非安全上下文断言、耗时与报告；旧浏览器只用于自有隔离测试数据，不用于浏览生产站点。不能把Chromium当作已验证的Google Chrome/Edge安装包，也不能把Linux WebKit当作真实Safari。

上述筛选覆盖真实首次设置/登录/改密/离线资源、登录错误不重放、点表1000行粘贴与10000行虚拟化、画布连线/属性/撤销重做；不是全部业务端到端或全部浏览器验收。实际结果统一见实施状态，不因提供命令就标为通过。

Linux原版Firefox128可另用标准WebDriver用例（不新增npm依赖），先在回环地址启动已核对来源的geckodriver：

```sh
/path/to/geckodriver --host 127.0.0.1 --port 18485
# 另一终端，web/目录；脚本自己启动并清理全新临时Host
TENONEDGE_TEST_URL=http://<本机网卡IP>:18483 \
TENONEDGE_WEBDRIVER_URL=http://127.0.0.1:18485 \
TENONEDGE_TEST_EXECUTABLE=/absolute/path/to/firefox \
TENONEDGE_EXPECT_BROWSER_MAJOR=128 \
TENONEDGE_SMOKE_RESULTS=/absolute/path/to/results \
node scripts/minimum-firefox-smoke.mjs
```

该用例经真实页面完成首次设置/退出/登录、深链刷新、1000/10000行点表、真实指针连线/拒绝环路及50步撤销重做。Firefox不接受构造的未信任ClipboardEvent所带文本；测试通过Gecko自动化上下文填充隔离的Linux无头浏览器剪贴板，再由WebDriver发送真实Ctrl+V，并断言paste事件受信任且收到全部文本。不直接调用应用store，不代表Windows/Excel或人工粘贴验收。报告记录原版浏览器/驱动版本、阶段耗时、采样的浏览器进程树RSS（不是JS堆或峰值），失败截图和自有Host日志；任何失败退出非零。脚本关闭自己的浏览器会话及Host，不关闭外部驱动服务；用后由启动者停止驱动。

将 `TENONEDGE_TEST_URL` 换为 `https://<本机网卡IP>:<空闲端口>`，并使用另一个结果目录，可以执行同样六阶段的 HTTPS 验证。脚本通过本机 `openssl` 只读取自有 Host 的公开证书，核对其控制台 SHA-256 指纹；先证明未信任会话被拒绝，再通过 Firefox 自有 `nsIX509CertDB` 将该证书登记为临时 profile 的 TLS 信任锚，并回读指纹与实际信任状态。全程显式 `acceptInsecureCerts:false`，不忽略证书错误、不修改系统信任；HTTPS 断言安全上下文，HTTP 仍断言非安全上下文。本机曾观察到外部 `certutil` 列出的信任记录未被 Firefox 128 实际采用，因此改由浏览器自身登记与回读；具体兼容原因未确定，不归因为某个 NSS 版本，也不修改浏览器或驱动代码。

### 最新 Google Chrome 的默认 HTTP 策略

V-03 的这条检查不能由普通 Playwright 启动代替：当前锁定版本的默认启动参数关闭了 `HttpsUpgrades`。使用已经核对来源的实际 Google Chrome 154 或以上安装包（可以仅解包，不安装或替换系统浏览器），在 `web/` 执行：

```sh
TENONEDGE_TEST_URL=http://<本机网卡IPv4>:<空闲端口> \
TENONEDGE_TEST_EXECUTABLE=/absolute/path/to/google-chrome \
TENONEDGE_SMOKE_RESULTS=/absolute/path/to/results \
node scripts/current-chrome-http-smoke.mjs
```

脚本直接启动自有 Chrome 和临时 Host，Playwright 只通过 CDP 附着到默认 profile，不创建无痕上下文。启动参数仅用于无头模式、回环调试端口、临时 profile 和跳过首次运行/默认浏览器提示；不关闭 HTTPS 升级、不豁免证书或非安全源、不禁用沙箱。有主机 Chrome 策略文件时拒绝将结果当作默认设置。临时 HOME/profile 不访问开发者的桌面会话总线或密钥服务；本机继承会话总线时，曾在独立 HTTP 页面上复现请求前阻塞，隔离总线后正常，不使用 `--password-store` 改写浏览器设置。报告包含这一环境边界、实际版本与进程参数、HTTP 首次设置/登录、深链刷新和画布建点结果；此项不替代最低版本、完整画布/点表或 Windows/Excel 验收。

浏览器须具备可用的原生沙箱。只解包的 Linux deb 中，`chrome-sandbox` 可能因不是 root 所有而无法使用；不能用 `--no-sandbox` 绕过。此次验证仅临时移开自有缓存中的解包 helper，使 Chrome 按官方选择规则采用系统已安装、root 所有且权限为 4755 的 helper（`CHROME_DEVEL_SANDBOX`），结束后恢复缓存文件；没有修改系统 helper。实际路径、版本、失败与通过证据见实施状态 §2.313。

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

### 运行参数子项（T3-09a）

`/system` 已接入预览/连接测试全局与每会话并发、审计保留期及调试缓存每对象/全局字节与条数的真实读取和管理员确认编辑。参数与审计同事务、版本冲突/丢回复不自动重放；并发降低不取消已有任务，审计保留不得短于部署下限，更新与原清理/导出生成互斥。调高资源上限提示本机实测。调试缓存降低预算会立即按捕获顺序淘汰旧诊断副本，单条仍截断64KiB，不删除业务队列。队列参数和代理规则已另有表单；在途字节也已接入同一个运行时预算（§2.236），降低不提前释放已有消息，增大唤醒等待。输入通道即时条数及新计划规模参数亦已接入：降低通道上限不丢弃已有消息；规模参数约束新编译与提交，不停止有效计划。API、生效边界与验证见 [系统设置](../docs/system-settings.md) 和实施状态 §2.227、§2.234、§2.237、§2.240。

### API 文档页面

`/api-docs` 从当前登录的宿主读取实时 OpenAPI，可筛选操作、查看请求/响应和完整模型；不使用构建时样本，不解析远程引用或文档HTML，也不提供业务请求发送器。沿用统一客户端的截止时间、会话隔离与401处理；三角色可读，匿名在Production下拒绝。证据见实施状态 §2.235。

### 独立验证界面构建

已有人工试用 Host 使用 `web/dist` 时，可先构建到仓库外目录并让自动化临时 Host 读取该产物，避免测试覆盖正在使用的静态文件。`TENONEDGE_TEST_WEBROOT` 仅供测试启动脚本和 owned-host 夹具使用，填写绝对路径；未设置时仍读取 `web/dist`。例如在 `web/`：

```sh
npx --yes npm@11.21.0 run build -- --outDir /absolute/path/to/validation/dist
TENONEDGE_TEST_WEBROOT=/absolute/path/to/validation/dist \
TENONEDGE_TEST_URL=http://<本机网卡IPv4>:<空闲端口> \
npx --yes npm@11.21.0 run test:e2e -- workspace.spec.ts
```

先完成固定安装再启动测试，不在运行中的浏览器测试期间重装依赖或替换其产物。测试使用临时数据目录；人工试用账号和配置不得作为自动化夹具。
