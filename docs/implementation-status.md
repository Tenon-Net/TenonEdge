# TenonEdge 实现状态与验证证据

- 用途：任务状态与验证证据的统一入口（PLAN-01 §1、`AGENTS.md`）。任务清单和开发准备不另外维护动态完成表；以后建立 Issue 时沿用同一任务 ID，并链接本文。
- 建立：T0-01，2026-09-29。
- 状态用语：代码状态分为未开始、进行中、待验收、完成、阻塞；验证状态分为未执行、通过、失败、跳过、不适用，并注明是本地结果还是远端 CI 结果。跳过须写原因。Agent 自报不等于验收人认可，本批任务在验收前一律记为“待验收”。
- 基线：分支 `main`，提交 `27db103`（任务拆分提交）。T0-A 经维护者 2026-09-30 授权，在本地分支 `t0-foundation` 上单独提交为 `0ff530a`，未推送。T0-B 经维护者 2026-09-30 验收，在同一分支上单独提交为 `b2dec2d`，未推送。T0-C 经维护者 2026-10-01 验收，在同一分支上单独提交为 `591a064`，未推送。T1 第一批（T1-01—T1-03）及 T1-04 经维护者 2026-10-08 授权在同一分支本地提交为 `c97521d`；随后按计划继续 T1-05。此前验证证据见 §4.11、§4.12，远端 CI 未执行。

## 1. 批次与任务状态

| 批次 | 任务 | 代码状态 | 验证状态 | 说明 |
|---|---|---|---|---|
| T0-A | T0-01 工程与公共构建设置 | 完成 | 本地通过；远端 CI 未执行 | §2.1 |
| T0-A | T0-02 测试底座与初始 CI | 完成 | 本地通过；远端 CI 未执行（未推送） | §2.2 |
| T0-A | T0-03 最小宿主、健康检查与 API 模板 | 完成 | 本地通过；远端 CI 未执行 | §2.3 |
| T0-B | T0-04 数据目录与单实例锁 | 完成 | 本地通过；远端 CI 未执行 | §2.4 |
| T0-B | T0-05 配置库与版本化迁移 | 完成 | 本地通过；远端 CI 未执行 | §2.5 |
| T0-B | T0-06 身份、ID、密钥与初始证书 | 完成 | 本地通过；真实容器验证已手动执行；远端 CI 未执行 | §2.6 |
| T0-B | T0-07 首次设置、登录与主机重置 | 完成 | 本地通过；远端 CI 未执行 | §2.7 |
| T0-B | T0-08 角色、会话失效与请求安全 | 完成 | 本地通过；远端 CI 未执行 | §2.8 |
| T0-B | T0-09 审计、基础日志与降级记录 | 完成 | 本地通过；远端 CI 未执行 | §2.9 |
| T0-C | T0-10 必要运行契约与基础数值测试 | 完成 | 本地通过；远端 CI 未执行 | §2.10 |
| T0-C | T0-11 MQTT 最小原型 | 完成 | 本地通过（真实 Broker 用例只在本机执行）；远端 CI 未执行 | §2.11 |
| T0-C | T0-12 Modbus 候选库比较 | 完成 | 本地通过；远端 CI 未执行；选型建议待认可 | §2.12 |
| T0-C | T0-13 独立发布与阶段证据汇总 | 完成 | 本地通过；实际 CI 报告缺失（未获推送授权） | §2.13 |
| T1 第一批 | T1-01 运行管理与种子计划 | 待验收 | 本地通过（含真实进程的启动保护）；远端 CI 未执行 | §2.14 |
| T1 第一批 | T1-02 统一消息与模拟输入 | 待验收 | 本地通过；远端 CI 未执行 | §2.15 |
| T1 第一批 | T1-03 持久化发送缓存 | 待验收 | 本地通过；远端 CI 未执行 | §2.16 |
| T1 继续 | T1-04 MQTT 输出与凭证解析 | 待验收 | 本地通过（含真实 Broker）；远端 CI 未执行 | §2.17、§4.12 |
| T1 继续 | T1-05 发送结果、退避与失败区 | 待验收 | 本地通过；远端 CI 未执行 | §2.18、§4.13 |
| T1 继续 | T1-06 目标隔离与暂停基础 | 待验收 | 本地通过；远端 CI 未执行 | §2.19、§4.14 |
| T1 继续 | T1-07 资源保护与运行诊断基础 | 后端软件交付完成，待维护者验收 | 预算、存储保护、校时、状态/趋势及计数子场景通过；跨阶段 AT 不标整体通过 | §2.20 |
| T1 继续 | T1-08 停机恢复与链路验收 | 后端软件交付完成，待维护者验收 | 队列损坏、4 个强制退出边界、真实 Broker 断网恢复及全量回归通过 | §2.21 |
| T2 | T2-01 统一连接管理与出站校验 | 基础软件链路待验收，扩展子项未完成 | 三协议共享所有权/实际 IP 校验通过；显式代理和预览借用仍未交付 | §2.22、§2.98 |
| T2 | T2-02 Modbus 采集 | 基础软件链路待验收 | TCP/RTU over TCP 软件从站、故障、共享 UnitId、Host 输出和规模停止通过；实物未验证 | §2.68—§2.74、§2.89—§2.97 |
| T2 | T2-03 MQTT 输入与会话 | 基础软件链路待验收 | 真实 Broker、持久接管、背压、重订与进程重启通过；恢复边界见 §2.98 | §2.40—§2.66、§2.75—§2.88 |
| T2 | T2-04 HTTP 输入与输出 | 基础软件链路待验收，证书子项未完成 | GET→Host→队列→POST、当前凭证、响应分类通过；客户端证书/连接信任未交付 | §2.27—§2.33、§2.98 |
| T2 | T2-05 字段映射与数值时间 | 后端软件子集待验收 | 数值精度、时间、独立分支与三种输出格式通过；网页预览属 T3 | §2.23—§2.26、§2.32—§2.39 |
| T2 | T2-06 输入复用与多输出 | 后端软件子集待验收 | 三协议共享链路及 Modbus Host 扇出/扇入通过；图编译与实时应用属 T3 | §2.34—§2.39、§2.96 |
| T2 | T2-07 设备直连与 LoRa 样例 | 软件子集待验收 | 已解码样例、真实 Broker ACL/持久化/积存边界通过；无线实物未验证 | §2.67、§2.76—§2.87 |
| T2 | T2-08 协议故障与资源回归 | 软件回归待验收，长期观察未完成 | 1754 项基线通过；连接/缓冲/线程有界和一分钟 Modbus 观测，不代表长期整机压力 | §2.62—§2.66、§2.80、§2.97—§2.98 |
| T3 | T3-01 前端精简基础 | 进行中，认证基础待验收 | 真实首次设置/登录/退出/密码修改、同源静态部署及普通 HTTP 浏览器通过；最低浏览器及强制改密/重置完整场景未验证 | §2.99—§2.100 |
| T3 | T3-02 点表交互原型 | 软件原型待验收，最低浏览器未验证 | 1,000 行粘贴事件、逐行修正/覆盖/批改、10,000 行虚拟化与查询500编辑保护通过；不保存或采集 | §2.101 |
| T3 | T3-03 画布交互原型 | 软件原型待验收，最低浏览器未验证 | 拖放/连线/环路拒绝、框选/组移动、复制删除及 50 步撤销重做通过；不保存或运行 | §2.102 |
| T3 | T3-04 连接草稿子项 | 部分实现，整项未完成 | Modbus/MQTT/HTTP 草稿 CRUD、并发/事务/权限与真实表单通过；未应用 | §2.103 |
| 后续 | T3-04 剩余及 T3-05—T3-11 | 未完成 | 尚未形成完整可视化配置闭环 | 沿维护者授权继续；不含 T4/T5/G1 实施 |

## 2. 实现内容

2.1—2.3 为 T0-A，2.4—2.9 为 T0-B，2.10 起为 T0-C。

### 2.1 T0-01 工程与公共构建设置

交付：

- `TenonEdge.sln`：经典 `.sln` 格式，仓库唯一的解决方案；包含 Host 与两个测试工程。
- `src/TenonEdge.Host`：ASP.NET Core 宿主（§2.3）。
- `global.json`：SDK `10.0.401`，`rollForward=disable`，`allowPrerelease=false`。
- `Directory.Build.props`：`net10.0`、可空引用检查、隐式 using、确定性构建、警告即错误、构建中强制代码风格、SDK 分析器 Recommended 规则集；所有工程生成锁文件；在 GitHub Actions 中启用 `RestoreLockedMode` 与 `ContinuousIntegrationBuild`。
- `Directory.Packages.props`：NuGet 集中版本管理；`NuGet.config`：清空继承的包源，只用 nuget.org，并做包源映射。
- 三份 `packages.lock.json`（由实际还原生成），以及 `.editorconfig` 和本文件。

未创建：Runtime、Adapters、Abstractions（本批没有实际用途，按规范不建空工程）；`web/`；企业、平台或升级工程。

| 验收项（PLAN-01 T0-01） | 结果 | 证据 |
|---|---|---|
| locked restore 成功 | 通过（本地；包括清空包缓存与 HTTP 缓存后从 nuget.org 重新下载） | §4.2 R3、R4、F1 |
| Release 构建成功 | 通过（本地；0 警告、0 错误） | R2、R5、F3 |
| SDK 与包清单可追溯 | 已记录 | §5 |
| 无无用占位工程、私有依赖或前端工程 | 通过：`src` 下只有 Host；只用 nuget.org；工程规则测试逐项检查 | F1、F5 |

### 2.2 T0-02 测试底座与初始 CI

交付：

- `tests/UnitTests`（xUnit.net v3，经 VSTest 执行）：
  - 工程规则：依赖方向、禁止依赖、SDK 固定、集中版本、锁文件齐全、唯一解决方案。工程与框架引用取自还原结果 `obj/project.assets.json`，包括经 `Directory.Build.*` 导入和 SDK 隐式加入的引用；包取自锁文件中的直接与传递依赖；
  - 规则本身的负向用例：用构造的工程描述证明各类违规都会被报告；
  - 宿主就绪条件的单元测试。
- `tests/IntegrationTests`：
  - `WebApplicationFactory` 进程内 API 测试，默认按 Production 环境启动；
  - 独立临时目录辅助；
  - 以真实进程启动已构建宿主的测试：监听端口由系统分配（端口 0），实际地址从宿主启动日志读取，避免预先挑选端口带来的竞争。
- `tests/tenonedge.runsettings`：未发现任何用例即失败（`TreatNoTestsAsError`），测试会话上限 15 分钟。
- `.github/workflows/backend-ci.yml`：Linux CI，依次执行：
  1. 按 `global.json` 安装 SDK，记录环境；
  2. locked mode 还原；
  3. 格式与代码风格检查；
  4. Release 构建；
  5. 测试并输出 TRX；
  6. 核对报告；
  7. 上传报告与环境记录。
- `.github/scripts/verify-test-results.py`：核对 TRX 报告。以下情况一律判为不通过：
  - 零用例；
  - 失败、跳过或未执行；
  - 测试运行没有正常完成（例如测试进程崩溃、会话超时）；
  - 缺少必需测试程序集的报告。

| 验收项（PLAN-01 T0-02） | 结果 | 证据 |
|---|---|---|
| 实际发现并执行非零用例 | 通过：共 44 个（单元 23、集成 21） | F5、F6 |
| 临时构造违规引用或失败断言时检查能失败，提交前恢复 | 通过：实验 Z、A—F 均按预期失败，改动已恢复并复验 | §4.3 |
| 测试不访问生产数据 | 通过（本批范围）：临时目录位于系统临时目录下的独立子目录，用后删除；进程测试断言宿主不在工作目录写文件。本批尚无数据目录 | 集成测试 `TestInfrastructureTests`、`HostProcessTests` |
| 失败不被脚本吞掉 | 通过：`dotnet test` 失败时退出码非零。核对脚本遇到以下情况均以非零退出：<br>• 失败、跳过、零用例；<br>• 运行中止；<br>• 缺少报告。<br>CI 各步骤不忽略退出码 | §4.3 |
| Linux CI | 已编写；actionlint 检查通过，本地逐步模拟通过；GitHub Actions 未运行 | R7、F1—F7、§4.4 |

### 2.3 T0-03 最小宿主、健康检查与 API 模板

交付：

- 代码组织：`Program.cs` 只做装配；`Bootstrap/` 负责服务注册、请求管道、管理 API 根分组与 OpenAPI 文档；`Features/Health/` 放健康检查端点与就绪条件；`Security/` 放 API 的 401/403 处理。
- 管理 API 根分组 `/api/edge-admin/v1` 默认要求认证。本批还没有登录能力，访问受保护端点一律返回 401 ProblemDetails。
- 统一错误：以下情况都返回 `application/problem+json`，不含异常类型、消息或堆栈：
  - 未处理异常：500；
  - 未匹配路由：404；
  - 方法不允许：405（带 `Allow` 头）；
  - 未认证：401。
- 依赖注入：所有环境都开启 `ValidateScopes` 与 `ValidateOnBuild`。
- 开发调试启动配置 `Properties/launchSettings.json`：供 `dotnet run` 与 IDE 调试使用，Development 环境，监听 `http://0.0.0.0:18080`，可从其他机器访问（§6 决定 10）。该文件不进入构建输出和发布物。

| 方法与路径 | 访问 | 行为 |
|---|---|---|
| `GET /healthz` | 匿名 | 进程存活：200，`text/plain` 正文 `Healthy`，`Cache-Control: no-store` |
| `GET /readyz` | 匿名 | 全部带 `ready` 标签的条件满足时返回 200（`Healthy` 或 `Degraded`），否则 503（`Unhealthy`）；只返回状态，不返回原因 |
| `GET /api/edge-admin/v1/openapi/v1.json` | 需认证；仅 Development 环境匿名 | 内置 OpenAPI 生成的 3.1 文档 |

就绪语义：本批还没有运行时，`/readyz` 目前只有一个条件，即宿主生命周期：全部托管服务已启动（ApplicationStarted），且尚未开始停机（ApplicationStopping）。总设计 §10.1 中“运行时已启动；处于运行失败状态时返回 503”，在运行时宿主实现时以同一 `ready` 标签接入（T1-01）。“尚无有效配置时也视为就绪”的规则保持不变，本批还没有配置概念。

| 验收项（PLAN-01 T0-03） | 结果 | 证据 |
|---|---|---|
| healthz/readyz 与实际状态一致 | 通过（本地）：<br>• 已启动时两个端点均为 200；<br>• 注入不满足的就绪条件时 `/readyz` 返回 503 且不含原因，`/healthz` 仍为 200；<br>• 不带 `ready` 标签的检查不影响 `/readyz`；<br>• 集成测试确认宿主把生命周期条件登记为就绪条件（实验 F：移除登记后该测试失败）；<br>• 该条件在未启动完成、已开始停机时不就绪，由单元测试覆盖；<br>• 真实进程中 `/readyz` 轮询到 200 | F5、F6、R8 |
| OpenAPI 可生成，operationId 唯一 | 通过（本地）：<br>• 生成 OpenAPI 3.1.1 文档；<br>• 每个操作都有显式、小驼峰、唯一的 operationId（`getLiveness`、`getReadiness`）；<br>• 测试固定全部 operationId 清单，改名或新增须显式更新 | F6、R9 |
| 错误不是 200 包装的失败 | 通过（本地）：401、404、405、500 均为对应状态码加 ProblemDetails | F6 |
| Program.cs 不堆全部业务 | 通过：只包含装配调用 | `src/TenonEdge.Host/Program.cs` |
| 测试专用端点不进入生产发布物 | 通过：本批没有测试专用端点，故障注入在测试工程中通过替换依赖注入服务完成；测试逐项核对生产环境全部路由端点及其访问级别 | `ManagementApiAccessTests` |

未覆盖（按计划在后续任务完成）：

- 真实登录、403、字段校验错误（尚无带请求体的接口）、枚举按字符串序列化（尚无枚举字段）：T0-07、T0-08。
- 401 响应的 `WWW-Authenticate` 质询头：RFC 9110 要求携带适用的质询。本批没有任何认证方案可以声明，401 暂不带该头；随认证方案一并处理（T0-08，API 令牌方案同样适用）。
- HTTPS 8443、HTTP/HTTPS 监听地址与端口的正式配置，以及请求协议判定：T0-06、T0-08。进展：监听配置已在 T0-06 实现（§2.6，决定 19）；请求协议判定属于 T0-08。
- 管理 API 请求体默认 1 MiB 上限（总设计 §8.7）：随第一个带请求体的接口（T0-07）实现。

### 2.4 T0-04 数据目录与单实例锁

交付：

- `Storage/DataDirectory.cs`：部署参数 `TenonEdge:DataDirectory`（环境变量 `TenonEdge__DataDirectory`）。只接受绝对路径，规范化后使用；目录须已存在，只有 Development 环境在缺少时创建（权限 700）。
- `Storage/DataDirectoryLock.cs`：以 FileShare.None 独占打开数据目录中的 `tenonedge.lock`（Linux 上为 flock，权限 600），进程存续期间一直持有。文件锁被关闭时（`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` 或 `System.IO.DisableFileLocking`）拒绝启动。需要数据目录的服务只能经由锁对象取得路径。
- `Bootstrap/HostStartup.cs`、`HostExitCodes.cs`：开始接收请求之前的启动检查。可预期的失败写一条说明原因的日志，不开始监听，并以约定的退出码结束：2 表示部署参数或路径错误，3 表示数据目录被占用。
- 测试基础设施：
  - 进程内测试的每个宿主（包括 WithWebHostBuilder 派生出的宿主）使用独立的临时数据目录；
  - 真实进程测试可以指定数据目录、附加参数和环境变量，并可强制终止进程。
- 调试启动配置增加开发数据目录 `%HOME%/.local/share/TenonEdge/dev-data`（`dotnet run` 会展开 `%HOME%`）。

主机侧维护命令复用同一把锁：锁已实现，重置命令随 T0-07 实现。

| 验收项（PLAN-01 T0-04） | 结果 | 证据 |
|---|---|---|
| 同目录第二实例被拒 | 通过（本地）：<br>• 第一个实例就绪后再启动第二个，第二个以退出码 3 结束并说明原因，没有开始监听；<br>• 第一个实例不受影响，随后正常停止；<br>• 同一进程内第二次取锁同样被拒绝 | `DataDirectoryProcessTests`、`DataDirectoryLockTests`，§4.6 B1 |
| 正常退出或终止进程后可重新取锁 | 通过（本地）：SIGKILL 强制终止后，新实例立即取得锁并就绪；SIGTERM 正常停止后同样可以 | 同上 |
| 工作目录变化不创建另一套数据 | 通过（本地）：<br>• 从两个不同的工作目录启动，数据只写入配置的数据目录，两个工作目录都保持为空；<br>• 相对路径被拒绝 | 同上，`DataDirectoryTests` |
| 路径错误明确报告 | 通过（本地）：未配置、相对路径、目录不存在、路径是文件四种情况都以退出码 2 结束，日志说明原因，没有开始监听；文件锁被关闭时同样拒绝启动 | 同上 |
| 取得锁后才打开运行数据 | 通过：取锁之前只校验部署参数（T0-06 起为监听参数，T0-09 起加上审计保留期），不访问数据目录；数据目录只能经由锁对象取得 | `Bootstrap/HostStartup.cs`；T0-05 起配置库依赖锁对象 |

未覆盖：

- 数据目录位于网络文件系统时锁的行为未验证：flock 在 NFS 上的语义取决于具体实现。数据目录应放在本机磁盘或容器数据卷上。
- 容器数据卷检查属于 T0-06。

### 2.5 T0-05 配置库与版本化迁移

交付：

- 依赖：Host 直接引用 SqlSugarCoreNoDrive 5.1.4.221 与 Microsoft.Data.Sqlite 10.0.12。D-09 按推荐方案试验并排除 SQL Server 驱动栈（`Directory.Build.targets`），Host 构建输出只有 9 个 DLL；依赖清单与试验结论见 `docs/reference-notes.md`。
- `Storage/ConfigDatabase.cs`：config.db 的访问入口。
  - SqlSugarScope 单例；同一异步流程内的并发分支用 CopyNew 取得独立客户端，用后释放。
  - 命令超时 30 秒（SqlSugar 默认 300 秒）。
  - 释放时清空本库的连接池。
- `Storage/SqliteConnectionSettings.cs`：PRAGMA 挂在 SqlSugar 的 CheckConnectionExecuted 事件上。
  - 以原生连接句柄识别物理连接，每个物理连接只设置一次 synchronous=FULL、busy_timeout=5000；
  - 设置后回读 journal_mode、synchronous、busy_timeout，不符就拒绝使用这个连接；
  - journal_mode=WAL 持久保存在库文件中，由启动初始化设置一次。
- `Storage/SqliteRuntimeInfo.cs`、`ConfigStoreInitializer.cs`：
  - 启动时读取实际加载的 SQLite 版本与编译选项，写入日志；
  - 版本低于 3.51.3 时登记“运行时启动条件”，就绪检查返回 503；
  - 新库以 600 权限创建，SQLite 创建的 -wal、-shm 沿用该权限。
- `Storage/Migrations/`：版本化迁移。
  - 迁移历史表 schema_migration 记录版本、名称、执行时间与程序版本，其中的最大版本号就是 schema 版本；
  - 全部待执行的迁移在一个事务中完成；
  - 库版本高于本程序或迁移历史不一致时拒绝启动；
  - 本任务的迁移 1 建立单行表 system_state，后续任务追加字段。
- `Bootstrap/RuntimeStartConditions.cs` 及其就绪检查：登记阻止运行时启动的原因，T1-01 的运行时宿主读取同一份结果。
- 启动顺序：取得锁 → 初始化配置库 → 开始接收请求。配置库无法打开或迁移失败时，以退出码 4 结束。

| 验收项（PLAN-01 T0-05） | 结果 | 证据 |
|---|---|---|
| 版本、compile_options 和 PRAGMA 有回读证据 | 通过（本地）：<br>• 实际加载 SQLite 3.53.3（Microsoft.Data.Sqlite 10.0.12、SQLitePCLRaw 2.1.12），启动日志记录版本和完整编译选项；<br>• 6 个同时借出的物理连接分别回读 journal_mode=wal、synchronous=2（FULL）、busy_timeout=5000 | `ConfigDatabaseTests`、`ConfigStoreProcessTests`，§4.6 B3 |
| 符合既定版本下限 | 通过（本地）：<br>• 3.53.3 高于下限 3.51.3；<br>• 下限判定的单元测试覆盖边界和无法识别的版本号；<br>• 登记阻止原因后 `/readyz` 返回 503 | `SqliteRuntimeInfoTests`、`ConfigStoreStartupTests` |
| WAL/FULL 与连接策略有效 | 通过（本地）：<br>• 回读结果同上；<br>• 64 个并发写入经独立客户端全部成功；<br>• 没有启用 WAL 的库，连接在回读校验时被拒绝 | `ConfigDatabaseTests` |
| 迁移不重复执行 | 通过：再次执行不改动迁移历史和数据；升级时只执行新增的迁移 | `ConfigMigratorTests` |
| 不静默清空 | 通过：库中已有同名表、却没有迁移历史时，整次迁移回滚，原有表和数据保持不变；迁移只做显式的建表和追加 | `ConfigMigratorTests` |
| 失败保留可诊断状态 | 通过：<br>• 失败时全部待执行的迁移整体回滚，库保持迁移前的版本；<br>• 错误说明失败的迁移和原因，宿主以退出码 4 结束；<br>• 库版本高于程序（降级）、迁移历史不一致、库文件损坏时同样拒绝启动 | `ConfigMigratorTests`、`ConfigStoreProcessTests` |
| 直接与传递依赖记录 | 已记录：`docs/reference-notes.md` §1（31 个包，其中 10 个随发布物分发），D-09 试验结论见 §2 | — |

未覆盖：

- 人为加载低于 3.51.3 的原生库：进程内只能验证判定逻辑和阻止路径（就绪检查返回 503、写日志）。以真实的低版本原生库启动需要另外构建带旧版 SQLitePCLRaw 的发布物，归入 T0-13。
- 自包含发布与 Docker 镜像中实际加载的版本：T0-13（V-07 发布环境部分）。
- 在系统信息中显示 SQLite 版本和阻止原因：随 system 分组的系统信息接口实现（T0-B 后续任务或 T3-09）。

### 2.6 T0-06 身份、ID、密钥与初始证书

交付：

- 迁移 2 `gateway_identity`：SystemState 增加 node_id、config_store_id、id_floor、revision_floor。
- `Storage/GatewayIdentity.cs`：配置库创建后第一次启动时，生成 NodeId 与 configStoreId（随机 UUID），以 SystemState 为准；之后每次启动只读取。
- `Storage/Ids/`：从 TenonAdmin 复制、按总设计 §10.1 修改的雪花 ID 生成器，来源与修改见 `docs/reference-notes.md` §3。
  - 启动时以 SystemState 的 ID 下限、各雪花主键表中已有的最大 ID 和构建时间下限确定起点；
  - 构建时间下限记录在程序集元数据中，默认 2026-09-30，可用构建属性 `TenonEdgeBuildTimestampFloor` 覆盖；
  - 使用雪花主键的表须登记，测试核对库中的表与登记清单一致。
- `Security/SecretProtection.cs`：Data Protection。
  - 固定应用名 TenonEdge；密钥环持久化在数据目录的 keys 子目录（700），不清理旧密钥；
  - 启动时做一次保护与解除保护的自检；各类秘密的用途字符串集中定义。
- `Security/HttpsCertificateStore.cs`：自签证书。
  - RSA 2048、SHA-256，有效期 825 天，剩余不足 30 天时启动重签；
  - 使用者可选名称为 localhost、主机名、回环地址和生成时本机的单播地址；
  - 经 Data Protection 保护后保存在 identity 目录（目录 700、文件 600）；无法解密时重新生成并告警。
- `Bootstrap/ListenerSettings.cs`、`KestrelListeners.cs`：正式的监听配置。
  - 部署参数 `TenonEdge:Http:{Enabled,Address,Port}`、`TenonEdge:Https:{Address,Port}`，默认 HTTP 8080、HTTPS 8443、监听全部地址，可关闭 HTTP，不发送 HSTS；
  - 参数在启动检查的第一步校验，错误时以退出码 2 结束；监听端点在启动检查的最后一步加入；
  - 调试启动配置改为 HTTP 18080、HTTPS 18443，都监听 0.0.0.0。
- `Bootstrap/ContainerDataVolume.cs`：在容器中运行、数据目录不在持久卷上时，登记运行时启动条件（就绪检查返回 503），并在日志中提示挂载数据卷；管理端点仍可使用。
- 测试基础设施：真实进程测试同时监听 HTTP 与 HTTPS，可指定程序目录；进程内测试可固定数据目录，用于重启测试。

| 验收项（PLAN-01 T0-06） | 结果 | 证据 |
|---|---|---|
| 身份重启保持 | 通过（本地）：<br>• 同一数据目录重启、从不同工作目录启动，NodeId 与 configStoreId 不变；<br>• 不同数据目录的身份不同；<br>• 真实容器删除后用同一数据卷重建，NodeId 不变 | `GatewayIdentityTests`、`DataDirectoryProcessTests`，§4.6 B5 |
| 换路径、重建挂卷容器后可解密 | 通过（本地）：<br>• 进程内：内容根变化后仍能解密；对照：应用名不同则无法解密；合并两个密钥环后新旧密文都能解密，合并前不能；<br>• 真实进程：把程序复制到另一路径后重启，HTTPS 证书从受保护文件中解密成功，TLS 握手出示的证书指纹不变；<br>• 真实容器：删除后用同一数据卷重建，证书指纹不变，日志显示证书为加载而非重新生成 | `DataProtectionTests`、`ListenerProcessTests`，§4.6 B5 |
| 不满足持久目录条件时按设计拒绝启动运行时 | 通过（本地）：<br>• 真实容器中不挂卷（数据目录在 overlay 上）、挂 tmpfs 两种情况下，就绪检查返回 503，日志说明原因并提示挂载数据卷，存活检查仍为 200；挂载命名数据卷时正常就绪；<br>• 挂载表解析与容器判定有单元测试，接线有真实进程测试 | `ContainerDataVolumeTests`、`ContainerVolumeProcessTests`，§4.6 B5 |
| 时钟回退和重启后的 ID 符合 §10.1 | 通过（本地）：<br>• 单元测试逐条覆盖：时钟落后沿用逻辑时间戳、序列用尽不等待时钟、读数达到 2^41 视为不可信且只告警一次、逻辑时间戳达到 2^41 时拒绝、构建时间下限、库内下限、ID 小于 2^53；<br>• 重启后以 SystemState 下限与库中最大 ID 确定起点 | `SnowflakeIdGeneratorTests`、`GatewayIdentityTests` |
| 秘密不入源码和日志 | 本任务范围通过：<br>• 证书私钥只以受保护形式落盘，不解除保护就无法读取；<br>• 密钥文件在 700 目录中；<br>• 日志只记录 NodeId、证书指纹等非秘密信息；<br>• 仓库中没有密钥或证书文件 | `HttpsCertificateStoreTests`；日志的系统性检查见 §2.9 |
| ID 来源记录 | 已记录（V-13）：`docs/reference-notes.md` §3 | — |

未覆盖：

- 真实容器验证是手动执行的（§4.6 B5），还没有纳入 CI；发布物在干净容器中的复验属于 T0-13。
- IP 变化后证书与地址不符是已知代价（总设计 §10.3）；上传正式证书属于 T5-01。
- 在系统信息中显示 NodeId 与证书指纹：随系统信息接口实现。进展：T0-09 已实现（§2.9）。

### 2.7 T0-07 首次设置、登录与主机重置

交付：

- 迁移 3 `accounts_and_audit`：账号表 user_account、审计表 audit_log，以及 SystemState 的首次设置状态、重置对象、完成时间与来源 IP、网关名称。
- 首次设置（D-01 未确认前按“首次访问设置加最小保护”实现，不要求设置码）：
  - `GET /setup`、`POST /setup`，两者都是匿名例外；
  - 服务启动后开放 30 分钟窗口，只在状态为“未初始化”或“重置待设置”时可用；窗口长度按单调计时计算，开机后系统时钟被校正也不会提前关闭或延长（T0-B 审查后修正，§4.7）；
  - 提交时在一个写事务中以比较并交换的方式完成：未初始化时创建管理员；重置待设置时只为被重置的账号设置新密码；
  - 记录完成时间与来源 IP，写入审计；成功后自动登录；
  - 窗口开放时，服务日志输出一行不含秘密的提示。
- 账号与密码：
  - 账号名为字母、数字和 -、_、.，长度 3—32 位，按小写规范化后比较；
  - 密码至少 8 位、同时含字母和数字、不等于账号、不在内置弱口令表中，最长 128 位；
  - 哈希使用 PasswordHasher 的 V3 格式。它随 ASP.NET Core 共享框架提供，无需另加包：单独引用时还原报 NU1510。
- 登录与会话：
  - `POST /auth/login`（匿名例外）；`POST /auth/logout`、`GET /auth/session`、`POST /auth/password`（需要登录）；
  - 失败统一提示“账号或密码错误”；同一账号名连续 5 次失败锁定 15 分钟，不存在的账号名同样计数；锁定时返回 429 并带 Retry-After；
  - 同一账号名的登录与修改密码尝试逐个处理，并发请求不能在锁定生效前多试密码（T0-B 审查后修正，§4.7）；
  - Cookie 会话：HTTP 与 HTTPS 分开命名，HttpOnly、SameSite=Strict，空闲 2 小时滑动过期；
  - 会话票据中写入安全戳、签发协议和防 CSRF 令牌，它们的校验属于 T0-08；
  - 临时密码（须修改密码）状态下，除查看会话、登出、改密以外的接口返回 403；
  - 修改密码须输入原密码，新密码不能与原密码相同；修改后更换安全戳。原密码错误单独计数，连续 5 次时更换安全戳、结束该账号的全部会话；登录锁定不影响已登录的用户修改密码；接口与登录共用限流（T0-B 审查后补充，§4.7、§6 决定 36）。
- 主机侧重置（D-02 未确认前按“重新开放窗口加最小保护”实现，不生成设置码）：
  - `TenonEdge.Host reset-admin [--account <账号>]`，默认账号 admin；
  - 与服务共用数据目录锁，服务运行时拒绝执行；
  - 清除该管理员的密码、更换安全戳、解除锁定，状态改为“重置待设置”，写入审计；
  - 输出中不出现任何密码；无法执行时以退出码 5 结束。
- 审计写入：首次设置、重置、修改密码与审计记录在同一事务中提交；登录与登出的审计单独写入，写入失败时改写到日志，不阻断登录。审计的查询与保留属于 T0-09。
- 请求体上限：Kestrel 全局默认 1 MiB，超出返回 413（总设计 §8.7）。

| 验收项（PLAN-01 T0-07） | 结果 | 证据 |
|---|---|---|
| 并发初始化仅一次成功 | 通过（本地）：8 个客户端同时提交，只有 1 个成功，其余 7 个返回 409 `setup.alreadyCompleted`；库中只有 1 个账号、1 条设置审计 | `SetupTests`（FS-09） |
| 窗口外拒绝 | 通过（本地）：窗口到期后查询显示关闭，提交返回 409 `setup.windowClosed`，没有创建账号 | `SetupTests` |
| 初始化后重启不再开放 | 通过（本地）：<br>• 未初始化时重启会重新开放窗口；<br>• 完成设置后重启，状态为已初始化、窗口关闭，再次提交被拒 | `SetupTests`（AT-24） |
| 弱口令拒绝 | 通过（本地）：长度不足、只有数字、只有字母、常见弱口令、与账号相同都返回 400，并在 password 字段给出原因；账号名、网关名称的格式错误同样按字段报告 | `SetupTests`、`PasswordPolicyTests`、`AccountNamesTests` |
| 错误登录受限 | 通过（本地）：<br>• 已有账号与不存在的账号在连续 5 次失败后都锁定 15 分钟，返回 429 与 Retry-After，锁定期间正确的密码也被拒绝，锁定期满后恢复；<br>• 密码错误、账号不存在、账号已停用得到相同的响应；<br>• 审查后补充：10 个并发的错误登录只校验 5 次密码，其余在锁定生效后直接被拒；修改密码时原密码连续错误 5 次即结束该账号的全部会话，而登录锁定期间已登录的用户仍能修改密码；不存在的账号名在全部未过期时也不超过 1 万条 | `LoginTests`、`LoginLockoutTests` |
| 无明文密码存库 | 通过（本地）：库中只有 PBKDF2 哈希，不含明文；哈希格式为 V3（HMAC-SHA512、10 万次迭代、16 字节盐） | `LoginTests`、`SetupTests` |
| 重置遵守锁与现行规则并记审计 | 通过（本地，真实进程）：<br>• 服务运行时命令以退出码 3 结束；<br>• 停服后重置成功，旧密码失效，设置页只允许为该账号设置新密码，另一名管理员不受影响；<br>• 审计中有重置与重新设置两条记录，命令与服务输出中都没有密码；<br>• 账号不存在、首次设置之前执行重置时，以退出码 5 结束 | `AdministratorResetProcessTests` |
| 无默认或演示账号 | 通过：全新网关的账号表为空 | `SetupTests`（GW-01、AT-01） |

未覆盖或留给后续任务：

- 防 CSRF 校验、按来源 IP 限流、安全戳与会话代次的校验、请求协议判定：T0-08。进展：已实现（§2.8）。
- 窗口开放期间记运行事件：运行事件存储随 T0-09 建立，届时补上。进展：已实现（§2.9）。
- 审计查询、保留与清理：T0-09。进展：已实现（§2.9）。
- 首页与系统信息中显示首次设置的时间与来源 IP：数据已记录，显示接口随系统信息接口实现。进展：系统信息接口已返回（§2.9）；首页属于前端（T3）。

### 2.8 T0-08 角色、会话失效与请求安全

交付：

- 角色与授权（`Security/AuthorizationPolicies.cs`）：
  - 固定三种角色，按角色集中定义策略：`role:administrator`（仅管理员，例如审计查询）、`role:configurator`（管理员与配置人员，T1 起用于修改配置）；只读状态查询只要求登录；
  - 未登录返回 401 并带 `WWW-Authenticate: Cookie realm="TenonEdge"` 质询头，已登录但角色不符返回 403，均为 ProblemDetails；
  - 作用域校验与构建时校验在所有环境开启（T0-03 起）；角色测试数据由测试夹具直接写库，没有生产后门。
- 会话失效（`Security/SessionSecurityState.cs`）：
  - 会话票据中有用户 ID、角色、安全戳、会话代次与签发协议；每次请求与内存缓存比较，任一不符即拒绝并清除 Cookie，校验不写库；
  - 修改密码、修改角色、停用账号使该用户的缓存失效（安全戳或角色变化）；会话代次加一使全部会话失效，供“只允许 HTTPS”与恢复备份（T5-01、T5-02）调用；
  - 首次设置、主机重置后的重新设置与修改密码都会清除相应缓存。
- 防 CSRF（`Security/CsrfProtectionFilter.cs`）：登录时生成与会话绑定的随机令牌，随会话响应返回；以 Cookie 认证的写请求（GET、HEAD、OPTIONS 以外）须在 `X-CSRF-Token` 请求头中回传，缺失或不符返回 403（code `auth.csrfTokenInvalid`）；匿名例外（登录、首次设置）不校验。
- 请求协议与受信任代理（`Security/RequestSecurityPipeline.cs`、`TrustedProxies.cs`）：
  - 部署参数 `TenonEdge:TrustedProxies` 登记代理的 IP 或 CIDR；没有登记时不启用转发头中间件，全部转发头被忽略；
  - 来自已登记代理的请求，协议只看 `X-Forwarded-Proto`，没有该头时按 HTTP；来源地址取 `X-Forwarded-For` 的最后一跳（ForwardLimit 1）；其他请求按到达的连接判断；
  - 框架在 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` 时自动注册的转发头启动过滤器被移除，设置该变量也不改变行为，并输出一行警告。
- Cookie：HTTPS 下为 `__Host-TenonEdge.Session`（Secure），HTTP 下为 `TenonEdge.Session`；按判定的请求协议选择 Cookie 名称，票据中的签发协议与当前协议不符时拒绝，两种协议的会话不能互换。
- 审计查询 `GET /audit`（仅管理员）在本任务建立，作为按角色授权的第一个接口；筛选与分页的验证属于 T0-09。
- 登录限流（`Security/LoginRateLimiting.cs`）：登录接口按来源地址每分钟 10 次（固定窗口，不排队），超出返回 429 与 Retry-After（code `auth.rateLimited`）；来源地址经受信任代理判定后计数。

| 验收项（PLAN-01 T0-08） | 结果 | 证据 |
|---|---|---|
| 未登录 401、无权 403 | 通过（本地）：匿名请求返回 401 且带质询头；配置人员、只读用户查询审计返回 403，管理员 200 | `SessionSecurityTests` |
| Cookie 写操作校验防 CSRF | 通过（本地）：缺少令牌、令牌错一位都返回 403；读操作不需要令牌；带正确令牌后写操作成功 | `SessionSecurityTests` |
| 伪造转发头不生效 | 通过（本地，真实进程）：<br>• 未登记代理时，伪造的 `X-Forwarded-Proto`、`X-Forwarded-For` 不改变协议判定与来源地址；设置 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` 时同样；<br>• 登记代理后只接受代理的转发头，没有 `X-Forwarded-Proto` 时按 HTTP 处理，即使到达的是 HTTPS 端口 | `RequestSecurityProcessTests`（V-11、FS-10、AT-32 子集） |
| HTTP/HTTPS 会话不能互换 | 通过（本地）：进程内以地址方案区分协议；真实 TLS 下 HTTPS 会话使用带 Secure 的 `__Host-` Cookie，拿到另一种协议下使用被拒 | `SessionSecurityTests`、`RequestSecurityProcessTests` |
| 角色/密码等变化使旧会话失效 | 通过（本地）：<br>• 修改密码后该账号的其他会话失效；会话代次加一后全部会话失效；<br>• 修改角色、停用账号的接口属于 T3-09，测试夹具直接改库并按约定清除缓存，验证的是会话校验机制；<br>• 读取缓存与失效并发时，读取期间被标记失效的旧值不写入缓存（审查后修正） | `SessionSecurityTests`、`SessionSecurityStateTests` |
| 限流按实际来源地址 | 通过（本地，真实进程）：同一地址第 11 次登录返回 429；来自 127.0.0.2 的连接不受影响 | `RequestSecurityProcessTests` |
| 不重复创建运行时 | 不适用：运行时宿主在 T1-01 建立。作用域与构建时校验已开启 | `ServiceProviderValidationTests` |

未覆盖或留给后续任务：

- API 令牌（Bearer，只能经 HTTPS 创建和使用，认证失败单独限流）：T3-09。
- 调用会话代次加一的“只允许 HTTPS”开关与恢复备份：T5-01、T5-02。本批只提供机制并测试。
- 在系统设置中登记受信任代理：T3-09；首次设置之前只能用部署参数登记。
- 完整的浏览器场景（跨站请求、SameSite 行为）：随前端（T3）回归。

### 2.9 T0-09 审计、基础日志与降级记录

交付：

- 迁移 5 `runtime_events`：运行事件表 runtime_event（首次与最近一次发生时间、级别、类型、对象、说明、合并次数），级别只能是 info、warning、error。
- 运行事件写入（`Features/Runtime/RuntimeEventLog.cs`）：
  - 同一对象的同类、同级别事件，自第一次发生起 5 分钟内合并为一条并计数，说明取最近一次；
  - T0 的事件生产者：服务启动完成、开始停止（`service.started`、`service.stopping`），首次设置窗口开放（`setup.windowOpen`，补上 T0-07 留下的一项），运行时启动条件不满足（`runtime.blocked`）；其他生产者随各模块落地。
- 写入失败时的回退（`Storage/StorageWriteFallback.cs`）：审计与运行事件写不进配置库时，不抛出、不阻断当前操作，改写到标准输出；每类记录每分钟最多逐条输出 20 条，额度用完时输出一行提示，其余的只计数，下一次输出前汇总被省略的条数。登录、登出与修改密码失败的审计沿用 T0-07 的写入，改走同一回退；登录失败计数写不进去时的日志同样限额。
- 保留与清理（`Storage/DataRetention.cs`）：
  - 审计默认保留 180 天，部署参数 `TenonEdge:Audit:RetentionDays` 可调长，最长 36,500 天，超出范围或不是整数时以退出码 2 拒绝启动；
  - 只有保存的时间可信的记录才按保留期删除：保存的时间（运行事件取首次发生）不早于 ID 中的逻辑时间戳减 1 小时。写入时时钟落后的记录，保存的时间过早，而逻辑时间戳只是真实时间的下限，两者都说明不了记录有多旧，这类记录不按保留期删除（运行事件仍受 1 万条上限约束）；
  - 运行事件按最近一次发生保留 7 天，最多 1 万条，超出时先删最旧的；
  - 后台服务在启动后与之后每小时清理一次，分批删除（每批 1000 条、一个短事务），失败时记日志，下次重试。
- 查询接口：
  - `GET /audit`（T0-08 建立，仅管理员）：按时间、账号、操作、对象筛选，从新到旧键集分页，本任务补上筛选与分页的验证；
  - `GET /runtime/events`（所有角色）：按时间（事件从首次到最近一次发生的区间与查询区间相交）、级别、类型、对象筛选，按首次发生从新到旧键集分页；
  - `GET /system/info`（所有角色）：版本、NodeId、网关名称、数据目录及其文件系统的总空间与可用空间、HTTPS 证书的 SHA-256 指纹与到期时间、配置库 schema 版本、实际加载的 SQLite 版本与编译选项、首次设置的时间与来源 IP、阻止运行时启动的原因。时钟同步状态随 T1-07 提供。
- 日志：
  - 应用日志只写标准输出，使用带命名字段的消息模板（LoggerMessage）；如需逐条 JSON，可用 ASP.NET Core 自带的部署参数 `Logging:Console:FormatterName=json`，本批未改默认格式；
  - 框架的 `XmlKeyManager` 类别最低级别固定为 Debug（见下文脱敏说明）；
  - 启动时访问配置库遇到 SQLite 错误（例如被其他程序长时间锁定）时，按配置库不可用以退出码 4 结束，不再以未处理异常结束（审查后修正）。

脱敏说明（总设计 §3.3、§10.3）：

- 密码只进入哈希计算与校验，不写入日志、审计或运行事件；请求体与请求头不记录（未启用 HTTP 日志中间件）。
- 登录失败时，不存在的账号名不写入审计：用户可能把密码误输入账号栏（审查发现，已修正）。已有账号只记录库中的账号名。
- 会话 Cookie、防 CSRF 令牌不写入日志；审计与运行事件只记账号、来源 IP、操作、对象、结果与说明，调用方不传入秘密。
- 证书私钥与密钥环只以文件形式保存在数据目录（700）中，日志只记录路径、密钥数量、NodeId、证书指纹与到期时间。
- 框架行为：Data Protection 的 `XmlKeyManager` 在某个密钥元素无法解析时，Error 级别只记录去掉子元素的密钥元素，Trace 级别则记录完整元素，其中含 Linux 上未加密的主密钥（aspnetcore v10.0.0 `XmlKeyManager.WriteKeyDeserializationErrorToLog`）。
  - 处理：在全部选项配置之后，为不指定提供程序的情况，以及控制台、调试器输出、EventSource、EventLog（Windows）四个提供程序分别登记最低级别 Debug 的规则，类别模式为“类别*类别”。
  - 依据：Microsoft.Extensions.Logging 选规则时，指定提供程序的规则优先；其次比较模式字符串的长度，前缀与后缀可以重叠；长度相同时后加入者优先。只含一个通配符、能匹配该类别的模式中，“类别*类别”最长，对上述提供程序，部署参数中的任何规则（包括带通配符的）都不会胜过它。
  - 范围：不在清单中的提供程序仍可被部署参数单独调到 Trace。测试核对宿主实际登记的每个日志提供程序都在清单中，今后增加提供程序时须一并加入（`KeyManagerLoggingTests`）。
  - 最初只按完整类别名登记，独立审查发现可以用更长的通配符规则调回 Trace（§4.7），已修正并纳入真实进程测试。
- 报文正文与点位的值不写入日志和运行事件（总设计 §3.3），相关模块自 T1 起按同一要求实现。

| 验收项（PLAN-01 T0-09） | 结果 | 证据 |
|---|---|---|
| 重启可查 | 通过（本地）：完成首次设置、错误登录后重启，执行主机侧重置，再次启动并重新设置、登录，审计查询按时间顺序列出全部 5 条记录；运行事件在重启后仍在，5 分钟内的两次启动合并为一条并计数 2 | `AuditQueryTests`、`RuntimeEventEndpointTests` |
| 权限正确 | 通过（本地）：审计只有管理员可查（配置人员、只读用户 403，见 §2.8）；运行事件与系统信息三种角色都可查看，未登录 401 | `SessionSecurityTests`、`RuntimeEventEndpointTests`、`SystemInfoTests` |
| 初始化/登录/重置有证据 | 通过（本地）：首次设置、失败与成功的登录、主机侧重置、重置后的重新设置都有审计记录，可经查询接口按账号、操作、对象、时间筛选和分页 | `AuditQueryTests`、`AdministratorResetProcessTests` |
| 密码、Token、私钥不入日志 | 通过（本地，真实进程，API 令牌除外）：全部日志类别调到 Trace，走一遍首次设置、错误与正确登录、修改密码、带会话的读请求与登出，标准输出中没有任何密码、会话 Cookie、防 CSRF 令牌与主密钥；预先放入的无法解析的密钥元素只以去掉子元素的形式出现，其中的哨兵主密钥没有出现 | `AuditAndLoggingProcessTests` |
| 存储异常可测，不无限写日志耗尽磁盘 | 通过（本地，模拟；范围见下）：把审计表或运行事件表改名移走后，登录仍然成功，审计改写到日志；连续 50 次写入失败只逐条输出 20 条，另有一行上限提示，下一分钟汇总省略的条数；时钟回拨时开始新的计数窗口。<br>有上限的是本实现的写入失败回退（审计、运行事件、登录计数）；其他异常日志（例如存储故障期间每个失败请求的错误日志）的总量依赖 journald 或容器日志驱动的轮转与大小上限（部署说明，T5） | `RuntimeEventLogTests`、`AuditQueryTests`、`StorageWriteFallbackTests` |
| 保留期清理 | 通过（本地）：审计超过保留期（默认 180 天）的被删除，调长后保留更久；运行事件按最近一次发生保留 7 天；超过 1 万条时删除最旧的 2,345 条（分 3 批）；宿主启动后即执行清理；保留期短于 180 天或超过 36,500 天时拒绝启动；写入时时钟落后的记录（包括在构建之后很久、以 1970 年的时钟完成设置的情况）不按保留期删除 | `DataRetentionTests`、`DataRetentionSettingsTests`、`AuditAndLoggingProcessTests` |
| 系统信息显示（V-07、AT-24 显示部分） | 通过（本地）：返回的 NodeId、证书指纹与到期时间、SQLite 版本、schema 版本、首次设置时间与来源 IP 与宿主实际状态一致；登记的运行时阻止原因同时出现在系统信息与运行事件中 | `SystemInfoTests`、`RuntimeEventEndpointTests` |

未覆盖或留给后续任务：

- 存储异常只以移走数据表模拟，没有在自动化测试中复现真实的磁盘写满；存储保护（按磁盘阈值拒绝写入）属于 T1-07。
- 配置库写入失败时：已有账号的登录失败计数写不进去，锁定不会生效，只剩按来源地址的限流；修改密码时原密码连续错误到上限、更换安全戳也会失败（返回 500），会话不会因此结束。两者随 T1-07 的存储保护一并考虑。
- 保留期清理以清理时的本机时钟为准：时钟被错误地调快（例如开机时实时时钟快了半年以上）时，审计记录会被提前删除且无法恢复。写入时时钟落后的记录不按保留期删除，可能一直保留；时钟可信之后是否为它们补记时间，与“时钟未同步时暂停清理”一起随 T1-07 的校时检测由维护者决定。
- 测试须在非容器环境、或数据目录位于挂载卷的容器中运行：在容器中以临时目录运行时，数据卷检查会登记阻止原因，就绪检查的部分用例因此返回 503。CI 在托管虚拟机上运行，不受影响。
- 审计 CSV 导出与导出操作本身的审计、系统设置中调整审计保留期：T3-09。
- 运行事件的其他生产者（连接、设备、目标、存储保护、应用、校时）随各模块落地；趋势与完整诊断不在本批。
- 日志的保存与大小上限由 journald 或容器日志驱动负责，部署说明随 T5 编写。
- API 令牌的日志检查：随 T3-09 补充到同一真实进程测试中。

### 2.10 T0-10 必要运行契约与基础数值测试

交付：

- 新工程 `src/TenonEdge.Abstractions`：只依赖 .NET 基础类型，不引用任何 NuGet 包（工程规则测试持续检查）。本批只有测试使用这些契约，Host 暂不引用（§6 决定 41）。
  - 消息（`Messages/`）：
    - `FieldValue`：输出字段的值，四种类型 boolean、integer、number、string（总设计 §6.4）。integer 为有符号 64 位，未经换算的 uint64 寄存器保留无符号 64 位；number 为 decimal 或 double，未经换算的 float32 按单精度写出。NaN 与无穷在创建时就被拒绝（`TryFromDouble`、`TryFromSingle` 返回 false，由调用方判为 nonFinite），不可能写入 JSON。`ToString()` 返回与 JSON 相同的数值文本（布尔为 true/false，字符串为原文），供管理 API“原文字符串附类型”使用；相等比较连同 decimal 的小数位数与浮点数的位模式，与写出的文本一致（decimal 的负零与零写出相同的文本，按零保存）。
    - `FieldType`、`QualityCode`（六个原因码）、`TimeSource`（device、upstream、gateway、gateway-unsynced）：枚举自带字符串转换器，名称由 `JsonStringEnumMemberName` 固定；不配置转换器时也写成这些名称，配置了带命名策略的转换器时同样不变。
    - `EdgeMessage`：最小统一消息，含消息编号、输入标识、设备标识、采集与接收时间（UTC）、时间来源、值与质量。值与质量为不可变、按序号排序的字典，键不重叠，原因码须为已定义的值；采集时间来自网关时必须等于接收时间。来源元数据、原始报文与字段级时间随 T1-02、T2 的使用者补充。
  - 运行计划（`Plans/`）：
    - `RuntimeUnitId`：单元种类加业务标识的确定性标识，例如 `input.modbus/meter-1`；两部分按格式校验，不接受末尾换行。业务标识可以是以 / 分隔的最多 4 段：只在设备内唯一的标识带上作用域，例如数据来源写作 deviceKey/inputKey（总设计 §10.2、§10.5）。
    - `RuntimeUnitPlan`：配置为 JSON 对象，按规范化形式保存（属性按序号排序、去掉空白、字符串统一转义、拒绝重名属性；数值保留原文，1、1.0 与 1E0 视为不同的配置），配置哈希为规范化文本的 SHA-256；配置不是有效的 JSON 对象（重名属性、无效的 UTF-16 字符、嵌套超过 64 层）时统一抛出 ArgumentException。
    - `RuntimePlan`：版本号与运行单元；单元按标识排序，同一标识只能出现一次。
  - 运行单元契约（`Contracts/`）：`IRuntimeUnitFactory.Build` 只做本地计算，不做网络 I/O（包括 DNS 解析），配置问题抛出 `RuntimeUnitBuildException`；`IRuntimeUnit.StartAsync` 启动循环后立即返回；`StopAsync` 等待循环退出，调用方的期限到时抛出取消异常，此时单元仍视为存活（单实例规则）；每个实例只能启动一次，请求停止之后不能再启动；`RuntimeUnitContext` 只含 TimeProvider 与输入的有界通道。
- 测试适配器（替身）`tests/IntegrationTests/Contracts/TcpLineInputFactory.cs`：连接一个 TCP“设备”，每读到一行整数生成一条消息；地址只接受 IP 字面量；读取故意不传取消令牌，停止时由单元释放连接来打断；调用约定写在类注释中（停止之后不能再启动，释放最多等待 10 秒）。T1 的运行时测试可以继续使用。
- 测试：单元测试 83 个用例（`FieldValueTests`、`RuntimePlanTests`、`EdgeMessageTests`），集成测试 16 个（`RuntimeUnitContractTests`、`HostLifecycleTests`）；`RepositoryConventionTests` 改为单元测试工程同时引用 Abstractions 与 Host。

| 验收项（PLAN-01 T0-10） | 结果 | 证据 |
|---|---|---|
| 构建不联网、不采集 | 通过（本地）：构建两个单元后观察 500 毫秒，设备没有收到连接、通道中没有消息；启动后才连接并生成消息。配置错误（缺地址、主机名、端口越界、类型不对、缺设备标识、种类不符）在构建时报结构性失败 | `RuntimeUnitContractTests` |
| 无 Host/ORM/协议类型泄入契约 | 通过（本地）：Abstractions 的锁文件没有任何包；依赖规则禁止它引用包、其他工程与 ASP.NET Core 共享框架 | `RepositoryConventionTests`、§4.8 C1 |
| 请求后不使用释放的上下文 | 契约层通过（本地）：运行单元的上下文只含进程级对象（TimeProvider 与有界通道），单元因此拿不到请求作用域中的服务，这由契约的形状保证；宿主没有登记 `IHttpContextAccessor`；单例依赖作用域服务由作用域校验拒绝（T0-03）。运行时层面的验证在 T1-01 | `HostLifecycleTests`、`ServiceProviderValidationTests` |
| 大整数、decimal/float、非有限值符合既定表示（V-12） | 通过（本地）：2^53+1 写为 `9007199254740993`；ulong 最大值逐位写出；decimal 最大值与 0.0000000000000000000000000001 不用指数，1.50 保留末尾的 0；float32 的 23.7 写为 `23.7`（按双精度会是 `23.700000762939453`）；double 按最短往返格式，1e21 写为 `1E+21`；NaN 与正负无穷不能成为字段值；德语地区设置不影响输出；integer 字段在 10^0 至 10^18 各量级都是不带指数的整数字面量 | `FieldValueTests` |
| 作用域与实例唯一性（BE-AT-03 基础） | 通过（本地）：宿主中的 4 个后台服务（DataProtection、保留期清理、健康检查发布、Web 宿主）各只一个实例，两次解析得到同一实例。目前没有按具体类型登记的后台服务，“按具体类型登记的单例与后台服务是同一个实例”的检查用于防回归，并用构造的错误登记（AddSingleton 加 AddHostedService）证明它能发现第二个实例；运行计划拒绝重复的单元标识 | `HostLifecycleTests`、`RuntimePlanTests` |
| 停止与资源释放 | 通过（本地）：单元阻塞在不响应取消的读取上时，停止在 2 秒内完成（用例总耗时不到 100 毫秒），设备一侧看到连接被释放（Linux 上为连接重置，§6 决定 44）；只能启动一次，启动前请求停止之后不能再启动，也不连接设备 | `RuntimeUnitContractTests` |
| 后台服务未处理异常的默认行为（V-01） | 已验证（本地）：.NET 10 默认 StopHost——宿主停止，记 Error“BackgroundService failed”与 Critical 说明；等待停机的调用正常返回、不抛出异常，`ExecuteTask` 保持故障状态。由此推断进程退出码为 0（`Program` 在 `RunAsync` 返回后返回 0；审查者用相同结构的程序复现了退出码 0）。产品宿主保留 StopHost；现有后台服务（保留期清理）在自己的边界捕获异常，失败时宿主照常服务 | `HostLifecycleTests`；§6 决定 45 |

未覆盖或留给后续任务：

- 运行时宿主、监督者、单实例规则在运行时的执行，以及按计划“全部构建、提交之后再启动”：T1-01。
- 只读边界接口（凭证解析、绑定状态、运行参数、状态查询）本批没有使用者，未定义：T1-04、T1-07、T2-01。
- 消息信封的序列化：T1-02；按字段描述把 JSON 数解析为 integer/number、比例偏移与“保留小数位”：T2-05；AT-30 端到端：T2—T4。
- 宿主因后台服务异常停止时进程的退出码（目前为 0）：T1-01，见 §6 决定 45。

### 2.11 T0-11 MQTT 最小原型

交付（验证代码，不是正式适配器，位于 `tests/IntegrationTests/Probes/Mqtt/`）：

- 测试 Broker：`MosquittoBroker` 用 Docker 运行 `eclipse-mosquitto:2.1.2-alpine`（digest `sha256:38c0da4f2ef84284d47b3b3eeea1cb3bdeabe81ee10caf0cd5c5ff61ee3ea408`），配置为同目录的 `mosquitto.conf`（交付物）：禁止匿名，用户名密码认证，明文 1883 与 TLS 8883 映射到 127.0.0.1 的随机端口。服务器证书由每个实例自己的测试 CA 签发（`TestCertificateAuthority`，SAN 为 localhost 与 127.0.0.1），私钥只在测试运行时生成。
- `TcpFaultProxy`：冻结后双向停止转发，但不关闭连接、不传递对端的关闭，模拟拔网线。
- `MqttProbe`：显式 MQTT 3.1.1、固定 ClientId、显式 CleanSession、保活与超时；每次调用都传入带超时的取消令牌。
- 容器带标签 `tenonedge.probe=mqtt`，释放时删除；Broker 启动失败时，先取出容器的输出写进异常再删除；测试私钥在容器复制之后即从宿主机的临时目录删除；docker 命令超时时结束该进程。
- 用例 22 个：库默认值 2 个不需要 Broker（类别 MqttProbe，CI 执行）；其余 20 个需要本机 Docker 与该镜像（类别 RealBroker），CI 用过滤器排除，本机执行（§6 决定 46）。

| 验证项 | 实测结果 | 与设计 |
|---|---|---|
| 显式 3.1.1 与认证 | 连接成功；匿名与错误密码都得到 CONNACK 结果码 NotAuthorized，ConnectAsync 返回结果而不抛异常，客户端未连接 | 一致；适配层须检查结果码 |
| QoS 1 发布与独立订阅者 | PUBACK：IsSuccess、ReasonCode Success（3.1.1 的 PUBACK 没有原因码）；另一个客户端逐字节收到已知内容（含超过 2^53 的数字、中文、0x00 与 0xFF）；Retain、Dup 为 false。QoS 0 同样送达 | 一致 |
| Broker 暂停响应（docker pause） | PublishAsync 在令牌的 1 秒到期时抛出 MqttCommunicationTimedOutException（实测 1000 毫秒），客户端仍显示已连接；恢复后这条“超时”的消息实际送达订阅者，迟到的 PUBACK 不导致断开（5.2.0 的修复），随后发布正常 | 一致；超时不代表未送达，按 Retry 重发会造成重复（总设计 §8.2，不宣称 exactly-once） |
| 持续 QoS 1 发布时拔网线 | 保活 2 秒，10 秒的观察窗内发布超时 10 次，客户端没有发现断线（IsConnected 为 true）；主动断开、网络恢复后重新连接并发布成功。测试代理在本机替客户端确认数据，模拟不出真实拔线时内核 TCP 重传超时（Linux 默认约 15 分钟）最终使套接字报错的情形 | 与 issue #2244 一致；须按设计 §2.3 在发布连续超时后主动断开重连 |
| 持续 QoS 0 发布时拔网线 | 8 秒内 80 次“成功”、0 次失败，库没有发现断线；调用方每轮发送带 1 秒超时的 PINGREQ（PingAsync）时，拔线后 1.0 秒 PINGREQ 超时（MqttCommunicationTimedOutException） | 设计 §2.3 的“发布连续超时后断开重连”不适用于 QoS 0（不等 PUBACK，不会超时）；QoS 0 输出须定期发送带超时的 PINGREQ，周期与阈值在 T1-04 确定 |
| 空闲时拔网线 | 3.0 秒后发现断线（满一个保活周期发 PINGREQ，再等半个周期） | 一致 |
| TLS | 默认校验拒绝测试 CA 签发的证书；只设置 AllowUntrustedCertificates 不起作用；以签发 CA 为 TrustChain 时接受；另一个 CA 的信任根被拒；主机名不符（TargetHost 为 wrong.example）即使信任根正确也被拒；按 SHA-256 指纹的校验回调只接受该证书。拒绝时抛出 MqttCommunicationException（内部 AuthenticationException），消息一律是“被 RemoteCertificateValidationCallback 拒绝”，不区分原因 | 一致（TLS 显式配置）；拒绝原因须由适配层自己记录 |
| 持久会话 | CleanSession=false 重连时 SessionPresent=1，断开期间发布的 3 条 QoS 1 消息按序送达，无需重新订阅；以 CleanSession=true 连接一次会清除会话，之后 SessionPresent=0、订阅也不在了 | 一致（会话丢失时须重新订阅） |
| 手动确认 | 关闭自动确认后不确认就断开的消息，重连后被重投且 DUP=1；同一条消息确认两次抛出 InvalidOperationException（already acknowledged）；已确认的不再重投 | 一致 |
| 在途窗口 | 不确认时 Broker 只投递 20 条（Mosquitto 默认 max_inflight_messages），按到达顺序确认后其余 10 条送达 | QoS 1 输入的背压可以依靠 Broker 的在途窗口（完整验证在 T2-03） |
| 库的默认值（S-02） | 默认 MQTT 5.0、会话过期 0、CleanSession true、ClientId 随机、选项超时 100 秒、保活 15 秒；TLS 默认在线吊销检查、没有信任根和校验回调 | 一致；须全部显式设置 |

适配层须补齐（T1-04 MQTT 输出、T2-03 MQTT 输入）：

- 显式 3.1.1、稳定 ClientId；每次调用使用“停止令牌 + 超时”的链接令牌（传入可取消令牌时，Connect、Subscribe 不再套用选项中的超时）。
- 检查 ConnectAsync 的结果码：认证失败是结果不是异常，按 SuspendTarget 处理（总设计 §8.2）。
- 发布超时按 Retry 处理，接收方可能收到重复；QoS 1 发布连续超时达到阈值时主动断开重连（#2244）；QoS 0 输出定期发送带超时的 PINGREQ（T0 已实测可以发现断线），超时即断开重连，周期与阈值在 T1-04 确定。
- TLS：以管理员信任的 CA 为 TrustChain，或在校验回调中按指纹信任；不使用 AllowUntrustedCertificates、IgnoreCertificateChainErrors；吊销检查模式显式设置（离线现场）；自带校验回调，记录 SslPolicyErrors 与证书链状态，给出可读的拒绝原因。
- 输入：关闭自动确认，提交后按到达顺序确认；确认绑定连接代次，重连后不为旧连接上的消息发确认；SessionPresent=0 时重新订阅并记运行事件；库按报文头分配接收内存，大小限制在回调中才生效，依靠 Broker 的报文大小上限（总设计 §8.5 检查表）。

未验证（所属任务）：保留消息的 RETAIN 标志与重复订阅（S-06，T2-03）；QoS 0 高速输入加慢回调时的内存（T2-03）；Broker 重启、持久化、积存上限、强制终止的丢失窗口（V-09 实测，T2-07、T5）；客户端证书认证、离线时的吊销检查、WebSocket（随需求）。

### 2.12 T0-12 Modbus 候选库比较

交付（验证代码，不是正式适配器，位于 `tests/IntegrationTests/Probes/Modbus/`）：

- 进程内模拟从站 `ModbusTestSlave`：同时支持 Modbus TCP 与 RTU over TCP，功能码 03/04，寄存器值为 0x1000 加地址；一个连接上的请求按到达顺序逐个处理；可按请求注入延迟、不应答、异常应答、较旧事务号、错误从站地址、CRC 错误、关闭连接、重复的应答帧。
- 两个库的薄封装 `ModbusProbeClients`：只建立连接、设置超时与读取，不补任何库没有的行为，测试观察到的就是库本身的表现。
- 4 个比较测试类共 42 个用例（类别 ModbusProbe，CI 执行）。断言固定的是库的实际行为，包括已知缺陷；注释写明缺陷与适配层的规避办法。

| 行为 | NModbus 3.0.83 | FluentModbus 5.3.2 | 设计要求与适配层 |
|---|---|---|---|
| 两种帧格式读取已知值 | 正确 | 正确 | 基线，不单独作为通过依据 |
| 显式读取超时（两库默认都是无限） | IOException（内部 SocketException TimedOut），约 300 毫秒，只发 1 个请求 | 同步：同左；异步：超时时关闭连接，抛出的异常类型取决于时序（TimeoutException、“连接意外关闭”的 InvalidOperationException 或 ObjectDisposedException），下一次读取 ObjectDisposedException | 显式设置超时；FluentModbus 异步超时后须重连，且不能据异常类型判断是否超时 |
| 库内重试 | 默认 3 次、间隔 250 毫秒：一次读取 1957 毫秒、4 个请求（每次新的事务号）；重试 1 次时 2 个请求、约 850 毫秒 | 没有库内重试 | 设为 0，由调度器重试（总设计 §6.1） |
| 读取阻塞时释放连接（超时为无限） | 同步读取在释放后 1 毫秒内结束（IOException） | 同步：同左；异步：取消令牌在 1 毫秒内结束读取，同时关闭连接 | 专用线程加“停止时释放连接”可行（总设计 §6.1、§8.1） |
| 异步 API | 不接受取消令牌，内部用 Task.Factory.StartNew 执行同步读取（依据源码，未单独测线程占用）；取消无效，只能释放连接 | 接受取消令牌 | NModbus 只用同步 API，跑在每个连接的专用线程上 |
| 从站忙（异常码 06） | 默认无限重试：2 秒内重发 8 次仍不返回；设置 SlaveBusyUsesRetryCount 后按重试次数结束（SlaveException） | 立即抛出 ModbusException（ServerDeviceBusy） | NModbus 须设 SlaveBusyUsesRetryCount=true |
| Modbus TCP 迟到应答（较旧事务号） | 默认：之后每个请求都读到上一个请求的应答，因事务号不符而失败，错位不会自行恢复；设置 RetryOnOldResponseThreshold=5 后跳过旧帧，结果正确 | 不校验事务号：把旧应答当作本次结果返回（请求地址 50 得到地址 0 的值），之后一直错位，没有任何错误 | 设计要求跳过较旧事务号的应答：NModbus 设阈值即可（阈值比较不覆盖事务号回绕，适配层另行处理）；FluentModbus 无法在库外补救 |
| Modbus TCP 应答的单元标识不符 | IOException（slave address does not match） | 不校验，照常返回数据 | — |
| 下一帧与本帧被同一次读取收下 | 按 MBAP 中的长度逐帧读取，结果正确，多出的帧留给下一个请求（默认因事务号不符失败） | 按本次读到的全部字节截取：请求 2 个寄存器得到 8 个，后一帧的 13 个字节被当作寄存器数据返回，那一帧丢失 | FluentModbus 的缺陷，无法在库外补救 |
| RTU over TCP 迟到应答 | 当作下一个请求的结果（请求地址 50 得到地址 0 的值）；发送前“清空缓冲”对网络流无效 | 同左 | RTU 帧没有事务号，须由适配层在超时后按静默间隔排空：实测静默 300 毫秒、丢弃 9 个字节后读取恢复正确（设计默认 500 毫秒，待实测） |
| RTU CRC 错误 | 立即 IOException（Checksums failed to match） | 把不合格的帧当作尚未收完，等到读取超时（约 300 毫秒）才报超时 | FluentModbus 无法区分 CRC 错误与不应答 |
| RTU 从站地址不符 | 立即 IOException | 等到读取超时 | 同上 |
| 对端关闭连接 | 立即 IOException（Read resulted in 0 bytes returned） | TCP：立即 InvalidOperationException（connection closed unexpectedly）；RTU over TCP：读到 0 字节后继续循环，同步读取 2 秒后仍未结束（超时 300 毫秒不起作用），释放连接才结束；异步方法在每次读取都同步返回 0 时从不让出线程，调用 ReadAsync 的线程空转、调用不返回，只能由其他线程取消令牌 | FluentModbus RTU over TCP 的缺陷：连接关闭后空转占用 CPU，异步时还占住调用线程 |

选型建议（待维护者认可，不作为已批准）：NModbus 3.0.83。它校验事务号（可设阈值跳过旧帧）、单元标识、功能码与 CRC 并立即报错，按帧长度读取，释放连接能可靠地打断读取，从站忙与重试都可配置。FluentModbus 的 TCP 不校验事务号与单元标识、会把后一帧当作数据返回，RTU over TCP 在连接关闭后空转，这些都发生在库内部，适配层无法补救，除非上游修复或自行维护分叉；它的长处（异步与取消令牌）在“专用线程 + 释放连接”的设计下不是必需，而且它的异步读取在连接关闭后会占住调用线程空转。

无论选哪个库，适配层都要补齐（T2-02）：显式读写超时；库内重试设为 0，NModbus 同时设 SlaveBusyUsesRetryCount=true；同步调用跑在每个连接的专用线程上，停止时释放连接；TCP 设置 RetryOnOldResponseThreshold 并处理事务号回绕；RTU over TCP 超时后按静默间隔排空，排不净时重连；异常映射为原因码：超时为 readTimeout，SlaveException 为 deviceException（附异常码），其他 IOException（CRC、从站地址、事务号、对端关闭）为 readError 并按需重连；一个从站超时不使同一连接上的其他从站失败（调度器）；请求间隔。

未验证（T2-02）：功能码 01/02 与全部数据类型、四种字节/字序、超过 2^53 的 64 位值、NaN 与无穷的位型、寄存器取位；ACK（异常码 05）循环；事务号回绕；共享连接上多个从站的调度与额外等待的上界（FS-19）；读取阻塞时停止运行单元（FS-20，本批只验证了库层的“释放连接打断读取”）；串口服务器与实物设备。

### 2.13 T0-13 独立发布与阶段证据汇总

交付：

- `src/TenonEdge.Host/TenonEdge.Host.csproj` 声明 `RuntimeIdentifiers` 为 linux-x64：还原时一并解析该平台相关的 NuGet 资产，Host 的锁文件新增 `net10.0/linux-x64` 一段（4 个包的平台资产）；普通构建不受影响。自包含发布用的运行时包（Microsoft.NETCore.App.Runtime 等）不在锁文件中，版本由 global.json 固定的 SDK 决定。
- 自包含发布：`dotnet publish src/TenonEdge.Host/TenonEdge.Host.csproj -c Release -r linux-x64 --self-contained -o <目录>`，112 MB、344 个文件，含 `libe_sqlite3.so`；SQL Server 驱动栈不在发布物中（D-09 的排除对自包含发布同样有效）。
- `.github/scripts/verify-published-host.sh`：在未预装 .NET 的干净容器中验证发布物（7 项，见下表），CI 与本机共用；先拉取镜像并记录 digest，读取失败即终止；每一项的说明同时写入摘要与控制台；删除容器之前重新保存每个容器的全部输出，任何一步失败时证据目录中都有完整的输出；容器、数据卷与临时文件结束时删除。
- CI（`.github/workflows/backend-ci.yml`）：测试用过滤器排除 RealBroker 类别；新增自包含发布与干净容器验证两步，干净镜像按 digest 固定；上传测试报告、环境记录与发布核对证据（含发布物的 SHA-256 清单）。测试发布物只在作业中生成和验证，不作为制品上传：仓库公开，上传即对外提供下载，而 SqlSugarCoreNoDrive 的许可仍待核查（D-09），按总设计 §17.3 在处理之前不得分发。作业超时改为 30 分钟。actionlint 检查通过。
- 依赖与许可汇总：`docs/reference-notes.md` 新增 §1.3（自包含发布带入的 .NET 运行时）与 §4（只用于测试的依赖、镜像与测试夹具）。

发布物验证（本机执行，§4.8 C7、C10、C16；干净镜像 `mcr.microsoft.com/dotnet/runtime-deps:10.0`，digest `sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da`，Ubuntu 24.04.5，只含原生依赖）：

| # | 检查 | 结果 |
|---|---|---|
| 1 | 镜像中没有 .NET | 通过：没有 dotnet 命令，/usr/share/dotnet 与 /usr/lib/dotnet 都不存在（逐项检查） |
| 2 | 启动、健康检查、实际 SQLite 版本（V-01、V-07） | 通过：以 /opt/tenonedge/TenonEdge.Host 启动，存活与就绪均为 200，日志记录 SQLite 3.53.3 |
| 3 | 经 API 首次设置与系统信息 | 通过：首次设置 200；系统信息返回 NodeId、SQLite 3.53.3 与 HTTPS 证书指纹 |
| 4 | 正常停止；换安装路径后的持久化（V-06、V-08） | 通过：SIGTERM 后退出码 0；以 /srv/relocated 重启，重启前的会话 Cookie 仍有效，NodeId 与 HTTPS 证书不变（密钥环、身份与配置库都在数据卷中，配置库连接设置在启动时逐连接回读核对） |
| 5 | 同一数据卷上的第二个实例（AT-10） | 通过：退出码 3，输出说明数据目录正被另一个进程使用 |
| 6 | 数据目录不在已挂载的卷上（V-08，T0-06 的容器检查） | 通过：数据目录在容器的可写层（日志显示位于挂载点 / 的 overlay）时，存活 200、就绪 503，日志说明原因并提示为数据目录挂载数据卷 |
| 7 | 实际加载低于 3.51.3 的 SQLite（V-07） | 通过：换入 SQLitePCLRaw.lib.e_sqlite3 2.1.11 的原生库（SQLite 3.49.1，包的 SHA-512 与 nuget.org 登记一致），存活 200、就绪 503，日志说明低于 3.51.3、运行时不会启动 |

发布物日志中的两条警告：Kestrel 提示覆盖了镜像设置的 `ASPNETCORE_HTTP_PORTS=8080`（宿主按部署参数显式配置监听，T5-04 制作镜像时去掉该变量）；Data Protection 的“未配置 XML 加密器”按 §6 决定 17 处理。

资料核查（2026-09-30 复核，只读资料，未安装任何前端依赖、未创建 web/）：

- V-02 前端构建环境（npm 的 latest 标签）：vite 8.3.1（Node ^20.19 或 ≥22.12）、react 19.3.0、tailwindcss 4.3.3、@xyflow/react 12.12.0、@tanstack/react-table 9.2.4、@tanstack/react-virtual 3.14.13、@tanstack/react-router 1.170.41、@tanstack/react-query 5.104.0、typescript 7.0.2、zustand 5.0.15、react-i18next 17.0.15、openapi-typescript 7.13.0、openapi-fetch 0.17.0、oxlint 1.86.0、vitest 5.0.3（Node ^22.12、^24 或 ≥26）、@playwright/test 1.63.0、react-hook-form 7.89.0、zod 4.6.5；typescript 与 @playwright/test 为 Apache-2.0，其余为 MIT。Node.js 最新 LTS 为 24.21.0（Krypton，2026-09-07），最新版本 26.10.0。与开发准备 S-09 一致；vitest 5 要求 Node 22.12 以上，构建环境宜用 24 LTS。主版本在 T3 按前端设计选定，不因“最新”而默认采用。
- V-09 附带 Broker：Mosquitto 最新标签 v2.1.2（GitHub），Docker Hub 的 `eclipse-mosquitto:2.1.2-alpine` 于 2026-09-18 重建；2.0 线为 2.0.22；NanoMQ 最新 0.25.6（2026-08-19）。T0-11 已实际运行 2.1.2，验证了禁止匿名与默认的在途窗口 20；持久化、积存上限、会话过期与强制终止的丢失窗口仍以 S-04 的资料为准，实测在 T2-07，写入部署文档在 T5。
- V-10 点表粘贴与大表格：资料与 S-10、S-09 相同：粘贴事件的 clipboardData 不受安全上下文限制（由文档推断，须 V-03 实测）；TanStack Table 不内置虚拟化，官方示例使用 TanStack Virtual。原型与测量在 T3。

T0 阶段结论（待验收）：

- 本阶段工作已执行：T0-01—T0-13 都有实现、测试与本地证据。T0-A、T0-B、T0-C 均已验收并在本地提交（T0-C 为 `591a064`，2026-10-01）。
- 实际 CI 报告：没有。最终版本的工作流已按 CI 的步骤在本机干净副本中完整执行并通过（§4.8 C16），但 GitHub Actions 须推送后才运行，本批未获推送授权（§7）。
- 进入 T1 的条件（开发准备 §1、开发计划 §3.5）：
  1. V-01、V-06、V-07、V-08：开发环境与自包含发布物都有结论，没有阻塞性的未通过项。V-07 的 Docker 镜像部分随 T5-04 的镜像；V-08 的密钥环合并随 T5-02。
  2. V-04 中与发布有关的结论：QoS 1 的发布超时与持续发布时的断线检测、QoS 0 输出用 PINGREQ 发现断线、TLS 证书校验均已实测，适配层要求见 §2.11。
  3. D-07：维护者 2026-10-01 确认（§7 第 11 项）。
  4. D-05：未确认，T1 实现连接与凭证 API 之前须确认。
  - 结论：技术条件已具备；D-07 于 2026-10-01 确认后，维护者授权进入 T1 第一批（T1-01—T1-03）。
- 遗留事项与所阻塞的任务见 §7。

### 2.14 T1-01 运行管理与种子计划

交付：

- 新工程 `src/TenonEdge.Runtime`（引用 Abstractions，包只有 Microsoft.Data.Sqlite 与 Microsoft.Extensions.Logging.Abstractions）与 `src/TenonEdge.Adapters`（引用 Abstractions，不引用任何包）；Host 引用三者。三个工程与 Host 一样声明 `RuntimeIdentifiers` 为 linux-x64，使自包含发布能以 locked mode 还原（§6 决定 74）。工程规则测试自动覆盖两个新工程（Runtime 不得引用 ASP.NET Core、ORM、协议库；Adapters 不得引用 ASP.NET Core、ORM、SQLite）。
- 运行单元契约随运行时调整（§6 决定 54）：
  - `IRuntimeUnit` 只实现循环 `RunAsync(stoppingToken)`，启动、停止、异常捕获与重启都由监督者负责；单元在停止令牌上注册的回调须在 `RunAsync` 返回之前释放（§6 决定 80）；
  - 输入工厂改名为 `IInputUnitFactory`，上下文改为 `InputUnitContext`：deviceKey 与 inputKey 由运行时从单元标识的两段解析，另含时钟、时钟同步状态、读取结果上报与本输入的有界通道；
  - 输出单元的工厂随 T1-04 另行定义。
- 监督者（`Runtime/Lifecycle/SupervisedUnit`、`UnitSupervisor`）：
  - 在线程池线程上运行单元的循环；异常在单元边界捕获，记录最后错误（只含说明，最长 500 字），释放该实例，按退避构建新实例再运行；
  - 退避从 1 秒起按 2 倍递增，最长 60 秒，随机缩短不超过 10%；连续运行满 60 秒后从头计算。循环没有请求停止就结束也算故障。故障记运行事件 `runtime.unitFaulted`；
  - 停止：取消令牌的回调（例如单元释放连接）在线程池上执行，不在请求停止的调用中内联执行；监督循环等回调执行完，才释放实例并结束，停止时正在运行的实例不会与回调同时释放。回调出错时，在循环结束之前记为停止故障（运行事件 `runtime.unitStopFailed`），不向上抛出；回调阻塞时循环不结束，由停止期限判定“停止超时”，单元仍占用其标识（§6 决定 75）；
  - 故障后先启动退避计时，再公布故障与下次重启时间：状态显示“退避”时计时已经开始（§6 决定 79）；
  - 状态转换：“停止中”只从运行中或退避转入；“已停止”只由监督循环在结束时设置，停止不会把它改成别的状态；再次停止时保持“停止超时”，超时事件只在第一次转入时记录（§6 决定 80）；
  - 单实例：上一个实例的循环退出并释放之后才构建下一个；停止期限（每个单元 5 秒）内没有退出的单元标为“停止超时”并记事件，继续占用其标识，同一标识不启动新实例，直到它真正退出。
- 运行时（`Runtime/Lifecycle/RuntimeHost`，宿主级单例，不是后台服务）：
  - `Prepare` 只构建、不启动：每个输入单元得到自己的有界通道与读取状态；没有注册的种类、业务标识不是两段、工厂抛出的结构性错误都记为构建失败及原因；
  - `StartCore` 启动发送缓存写者；
  - `ActivateAsync` 启动构建成功的输入单元，构建失败的不启动并记事件（启动时的规则，总设计 §7.4）；
  - `StopAsync` 按停机顺序：停止全部输入 → 写者提交请求队列中已有的请求后停止 → 关闭请求队列、之后到达的请求报告失败 → 关闭读连接池；某一步意外出错只记日志，后面的步骤照常执行；重复或并发的调用等待同一次停止。
- 宿主侧的协调者（`Host/Features/Runtime/RuntimeCoordinator`）是唯一的运行时后台服务，按同一个单例登记为托管服务。启动判定在宿主开始接收请求之前完成，依次为：
  1. 运行时启动条件不满足（SQLite 低于 3.51.3、容器中数据目录不在持久卷上）时什么都不启动，状态为 Blocked；
  2. 丢弃提交前中断的 pendingRevision，标为“未生效：进程中断”；
  3. 处理“运行中”标记：仍在说明上次非正常退出，该版本从未稳定时非正常退出次数加一；
  4. 导入内容有变化的种子快照；
  5. 核对有效快照的内容哈希，按启动保护判定（见下文）；
  6. 加载有效版本时，先落盘“运行中”标记，再构建它的运行单元，构建或激活过程中崩溃都计入。新导入或自动回滚生成的版本在提交之后、激活之前落盘；它们在提交之前的构建中崩溃不计入启动保护，由中断上限处理（见下文，§6 决定 76）；
  7. 启动写者，然后启动输入。
  - 之后在后台计时，连续运行满稳定时间（默认 60 秒，部署参数 `TenonEdge:Runtime:StableAfterSeconds`）把版本记为“已稳定”。
  - 停止时只有正常停止才清除标记：后台服务故障时保留，下次启动计为非正常退出。停止运行时的过程中出错只记日志，仍按正常停止清除标记。
  - 导入种子或自动回滚在提交前连续中断（例如构建运行单元时进程崩溃）：同一份种子连续 3 次后不再导入（原有效版本不变，记运行事件）；回滚到同一目标连续 3 次后进入“运行失败”。这样不会形成崩溃循环，快照也不会无限增长（§6 决定 76）。
    - 只计最近一段连续的中断：同一内容之后有一次生效过、或因其他原因（例如构建失败）未生效的版本，之前的中断就不再计入；
    - 提交或放弃时配置库写入失败留下的 pending，下次启动同样标为“进程中断”并计入；
    - 被封顶的种子只有内容改变才会再次导入：内容哈希按规范化之后的内容计算，只改格式（空白、属性顺序）不算改变。
- 种子快照（§6 决定 56）：
  - 部署参数 `TenonEdge:Runtime:SeedSnapshot` 指向一个运行计划文件（`{"schemaVersion":1,"units":[{"kind","key","configuration"}]}`），内容按运行计划规范化，单元排序，配置用 T0-10 的规范化形式，内容哈希与文件格式无关；
  - 内容与上次导入的不同时按“冻结、准备、提交”导入：冻结分配新版本号（不低于下限、不复用）并写入 pending；准备阶段有任何构建失败即放弃，标为“未生效”并注明原因，原有效版本不受影响；成功则单事务提交，previous = 原 effective、effective = 新版本，记审计 `runtime.applySeedSnapshot`（操作人“系统”）与运行事件；
  - 种子的内容哈希在提交或放弃时记下：同一份种子不再重复导入，包括没有生效的那次；冻结之后、提交之前进程退出时不记，下次启动丢弃中断的版本后重新导入同一份种子；
  - 文件缺失或内容无效只记运行事件 `runtime.seedRejected`，不生成版本；
  - 路径不是绝对路径、稳定时间无效都属于部署参数错误：在启动检查中与其他部署参数一起校验，宿主以退出码 2 结束并说明原因，不在数据目录中留下文件（审查前漏了这一步，进程以 134 崩溃，见 §4.11）。
- 配置库迁移 6（`runtime_snapshots`）：
  - `runtime_snapshot` 表：版本号、schema 版本、规范化内容与内容哈希、来源（seed、autoRollback）及补充说明、状态（pending、effective、history、notEffective）与原因、是否已稳定及时间、非正常退出次数、创建时间与操作人、生效时间；
  - SystemState 增加 effective、previous、pending 三个指针、“运行中”标记 `running_revision` 与 `seed_hash`。版本号每次分配都推进 `revision_floor`，删除旧版本之后也不复用。
- 启动保护（总设计 §7.4、§8.4，D-07，`StartupProtection`）：
  - 只针对从未稳定的有效版本，非正常退出累计 3 次触发；
  - 上一生效版本曾经稳定时自动回滚一次：新版本内容与它相同，来源 autoRollback，操作人“系统”，同样经“冻结、准备、提交”，记审计 `runtime.autoRollback` 与运行事件；
  - 上一版本从未稳定、没有上一版本，或有效版本本身就是自动回滚生成的，进入“运行失败”：输入不启动，写者照常运行，`/readyz` 为 503，管理接口可用，记运行事件 `runtime.runFailed` 与 Critical 日志；
  - 有效快照的内容哈希不符同样进入“运行失败”，配置不清空；
  - 本批走出“运行失败”的办法是换一份种子（新版本从零计数）；界面上的重试与回滚在 T3-06。
- 就绪：新增运行时就绪条件。有效版本已激活或尚无有效配置时就绪；启动判定中、启动条件不满足、运行失败、停止中返回 503（总设计 §10.1）。
- 退出码：后台服务因未处理的异常结束时，宿主停止后进程以退出码 6 结束（`HostExitCodes.BackgroundServiceFailed`），不再是 0（§6 决定 45、62）。退出码在宿主停止之后、服务容器释放之前取得。
- 测试：
  - 单元测试：`RestartBackoffTests`（5）、`StartupProtectionTests`（8）、`RuntimePlanDocumentTests`（15）、`RuntimeSettingsTests`（7）、`HostExitCodesTests`（2）；
  - 集成测试：`UnitSupervisorTests`（11）、`RuntimeCoordinatorTests`（19）、`StartupProtectionProcessTests`（4，真实进程），改写后的 `RuntimeUnitContractTests`（11，经真实运行时），`HostLifecycleTests` 补充运行时只创建一次的检查。
  - 测试基础设施：`FixedDataDirectoryApplicationFactory` 可传入额外部署参数与测试用的服务（例如测试用的输入工厂），释放前先等宿主发出“已停止”（§6 决定 72）；`RuntimeStateReader` 直接读取配置库中的版本元数据；`RuntimeTesting` 装配运行时与记录事件的替身。

| 验收项（PLAN-01 T1-01） | 结果 | 证据 |
|---|---|---|
| 单元实例唯一 | 通过（本地）：<br>• 故障重启 6 次，同时存活的实例始终为 1，每个实例释放之后才构建下一个；<br>• 不响应取消的单元停止超时后仍占用标识，同一标识的新实例被拒绝，旧循环退出后才允许启动；<br>• 运行时宿主与协调者各只有一个实例，协调者作为后台服务与按具体类型取得的是同一个实例；<br>• 运行计划拒绝重复的单元标识 | `UnitSupervisorTests`、`HostLifecycleTests`、`RuntimePlanTests` |
| 构建不采集 | 通过（本地）：<br>• 准备两个单元后观察 500 毫秒，设备没有收到连接；激活后才连接并生成消息；<br>• 放弃的计划（释放后）不能再激活，不连接设备，也不改变运行时的状态；<br>• 令牌已取消时运行单元立即返回、不连接；<br>• 种子导入在准备阶段失败时原有效版本照常激活 | `RuntimeUnitContractTests`、`RuntimeCoordinatorTests` |
| 异常受控 | 通过（本地）：<br>• 单元抛出异常或循环意外结束，记录最后错误与运行事件，按 100、200 毫秒（测试参数）退避后重启，退避到期之前不重启；<br>• 重新构建失败同样退避重试；<br>• 一个单元反复故障时，另一个单元照常运行；<br>• 连续运行满退避上限（测试中 10 秒）之后再故障，等待回到初始的 100 毫秒；<br>• 取消回调抛出异常：停止照常完成，记为停止故障，不向上抛出；经协调者停止时写者照常停止、迟到的请求报告失败、“运行中”标记照常清除；<br>• 取消回调阻塞 1.5 秒、停止期限 200 毫秒：不到 1 秒返回并报告“停止超时”，回调结束后才变为已停止；<br>• 停止超时之后再次停止：等待期间保持“停止超时”，到期后仍报告超时，超时事件只有一条；<br>• 循环看到取消标志就自行退出、取消回调仍阻塞：同样报告“停止超时”并占用标识，实例在回调结束之后才释放，之后才变为已停止；<br>• 在记录故障事件的那一刻把可控时钟推进到退避到期：按时重启（退避计时在公布故障之前已经开始）；<br>• 后台服务故障时退出码为 6；运行时部署参数无效时退出码为 2（真实进程） | `UnitSupervisorTests`、`RuntimeCoordinatorTests`、`HostExitCodesTests`、`StartupProtectionProcessTests` |
| 进程终止验证启动保护（FS-05 的 T1 部分） | 通过（本地，真实进程）：<br>• 正常停止（SIGTERM）两次不计数；<br>• 运行从未稳定的版本时被 SIGKILL 三次，第四次启动进入“运行失败”：就绪 503、存活 200、进程不退出，正常停止退出码 0；换一份种子后新版本从零计数、照常就绪；<br>• 已稳定的 r1 上被强制终止不计入；新版本 r2 被强制终止三次后，第四次启动自动回滚为 r3（内容同 r1，来源 autoRollback，操作人“系统”，有审计），照常就绪；<br>• r3 再被强制终止三次后进入“运行失败”，不再回滚 | `StartupProtectionProcessTests`；进程内同类场景见 `RuntimeCoordinatorTests` |
| 种子快照与最小有效版本元数据 | 通过（本地）：<br>• 种子导入为 r1，三个指针、运行中标记、seed_hash、审计与事件正确；种子不变时不重复导入；内容变化生成 r2，r1 转为历史；<br>• 准备失败的版本标为“未生效”并注明原因，版本号不复用；无效或缺失的种子不生成版本；<br>• 满稳定时间记为“已稳定”；正常停止清除运行中标记；<br>• 留下的标记只对从未稳定的版本计数；<br>• 有效快照内容被改动时进入“运行失败”且内容原样保留；提交前中断的 pending 版本标为“未生效：进程中断”；<br>• 种子导入在冻结之后、提交之前中断时，下次启动重新导入同一份种子；同一份种子连续 3 次中断后不再导入，原有效版本不变；回滚到同一目标连续 3 次中断后进入“运行失败”；之后一次因构建失败而未生效的导入会打断连续，之前的中断不再计入；<br>• 加载有效版本时，工厂构建运行单元的那一刻“运行中”标记已经落盘 | `RuntimeCoordinatorTests` |

未覆盖或留给后续任务：

- 应用新版本时的静默、切换与第 3 步的停止超时处理（激活一次之后再换计划）：T3-06。本批每个进程只激活一次，换版本靠重启。
- 界面上的重试与回滚、版本历史与保留数量（最近 20 个生效过的版本）：T3-06。本批不删除旧版本。
- 回滚须通过的总设计 §8.3 目标变更检查：本批没有待发记录的目标与指纹，T1-06、T3-06。
- 运行状态的管理 API（总览、运行单元与设备状态）：T1-07。本批通过日志、运行事件、就绪与配置库核对。
- 后台服务故障导致的退出码 6 只有单元测试：产品中没有令后台服务故障的途径，进程级未验证（§6 决定 62）。
- 宿主停机超时与容器停机宽限期对齐（Docker 默认 10 秒）：T5-04。停机超过宽限期被强制终止时，从未稳定的版本会计一次非正常退出。

### 2.15 T1-02 统一消息与模拟输入

交付：

- 标识与消息：
  - `BusinessKeys`：业务标识的字符规则（ASCII 字母、数字和 -、_、.，长度 1—64，首字符为字母或数字，总设计 §6.5）。`RuntimeUnitId` 的各段与 `EdgeMessage` 的 deviceKey、inputKey 都按它校验。
  - `EdgeMessage` 的 InputId 为设备内唯一的 inputKey，与 deviceKey 一起确定数据来源。
- 标准信封 `StandardEnvelope`（总设计 §6.5、§9.4）：
  - 字段为 schemaVersion、messageId（投递编号）、sourceMessageId、deviceKey、collectedAt、timeSource、values、quality；values 与 quality 总是写出，可以为空对象；
  - quality 中每个字段为 `{"status":"bad","reason":…}`，数值按 `FieldValue` 逐位写出；
  - collectedAt 为 UTC，固定三位毫秒并带 Z 后缀；时间来源与原因码的名称与枚举的 JSON 名称一致（测试核对）。
  - 报文用 JSON 写入器生成，是纯函数。本批只有测试使用它，产品中的使用者是 T1-04 的输出适配器（生成冻结的报文）。
- 时钟同步状态 `IClockSyncStatus`：只有确认未同步时，网关生成的时间记为 gateway-unsynced；未知或已同步时为 gateway。检测方式在 T1-07 核实（V-14），此前产品中的状态总是“未知”（§6 决定 66）。
- 读取状态 `InputReadStatus`（功能需求 §7.2）：
  - 最近一次全部成功为正常，有字段失败为部分失败，整组失败为失败中，连续 3 个周期整体失败为离线；
  - 离线与恢复各记一条运行事件 `device.offline` / `device.online`，对象为 deviceKey；
  - 另计读取成功、失败与跳过的周期数（overrun），只在内存中，持久化在 T1-07。
- 有界通道：每个输入一个，容量 100（总设计 §8.4），由运行时持有。单元故障重启后沿用同一个通道与读取状态。
- 模拟输入 `Adapters/Simulator`（种类 `input.simulator`，GW-07，功能需求 §3.5）：
  - 配置：`periodMs`（默认 5000，1000—86400000）；`seed`（可选，省略时由 deviceKey/inputKey 的 SHA-256 推出）；`template` 为 meter 或 `sample`（自定义 JSON 样本，一层字段，值为数值、布尔或字符串）二选一；`faults` 设定缺字段、非法值、连接错误三种比例（0—1）。未知的配置项、越界与类型错误在构建时报结构性失败。
  - 序列：由 SplitMix64 与配置决定，与运行时间和通道状态无关。每次读取消耗固定个数的随机数，因此第 k 次读取的结果只取决于 k；落后时被跳过的周期不读取，序列也不前进。
    - 电表模板：电压 214.0—226.0 V、电流 8.00—12.00 A、频率 49.90—50.10 Hz 按整数步长随机取值；功率（kW）为电压 × 电流 ÷ 1000 舍入到三位小数；电能（kWh）按功率与周期累加。全部为 decimal，逐位确定。
    - 自定义样本：每周期原样发出。
  - 故障：
    - 连接错误时整组读取失败，不生成消息，上报失败原因；
    - 非法值使一个数值字段读到 NaN，按总设计 §6.4 从值中省略、标为 bad（nonFinite），不变成 0；
    - 缺字段使一个字段不出现，既不在值中也不在质量中（“未携带”）。
  - 运行：
    - 固定周期的计时器决定第 n 个周期在启动后 n × 周期开始；
    - 每次读取生成 UUIDv7 编号，采集时间等于接收时间（读取完成的时刻），时间来源取决于时钟同步状态；
    - 通道满时等空位，到下一个周期开始仍无空位就丢弃本条并计 overrun，不在单元内另建缓冲；
    - 进程被暂停而落后多个周期时，按单调时钟补计被跳过的周期；
    - 停止时在当前等待处返回，不计 overrun。
- 测试：
  - 单元测试：`BusinessKeysTests`（18）、`StandardEnvelopeTests`（5）、`SimulatorSequenceTests`（27，含序列的固定值）、`InputReadStatusTests`（3）；
  - 集成测试：`SimulatorInputTests`（9，可控时钟 FakeTimeProvider）。

| 验收项（PLAN-01 T1-02） | 结果 | 证据 |
|---|---|---|
| 同种子/配置可复现 | 通过（本地）：<br>• 同一种子与配置 500 个周期的结果完全相同，换种子后不同；<br>• 种子 42 的第一个周期固定为“电压 216.6、电流 11.21、频率 49.97、功率 2.428、电能 74156.003”，序列不随 .NET 版本变化（SplitMix64，不用 `Random`）；<br>• 经运行时激活的两个同种子单元 20 个周期逐条相同（含非法值与缺字段），只有消息编号不同 | `SimulatorSequenceTests`、`SimulatorInputTests` |
| 契约正确 | 通过（本地）：<br>• 每个周期一条消息：采集时间依次为启动时刻加 0、5、10、15 秒，等于接收时间；<br>• deviceKey 与 inputKey 来自单元标识，编号为 UUIDv7 且不重复；<br>• 时钟未同步时为 gateway-unsynced，未知或已同步时为 gateway；<br>• 标准信封的字段、顺序与总设计 §9.4 的平台报文一致，测试按完整的期望文本逐字核对；超过 2^53 的整数与 ulong 最大值逐位写出；<br>• 不符合字符规则的设备与输入标识被拒绝 | `SimulatorInputTests`、`StandardEnvelopeTests`、`BusinessKeysTests` |
| 满通道不无限占内存 | 通过（本地）：<br>• 通道容量 2、无人取走时推进 10 个周期，通道中始终只有 2 条，跳过 8 个周期并计 overrun；<br>• 取走之后先得到早先的两条，再得到正在等待的当前周期，被跳过的周期不会补出来；<br>• 停止时无论在等下一个周期还是在等空位都立即返回；<br>• 读取之后的处理被拖住 3 个周期：补计 2 个被跳过的周期，下一次读取在第 3 个周期，不补出错过的读取 | `SimulatorInputTests` |
| GW-07：可复现正常、缺字段及连接错误场景 | 通过（本地，后端）：<br>• 1 万个周期中连接错误、非法值与缺字段的出现次数在配置比例附近；<br>• 连接错误不生成任何字段，连续 3 个周期后判为离线并记事件 | `SimulatorSequenceTests`、`SimulatorInputTests`、`InputReadStatusTests` |
| AT-07 子集：异常可见、不伪造 0 或旧值 | 部分通过（本地，输入侧）：非法值标为 bad（nonFinite），从值中省略；连接错误不生成消息。必填字段、非法 JSON、溢出与超限在映射与输出侧，T2-05、T1-04 | 同上 |
| AT-29 子集：输入通道有界 | 部分通过（本地）：通道满时等到截止并丢弃计数，内存中最多容量那么多条。全局在途字节预算（64 MiB）在 T1-07 | `SimulatorInputTests` |

未覆盖或留给后续任务：

- 输入通道的消费者（分发器）与路径：T1-04。没有它之前，产品中模拟输入的通道写满后每个周期都计 overrun（§6 决定 70）。
- 全局在途字节预算、调试缓存、计数持久化与状态查询：T1-07。
- 校时检测（V-14）：T1-07。
- 来源元数据（source）、原始报文与字段级时间：T2 的协议输入。

### 2.16 T1-03 持久化发送缓存

交付（`Runtime/Deliveries`，§6 决定 68、69、71）：

- queue.db：
  - 位于数据目录，由 Runtime 直接用 Microsoft.Data.Sqlite 读写，与 config.db 分开；新建文件只有属主可以读写，-wal、-shm 沿用。
  - 打开时 journal_mode=WAL；写者的连接与初始化连接显式设置 synchronous=FULL 与 busy_timeout，并回读核对，不符即拒绝。读连接只执行查询，不设置 synchronous（它只影响写入的持久性）。
  - 实际加载的 SQLite 低于 3.51.3 时拒绝打开；运行时启动条件本已拦截这种情况，这里是第二道检查。
  - 表结构版本保存在 `PRAGMA user_version` 中，由更新版本的程序迁移过的库拒绝打开。
  - 打开时记一行日志：路径、SQLite 版本、journal_mode、synchronous 与表结构版本（V-07 复验）。
- 表结构版本 1：
  - `queue_meta`：所属 configStoreId 与创建时间。
  - `delivery`：
    - 标识与归属：seq（AUTOINCREMENT，删除后不复用）、deliveryId（唯一）、connectionId、sourceMessageId、deviceKey、路线 ID 与节点标签、生成记录的配置版本号；
    - 冻结的内容：报文（BLOB）、topic 或 path、QoS、非秘密请求头（按名称排序的 JSON）；
    - 目标指纹与可读摘要；
    - 状态与处理信息：创建时间、失败次数、状态（pending、failed、quarantined）、隔离原因、最后错误、迁移编号。
  - 索引：(connection_id, state, seq)。
- `DeliveryDraft`：
  - deliveryId 在生成报文之前分配，报文中的 messageId 就是它。
  - 构造时校验：报文 1 字节到 256 KiB、QoS 只能是 0 或 1、deviceKey 符合规则、连接与版本号为正数，并复制一份报文，之后不受调用方缓冲区的影响。
  - 凭证不进入记录。
- 唯一的写者（`DeliveryWriterUnit`，由监督者管理，标识 `delivery.writer/queue`）：
  - 全部写操作经同一个有界请求队列（1024 个请求，满时提交方等待）。
  - 分组提交：收到第一个请求后最多再等 50 毫秒，或凑满 1000 条，把期间的请求放进一个 `BEGIN IMMEDIATE` 事务。一个请求的记录（例如一条源消息发往多个目标）总在同一事务中。
  - 提交成功：先更新状态，再报告成功与分配的 seq，此时才算“接管”（总设计 §8.5）。
  - 提交失败：整批显式回滚，批中每个请求都报告失败（它们都没有被接管）；状态记为“存储异常”，记事件 `storage.fault`，写连接重新打开；之后的批次成功时清除“存储异常”并记 `storage.recovered`。
  - 每个请求在自己的保存点中执行：违反约束（例如投递编号与已有记录重复）只回滚并拒绝该请求，报告“数据错误”，计入被拒绝的记录数；同批其他请求照常提交，不记“存储异常”（§6 决定 77）。
  - 打不开发送缓存时（文件损坏、权限、SQLite 版本或持久化设置不符），同样先记“存储异常”与事件，再让请求队列中已有的请求报告“发送缓存不可用”，然后异常退出，由监督者按退避重启后再试；
  - 写者因其他原因异常退出时，已取出的请求报告失败，其余请求留给监督者重启的新实例。
- 读取：
  - `ReadPending(connectionId, afterSeq, limit)` 用短连接与短事务，按 seq 读某个目标已提交的待发记录，最多 1000 条；
  - WAL 下读不到未提交的写入，也不阻塞写者。
- 删除：
  - `DeleteAsync(seqs)` 经写者在下一次分组提交中删除已确认的记录，返回实际删除的条数。
  - 发送者在 T1-04 使用；“已确认但删除尚未提交的记录不再发送”由发送者在内存中标记，也在 T1-04。
- 停机：写者收到停止请求后不再等提交间隔，把请求队列中已有的请求全部提交后退出；之后请求队列不再接受新请求，迟到的请求报告“发送缓存已停止”。
- 测试：
  - 集成测试 `DeliveryCacheTests`（15，全部使用磁盘上的真实 SQLite 文件；等待一律有期限，写者异常时用例失败而不是挂起）；
  - 进程测试 `ConfigStoreProcessTests.QueueDatabaseUsesTheSameSqliteWithWalAndFullSynchronousWrites`（真实宿主）。

| 验收项（PLAN-01 T1-03） | 结果 | 证据 |
|---|---|---|
| 发送只取已提交记录 | 通过（本地）：<br>• 另一个连接中尚未提交的插入读不到，提交后读到；<br>• 入库请求报告成功时记录已可读；<br>• 失败批次的记录读不到 | `DeliveryCacheTests` |
| 失败批次不假装接管 | 通过（本地）：<br>• 用触发器使一条记录写入失败（触发器中整数溢出，SQLite 报一般错误，与磁盘写满等同类）：同一事务中的两个请求（4 条记录，涉及两个目标）全部报告失败，原因为回滚的 SQLite 错误（integer overflow），两个目标都读不到任何记录；<br>• 状态为“存储异常”（失败批次 1、记录 4），记一次 storage.fault；<br>• 之后的请求照常提交，状态恢复并记 storage.recovered；<br>• 等待方收到失败结果时状态中已是“存储异常”；<br>• 违反约束的请求（投递编号与已提交的记录重复）只拒绝该请求，同批的正常请求照常提交，不记“存储异常”，被拒绝的记录数为 2 | `DeliveryCacheTests` |
| 重启保留已提交数据 | 通过（本地，写者重启）：停止写者、以新的缓存实例重新打开同一文件，seq、deliveryId 与报文逐字节相同。强制终止进程后的保留与重复范围（FS-14）在 T1-08 | `DeliveryCacheTests` |
| 冻结报文与目标 | 通过（本地）：报文（含 2^64−1 的整数文本）、topic、QoS、按名称排序的请求头、路线标签、指纹与摘要、配置版本号原样读回；超过 256 KiB 的报文、QoS 2、不合规的设备标识、空报文与同一请求中重复的投递编号被拒绝 | `DeliveryCacheTests` |
| FIFO 与大量积压 | 通过（本地）：两个目标交错写入 5000 条（100 个请求），按 seq 游标每页 100 条读取一个目标的 2500 条，边读边删除已读记录：不重不漏、顺序与入库一致；另一个目标的 2500 条不受影响；删除最大 seq 之后新记录的 seq 仍然更大 | `DeliveryCacheTests` |
| 分组提交与背压 | 通过（本地）：200 个并发请求用了不到 20 次提交（提交间隔 100 毫秒）；请求队列满时提交方等待，取消只结束等待；写者开始运行后，排队的请求照常提交 | `DeliveryCacheTests` |
| 停机时的最后一次提交 | 通过（本地，经运行时）：提交间隔设为 30 秒，停止前有 3 个请求在等待提交；停止后 3 个请求全部报告已提交，4 条记录可读；停止之后到达的请求报告“已停止” | `DeliveryCacheTests` |
| 发送缓存打不开 | 通过（本地，经运行时）：queue.db 的位置被目录占住时，排队的请求立即报告“发送缓存不可用”，状态为“存储异常”并记事件，不挂起；排除问题后监督者按退避重启写者，之后的请求照常提交，状态恢复；由更新版本的程序迁移过的库同样如此；<br>• 写者初始化之后打开写连接失败：已取出的本批（3 条）报告失败并计入失败记录数 | `DeliveryCacheTests` |
| V-07 复验（queue.db） | 通过（本地，真实宿主进程；干净容器中的自包含发布物见 §4.10 D9）：<br>• 日志记录的 queue.db 版本与配置库相同（3.53.3）；journal_mode=wal，synchronous=2（FULL）；<br>• queue.db 及 -wal、-shm 只有属主可读写；<br>• 进程退出后库文件仍为 WAL，表结构版本 1，所属 configStoreId 与配置库一致；<br>• 由更新版本的程序迁移过的 queue.db 拒绝打开 | `ConfigStoreProcessTests`、`DeliveryCacheTests` |

未覆盖或留给后续任务：

- 四类发送结果、失败次数、失败区：T1-05。配额、保留期、过期计数与目标指纹隔离：T1-06。
- 存储保护（磁盘空间）、计数持久化（最长 10 秒落盘一次）与状态查询：T1-07。
- 强制终止进程时的保留与重复范围（FS-14），以及 queue.db 损坏后移到一旁并新建（FS-16）：T1-08。
- 恢复备份或配置库来源不同时的“配置库来源不同”隔离：T1-06、T5-02。本批只记下所属 configStoreId。

### 2.17 T1-04 MQTT 输出、分发器与当前凭证

本次在迁入 Claude 会话 `47c15c52-807d-4aa9-bc44-3fcf48e8abeb` 的 T1-01—T1-03 工作区上继续。维护者 2026-10-08 答复 D-05：“采纳推荐：默认作废，管理员可显式保留并审计”。只推进 T1-04 及它所需的写入结果分类；T1 第一批仍待验收，没有提交或推送。

交付：

- **输出定义与发送实例**：Abstractions 的 `IOutputTarget` 只提供本地报文格式化和构建发送实例；`IOutputAdapter` 只发送冻结的报文。Adapters 引用 MQTTnet 5.2.0.1603，协议类型不进入 Runtime 或 Abstractions。客户端在第一次发送时创建，归被监督的单个发送者持有，循环退出之后释放，没有断开回调中的重连任务。
- **种子配置**：schemaVersion 仍为 1。新增 `output.mqtt`，字段为 `connectionId`、`host`、`port`、`tls`、`usePassword`、`topic`、`qos`、`timeoutMs`、`pingIntervalMs`、`clientId`、`inputs`。默认 QoS 1、请求超时 5 秒、PING 间隔 15 秒；未显式填写 ClientId 时由持久 NodeId 与 connectionId 组成，跨重启稳定、不同网关互不冲突。IPv6 主机规范化为 URI 绑定使用的同一压缩形式。只支持 `{deviceKey}` Topic 占位符。引用为明确的 `deviceKey/inputKey`；一个连接只定义一个输出，可引用多个输入，单输入最多 32 条投递。准备阶段检查引用、重复连接与同一 Broker 的 ClientId 冲突，不做网络 I/O。MQTT 配置的未知字段在冻结前拒绝，密码不能借未生效快照落盘。
- **分发与发送**：每个有输出的输入一个有界通道消费者；一条源消息的多目标记录一起提交。发送者只读已提交记录，每目标串行、单条在途、FIFO。存储回滚时最多保留本输入的一条消息及 32 条冻结记录，按监督者退避重试同一组稳定编号。入库的数据约束错误明确拒绝；发送后删库失败按存储故障处理。`DeliveryWriteStatus` 区分 `Committed`、`Rejected`、`StorageFailed`、`Stopped`。
- **协议确认**：显式 MQTT 3.1.1、CleanSession=true、不 retained；QoS 1 只在 PUBACK 返回成功后删库，连接/发布/显式 PING 共用本次操作的显式期限，取消与超时不视为 Ack。QoS 0 只证明本地发送完成；持续发布时仍定期带期限 PING，失败即重建客户端并退避。协议确认不等于业务落库确认。
- **删除边界**：目标状态在内存中保留唯一已确认 seq，跨发送者重建继续重试删除，不重发。进程退出后该标记丢失，删除未提交的记录仍可能重发；不承诺 exactly-once。
- **凭证**：config.db 迁移 7，`mqtt_credential` 以种子 connectionId 为逻辑引用主键，不自行分配新的雪花 ID。`ICredentialResolver` 只返回当前值对象；格式化只读非秘密身份，暂停期间仍可入队，发送才取得绑定接收方的密码。Data Protection 固定用途加 connectionId，秘密不进入种子、快照、queue.db、响应、审计或输出事件。
- **管理 API**：`GET /api/edge-admin/v1/connections/{connectionId}/mqtt-credential` 所有已登录角色只读状态；`PUT` 仅管理员/配置人员，并继承会话、密码必改与 CSRF 过滤器。已有记录须带 `expectedVersion`；同用户名且有待发数据时更新密码必须显式声明 `accountChanged`，选择更换账户时生成随机账户标记。响应仅包含 connectionId、接收方、版本、configured、更新时间。
- **D-05**：PUT 更新 scheme/TLS、主机或端口的绑定时，默认清除受保护密码；仅管理员可显式 `retainExisting=true`，凭证与审计在同一事务中提交。种子提交也在切换有效版本的同一事务中作废不匹配的旧绑定；审计写入失败则整体回滚。恢复旧地址不会恢复已经作废的密码。发送前始终校验当前接收方和目标指纹；不匹配的记录保持待发且停止发送。
- **停机**：停止输入、封闭并排空通道/分发器、写者最后提交、取消在途发送并释放客户端。排空超时记运行事件，内存中尚未接管的消息可能丢失。此次常规停止与等待 PUBACK 时的停止已测试，强制退出矩阵仍在 T1-08。
- **工程**：实际重新生成 NuGet 锁文件，MQTTnet 进入生产依赖与许可清单。CI 已配置拉取固定版本 Mosquitto 并执行 RealBroker；报告核查仍拒绝零用例、失败和跳过。GitHub Actions 未执行。

T1 操作顺序（全量连接管理在 T3）：先以无种子宿主完成首次设置，通过受保护 PUT 写入所需 connectionId 的用户名/密码及 `mqtt[s]://主机:端口` 绑定；正常停机后启动含非秘密输出定义的种子。启动时缺少可解密且匹配接收方的凭证，种子不会成为有效版本；修复凭证后须调整种子内容以触发新的导入（沿用 §2.14 的失败种子不重复导入规则）。已运行目标的密码轮换直接生效，更新版本使暂停的发送者重新探测。

本次边界与未覆盖：

- 不实现完整连接 CRUD、网页、配置在线应用、共享 MQTT 输入/预览客户端或出站地址白名单；共享管理与出站校验为 T2-01，网页和在线应用为 T3。
- 当前认证仅 MQTT 用户名/密码；TLS 使用系统信任链，没有忽略证书错误选项。正式适配器已验证拒绝不可信证书；系统信任链上的成功 TLS 发布、管理员 trustedCa/客户端证书上传流程本次未验证或未实现。
- 为满足当前 MQTT 发送提供 Retry 退避、认证/TLS 暂停和凭证更新后探测；完整四分类、连续拒绝阈值、毒消息和失败区在 T1-05。本次 `RejectMessage` 保守停止目标，不冒充已实现失败区。
- 指纹变化的旧记录不发送，但尚未转入持久隔离区，也会阻塞本目标队头；隔离、持久手动暂停、迁移、配额/保留期在 T1-06/T3。失败区还未存在，凭证账户声明的查询本次只覆盖待发记录。
- 在途字节预算、持久化计数、磁盘存储保护、状态 API、校时在 T1-07；强制终止进程、queue.db 损坏与完整退出/重复矩阵在 T1-08。真实 Broker 是 Docker Mosquitto，不是实物设备或完整生产部署验收。

### 2.18 T1-05 发送结果、退避与失败区

维护者 2026-10-08 指示“先本地提交然后继续开发”；先提交已有 T1-01—T1-04 为 `c97521d`，再按依赖推进 T1-05。本次涉及 Abstractions 结果契约、Runtime 发送与 queue.db、MQTT 异常分类、凭证轮换的失败区检查及相应测试。无新增依赖。已读参考项目总汇总和 app-functions-sdk-go 笔记，仅借鉴思路，没有移植源码。

- 四类结果：Ack 继续确认后删除；Retry 按 connectionId 在原受监督循环中指数退避并抖动、成功后复位；SuspendTarget 保持待发且不加失败次数；RejectMessage 只将该条移到失败区。连续 20 条拒绝、期间无成功时暂停目标，凭证代次变化后重新探测。暂停/退避状态跨监督者重建保留，进程重启后先单条探测。
- `OutputSendResult` 增加 `CountsTowardPoison` 和可选 `RetryAfter`，后者不缩短指数退避且最高 60 秒。网络、超时、主动取消不计入毒消息；MQTT 未识别的响应/本地未知异常才计入。适配器原异常文字不落盘或写入事件；结果说明须由适配器提供脱敏文本。重试仍产生运行事件。
- 毒消息：失败次数达到 10 时只读后续首条未达阈值的记录探测；后续成功后立即重试队头，队头再次出现计入的失败才进入失败区。没有可探测记录时继续队头退避，不按次数丢弃。探测后网络失败撤销判定依据，须再次成功探测。
- 结果存储：失败次数使用绝对值，状态更新只匹配 pending；跨发送者重建保留一个待提交结果，存储失败仅重试写入，不再次发送。失败区状态/满额删除和累计计数同事务；失败次数不会因写入重试累加。
- queue.db 迁移至版本 2，增加 `failed_at` 与 `delivery_failure_totals`。旧库迁移保留编号、正文、配置归属及待发记录。失败区可按目标/seq 分页读取；拒绝、毒消息、满额不保留及过期数量按目标持久累计，不与存储失败计数混用。
- 失败区初始参数为每目标 100 条、全局 1,000 条、单独保留 7 天，可在宿主装配时调整。这是保守初值，不是容量实测承诺；按当前单条 256 KiB 上限，正文最多约 250 MiB（不含 SQLite/WAL/索引开销）。满额只丢新失败记录并计数，不挤出已有失败记录。由唯一写者在循环中每隔约一分钟（闲置时最多两分钟）最多清理 100 条过期失败记录；运行期间墙钟相对单调时间跳变超过 1 小时则保守不清理，时间顺序异常也不清理。跨重启校时与完整存储保护仍属 T1-06/07。
- 当前 MQTT 凭证轮换的账户声明检查同时覆盖待发和失败区数据，防止仅剩失败记录时绕过声明。

边界：HTTP 状态及业务码在本批用确定性结果替身验证，真实 HTTP 分类在 T2-04。手动暂停、指纹隔离与待发配额为 T1-06；完整状态 API、在途预算、计数和磁盘保护为 T1-07；网页查看/人工重试/清理/导出为 T3-08。没有创建产品前端。平台设备级拒绝语义属于 T4。

### 2.19 T1-06 目标隔离、持久手动暂停、配额与保留期

维护者在 T1-05 交付后指示“继续”，本次沿 PLAN-01 推进 T1-06。保留 T1-05 未提交改动，不自动推进 T1-07。前置 T1-03/05 的队列与四类结果已通过本地验证；D-05/D-07 沿已确认决定。涉及 Abstractions、Runtime 发送/存储、Host 控制与启动、MQTT 身份核对及测试；无新增依赖或后台服务。

- **隔离边界**：输出目标提供当前非秘密指纹。启动目标核对、每次身份变化及发送前核对待发与失败记录；适配器在取当前凭证时再次发现不匹配，则用四类结果中的 SuspendTarget 携带 `FingerprintMismatch`，发送者转为隔离，不暂停后续新身份记录。隔离只经唯一写者提交；写入失败时保持停止发送并重试，返回旧账户不会自动解除隔离。
- **三种原因**：`targetChanged`、`targetMissing`、`configStoreChanged`。有效计划中的目标缺失才按“不存在”隔离；有输出单元构建失败时保守跳过缺失判定，不能把构建失败当成删除。宿主确认没有有效配置时核对空目标集；损坏或启动保护中的有效配置不作此处理。queue.db 所属配置库变化时，在初始化事务中隔离旧记录、更新归属，新记录正常接管；旧隔离记录也明确标为来源不同。
- **冻结数据**：隔离保留 seq、deliveryId、sourceMessageId、报文、Topic/QoS/请求头、指纹、创建时间和原失败时间/次数。可按目标/seq 分页读取隔离记录；隔离累计数量随状态变更同事务持久化。隔离不占目标待发配额，仍计入全局待发/隔离字节配额。
- **手动暂停**：config.db 迁移 8 新增 `output_control`，按连接逻辑 ID 保存暂停及恢复代次；状态与审计同事务，审计失败则回滚。`GET/PUT /api/edge-admin/v1/connections/{connectionId}/delivery-control` 使用现有认证、角色、CSRF 与密码必改过滤器；GET 所有已登录角色可读，PUT 仅管理员，必须 `confirmed=true`，并用 `expectedVersion` 防止覆盖别人更新。读回的 `resumeVersion` 用于下次写入的版本核对。
- **发送语义**：手动暂停期间照常入队，重启保持，凭证变化不会解除；显式恢复也清除自动暂停，重新单条探测。发送前再检查暂停，退避期间每 250 毫秒观察控制变化。已经在途的一条发送允许在协议超时内结束，接口描述明确这一边界。停机仍沿输入→分发→最后提交→发送者的现有顺序。
- **配额**：默认每目标最多 10,000 条或 64 MiB，全局待发与隔离合计 256 MiB。字节为正文及冻结 UTF-8 文本、加每条 256 字节固定开销的逻辑计费值，不是 SQLite/WAL 文件占用。入库时在写者事务中逐目标核对，符合配额的投递与超额拒绝计数一同提交；单目标超额不拒绝其他目标。`QuotaRejectedRecords` 明示本请求的超额拒绝数。失败区继续使用独立配额。
- **保留期**：待发默认 24 小时，隔离沿用原创建时间；从失败区隔离的记录保留原失败时间及独立保留期。唯一写者现有维护循环分批清理，每轮最多 100 条待发/隔离记录和 100 条失败记录；过期与删除同事务计数。墙钟相对启动单调时间跳变超过 1 小时不清理；未来创建时间、未来失败时间或创建时间距启动超过一年时持久标记 `clock_anomaly`，保守保留供诊断。该一年阈值是当前保守实现值，非时钟同步检测或 SLA。
- **配置保护**：种子导入及启动回滚在准备阶段检查积压，提交时复核；存在待发/失败区数据时，删除目标或改变主机、端口、TLS、Topic 被拒，原有效版本保留。超时变化允许，隔离数据不阻止修改接收方。T3 在线应用仍须完成停止输入后的复核与完整跨库协同。
- **初始化失败**：隔离核对无法提交时不启动发送，运行时标为失败、readyz 为 503；管理 API 与 healthz 保持可用。修复存储后重启重新核对，不在失败状态下绕过隔离。
- **迁移**：queue.db 版本 3 增加逻辑字节、时钟异常标记和策略计数表。版本 1/2 升级保留原记录、失败区元数据与计数，按同一 UTF-8 规则补算字节。config.db 版本 8；两种库都继续拒绝新于当前程序的 schema。

验证边界：新增 27 个集成用例，含真实 SQLite、HTTP 管理 API、Host 重启与发送结果替身；原有真实 MQTT Broker 场景全量回归。不是网页、实物断电、生产容量或所有跨库故障矩阵验收。完整状态查询、在途预算、磁盘保护和校时仍在 T1-07；强制退出/损坏矩阵在 T1-08；网页清理、重试、导出及迁移在 T3-08。本次没有实现迁移记录到新接收方，也未创建 web/。

### 2.20 T1-07 资源保护与运行诊断基础（后端软件交付完成，待验收）

2026-10-08 维护者授权“开始 T1-07，直到可以可视化访问系统”。基线为 `c97521d` 加工作区已有 T1-05/06 改动，保留这些改动；不提交、推送或发版。按 T1-07 → T1-08 → T2 → T3 顺序推进，网页不提前加入 T1/T2。

本轮实现校时子项：Host 的 `ClockSyncStatus` 以 Linux x64 libc `adjtimex` 的零 modes 只读查询内核状态，10 秒单调时钟缓存；不可用或未支持平台返回 unknown，不修改系统时钟，不依赖 systemd/D-Bus 或启动 Shell。状态变化写入既有运行事件；输入时间来源解析与 `GET /api/edge-admin/v1/system/info` 共用同一实例，API 返回 synchronized / notSynchronized / unknown。内核状态不等于实际时间准确性的保证。其他架构仍为 unknown，未声明 ARM64 支持。

实际证据（Linux x64、SDK 10.0.401）：

- `dotnet build TenonEdge.sln -c Release --no-restore`：修改前和校时实现后均通过，0 警告、0 错误；日志 `/tmp/tenonedge-t1-07-baseline-build.log`、`/tmp/tenonedge-t1-07-clock-build.log`。
- `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~ClockSyncTests|FullyQualifiedName~SystemInfoTests' --logger 'trx;LogFilePrefix=t1-07-clock' --results-directory /tmp/tenonedge-t1-07-clock-tests`：单元 10、集成 7，通过 17、失败/跳过 0。覆盖内核返回值（含闰秒状态）、缓存/事件变化、本机原生调用和三状态 API 序列化及既有系统信息权限。TRX 与日志位于上述临时目录和 `/tmp/tenonedge-t1-07-clock-tests.log`。
- ABI 辅助核查：读取本机 `/usr/include/x86_64-linux-gnu/bits/timex.h`；独立 C 查询 `/tmp/tenonedge-clock-abi.c` 编译后输出 `sizeof_timex=208 result=0`。这是 ABI 辅助证据，不替代项目/容器测试，不复制 glibc 源码。

追加进展：实现文件系统空间保护（默认低于 500 MB 或 5% 即拒绝新增入队；空间不可读取时保守拒绝），保留确认删除和发送能力，恢复后自动解除。队列写者在每批提交前及空闲最长 10 秒检查，状态变化记事件。queue.db 增至 schema 4，新增固定用途的全局计数表，存储保护拒绝、接管和确认删除/数据拒绝计数与该批同事务持久化；不将存储保护拒绝混作目标配额拒绝。`GET /runtime/status` 为所有已认证角色提供协调者/运行单元/队列/空间保护/校时和持久计数；计数不可读取时返回 null 而不是伪造零。

- 空间测试使用注入的空间读数及真实临时 SQLite，并非真实磁盘写满/断电。覆盖字节和比例阈值、不可读、恢复、保护下删除及重启计数。
- 首轮定向测试：28 通过、1 失败；旧版本迁移夹具降到 v2 时遗留新增 v4 表，已修正夹具为真正旧结构，未修改迁移绕过问题。v1 夹具同样更新。
- 修正后命令：`dotnet test tests/IntegrationTests/TenonEdge.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~StorageProtectionTests|FullyQualifiedName~DeliveryCacheTests|FullyQualifiedName~DeliveryPolicyTests|FullyQualifiedName~DeliveryFailureStoreTests|FullyQualifiedName~SystemInfoTests|FullyQualifiedName~ManagementApiAccessTests|FullyQualifiedName~OpenApiDocumentTests' --logger 'trx;LogFileName=storage-api.trx' --results-directory /tmp/tenonedge-t1-07-storage`：49 通过，0 失败/跳过；日志 `/tmp/tenonedge-t1-07-storage-api-test.log`。

- 全量回归首轮发现真实进程测试仍断言 queue.db schema 3（单元 335 通过；集成 395 通过、1 失败），同步为 schema 4 后重跑：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-progress-r2' --results-directory /tmp/tenonedge-t1-07-regression`，单元 335、集成 396，合计 731 通过、0 失败/跳过。日志 `/tmp/tenonedge-t1-07-regression-r2.log`，TRX 同目录。此次为当前已有测试回归，不证明尚未实现的 T1-07 预算或 T2/T3 能力完成。

在途预算追加进展（仍未完成 AT-29）：已加入进程共享额度、预算输入通道、分发提交前保留额度及输出最坏报文字节预留，运行状态 API 暴露当前/峰值预留量。通道关闭会唤醒等待共享预算的写者并返回 ChannelClosedException，保留关闭原因，不释放其他通道持有的额度；调用方取消和通道容量等待失败均归还本次已取得额度。

- 定向验证：`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~InFlightBudgetTests --logger 'trx;LogFileName=budget-close.trx' --results-directory /tmp/tenonedge-t1-07-budget-close`，5 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-budget-close.log`。覆盖跨通道共享额度、HoldingReader 持有到显式释放、取消、正常/异常关闭唤醒。首次构建因新增 CTS 字段未实现释放触发 CA1001，改为无须释放的完成信号与写操作局部 CTS 后通过。
- 这些单元测试不证明实际 SQLite 提交边界或进程堆内存上界。生产者仍在预约前构造消息，超大消息的拒绝计数、WaitToWriteAsync 的预算背压、真实运行时停止/提交的额度测试仍待补齐；不得将当前预留量视为完整接收至提交的内存计量。
- 当前代码全量回归：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-budget-close' --results-directory /tmp/tenonedge-t1-07-budget-regression`，单元 340、集成 396，合计 736 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-budget-regression.log`，TRX 位于上述目录。`git diff --check` 通过。这仍是已有用例回归，不能覆盖上述未实现边界。

周期校时追加进展：新增宿主管理的 `ClockSyncMonitor`，启动后及每 10 秒访问共用 `IClockSyncStatus`，无采集或 HTTP 请求时也检测，停止时取消等待并释放定时器；不另建运行时实例。定向单元命令 `dotnet test tests/UnitTests -c Release --no-restore --filter 'FullyQualifiedName~ClockSync' --logger 'trx;LogFileName=clock-monitor.trx' --results-directory /tmp/tenonedge-t1-07-clock-monitor`：11 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-clock-monitor.log`，包含假时钟下周期与停止验证。加入宿主集成用例前的全量回归（同前述命令，前缀 `t1-07-monitor`、目录 `/tmp/tenonedge-t1-07-monitor-regression`）为单元 341、集成 396，共 737 通过，0 失败/跳过，日志 `/tmp/tenonedge-t1-07-monitor-regression.log`。

- 宿主集成补证：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~SystemInfo' --logger 'trx;LogFileName=monitor-host.trx' --results-directory /tmp/tenonedge-t1-07-clock-monitor`：8 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-monitor-host.log`。新用例不发 HTTP 请求，核对实际宿主只注册一个监测服务、使用同一校时状态对象、真实 10 秒周期读取以及宿主释放后后台任务正常完成。探针状态为测试替身，不宣称此测试完成 Docker 内核验证。首版夹具替换全宿主时间为假时钟时未完成，已主动中断且不计通过（日志 `/tmp/tenonedge-t1-07-monitor-host-interrupted.log`）；宿主用例改为真实时钟，确定性时间推进仅保留在独立监测器单元测试中。`git diff --check` 通过。

审计时钟保护追加进展：`DataRetentionService` 按时间清理前要求校时状态为 synchronized，并核对当前墙上时间与服务构造时的时间加单调运行时长，偏差超过一小时则暂停。状态变化写日志。未同步、未知、前跳/回拨时保留审计和按时间过期的运行事件，但运行事件仍执行 1 万条上限；不引入审计条数截断。正常时间线恢复后自动恢复清理。测试显式注入校时状态，不依赖 CI 机器是否同步。

- 保护边界：一小时沿用既有时间容差；这是运行期间的异常跳变保护，不证明内核所称“同步”就一定准确。启动时已错误且被报告为同步的时间仍无法单靠本机判断。若开机后从错误日期校正且与构造基准偏差超过容差，本次进程保守暂停按时间清理，下一次正常启动重新建立基准；不将错误时间重新采样成可信基准来解除保护。
- 定向命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~DataRetentionTests|FullyQualifiedName~HostLifecycleTests' --logger 'trx;LogFileName=retention-clock.trx' --results-directory /tmp/tenonedge-t1-07-retention-clock`：18 通过、0 失败/跳过；真实临时 SQLite，覆盖未知/未同步、正负 400 天跳变、恢复、正常保留期及事件条数上限。日志 `/tmp/tenonedge-t1-07-retention-clock.log`。
- 全量回归首轮单元 341 通过，集成 401 通过、1 失败：既有 `NModbusLeavesAFollowingTcpFrameForTheNextRequest` 首次读取超时，尚未证明根因，不改测试放宽断言。日志 `/tmp/tenonedge-t1-07-retention-regression.log`。按 `dotnet test tests/IntegrationTests -c Release --no-build --no-restore --filter 'FullyQualifiedName~NModbusLeavesAFollowingTcpFrameForTheNextRequest'` 单独重复 5 次均通过，日志/TRX 在 `/tmp/tenonedge-t1-07-modbus-repeat/`；不将重复通过写作根因已修复。
- 当前全量重跑：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-retention-r2' --results-directory /tmp/tenonedge-t1-07-retention-regression-r2`：单元 341、集成 402，合计 743 通过、0 失败/跳过。日志 `/tmp/tenonedge-t1-07-retention-regression-r2.log`。保留上述偶发失败记录。

V-14 原生/容器补证：同一项目 `ClockSyncTests` 输出 OS、架构、实际内核返回值与状态；本机 Ubuntu 26.04 x64 与 Docker Ubuntu 24.04.5 x64 均为 `adjtimex=0; State=Synchronized`。不修改系统时间来制造未同步；未同步/未知分支由内核返回值分类用例验证，不宣称真实改变过宿主同步状态。ARM64 未实测，当前返回 unknown。

- 原生命令：`dotnet test tests/UnitTests -c Release --no-build --no-restore --filter 'FullyQualifiedName~ClockSyncTests' --logger 'trx;LogFileName=clock-native.trx' --results-directory /tmp/tenonedge-t1-07-clock-native`：10 通过、0 失败/跳过，原生查询输出在 TRX 中。
- 容器使用本机已有 SDK 镜像，未拉取/修改镜像，固定摘要 `mcr.microsoft.com/dotnet/sdk@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29`；.NET 10.0.12、镜像自带 VSTest 18.7.0（此项为跨部署检测补证，不替代项目固定 VSTest 的回归）。构建日志 `/tmp/tenonedge-t1-07-clock-docker-build.log`。容器网络关闭、全部 capabilities 移除、根文件系统与项目挂载只读，无 systemd/D-Bus 依赖，无 SYS_TIME 特权；执行后自动删除，仅操作专用测试容器。
- 容器命令：`docker run --rm --name tenonedge-t1-07-clock-probe --network none --cap-drop ALL --security-opt no-new-privileges --read-only --tmpfs /tmp:rw,nosuid,nodev -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -v "$PWD:/workspace:ro" -v /tmp/tenonedge-t1-07-clock-docker:/results mcr.microsoft.com/dotnet/sdk@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 dotnet vstest /workspace/tests/UnitTests/bin/Release/net10.0/TenonEdge.UnitTests.dll '/TestCaseFilter:FullyQualifiedName~ClockSyncTests' /ResultsDirectory:/results '/Logger:trx;LogFileName=clock-docker.trx' '/Logger:console;verbosity=detailed'`：10 通过、0 失败/跳过。该镜像未产出请求的 TRX，实际证据为完整控制台日志 `/tmp/tenonedge-t1-07-clock-docker-r2.log`（含原生探针输出），不声称存在容器 TRX。镜像报告 workload 验证提示，未执行 workload 更新，测试正常完成。

在途预算生产入口追加修正：新增 `InputMessageWriter.ProduceAsync`，运行时在调用读取/解析回调前预约源消息峰值估算和输出预留。模拟输入已改用此入口；额度不足时尚未创建读取结果，按周期截止取消并计 overrun。回调异常、无数据、超限和关闭均归还额度；成功构造后缩减到消息加输出的持有量，移交通道/分发器，直到提交或明确拒绝。超限作为输入错误记录，不重启模拟输入。普通直接通道写入仍只覆盖调用后的生命周期，不能作为生产适配器的预算前置入口；后续协议输入须使用 ProduceAsync 并提供自身真实峰值上界。

- `WaitToWriteAsync` 现在等待共享预算；单写者的 TryWrite 失败后记住所需字节数，避免通道有空位但额度不足时忙循环，关闭会唤醒。原始 Reader 支持 Count 并释放额度，HoldingReader 仍保留额度直到分发处理结束。
- 构建首次出现 CA2012（条件表达式内的 ValueTask 转换），改为各分支立即 AsTask，没有禁用分析器。
- 首轮新增 SQLite 提交边界夹具用 RAISE(ABORT)，实际触发的是既有数据拒绝分支，两个用例等待存储故障超时；修正为触发器查询不存在表，确实触发 SQLITE_ERROR 和整批回滚，没有改写生产错误分类。失败证据 `/tmp/tenonedge-t1-07-production-budget-r2.log`。
- 修正后定向命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~InFlightBudgetTests|FullyQualifiedName~SimulatorInputTests|FullyQualifiedName~RuntimeUnitContractTests|FullyQualifiedName~OutputPipelineTests' --logger 'trx;LogFilePrefix=production-budget-r3' --results-directory /tmp/tenonedge-t1-07-production-budget-r3`：单元 9、集成 29，共 38 通过、0 失败/跳过。验证先预约再调用、错误/空消息/超限释放、预算等待、模拟输入持续超限不崩溃，以及真实 SQLite 提交失败时保留额度、修复后提交释放、排空超时但写者/分发器已安全停止后释放；正常排空也断言额度归零。
- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-production-budget' --results-directory /tmp/tenonedge-t1-07-production-budget-regression`：单元 345、集成 405，共 750 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-production-budget-regression.log`。
- 全量之后追加一个“不响应取消的读取仍持有额度，回调真正退出才释放”的单元用例；`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~InFlightBudgetTests --logger 'trx;LogFileName=production-cancel.trx' --results-directory /tmp/tenonedge-t1-07-production-cancel`：10 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-production-cancel.log`。未将该单元用例冒充整个运行时的停止超时验证。`git diff --check` 通过。
- 仍未证明完整堆内存上界：输出 JSON 序列化目前先扩容再检查 256 KiB 报文限制，临时编码缓冲可能超过原输出预留；须补有界序列化与压力/停止超时验证。当前已解决模拟输入的“构造后才预约”缺口，不将尚未实现的 T2 协议读取或第三方适配器视为已覆盖。

输出序列化追加修正：标准信封 Serialize 使用有硬上限的 IBufferWriter，以实际 Advance 字节数执行 256 KiB 限制；额外暂存只用于 Utf8JsonWriter 的最坏情况 sizeHint，不把它误认为真实报文大小。原始单 token 长度已不可能装入报文时提前拒绝；字符串值按 512 个 UTF-16 单元分段写入。已核对本机 .NET 10.0.12 引用文档中的 WriteStringValueSegment API。报文大小常量与 DeliveryDraft 共用；输入预约/持有额度时另计按源大小估算的编码工作区（目标逐个格式化，不为每条路径重复预约工作区）。估算偏保守，仍需压力/容量实测，不将逻辑预约称为 CLR 总堆上限。

- 首轮构建 CA1512 要求使用 ThrowIfGreaterThan，已修正而未抑制规则。
- 定向命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~StandardEnvelopeTests|FullyQualifiedName~FieldValueTests|FullyQualifiedName~InFlightBudgetTests|FullyQualifiedName~OutputPipelineTests|FullyQualifiedName~MqttOutputTests' --logger 'trx;LogFilePrefix=bounded-json' --results-directory /tmp/tenonedge-t1-07-bounded-json`：单元 87、集成 16，共 103 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-bounded-json.log`。覆盖 ASCII/转义文本恰好 256 KiB、再多 1 字节、长字段名、跨段代理对/孤立代理字符、超大 token 提前拒绝。之后补 7 个组合，以 GC.GetAllocatedBytesForCurrentThread 核对接受/拒绝路径的本线程分配不超过工作区预约，进入后续全量。

Modbus 探针调度修正（仅测试从站，不是提前实现 T2）：全量首轮单元 360 通过、集成 404 通过/1 失败，同一个 NModbus 重复帧探针再次在首次读取超时；日志 `/tmp/tenonedge-t1-07-bounded-json-regression.log`。最小用例 PeerRespondsWhileSynchronousClientBlocksCallingContext 阻塞调用方同步上下文，未经修改的测试从站稳定产生相同超时。发现构造时直接调用 AcceptAsync 捕获了客户端测试上下文，同步 Read 占住该上下文时，对端无法接收。将从站 Accept 循环通过 Task.Run 放到独立任务，不修改超时、重试或协议断言。

- 红灯命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ModbusTestSlaveSchedulingTests --logger 'trx;LogFileName=modbus-scheduling-red.trx' --results-directory /tmp/tenonedge-t1-07-modbus-scheduling`：0 通过、1 失败，373 ms 读取超时，日志 `/tmp/tenonedge-t1-07-modbus-scheduling-red.log`。
- 绿灯命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~Probes.Modbus' --logger 'trx;LogFileName=modbus-scheduling-green.trx' --results-directory /tmp/tenonedge-t1-07-modbus-scheduling`：43 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-modbus-scheduling-green.log`。
- 连续两轮全量：对 i=2、3 分别执行 `dotnet test TenonEdge.sln -c Release --no-restore --logger "trx;LogFilePrefix=t1-07-bounded-json-r$i" --results-directory "/tmp/tenonedge-t1-07-bounded-json-regression-r$i"`，两轮均单元 360、集成 406，合计 766 通过、0 失败/跳过；日志分别为 `/tmp/tenonedge-t1-07-bounded-json-regression-r2.log`、`/tmp/tenonedge-t1-07-bounded-json-regression-r3.log`。`git diff --check` 通过。此证据修复已复现的测试上下文调度缺陷，不证明任何设备兼容性，也不替代完整资源压力验证。

计数落盘追加进展：按输入累计 readsCompleted / failedFields / readsFailed / overruns，按输入与目标累计格式拒绝；存储不可用次数、失败批次和失败记录操作次数另记全局计数。增量由 DeliveryCache 持有，唯一写者取快照并与分组提交同事务写 queue_counters，仅在事务成功后扣除该快照；提交期间新到的增量保留，回滚后旧增量也保留。无投递请求时最长 10 秒尝试落盘，正常停止最终落盘；存储故障下无法承诺 10 秒成功落盘，诊断保留存储异常和未持久化增量。API 新增 unpersistedCounters，与 persistentCounters 分开返回，不能把不同读取时刻的两个快照相加当作强一致总数。

- 范围/口径：瞬时设备健康仍在进程重启后重置，累计计数从 queue.db 恢复；重建 queue.db 会重置累计值。storageFailedRecordAttempts 是失败的记录操作次数（包括重试），不是已经丢失的数据条数。当前分发器仍会重试失败的存储接管；须在后续故障链路验收中核对总设计 §8.5 对非 MQTT QoS 1 输入的丢失语义，不能用此计数冒充丢失量。
- 初始定向验证（同下方过滤条件，前缀 persistent-counters、目录 `/tmp/tenonedge-t1-07-persistent-counters`）：单元 2、集成 23，共 25 通过、0 失败/跳过。增加边界测试后的全量首轮单元 362 通过、集成 411 通过/1 失败，发现“启动写者后立即停止，尚未进入 RunAsync”的计数遗漏；日志 `/tmp/tenonedge-t1-07-counters-regression.log`。
- 最小红灯：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~NeverStartedWriterFinalizes --logger 'trx;LogFileName=early-stop-red.trx' --results-directory /tmp/tenonedge-t1-07-counter-stop`，0 通过、1 失败，证据 `/tmp/tenonedge-t1-07-counter-stop-red.log`。修正为从未运行但已存在计数的写者在释放时执行自身的停止分支；不修改通用监督者的取消行为、不启动并行写者、不在请求作用域写库。已运行的写者仍只在原循环中做最终提交。
- 修正后定向命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~PendingCounterTests|FullyQualifiedName~RuntimeCounterTests|FullyQualifiedName~StorageProtectionTests|FullyQualifiedName~SystemInfoTests|FullyQualifiedName~OutputPipelineTests' --logger 'trx;LogFilePrefix=persistent-counters-r2' --results-directory /tmp/tenonedge-t1-07-persistent-counters-r2`：单元 2、集成 27，共 29 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-persistent-counters-r2.log`。覆盖并发增量、快照确认、空闲落盘、重启、生产模拟输入的装配、计数写入失败时投递同事务回滚/恢复、路径拒绝归零预算、正常及不可写存储下的最终落盘、未运行便释放的写者。

- 当前全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-counters-r2' --results-directory /tmp/tenonedge-t1-07-counters-regression-r2`：单元 362、集成 413，共 775 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-counters-regression-r2.log`，TRX 在上述目录。`git diff --check` 通过。

趋势追加进展：新增固定六指标的进程活动计数及 RuntimeTrendHistory，独立宿主管理的 RuntimeTrendMonitor 每 10 秒采样，最多保留 360 点；不保存报文、字段值或逐输入标签字典，也不轮询 SQLite。GET /runtime/trends 使用既有管理组权限，所有已认证角色可读，匿名受保护，OpenAPI 和端点清单同步更新。启动时为空，重启不恢复历史；展示时间用 UTC，采样分桶、窗口清理和间隔均使用单调时间。每点包含实际 DurationSeconds，调度延迟不补造零值点；暂停超过整个一小时窗口则清空旧点并重新建基线，无法归属到窗口内的累计增量不伪装成最近一小时数据。加入“10.1 秒采样后，20 秒采样不能因间隔 9.9 秒被跳过”的调度抖动测试。

- 口径：Received 是成功读取的源消息数；Sent 是适配器 Ack 次数（MQTT QoS 0 为本地发送、QoS 1 为 PUBACK），不是业务持久化确认，也不是删除队列记录数。读失败、发送失败尝试、路径拒绝和失败存储记录操作分别返回，不把它们混作已丢失数据条数。发送确认后删除失败不会重复增加 Sent。当前交付全局趋势；逐目标趋势与完整诊断页面仍随相应后续任务验证。
- 基础全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-trends' --results-directory /tmp/tenonedge-t1-07-trends`，单元 364、集成 413，共 777 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-trends.log`。随后增加调度抖动、实际宿主无人访问采样/唯一监测实例/停止测试及发送/路径计数断言。
- 单元覆盖 400 个采样周期、360 点上限、过期清理、跨窗口暂停、重建后为空、墙上时钟前跳/回拨；使用可控时间验证内存窗口，不将其冒充真实运行一小时的压力验证。宿主用例用测试活动计数驱动实际后台计时，无 HTTP 请求触发采样；生产链路用例另断言真实运行时的发送确认和格式拒绝活动数。
- 追加用例后全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-trends-r2' --results-directory /tmp/tenonedge-t1-07-trends-r2`：单元 365、集成 414，共 779 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-trends-r2.log`。`git diff --check` 通过。

采样丢失语义对齐：核对总设计 §8.5 后，移除当前模拟/轮询分发器在 SQLite 提交失败时重试旧采样的行为。真实提交回滚后释放该源消息及投递预算，记录 input.storageLoss 错误事件，并累计 storageLostSourceMessages 与 storageLostDeliveryRecords；它们与失败操作尝试次数分开。修复存储后接收新采样，不重放已丢失的旧采样。此处替代本节早期“提交失败保留并重试”实现及其测试口径，不改写设计；T2 MQTT QoS 1 必须另以确认契约断开并由 Broker 重投，不能套用当前分支假装确认。

- 命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~OutputPipelineTests|FullyQualifiedName~RuntimeCounterTests' --logger 'trx;LogFileName=sampling-storage-loss.trx' --results-directory /tmp/tenonedge-t1-07-storage-loss`：15 通过、0 失败/跳过，日志 `/tmp/tenonedge-t1-07-storage-loss.log`。真实临时 SQLite 故障注入，断言旧消息不入库、预算归零、丢失源/投递计数落盘、恢复后仅新 sourceMessageId 入库；这不是实物磁盘故障测试。

资源压力/超时补证：32 路输出、250 条源消息，共 8000 条真实 SQLite 投递；使用阻塞格式化闸门让默认 64 MiB 共享预约达到背压，第四条 32 路消息等待而不是继续构造。解除闸门后提交全部已接收消息，逐目标核对 250 条待发，正常停止后预约归零，所有运行单元停止。输出为测试暂停适配器，不将其称为 Broker 吞吐量或设备性能测试。

- 定向命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ThirtyTwoWayFanout --logger 'trx;LogFileName=budget-pressure.trx' --results-directory /tmp/tenonedge-t1-07-pressure`：1 通过、0 失败/跳过，18 秒，日志 `/tmp/tenonedge-t1-07-pressure.log`。峰值逻辑预约不超过容量；不据此宣称整个进程 RSS/GC 堆的上限。
- 停止超时用例另外阻塞格式化器并执行真实运行时停止，核对分发器 StopTimedOut 时预算仍保留、写者未提交、输出未发送。迟到退出后，已停止的旧运行时保守保留状态与预约，不冒充已回收或重新激活；需重启服务。正常结束的路径仍清空预约。
- 当前全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-pressure-loss' --results-directory /tmp/tenonedge-t1-07-pressure-loss`：单元 365、集成 416，共 781 通过、0 失败/跳过；日志 `/tmp/tenonedge-t1-07-pressure-loss.log`。`git diff --check` 通过。

配额拒绝分类补证：全局满时累计 `globalQuotaRejectedRecords`，仅目标配额满时累计 `target/{connectionId}/quotaRejectedRecords`；两者互斥，同时触发时归全局。既有按目标 `QuotaRejected` 汇总仍包含该目标的全部配额拒绝，不能与新增分类重复相加。计数与入队判断同事务提交，使用已有 queue_counters，不增加 schema 版本。

- 定向命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~DeliveryPolicyTests --logger 'trx;LogFileName=quota-classification.trx' --results-directory /tmp/tenonedge-t1-07-quota-classification`：10 通过、0 失败/跳过。覆盖目标条数/字节配额、隔离占用全局容量、重开后计数保留、目标分类回滚；新增全局计数写入失败整批回滚以及两种配额同时满时只归全局的用例。
- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-quota' --results-directory /tmp/tenonedge-t1-07-quota`：单元 365、集成 417，共 782 通过、0 失败/跳过；已核对两个 TRX 的 executed/passed/failed/notExecuted，日志 `/tmp/tenonedge-t1-07-quota.log`。环境：Linux x86_64、SDK 10.0.401。
- `git diff --check` 通过；`dotnet format TenonEdge.sln --verify-no-changes --no-restore --include src/TenonEdge.Runtime/Deliveries/DeliveryStore.Policy.cs tests/IntegrationTests/Runtime/DeliveryPolicyTests.cs` 退出码 0，日志 `/tmp/tenonedge-t1-07-quota-format.log`。此格式检查仅覆盖本次分类涉及的两个代码文件。

输入错误分类补齐：`IInputReadReporter.ReadFailed` 增加明确的 ReadError / DataError 分类，默认仍为读取/连接错误。模拟输入超限传入 DataError；输入快照返回 DataRejected，持久计数分别保存 `input/{device}/{input}/dataRejected` 和 `readErrors`。既有 readsFailed 与读取失败趋势保留为总数，DataRejected 是其子集，不能相加；升级前没有分类的历史 readsFailed 不推测回填，因此历史总数不必等于新分类之和。三次整组读取失败的健康状态规则不变，恢复成功不清空累计分类。

- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-07-input-errors-r2' --results-directory /tmp/tenonedge-t1-07-input-errors-r2`：单元 366、集成 417，共 783 通过、0 失败/跳过；已核对 TRX，日志 `/tmp/tenonedge-t1-07-input-errors-r2.log`。环境沿用 Linux x86_64、SDK 10.0.401。
- 扩展 SimulatorInputTests，连续三个超限周期逐次断言数据错误数且不构造消息；RuntimeCounterTests 通过真实 SQLite 空闲落盘与重开验证两类失败分开保留；新增 InputReadStatusTests 验证分类是总失败子集、恢复后保留及非法枚举不改变状态。
- 本次七个代码/测试文件的格式检查首轮发现换行和 imports 顺序问题（含这些文件此前已有的格式），已限定文件运行 dotnet format 修正；不扩大到其他工作区文件。再次以相同 --include 文件列表执行 `dotnet format TenonEdge.sln --verify-no-changes --no-restore` 退出码 0，日志 `/tmp/tenonedge-t1-07-input-errors-format-r2.log`；`git diff --check` 通过。

退出核对：下表确认本任务的后端软件交付，不扩大为完整跨阶段验收。上述压力是有限软件场景，不代替 T1-08 故障矩阵、实际设备或整机容量验收。V-14 完成 Linux x64 原生/容器后端检测子场景，诊断网页仍待 T3；AT-12/29 和 T1-07 整体不标通过。后续须完成 T1-08 和 T2 后才进入前端。

已发现需维护者确认的既有冲突：总设计 Modbus UnitId 为 1–247，功能需求 TCP 为 0–255、RTU over TCP 为 1–247。已建议采用后者；维护者尚未明确批准统一文档，暂不改写规则，不阻塞 T1-07。


T1-07 退出核对（2026-10-08；详细命令及历次失败/修正见本节）：

| 交付物 | 当前代码与验证证据 | 结论/边界 |
|---|---|---|
| 在途预算 | BudgetedInputChannel / InFlightBudget / 有界 StandardEnvelope；InFlightBudgetTests、32 路 8000 投递压力与 StopTimedOut 用例 | 软件场景通过；仅逻辑预约，不称为进程 RSS/实物容量保证 |
| 存储保护 | StorageProtection / 唯一写者；StorageProtectionTests 字节/比例/未知/恢复/保护下删除 | 软件故障注入通过；真实磁盘填满及管理网页仍不声称通过 |
| 运行事件 | 共用 IRuntimeEventSink；空间/校时/读取健康转换及存储丢失事件 | 本地回归通过；不将所有 T3 诊断功能提前标完成 |
| 计数持久化 | queue_counters、PendingCounters；RuntimeCounterTests、DeliveryPolicyTests、OutputPipelineTests | 回滚、空闲/停止落盘、重开、分类测试通过；故障无法落盘时保留未持久化增量 |
| 状态查询 | RuntimeStatusEndpoints、RuntimeTrendMonitor；SystemInfoTests 各角色、OpenAPI 清单及宿主监测测试 | API 和有限趋势窗口通过；不是网页验收 |
| 校时检测 | ClockSyncStatus / ClockSyncMonitor；本机及受限容器原生探针和三状态 API | Linux x64 后端子场景通过；ARM64 未验证 |

据 PLAN-01 T1-07 范围可继续 T1-08；这不代表 T1 整阶段、AT-12/29 全场景或用户的可视化目标完成。T2 协议入口仍须遵守先预约再读取契约；调试/预览、逐目标页面与完整总览在 T3。没有提交或推送。

### 2.21 T1-08 停机恢复与链路验收（后端软件交付完成，待验收）

前置：T1-01—07 的后端软件交付已具备，沿维护者“直到可以可视化访问系统”和继续指令推进。当前先处理 queue.db 损坏恢复，涉及 Runtime 初始化、Host 共用事件接线及软件/进程测试；不改变配置库损坏时拒绝启动的规则，不提前创建前端或协议适配器。

- 初始化增加 quick_check(1)，只对 SQLITE_CORRUPT / SQLITE_NOTADB 或完整性检查明确失败启动恢复。权限、忙锁、磁盘写满、迁移错误和更高 schema 不当作损坏静默清空。
- 旧主库与 -wal/-shm 收入属主专用暂存目录，再改名为唯一 queue.db.corrupt-* 目录；保留证据，不删除旧库。固定 .recovering 标记使移动中断后先收齐旧侧文件再建新库，移动失败则停止初始化。活跃库运行中发生损坏时仍报告存储失败，需停止/重启后恢复，不能在活跃读写连接旁替换文件。
- 可读时尝试 COUNT delivery（不使用索引），否则明确数量未知；记录 error 级 storage.queueRebuilt 事件，说明隔离旧库并将尝试重建。新库计数从零开始。事件写入失败沿既有配置库事件组件回退日志，不假装一定持久化成功。
- 定向验证：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~DeliveryRecoveryTests --logger 'trx;LogFileName=recovery-r2.trx' --results-directory /tmp/tenonedge-t1-08-recovery`：5 通过、0 失败/跳过。覆盖随机损坏头、真实 b-tree 页破坏且旧投递条数可读、半完成移动的侧文件收齐、移动冲突不覆盖、较新 schema 不恢复；日志 `/tmp/tenonedge-t1-08-recovery-r2.log`。
- 真实进程验证：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~QueueRecoveryProcessTests --logger 'trx;LogFileName=process.trx' --results-directory /tmp/tenonedge-t1-08-recovery`：1 通过、0 失败/跳过，约 4 秒。先启动产品并首次设置，再正常退出、损坏 queue.db、启动产品；原账号可登录、NodeId 不变、readyz 就绪、新库无存储故障，旧库原文保留，config.db 中存在错误事件；日志 `/tmp/tenonedge-t1-08-process.log`。不是断电或磁盘硬件损坏验证。
- 首次构建因 partial 声明可见性不一致失败，改为既有 internal 后通过，未修改公共类型可见性；证据 `/tmp/tenonedge-t1-08-build.log`。限定本次文件执行格式修正，未扩大到其他工作区文件。

退出边界补证：DeliveryExitProcessTests 启动真实产品子进程并 SIGKILL，使用可控 MQTT 3.1.1 TCP 对端只用于确定确认时刻，不冒充真实 Broker。

- 四个位置：下一条已经读取、持有在途预算但 SQLite 写锁阻止提交；已提交/尚无 PUBACK；已返回 Ack 但删除被 SQLite 触发器回滚；删除已提交。退出后直接检查 queue.db，再启动产品验证 FIFO 补发。未删除记录重发的 payload 逐字节相同，含相同 messageId/采集时间；已删除记录不重现；未提交样本未入库也未发送。
- 命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~DeliveryExitProcessTests --logger 'trx;LogFileName=exit-r3.trx' --results-directory /tmp/tenonedge-t1-08-exit`：4 通过、0 失败/跳过，约 21 秒；日志 `/tmp/tenonedge-t1-08-exit-r3.log`。首轮构建 raw string 插值括号冲突（`/tmp/tenonedge-t1-08-exit.log`），修正后前三场景通过，再追加提交前场景。
- 真实 Broker 断网：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~BrokerNetworkOutageBoundsTheQueue --logger 'trx;LogFileName=outage.trx' --results-directory /tmp/tenonedge-t1-08-outage`：1 通过、0 失败/跳过，约 6 秒。实际模拟采集→生产 MQTT 输出→TCP 故障代理→Mosquitto→独立订阅者；冻结网络后目标最多 3 条待发，至少 2 条配额拒绝被持久计数，网络故障未计毒消息；恢复后核对积压逐条冻结正文，队列排空、正常停止预算归零。该 3 条配额是测试配置，不替代默认容量实测；日志 `/tmp/tenonedge-t1-08-outage.log`。
- 恢复实现后第一轮全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-08-recovery' --results-directory /tmp/tenonedge-t1-08-regression`：366 单元 + 423 集成 = 789 通过。追加退出/断网用例后全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t1-08-boundaries' --results-directory /tmp/tenonedge-t1-08-boundaries`：366 单元 + 428 集成 = 794 通过，均 0 失败/跳过。日志 `/tmp/tenonedge-t1-08-regression.log`、`/tmp/tenonedge-t1-08-boundaries.log`；Linux x86_64、SDK 10.0.401。

本阶段软件链路验收已覆盖：正常停输入/排空/最终提交/停发送（既有 OutputPipelineTests、RuntimeCounterTests）、四个进程退出边界、网络断开积压与恢复、队列损坏不影响配置。连同 T1-01 的实例唯一/启动保护及 T1-07 资源场景，可以继续 T2；维护者复核与提交推送仍未代行。整机断电/fsync 硬件保证、MQTT 输入未确认会话恢复（T2-03）以及完整管理网页（T3）不标通过。


### 2.22 T2-01 统一连接管理与出站校验（进行中）

前置：T1 软件退出证据见 §2.20/2.21，沿“直到可以可视化访问系统”授权继续。已复核总设计 §6/8.1/10.3、功能 §2.5，阅读本机参考 SUMMARY、ThingsBoard 与 MQTTnet 相关笔记；只借模式，未复制源码。当前先实现实际 IP 出站检查并接入既有 MQTT 输出，不将此子项称为统一连接管理已完成。

- Abstractions 新增 IOutboundAddressPolicy / IOutboundConnector；Runtime 的 OutboundConnector 每次重连重新解析，检查实际 IP 后直接连接该 IP，不再把域名交给 Socket 二次解析。取消/连接失败释放 Socket，返回的 NetworkStream 拥有 Socket。纯 IP 不走 DNS，IPv4 映射 IPv6 先归一化；空 DNS 结果是解析失败，不混作规则拒绝。
- OutboundAddressRules 默认仅允许非本机 RFC1918 与 IPv6 ULA，其他公网须 CIDR+端口。危险地址不能被宽泛公网规则放行；本机端口例外独立，管理端口始终拒绝，覆盖回环、网卡地址和 IPv4 映射 IPv6。
- Host 从当前部署配置读取 TenonEdge:Outbound:Networks（Cidr/Port）及 LocalPorts，连接时枚举本机地址并核对管理监听端口。动态端口尚未绑定或本机地址枚举失败时拒绝建立连接并作为可重试异常；无效规则保守拦截，不能计入毒消息次数。初轮仅部署参数；本节后续补充已接入持久白名单与审计 API，README 说明实际参数与边界。
- MQTTnet 5.2.0.1603 的已安装 net10.0 XML 确认 WithStreamProvider / IMqttClientStreamProvider 支持，实际构建核对签名。MQTT 输出必须注入 connector，无默认绕过路径；由库在受检查的原始 TCP 流上执行 TLS/SNI，保留证书校验。出站拒绝经库包装异常仍识别为 SuspendTarget，不计毒消息。测试使用明确的回环策略或部署端口例外，不改变产品默认安全策略。
- 定向规则命令：`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~OutboundAddressRulesTests --logger 'trx;LogFileName=rules.trx' --results-directory /tmp/tenonedge-t2-01`：22 通过；连接命令同样使用 tests/IntegrationTests、过滤 OutboundConnectorTests、日志 connector.trx：4 通过。实际回环 Socket 验证已检查 IP 就是被连接 IP、再次解析变为元数据地址时无连接、映射地址、取消、空解析分类。不是外部 DNS 服务实测。
- 接入回归：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~MqttOutputTests|FullyQualifiedName~DeliveryExitProcessTests|FullyQualifiedName~Outbound' --logger 'trx;LogFilePrefix=outbound-wiring' --results-directory /tmp/tenonedge-t2-01-wiring`：单元 50、集成 18，共 68 通过、0 失败/跳过，含真实 Mosquitto、TLS 拒绝、强制退出和拒绝时监听器无连接。日志 `/tmp/tenonedge-t2-01-wiring.log`。
- Host 策略初轮：过滤 HostOutboundPolicyTests，报告 `/tmp/tenonedge-t2-01/host-policy.trx`：3 通过（配置变化、实际动态管理端口、宽白名单不能放行元数据）。之后追加非法部署规则保守拒绝用例，纳入后续全量。构建日志 `/tmp/tenonedge-t2-01-wiring-build.log`：0 警告/错误。

- 当前全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t2-01-outbound' --results-directory /tmp/tenonedge-t2-01-regression`：单元 392、集成 433，共 825 通过、0 失败/跳过；已核对两个 TRX 的实际执行计数。日志 `/tmp/tenonedge-t2-01-regression.log`；Linux x86_64、SDK 10.0.401。`git diff --check` 通过。

共享生命周期追加进展：Abstractions 的 ISharedConnections / IConnectionLease 与 Runtime 的 SharedConnections 按逻辑 connectionId 唯一持有会话，协议或非秘密配置身份不一致则拒绝，不允许同编号换协议产生第二会话。借用者共用串行执行器，等待可取消，最后一次释放等待实际操作退出再 Dispose；释放失败或不合作操作保留标识，不能建第二实例。重复释放等待同一个任务；已释放借用的排队操作不再执行。会话总数上限 500。

- 生产 MQTT 由原适配器内独占客户端改为 MqttSession，由运行时共享管理拥有；MqttOutputAdapter 仅持有借用，输出总超时包含串行排队。Host 登记唯一共享管理单例，没有第二套运行时。TLS、凭证代次、重连、指纹与确认行为仍由同一 MQTT 会话实现，未引入后台回调重连。
- 首轮定向：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~MqttOutputTests|FullyQualifiedName~SharedConnectionsTests|FullyQualifiedName~DeliveryExitProcessTests' --logger 'trx;LogFilePrefix=shared-mqtt' --results-directory /tmp/tenonedge-t2-01-shared-mqtt`：32 单元、14 集成通过；日志 `/tmp/tenonedge-t2-01-shared-mqtt.log`。
- 追加用例验证两个真实 Broker 借用者仅建立一个 TCP/MQTT 客户端，释放一个仍可发送，最后释放后再借用才建立第二连接；单元覆盖共享/串行/取消排队/释放等待/配置冲突/释放失败保留标识。之后改为 connectionId 跨协议唯一，过滤 SharedConnectionsTests 的 `shared-identity.trx`：5 通过。
- 首轮全量（跨协议身份收紧前）：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t2-01-shared' --results-directory /tmp/tenonedge-t2-01-shared-regression`：397 单元 + 434 集成 = 831 通过、0 失败/跳过，日志 `/tmp/tenonedge-t2-01-shared-regression.log`。身份收紧后的全量：相同命令将前缀与目录改为 `t2-01-shared-r2`、`/tmp/tenonedge-t2-01-shared-regression-r2`，仍为 397 单元 + 434 集成 = 831 通过、0 失败/跳过，已核对两个 TRX；日志 `/tmp/tenonedge-t2-01-shared-regression-r2.log`。对 SharedConnections.cs / SharedConnectionsTests.cs 的 `dotnet format --verify-no-changes --no-restore` 检查退出码 0，日志 `/tmp/tenonedge-t2-01-shared-format-check.log`；`git diff --check` 通过。

#### HTTP 传输、持久规则及异常防护补充

- HTTP 共用 OutboundHttpTransport：SocketsHttpHandler.ConnectCallback 复用实际 IP connector，保留 Host/SNI；关闭环境代理、自动重定向与 Cookie。显式有限超时；这里只是传输基础，不是 T2-04 业务适配器。真实回环 HTTP 验证 Host、302 不访问跳转目标、拒绝不建连接、慢响应超时；共享管理另补 500/501 容量、满容量借用和释放后复用。
- 定向命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~OutboundHttpTransportTests|FullyQualifiedName~SharedConnectionsTests' --logger 'trx;LogFilePrefix=http-transport' --results-directory /tmp/tenonedge-t2-01-http`：6 单元 + 4 集成通过。日志 `/tmp/tenonedge-t2-01-http.log`。
- config.db migration 9 增加 outbound_policy。`GET/PUT /api/edge-admin/v1/system/outbound-policy`：所有登录角色可读、仅管理员带 CSRF 可写；expectedVersion 乐观并发，配置与 system.outboundPolicyChanged 审计同事务，记录非秘密规则和版本。部署规则与持久规则取并集，接口不能撤销部署授权；下一次连接检查生效，不强断现有连接。两类规则各 1000 条是本次防止无界匹配的实现防护上限，不称为已批准的产品规格。
- API 初轮 23 用例中 2 个端点清单检查失败：空子路径产生尾斜杠；改为 /system 分组加显式 /outbound-policy，未放宽断言。复测过滤 `OutboundPolicyTests|ManagementApiAccessTests|OpenApiDocumentTests|GatewayIdentityTests`：23 通过，日志 `/tmp/tenonedge-t2-01-policy-api-r2.log`，TRX `/tmp/tenonedge-t2-01-policy/policy-api-r2.trx`。
- 持久 JSON 读取也执行同一规范化与限额校验；空数组对象、null 元素、非法 CIDR/端口及损坏 JSON 转为可重试 IOException，绝不放行或误计毒消息。过滤 OutboundPolicyTests：15 通过、0 失败/跳过，命令 `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~OutboundPolicyTests --logger 'trx;LogFileName=policy-hardening.trx' --results-directory /tmp/tenonedge-t2-01-policy`；日志 `/tmp/tenonedge-t2-01-policy-hardening.log`。
- MQTT 总期限补证：真实 SharedConnections 串行门保持占用，以 FakeTimeProvider 推进 999/1000ms，排队发送在总期限结束时 Retry 且不计毒消息；调用方取消仍抛取消，不改写成超时。
- 首轮全量新增测试编译时发现 IOutputTarget 到内部 MqttOutputTarget 类型不匹配；用显式 Assert.IsType 修正测试，集成部分独立完成 453 通过。修正后的单元独立回归 400 通过；不能把有编译错误的首轮命令写成全量通过。

- 修正后全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=policy-regression-r2' --results-directory /tmp/tenonedge-t2-01-policy-regression-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：400 单元 + 453 集成 = **853 通过、0 失败/跳过**。Linux x64、SDK 10.0.401；日志 `/tmp/tenonedge-t2-01-policy-regression-r2.log`，两个 TRX 位于同名结果目录。HTTP 与管理 API 是软件验证，不是网页或现场设备验收。

待完成：显式代理支持/校验评估及协议调用方的跨协议共享验证。当前明确禁用环境代理，未实现显式代理支持。T2-01、AT-06/18 整体未通过。Modbus 选型与 UnitId 文档统一仍等待维护者明确确认，不阻塞其他子项。


### 2.23 T2-05 字段映射：固定 JSON 定位子项（进行中）

前置 T1-02/T0-10 已有证据；按授权并行推进不依赖 Modbus 选型的纯函数，不绕过 T2-06/08 或提前创建前端。复核总设计 §6.4、功能 §3.4 和本机参考 SUMMARY / thingsboard-gateway 笔记；不采用参考库 JSONPath、表达式或脚本，无源码移植。

- Runtime/Mapping/JsonFieldPointer 构建时验证 RFC 6901 字符串形式，支持空路径、空成员、~0/~1 与固定数组下标；不做 URI 解码，不接受查询或片段语法。不通过 double 解释报文数值；返回 JsonElement 借用原文档，不持有上次值或克隆原始报文。
- 路径执行对不存在成员、非法数组下标、标量继续遍历返回未定位；null 字段区别于不存在；选中的重复同名成员不任取一项。不同调用相互独立。
- 首轮构建触发 CA1720（公开参数名 pointer 包含类型名），改为 path，未禁用分析器。命令 `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~JsonFieldPointerTests --logger 'trx;LogFileName=json-pointer-r2.trx' --results-directory /tmp/tenonedge-t2-05`：**35 通过、0 失败/跳过**；日志 `/tmp/tenonedge-t2-05-pointer-r2.log`。含 RFC 示例、转义次序、整数原文、数组越界/前导零、重复成员与不复用旧值。
- 补充严格 ASCII 数组下标校验，拒绝尾随 NUL 与非 ASCII 数字，不依赖运行库数字解析的宽松细节；同一定向命令改用 `json-pointer-r3.trx`：37 通过，日志 `/tmp/tenonedge-t2-05-pointer-r3.log`。
- T2-01/本子项涉及文件的 `dotnet format TenonEdge.sln --verify-no-changes --no-restore --include ...` 检查退出 0，日志 `/tmp/tenonedge-t2-format-r2.log`；此前 SharedConnectionsTests 的导入顺序与一处空白已按格式器修复。`git diff --check` 通过。

- 当前全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mapping-start-r2' --results-directory /tmp/tenonedge-t2-mapping-start-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**437 单元 + 453 集成 = 890 通过，0 失败/跳过**；两个 TRX 已核对，日志 `/tmp/tenonedge-t2-mapping-start-r2.log`。最终 Mapping 目录格式检查退出 0，日志 `/tmp/tenonedge-t2-05-format.log`。

字段定位子项结束时尚未实现类型换算、质量传播、时间映射、完整消息分支转换以及协议/路线调用；时间纯函数后续补充见 §2.24。上述定向用例只是字段定位单元测试，**不表示 T2-05 或 AT-07/30 整体通过**。无 Web 界面、无可验证可视化地址。


### 2.24 T2-05 上游时间映射纯函数（进行中）

本地检查点提交 `a67bfc9` 后继续，范围仍为 T2-05，不提前实施前端。

- UpstreamTimeMapping 显式声明 JSON Pointer、格式（RFC 3339、Unix 秒、Unix 毫秒）及 Device/Upstream 来源。无系统时钟读取、无历史值、无磁盘持久化；返回 InvalidUpstreamTime，供后续协议调用方累计无效时间计数。
- 缺失、格式错误、超出时间范围时回退到本条 receivedAt，未同步记 gateway-unsynced，未知/已同步记 gateway。仅已同步时拒绝超前接收时刻五分钟以上的上游时间；恰好五分钟接受，旧积压时间正常采用，不排序消息。
- RFC 格式必须带时区；接受大小写 T/Z、显式偏移、任意小数位（内部只保留 100ns）。按 UTC 计算，不把 DateTimeOffset 的 ±14h 偏移构造限制误作为 RFC 语法限制。无法用 .NET 时间表示的闰秒、超出公历范围等回退并报无效，不声称完整闰秒时间轴支持。
- Unix 数字/数字字符串按显式单位以 decimal 计算，不经 double；低于 100ns 的部分截断。拒绝空白、尾随 NUL、非数值、溢出，不猜测秒与毫秒单位。
- 定向命令：`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~UpstreamTimeMappingTests --logger 'trx;LogFileName=time-mapping-r2.trx' --results-directory /tmp/tenonedge-t2-05`：**37 通过，0 失败/跳过**；日志 `/tmp/tenonedge-t2-05-time-r2.log`。覆盖上述格式边界、五分钟阈值、三种校时状态、设备/上游来源及不复用旧值。

- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=time-mapping-full' --results-directory /tmp/tenonedge-t2-05-time-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**474 单元 + 453 集成 = 927 通过，0 失败/跳过**（Linux x64 / SDK 10.0.401）；日志 `/tmp/tenonedge-t2-05-time-full.log`，对应目录两个 TRX 已核对。格式初检发现换行问题，限定新文件格式化后复检；证据 `/tmp/tenonedge-t2-05-time-format-r2.log`。`git diff --check` 通过。

仍未接入 MQTT/HTTP 输入，也未接入运行时计数或页面；后续继续类型换算、质量传播和完整映射路径。T2-05/AT-07/30 仍未整体通过，可视化访问目标未完成。


### 2.25 T2-05 已有数值的换算核心（进行中）

沿用 T2-05，不变更目标类型规则或引入表达式。复核总设计 §6.4 与功能 §5.4；下一步仍须完成 JSON/字符串转换、字段映射及质量传播，而不是用此纯函数替代完整任务。

- FieldValue 提供精确数值与浮点数值的分别提取；不把整数先转 double，也不把 float32 转为其显示文本再解析。NumericFieldConversion 使用 decimal 对整数/decimal 做比例偏移，用 double 处理浮点；未换算的 uint64 与 float32 保持原表示。
- 显式 scale/offset/小数位触发换算，uint64 的未经换算特殊范围不再沿用；转 integer 使用有符号 64 位 checked 边界。小数未取整为 conversionFailed，decimal/整数越界为 overflow，浮点非有限运算为 nonFinite；失败不返回字段值。
- 四舍五入使用 AwayFromZero；未声明保留 0 位不能静默截掉小数。小数位 0—28 为当前 decimal 算法的实现范围，不宣称新增产品决策。浮点 16—28 位对足够小的值按缩放取整，对已经超出有效精度的大数不作可能溢出的乘法。
- 入口目前只接收已有数值 FieldValue；布尔/字符串不在这里隐式变数值。后续 JSON/字符串显式解析将复用此算法，单位/schema、必填/可选质量传播仍在完整映射层完成。
- 定向命令：`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~NumericFieldConversionTests --logger 'trx;LogFileName=numeric.trx' --results-directory /tmp/tenonedge-t2-05`：**17 通过，0 失败/跳过**，日志 `/tmp/tenonedge-t2-05-numeric.log`。覆盖 int64/uint64 精确算术、float32 保留、正负舍入、浮点转 int64 边界、decimal 溢出和非有限值、精度与失败不返回旧值。

- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=numeric-full' --results-directory /tmp/tenonedge-t2-05-numeric-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**491 单元 + 453 集成 = 944 通过，0 失败/跳过**，两个 TRX 已核对；日志 `/tmp/tenonedge-t2-05-numeric-full.log`。环境 Linux x64、SDK 10.0.401。限定本批三个源码/测试文件的格式化复检退出 0，日志 `/tmp/tenonedge-t2-05-numeric-format-check.log`；`git diff --check` 通过。

T2-05 仍在进行中，没有运行路线/页面完成证据。


### 2.26 T2-05 JSON 标量与单路径字段映射核心（进行中）

继续完成 T2-05 的真实纯函数处理，不以单元通过代替协议输入/运行路线/网页通过。

- JsonFieldValueReader 按声明的四种固定类型读取 JSON 标量。整数按十进制有效数字和指数精确判定是否为有符号 64 位整数；支持等值指数形式，不经 double，也不使用已舍入的 decimal 判断整数。字符串数值使用与地区无关的明确语法，不接受千分位、空白或尾随 NUL。
- number 先验证 decimal 的规范化往返值逐位一致；若 decimal 表示不了该值（包括精度不足），按设计采用有限 double。double 非有限结果标 nonFinite；对象/数组/null 不转成字符串、零或旧值。JSON integer 不享有 uint64 寄存器的特殊无符号范围。
- FieldValueConversion 组合标量类型转换与已有 NumericFieldConversion；数字/布尔转字符串不改变接收方声明类型。boolean 只接受已有布尔、整数 0/1 或精确小写 true/false 字符串，不把 number 的 1.0 自动当布尔。原始 JSON 数值按数学整数判定 0/1；其他数值为转换失败。
- FieldMapping 的规则明确区分来源字段、JSON Pointer 和固定值，且每条只能选一种；重复目标、无来源、非法定位在构建时拒绝。输出 schema 采用目标类型与配置单位。按本条消息创建独立不可变结果，不共享可变 JSON 或沿用上次值。
- 来源 bad 沿用原原因码；可选字段不存在仅省略并报告提示，转换失败则省略并标 bad。任一必填字段缺失/bad/转换失败，整条路径不返回消息，不能交付已成功的半成品；其他映射路径不受影响。消息身份与采样/接收时间保留。
- 定向命令：`dotnet test tests/UnitTests -c Release --no-restore --filter 'FullyQualifiedName~FieldMappingTests|FullyQualifiedName~JsonFieldValueReaderTests' --logger 'trx;LogFileName=mapping-core-r2.trx' --results-directory /tmp/tenonedge-t2-05`：**53 通过，0 失败/跳过**；日志 `/tmp/tenonedge-t2-05-mapping-core-r2.log`。一个用例执行 5000 个固定种子的有符号整数指数表示往返，按 1 个测试计，不虚增用例数；另有映射结果到真实 StandardEnvelope 序列化的无损整数/字符串/quality 检查，但不是 Broker 或完整投递路径测试。

- 全量首轮不是通过：544 单元通过，集成 452 通过 / 1 失败（ClockSyncMonitorTests 首个观测序号预期 1、实际 2），日志 `/tmp/tenonedge-t2-05-mapping-core-full.log`。单独复跑原用例 1 秒通过，反而证明它没有等到 10 秒周期；临时调用栈观测断言在 `/tmp/tenonedge-t2-05-clock-origin-repro.log` 确认 DataRetentionService 启动清理读取同一个时钟，不能将前两次读取都算成监控器采样。并发计数/Channel 写入不是单个原子操作，会出现序号乱序。
- 修正只限宿主监控生命周期测试：保留生产 ClockSyncMonitor 注册与依赖绑定，移除测试宿主中会干扰观测的 DataRetentionService 注册；生产清理和监控代码不变，清理自身测试继续执行。初次尝试全宿主 FakeTimeProvider 未结束，终止本次测试进程后撤回该尝试；最终宿主测试使用真实时钟，9/10 秒精确边界继续由原有独立 FakeTimeProvider 单元测试覆盖。xUnit2031 触发后改用 Assert.Single 的筛选重载，没有禁用分析器。
- 修正后单项命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~HostStartsOneMonitorWithoutHttpRequestsAndStopsItOnDisposal --logger 'trx;LogFileName=clock-isolated-r4.trx' --results-directory /tmp/tenonedge-t2-05-clock --blame-hang-timeout 30s --blame-hang-dump-type none`：1 通过，实际等待 10 秒，日志 `/tmp/tenonedge-t2-05-clock-isolated-r4.log`。临时调用栈观测已移除。

- 反向验证：临时把生产监控周期改成 20 秒，修正后的宿主测试在等候第二次采样时超时失败（`/tmp/tenonedge-t2-05-clock-mutation-r2.log`，1 个失败；第一次变异尝试仅编译错误，不计为捕获）。随后恢复原文件，`git diff` 确认生产监控无改动；这证明测试不再靠启动清理的读取假通过。

- 修正后全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mapping-core-full-r2' --results-directory /tmp/tenonedge-t2-05-mapping-core-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**544 单元 + 453 集成 = 997 通过，0 失败/跳过**，已核对两个 TRX；日志 `/tmp/tenonedge-t2-05-mapping-core-full-r2.log`。Linux x64 / SDK 10.0.401。本轮映射文件及监控测试限定格式复检退出 0，日志 `/tmp/tenonedge-t2-05-mapping-format-check.log`、`/tmp/tenonedge-t2-05-clock-format-check.log`；`git diff --check` 通过。

剩余：与上游时间映射、协议输入、运行计划/分发路径、资源预算和运行计数整合；完整 T2-05 与 AT-07/30 不宣称通过。T2-06 多输入输出仍按其协议依赖实施，前端仍不提前到 T2。


### 2.27 T2-04 HTTP 输出响应分类与成功判定子项（进行中）

前置使用 T1 已有四类发送结果契约及 T2-01 已验证的实际 IP 检查、禁代理/禁重定向 HTTP 传输；先推进可独立验证的响应处理，不将 T2-01 的后续协议/预览联动或 T2-04 整体称为完成。复核总设计 §6.3/6.5/8.2、功能 §2.5/5.5 与既有参考笔记。

- JsonFieldPointer 从 Runtime/Mapping 移至 Abstractions/Messages，供 Runtime 映射和 Adapters 的固定字段成功判断共同使用；不复制解析器，也不令 Adapters 反向依赖 Runtime。保留原定位测试，名称空间随之调整。
- HttpOutputResponse 按状态分类：默认 2xx Ack；400/409/413/422 RejectMessage；408/429 Retry 不计毒消息；3xx 与其余 4xx SuspendTarget；5xx Retry，其中 502/503/504 不计毒消息，其余 5xx 与未识别响应计数。只适用通用 HTTP，不替代 TenonIoT 的额外业务码规则。
- 可选 HttpSuccessRule 为固定 JSON Pointer + 固定标量/null，构建时克隆常量；缺字段、重复成员、无效 JSON、类型不同或值不等不能 Ack。实际 .NET 10 JsonElement.DeepEquals 测试确认数字等值比较及大整数末位差异，不把 9007199254740992 与 9007199254740993 混同；不是执行表达式。
- Retry-After 支持秒数与日期，过期/非正/非法值忽略，最长 60 秒。结果错误信息只有安全分类，不回显外部响应正文。
- 配置业务判定时正文按 64 KiB 实现上限读取（参考发送测试响应上限，不称为新增已批准规格），未知长度最多多读 1 字节判断超限。截断、过大和不可识别正文 Retry 且计数；响应头到达后正文断连仍是网络失败，不计毒消息；调用方取消继续抛取消，由后续输出适配器区分停机与总期限。
- OutboundHttpTransport 增加 MaxResponseDrainSize=0，达到限制或无需正文后释放响应，不为复用连接而继续自动读取未消费的大正文。测试用头部声明 1,000,000 字节但不发送正文，确认分类不等待正文，Dispose 后真实 TCP 对端收到 EOF。
- 单元首轮命令：`dotnet test tests/UnitTests -c Release --no-restore --filter 'FullyQualifiedName~HttpOutputResponseTests|FullyQualifiedName~JsonFieldPointerTests|FullyQualifiedName~FieldMappingTests|FullyQualifiedName~UpstreamTimeMappingTests' --logger 'trx;LogFileName=http-response.trx' --results-directory /tmp/tenonedge-t2-04`：122 通过，包含迁移后的定位和映射回归，日志 `/tmp/tenonedge-t2-04-http-response.log`。随后新增正文恰好 64 KiB 与超出 1 字节的边界测试，见本节全量结果。
- 真实回环 TCP HTTP 命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~HttpOutputResponseTests|FullyQualifiedName~OutboundHttpTransportTests' --logger 'trx;LogFileName=http-response-wire.trx' --results-directory /tmp/tenonedge-t2-04`：16 通过，0 失败/跳过（12 新响应测试 + 4 传输回归），日志 `/tmp/tenonedge-t2-04-http-response-wire.log`。含四类状态/业务结果、正文中断、已知/未知长度超限、头到达后慢正文取消。这些请求只验证真实 HTTP 传输和分类，尚不是正式 POST 输出/持久队列集成。

- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-response-full' --results-directory /tmp/tenonedge-t2-04-http-response-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**584 单元 + 465 集成 = 1049 通过，0 失败/跳过**，两个 TRX 已核对；日志 `/tmp/tenonedge-t2-04-http-response-full.log`。Linux x64 / SDK 10.0.401。本批限定文件的格式化复检退出 0，日志 `/tmp/tenonedge-t2-04-http-format-check.log`；`git diff --check` 通过。

剩余：HTTP 连接与路径配置、非秘密请求头冻结、Idempotency-Key、当前凭证解析、共享会话和总期限、GET 输入、正式 POST 输出、Host 装配与运行路径验收。T2-04、GW-12、AT-04/18/26 未整体通过。无前端可视化访问证据。


### 2.28 T2-04 HTTP POST 适配器、共享客户端与冻结记录（进行中）

在已有响应分类基础上接入 IOutputAdapterFactory/Target/Adapter，不以临时发送函数代替运行时链路；Host 注册和凭证管理仍未开放，见本节剩余范围。

- HttpOutputSettings 验证固定 HTTP(S) 基础地址和相对路径；基础路径不被前导 / 丢弃，拒绝切换 authority、用户信息、片段、穿越及歧义双重转义。基础地址不含查询，固定输出路径可带非秘密查询；URL 不使用数据字段占位符。静态头仅接受有效名称及 ASCII 可打印值/横向制表，禁止认证头、Cookie、Host、请求定界和 Idempotency-Key 覆盖。64 个头、单值 4096 字符、合计 16 KiB 是当前防护限额，不宣称新增已批准产品规格。
- IHttpCredentialResolver 提供非秘密身份与按基础接收方绑定的当前秘密。适配器支持 Basic、Bearer、API Key 请求头/查询，认证方式来自凭证类型；没有在快照/冻结记录中保存秘密。凭证版本轮换不改变账户指纹，账户或接收方改变暂停旧记录；两次元数据读取交错变化时 Retry，不误暂停成无法自动恢复。凭证格式/静态参数冲突 SuspendTarget，不计毒消息。
- HttpOutputTarget 通过 StandardEnvelope 生成有限正文，冻结静态头和 `Idempotency-Key: deliveryId`；发送时只添加当前凭证，不改变冻结正文。HttpConnectionSession 按 connectionId/BaseAddress/TimeoutMs 共享一个 HttpClient，路径和成功规则逐请求传入，不绑死第一个输出节点；最后一个借用者释放才关闭。总期限从排队前开始，贯穿建连、POST 和响应正文，调用方取消保持取消语义。
- 统一发送契约补齐 Headers，QoS 改为可空以表达 HTTP；分发器传入 DeliveryDraft，发送者从 StoredDelivery 回传冻结头，并移除对 QoS 的强制取值。queue.db 已有 headers 列，不增加迁移。IOutputTarget.ClientId 允许 null，仅有实际 ClientId 的目标参与 MQTT 同 Broker 冲突检查，不为 HTTP 伪造 ClientId。
- 输入/分发在途预算按输出目标声明的 FormattingReservationBytes 求和：默认仍是双份 256 KiB 正文，HTTP 另加 128 KiB 保守覆盖静态头转义与字典暂存。构建拒绝无效或单路径超过全局预算的声明；HTTP SQLite 链路用例检查实际峰值包含额外预约。不是 CLR 全进程堆上限或吞吐承诺。
- 构建初轮 CA1865（单字符 StartsWith）及定向初轮 CA1861（测试中的重复常量数组）均修正，未禁用分析器，也不将编译失败命令算作测试通过。
- 定向第二轮：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~HttpOutputSettingsTests|FullyQualifiedName~HttpOutputTests' --logger 'trx;LogFilePrefix=http-post-r2' --results-directory /tmp/tenonedge-t2-04-post`：25 单元 + 6 集成通过。随后加入真实 RuntimeHost/SQLite 重启补发及 HTTP 排队期限；第三轮同命令增加 OutputPipelineTests 过滤、前缀改 http-post-r3：27 单元 + 18 集成通过，日志 `/tmp/tenonedge-t2-04-post-r3.log`。后续控制字符、零端口与不同 HTTP 连接相同地址不冲突的追加用例见全量。
- 真实 TCP 用例验证四种当前凭证、503 后同正文/幂等编号重试、凭证更新立即用于下一次请求、账户变化/不可解密不连接；同 connectionId 两个不同路径借用只用一个 TCP 连接，首个释放后第二个仍工作。
- 真实运行时与 SQLite：OneShot 输入经分发/缓存/正式 HTTP adapter 发 POST，401 后停止；换成不同静态头的运行定义重启，以 204 确认旧记录。实际接收正文逐字节相同、仍为冻结旧头，幂等编号与持久 deliveryId 一致；凭证不在 queue headers 中，确认后记录删除、输入/分发预算归零。这是软件链路验证，不是主机断电或现场 HTTP 平台联调。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-post-full-r2' --results-directory /tmp/tenonedge-t2-04-post-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**613 单元 + 473 集成 = 1086 通过，0 失败/跳过**，已核对两个 TRX；日志 `/tmp/tenonedge-t2-04-post-full-r2.log`（Linux x64 / SDK 10.0.401）。本批限定文件格式复检退出 0，日志 `/tmp/tenonedge-t2-04-post-final-format-check.log`；`git diff --check` 通过。此前全量 612+473 通过为零端口校验加入前的证据，不用它覆盖最后一项改动。

剩余：config.db 受保护 HTTP 凭证及审计 API、接收方变更/目标迁移协同、Host 注册与配置契约、HTTP GET 输入、客户端证书/连接信任、输出其他报文形式及完整协议回归。当前不能通过产品管理接口启用 HTTP 输出；不把测试注入的凭证解析器当成凭证管理已实现。T2-04 与可视化目标均未完成，未新增提交/推送。

### 2.29 T2-04：受保护 HTTP 凭证、审计 API 与实际宿主接线（2026-10-08）

授权延续至 T2/T3 可视化目标；本子项关联 T2-04、D-05、GW-10/12、FS §2.3、AT-26 与 BE-AT-02/03。开始前已把截至 §2.28 的映射与 HTTP POST 核心整理为本地提交 `1d9cd4e`，基线 1086 项通过，未推送；本节为该提交之后的继续实现，不表示 T2 整体完成。

交付：
- config.db 显式迁移 10 增加 http_credential，HTTP 专用 Data Protection purpose 再按 connectionId 隔离；秘密不进入快照、队列、API 响应或审计详情。Basic、Bearer、API Key 请求头/查询参数共用管理写入与发送格式检查；绑定基础地址按适配器规则规范化（保留基础路径、尾部 /）。
- GET/PUT `/api/edge-admin/v1/connections/{connectionId}/http-credential`：登录角色可读状态；配置人员/管理员可写，受现有 CSRF 和乐观版本约束。改变基础接收方默认作废，保留旧凭证仅管理员可显式选择；作废后仍保留身份元数据，不能退化成匿名发送。待发/失败记录下相同非秘密身份更换秘密须声明账户是否变化，真正变更账户时换 marker。暂不提供凭证删除，完整连接管理仍在 T3。
- 凭证写入/失效与审计在同一配置事务内；审计失败回滚秘密、版本及运行版本指针。HTTP/MQTT 不允许同一个逻辑连接 ID 同时保存两套协议凭证。数据库读取使用短生命周期客户端，单例没有保留请求上下文或数据库事务。
- 正式 Host 显式登记 HttpOutputFactory，复用既有单例共享连接与出站保护；种子解析冻结前拒绝 HTTP 秘密/未知属性。运行版本提交同时校验 HTTP 基础地址和完整输出路径（含查询），待发/失败区未清空时阻止删除/改变接收方；超时变化不误阻止，隔离记录不阻止新目标。基础地址变更在提交事务内默认作废凭证。
- 真实宿主用管理 API 保存 Bearer 秘密，停止后从同一数据目录重启，加载 HTTP 种子，经正式模拟输入→SQLite 队列→HTTP POST，在真实 TCP 服务上核对认证头、路径和正文；204 后队列确认删除。该用例未替换产品凭证解析器、连接器或输出工厂，只通过部署出站例外允许测试端口。

验证（Linux x64、SDK 10.0.401）：
- 第一轮定向失败 4 项：原 HTTP 测试解析器给所有认证类型同时填了 Basic 用户名和 API Key 名称，新共同校验正确拒绝这类互斥字段；测试输入改成各类型合法字段，保留原重试/换密/正文/幂等断言。日志 `/tmp/tenonedge-http-credentials-test.log`，不计为通过。
- 第二轮定向命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~Credentials|FullyQualifiedName~ManagementApiAccess|FullyQualifiedName~GatewayIdentity|FullyQualifiedName~ConfigMigrator|FullyQualifiedName~TargetChangeTests|FullyQualifiedName~HttpOutputTests' --logger 'trx;LogFileName=http-credentials-r2.trx' --results-directory /tmp/tenonedge-http-credentials-r2`：100 项通过，0 失败/跳过。覆盖权限/匿名/CSRF、加密/绑定/重启/用途隔离、乐观冲突、四种凭证格式、账户声明、两种事务审计失败、HTTP 目标改变及真实 Host 链路。
- 第一轮全量：613 单元通过，集成 518 通过、1 失败；遗漏更新 OpenAPI operationId 固定清单，新增 getHttpCredentialStatus/writeHttpCredential 后修正。日志 `/tmp/tenonedge-http-credentials-full.log`，不计为全量通过。

- 最终全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-credentials-full-r2' --results-directory /tmp/tenonedge-http-credentials-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**613 单元 + 519 集成 = 1132 通过，0 失败/跳过**，两个 TRX 计数已核对；日志 `/tmp/tenonedge-http-credentials-full-r2.log`。限定本批 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...` 无差异，日志 `/tmp/tenonedge-http-credential-format-check.log`；`git diff --check` 通过。

剩余：HTTP GET 输入、客户端证书/自定义信任、其他输出报文形式、映射与运行路径整合、完整 T2 协议验收及 T3 网页均未完成。种子与凭证 API 已能在产品宿主启用 HTTP POST，但不等于完整连接 CRUD 或网页已交付；未验证现场 HTTP 平台、设备兼容性或吞吐。

### 2.30 T2-04：HTTP GET 有界响应读取核心（2026-10-08）

继续 T2-04，关联 GW-06、AT-04/18 与总设计 §6.3、§8.7；上一轮为有实际代码和测试证据的进展。本节只实现输入响应读取边界，不宣称轮询输入已交付。

- HttpInputResponse 只接受 HTTP 2xx 后的单个 JSON 文档，最大 1 MiB、最大深度 64。已知 Content-Length 超限及非 2xx 时不读正文；未知长度最多读上限加 1 字节判定超限。拒绝多文档、非法 JSON、尾部非空内容和超深嵌套。显式校验 UTF-8 字节合法性，避免 JsonDocument 延迟解码漏检。不自动解压、不自动分页或展开数组。
- 返回的 JsonDocument 独立拥有内存；池缓冲归还前清零，文档不能借用已归还池数组。调用者须释放文档。大整数保留原始 JSON 表示，不在读取层经过 double。
- 可分类的状态/超限/编码/JSON 错误不含正文或解析器原始异常；网络失败和主动取消保持原异常类型，留给输入循环分别上报。总期限、周期不重叠、字段提取和全局在途预算预约由后续正式 input.http 单元整合，本读取器不创建旁路缓冲队列。
- 单元边界：5 种合法文档、4 种非成功状态不读正文、6 种非法文档无秘密回显、64/65 层、恰好 1 MiB、池复用后原文档不变、已知/未知超限、编码、网络与取消，以及非法 UTF-8，共 22 项。
- 真实 TCP GET：Content-Length 与 chunked 的精确大整数、仅发送超限响应头后观察客户端 EOF（不排空正文）、正文中断保持网络错误、收到头后取消能打断阻塞正文并关闭连接，共 5 项。使用实际 OutboundHttpTransport 与实际 IP 连接器，只为测试端口允许 loopback；没有用静态页面或 fake handler 冒充接入。
- 定向命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter FullyQualifiedName~HttpInputResponseTests --logger 'trx;LogFilePrefix=http-input-response' --results-directory /tmp/tenonedge-http-input-response-all`：21 单元 + 5 集成通过，0 失败/跳过；日志 `/tmp/tenonedge-http-input-response-all.log`。Linux x64 / SDK 10.0.401。

- 第一轮全量 634 单元 + 524 集成通过（`/tmp/tenonedge-http-input-core-full.log`），但之后追加非法 UTF-8 用例发现 JsonDocument.ParseValue 会延迟到取字符串时才校验；原实现该用例失败，日志 `/tmp/tenonedge-http-input-utf8.log`。核对 SDK 的 Utf8.IsValid API 后，在 ParseValue 前显式校验字节，不把有缺陷的首轮当最终结果。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-input-core-full-r2' --results-directory /tmp/tenonedge-http-input-core-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**635 单元 + 524 集成 = 1159 项通过，0 失败/跳过**，已核对 TRX 计数；日志 `/tmp/tenonedge-http-input-core-full-r2.log`。新增三份 C# 文件已执行限定范围的 dotnet format；`git diff --check` 通过。

未完成：正式 GET 轮询单元（共享会话、凭证、周期/截止、不重叠、在途预算）、字段提取/上游时间与诊断计数、Host input.http 注册和完整端到端输入验收。下一步整合这些使用者，不能仅因本核心通过便将 T2-04 或可视化目标标为完成。无新增提交/推送。

### 2.31 T2-04：正式 HTTP 轮询输入与共享 GET→队列→POST 链路（2026-10-08）

继续既有授权，关联 T2-01/04/05、GW-06/12、AT-04/18/26 和 BE-AT-03。上一轮完成响应读取核心，本轮把它接入正式 Host 运行链路；未扩展 Webhook、分页、OAuth 或前端。

交付：
- `input.http` 工厂、配置和运行单元正式注册。输入字段采用显式 key/path/type；1 秒—1 天固定周期、固定相对路径/查询、非秘密头和连接超时；与 POST 共用 HttpRequestSettings 的地址与头校验。构建只做本地验证，运行循环才借用会话并访问网络。
- GET 与 POST 共用同一 connectionId 的 HttpConnectionSession、当前凭证与出站保护；基础地址和超时组成相同连接配置标识，路径和头逐次提供。种子在冻结前检查同一 HTTP 连接的地址/超时一致，并校验输入字段，不把秘密旁路冻结入快照。版本提交覆盖输入接收方的默认凭证失效及事务审计。
- Runtime 的 JsonInputDecoderFactory 经 Abstractions 小接口供协议输入调用，不增加 Adapters→Runtime 引用；复用既有固定 JSON Pointer 与精确数值转换。缺字段为未携带，非法字段只带 bad 原因，不发旧值为新值；解码每字段检查取消。当前输入描述最多 1000 字段、解码结果 4 MiB，是实现防护上限，不视为另行批准的产品规模承诺。
- 当前消息使用读取完成的网关 receivedAt/collectedAt，生成 UUIDv7，并按校时状态标注 gateway/gateway-unsynced。整文档错误计数据拒绝，状态码/网络/TLS/凭证错误计整组读取失败，错误说明不回显外部正文。连续读取失败沿用运行时离线规则。
- 一个循环串行执行，不叠加上一请求；请求期限含共享执行器排队/网络/解码，并受本周期截止限制。网络阶段超时为读取失败；等待全局额度或通道到周期截止为 overrun。停机取消等待请求退出再释放借用，不计人为停机为读取失败。
- 扩充 ProduceAsync 的两阶段预约：解析工作峰值与结果消息上限分别声明，实际预约为“解析峰值”和“结果+输出序列化预约”两者较大值。回调返回前须释放解析暂存，结果超过声明仍拒绝；旧单上限调用保持原行为。HTTP 当前预约 48 MiB 工作峰值（覆盖 1 MiB 正文、JSON 元数据扩容和字段临时值），结果上限 4 MiB+8 KiB；用例确认真实链路可在全局 64 MiB 内运行。这是保守逻辑预算，不是 CLR 全堆测量或并发性能承诺。

验证：
- 定向第一轮 37 单元+15 集成通过；第二轮 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~HttpInput|FullyQualifiedName~JsonInputDecoder|FullyQualifiedName~InFlightBudget' --logger 'trx;LogFilePrefix=http-input-pipeline-r2' --results-directory /tmp/tenonedge-http-input-pipeline-r2`：48 单元+12 集成通过，日志 `/tmp/tenonedge-http-input-pipeline-r2.log`。
- 真实 Host 用管理 API 写入 Bearer 凭证后从同一配置目录重启；正式 HTTP GET 取得 signed64 最大值，经解码与 SQLite 缓存，在**同一个 keep-alive TCP 会话**上收到带认证的 POST，正文整数逐位一致，204 后队列确认删除。没有替换正式输入工厂、输出工厂、字段解码器或凭证存储。
- 可控时钟配合真实 TCP：999ms 未出现第二请求；1s 截止关闭旧请求，记读取失败，下一请求正常读取；停机关闭阻塞请求且不计失败。全局预算被占满时无 TCP 连接，周期到期计 overrun；正常读取预约峰值与释放归零有断言。两阶段预算另有单元用例防止用较大工作额度绕过结果大小上限。
- 全量首轮集成构建因新增测试常量数组 CA1861 未过；第二轮补充解码取消参数后单元测试调用遗漏 token，xUnit1051 拦截。均已改正，没有把未执行的用例当通过。
- 第二轮集成实际执行 534 通过、1 失败：既有 NModbus 剩余 TCP 帧探针在首次正常读取（尚未检查残留帧前）发生 300ms 读超时，日志 `/tmp/tenonedge-http-poller-full-r2.log`。未修改其超时或断言；原样独立连续重跑 3 次通过，TRX 在 `/tmp/tenonedge-http-poller-probe-recheck/`。这只证明未再次复现，不证明偶发原因已消除；保留后续观察事项。

- 最终全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-poller-full-r3' --results-directory /tmp/tenonedge-http-poller-full-r3 --blame-hang-timeout 5m --blame-hang-dump-type none`：**651 单元 + 535 集成 = 1186 项通过，0 失败/跳过**，已核对两份 TRX，日志 `/tmp/tenonedge-http-poller-full-r3.log`。环境 Linux x64 / SDK 10.0.401。限定改动 C# 文件格式复检日志 `/tmp/tenonedge-http-poller-format-check.log`；`git diff --check` 通过。

未完成：HTTP 上游时间映射及无效时间计数、保留原始消息供路线 JSON 映射、完整路线处理/扇出、客户端证书与自定义信任、其他报文形式及完整 T2 协议验收。正式 GET 输入与 POST 种子链路已经运行，不等于连接 CRUD、采集预览或 T3 网页已交付。无新增提交/推送。

### 2.32 T2-04/05：HTTP 上游时间与独立持久计数接线（2026-10-08）

本轮延续开发授权，关联 T2-04/05、GW-06/08、AT-30 与总设计 §6.4。上一轮已有正式 GET→队列→POST，当前把已验证的纯时间映射接入该链路；不把本子项当完整 T2 或网页完成。

- input.http 可配置 `upstreamTime: { "path": "/sampleAt", "format": "rfc3339", "source": "device" }`；format 也可为 `unixSeconds` / `unixMilliseconds`，须显式提供，不猜测单位。source 可选 device/upstream，省略为 upstream；未配置或显式 null 表示使用网关接收时间。未知属性、数字枚举、不支持格式、缺少格式或 gateway 来源在冻结/构建时拒绝。
- JsonInputDecoderFactory 经既有 Abstractions 解码接口返回字段和时间，复用 UpstreamTimeMapping，不在 HTTP adapter 中重写解析规则。InputUnitContext 暴露三态 ClockState，一次解码使用一次状态取值；仅 Synchronized 启用未来 5 分钟校验，Unknown/NotSynchronized 不被误认为已同步。
- 有效上游时间连同 device/upstream 来源进入 EdgeMessage 和标准输出信封；早于网关的时间照常保留。映射字段缺失、无效或已同步时超过未来阈值，改用 receivedAt 与 gateway/gateway-unsynced，不改变字段质量、不丢弃本条、不把回退当作整组读取失败。未映射时间的输入不会因为报文没时间而计“无效”。
- IInputReadReporter.UpstreamTimeInvalid 对接运行时 InputReadSnapshot.InvalidUpstreamTimes 和 `input/<device>/<input>/invalidUpstreamTimes` 累计键；队列唯一写者沿用已有事务批量落盘、失败不清零及停机刷新，不加新表或每条外部正文日志。当前单元读数与持久累计仍按原有 API 约定分别展示。
- 解码在时间处理前及每个字段前检查取消；工作/结果预算及错误信息脱敏沿用 §2.31，不另建缓冲。

验证（Linux x64 / SDK 10.0.401）：
- 定向：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~HttpInputTests|FullyQualifiedName~JsonInputDecoderTests|FullyQualifiedName~InputReadStatusTests|FullyQualifiedName~RuntimeCounterTests' --logger 'trx;LogFilePrefix=http-time' --results-directory /tmp/tenonedge-http-time`：13 单元+23 集成通过，0 失败/跳过；日志 `/tmp/tenonedge-http-time.log`。此后增加显式配置负例和 API 断言，见最终全量。
- 真实 TCP + 可控时钟：三种格式、null/坏时间、恰好未来 5 分钟/多 1 秒、Unknown 不拒绝未来时间；每项核对字段值仍存在、无 bad 字段、无整组读取失败及独立计数。纯解码追加“未映射/已映射但缺时间”的区别、固定格式/source/未知属性拒绝。
- 真实正式 Host、受保护 API 凭证、同一共享 TCP 上 GET→POST：device 时间逐字写入标准信封；无效时间同样发送 values，来源回退为 gateway 系列。登录读取 `/api/edge-admin/v1/runtime/status` 核对 invalidUpstreamTimes，诊断 JSON 不出现非法时间原文。
- 实际运行时唯一写者在无投递情况下刷新含 invalidUpstreamTimes 的增量；重开 queue.db 后值仍为 2（扩充 RuntimeCounterTests）。单元用例确认回退计数不造成健康失败或原文事件。
- API 追加断言之前的全量为 662 单元+543 集成通过，日志 `/tmp/tenonedge-http-time-full.log`；不是最后一版验证。

- 最终全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-time-full-r2' --results-directory /tmp/tenonedge-http-time-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**662 单元 + 544 集成 = 1206 项通过，0 失败/跳过**；两份 TRX 已核对，日志 `/tmp/tenonedge-http-time-full-r2.log`。限定改动 C# 文件格式复检日志 `/tmp/tenonedge-upstream-format-check.log`；`git diff --check` 通过。

剩余：原始 JSON 的消息所有权/预算及路线 JSON 映射、完整路径处理/多分支集成、HTTP 自定义信任/客户端证书及其他输出格式、其余 T2 协议与 T3 页面。无新增提交/推送。

### 2.33 T2-04/05：原始 JSON 不可变所有权与预算（2026-10-08）

继续原有授权，关联 T2-04/05、GW-06/08、AT-07/30 和总设计 §6.4、§8.7。上一轮完成上游时间接线，本轮补齐后续路线 JSON Pointer 读取所需的数据寿命；路线配置到分发器的编译/执行仍未完成。

- RawJsonPayload 持有独立的只读 JSON 根副本，不借用已释放的协议响应文档或池数组；多分支共享同一不可变所有者，而不是每条路线再次复制。CopyFrom 的使用前提是已完成输入大小/深度校验，再核对根值 UTF-8 大小不超过 1 MiB 才复制树。它保留 JSON 值内容，不承诺保留 HTTP 头或根值外部空白的逐字节线缆记录。
- EdgeMessage 的 RawJson 可选；普通 JsonSerializer 序列化显式忽略，标准信封仍只写规定字段，RawJsonPayload.ToString 也不回显正文。HTTP 完成字段/时间解码后，在原文档释放前取得该只读所有者。队列只保存已生成的输出报文，不因此保存所有未选原始字段。
- RawJsonPayload 按 `16 × UTF-8 根值字节数 + 1024` 保守计入原始字节与 DOM 节点元数据；其值加入 EdgeMessage.BudgetBytes，HTTP 的结果大小声明同步增加原始 JSON 上限。解析工作峰值仍为 48 MiB；实际预约沿用两阶段较大值，包含输出时仍受全局 64 MiB 检查。这不是 ArrayPool 保留量或全 CLR 堆的实测保证。
- FieldMapping.Apply 没有显式临时 payload 时从 source.RawJson 定位；映射结果沿用原始所有者，因此后续映射与其他分支仍能读取原始固定位置。显式 payload 参数的现有行为不改，输入 values、时间和 sourceMessageId 不因读取原文被修改。

验证：
- 定向命令 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~RawJsonPayloadTests|FullyQualifiedName~FieldMappingTests|FullyQualifiedName~HttpInputTests' --logger 'trx;LogFilePrefix=raw-json' --results-directory /tmp/tenonedge-raw-json`：17 单元+18 集成通过，0 失败/跳过；日志 `/tmp/tenonedge-raw-json.log`。
- 单元：源文档释放后精确 signed64 仍可读取；ASCII 与汉字按 UTF-8 字节上限判断；预算增加量准确；普通 JSON 序列化和标准信封不输出未选秘密；两个字段映射分支及串联映射共用原文且结果互不污染。
- 正式 Host 的真实 GET→队列→POST 用例在 GET 正文加入未选择的 private 字段，最终 POST 正文不包含它；原有认证、共享 TCP、时间与计数断言保留。另加真实 GET 大未选字段用例：通道尚未消费时预算占用超过 4 MiB，取走后归零，原文仍在其合法消息所有者上可读，values 仍只有选中字段。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=raw-json-full' --results-directory /tmp/tenonedge-raw-json-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**669 单元 + 545 集成 = 1214 项通过，0 失败/跳过**，两份 TRX 已核对；日志 `/tmp/tenonedge-raw-json-full.log`。Linux x64 / SDK 10.0.401。限定改动文件格式复检日志 `/tmp/tenonedge-raw-json-format-check.log`；`git diff --check` 通过。

剩余：路线映射配置/编译/分发器接线、分支限额与投递上限、其他输出报文形式、自定义 TLS 信任及其余 T2 协议、T3 页面。不能把“消息持有原文、纯映射可访问”写成整条路线处理已交付。无新增提交/推送。

### 2.34 T2-05/06：输出路径映射接线与分支隔离（2026-10-08）

继续既有授权，关联 T2-05/06、GW-08/13、AT-06/07/29/30。本轮把纯映射从测试调用接到实际分发器，不加入图编辑器或脚本；T3 仍未开始。

- HTTP/MQTT 输出配置可选 `mapping` 规则数组；未提供/null 为透传。规则包括 target/type、sourceField/sourcePath/constant 三选一，以及 unit、required、scale、offset、decimalPlaces。格式采用固定类型/JSON Pointer；未知属性、表达式、缺类型、不合法固定值、来源冲突在冻结前拒绝。固定值按精确目标类型读取，比例偏移只执行一次。
- RuntimeHost 准备阶段编译规则，PreparedOutput 保留不可变 FieldMapping；分发器对每个输入→目标路径独立应用，然后调用原输出适配器冻结正文。必填缺失/转换失败只拒绝该路径，不把半成品入队；可选缺失不伪造值，可选错误保留 bad 原因；其他目标不受影响。沿用同一个 sourceMessageId、时间与原始只读 JSON，投递 ID 仍每路径独立。
- 增加路径计数 mappingInvalidFields、mappingMissingFields、requiredFieldFailures；被拒路径仍计 dataRejected/RejectedPath，原因事件区分必填失败与格式/大小失败，不回显字段值或原文。统计随现有唯一写者落盘，不加迁移。
- 映射最多 1000 规则、目标名称最多 64 字符、结果工作集合上限 4 MiB，避免先无限展开再遇到输出 256 KiB 上限。当前防护限额不是性能承诺。每个输入只预约一份 8 MiB 映射暂存和一次最坏序列化暂存，各路径顺序复用；冻结输出的空间仍按路径累加。
- 双上限 ProduceAsync 在保守结果上界导致输出阶段大于总额度时，先预约总额度、再按实际消息复核；真实输出阶段仍超额则拒绝并释放，不能突破 64 MiB。解析工作本身超额仍在回调前拒绝。原单上限入口保持旧行为。这使小报文多路径可执行，而不是因其理论最大原文而全部被拒。
- 映射只在入队前执行；已有队列记录不因映射变化重新处理。实际 Host 的受保护 HTTP GET→共享 TCP POST 用例改为从原始 `/v` 映射到新字段 mapped，验证确实经过正式映射，同时保留精确大整数、时间、凭证、秘密隔离与 API 计数断言。

验证：
- 定向命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~MappedOutputTests|FullyQualifiedName~FieldMapping|FullyQualifiedName~HttpInputTests|FullyQualifiedName~InFlightBudgetTests' --logger 'trx;LogFilePrefix=mapping-path' --results-directory /tmp/tenonedge-mapping-path`：31 单元+23 集成通过，0 失败/跳过，日志 `/tmp/tenonedge-mapping-path.log`。
- 真实 RuntimeHost/SQLite（发送端用拒绝连接器保持待发，不冒充外部 HTTP 确认）：一条必填失败路径没有记录，其他路径的比例偏移、bad 质量和可选缺失正确；sourceMessageId 相同、输入只启动一次，拒绝计数落盘。重启换 mapping 后旧冻结正文逐字不变。
- 32 个不同 connectionId 的映射输出路径各入队一次，峰值在 60—64 MiB，结束归零；未为每分支各占一份 8 MiB。两个输入汇入一个目标各生成独立记录，不做 join。实际 HTTP 网络路径由上一条正式 Host 用例覆盖，MQTT 有冻结前配置校验，但本轮没有新增 Broker 上的映射报文专项验收。
- 第一轮全量 678 单元+550 集成通过，日志 `/tmp/tenonedge-mapping-full.log`；后续追加预算封顶后的实际超限回滚及双输入用例，见最终全量。

剩余：当前仍是一 connectionId 一份输出定义，每份定义上的 mapping 用于其所有输入；同一连接的多条不同输出路径、完整图到 RuntimePlan 的编译与上限、其他格式、其余 T2 协议与 T3 页面仍待实现。不把以上 32 个不同连接的路径用例写成所有路线拓扑均通过。无新增提交/推送。

最终检查点：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mapping-full-r2' --results-directory /tmp/tenonedge-mapping-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：679 单元 + 551 集成 = **1230 项通过**，0 失败/跳过；已独立读取两份 TRX 的 Counters 确认 executed=passed=total。环境 Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-mapping-full-r2.log`。变更 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include $(cat /tmp/tenonedge-mapping-files)` 退出 0，`git diff --check` 通过。

本地提交整理：按维护者“整理提交一下未提交的，然后继续”的授权，将 §2.29—§2.34 交织的 HTTP 凭证、GET 输入、时间映射、原文所有权与输出映射作为整体回归检查点提交，不推送。不代表 T2 全部完成或已达到网页访问目标。

### 2.35 T2-06：无响应输出与正常输出的真实网络隔离

继续本地提交 `9dba961` 之后的既有授权，补充 AT-06/GW-13 的软件网络验收：单次输入分别映射到两个不同 HTTP 连接，慢端接收 POST 后被测试屏障阻止返回响应；正常端实际收到另一份映射后的 POST 并返回 204。保持慢端请求仍未结束时，检查正常端队列记录已删除、慢端记录仍待发；随后慢端返回 503、停机后仍保持待发。两路数值分别为 84 和 126，sourceMessageId 相同，输入只启动一次，在途预算最终归零。不是真实外部设备验证。

- 用例：`HttpOutputTests.UnresponsiveMappedTargetDoesNotBlockAnotherTargetsAcknowledgement`，复用正式 HTTP 适配器、RuntimeHost、SQLite 与两个 loopback TCP 服务，不使用模拟发送确认。
- 定向：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~UnresponsiveMappedTarget --logger 'trx;LogFilePrefix=mapping-isolation' --results-directory /tmp/tenonedge-mapping-isolation`，1/1 通过，0 失败/跳过。
- 格式：`dotnet format TenonEdge.sln --no-restore --verify-no-changes --include tests/IntegrationTests/Runtime/HttpOutputTests.cs` 退出 0。

- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mapping-isolation-full' --results-directory /tmp/tenonedge-mapping-isolation-full --blame-hang-timeout 5m --blame-hang-dump-type none`：679 单元 + 552 集成 = **1231 项通过**，0 失败/跳过。两份 TRX 的 Counters 已独立核验，日志 `/tmp/tenonedge-mapping-isolation-full.log`；Linux x64 / SDK 10.0.401。`git diff --check` 通过。

边界：本用例验证不同 connectionId 的隔离，不宣称同一连接存在独立发送者或独立退避。同连接多路径尚未实现；其发送者、队列与暂停必须仍以 connectionId 为单位。后续须按总设计 §8.3 核对 HTTP 指纹的基础路径语义及冻结请求路由，不能直接删除重复连接保护。T2、T3 和可视化目标仍未完成。

### 2.36 T2-06：HTTP 连接身份与输出路径分离

前一轮已提交 `21f052b`，本轮继续同连接多路径的前置修正。总设计 §8.3 明确 HTTP 身份指纹由 scheme、主机、端口、基础路径和非秘密账户身份/标记组成；此前实现使用完整 endpoint，且把 API Key 参数名也计入，与该基线不符。本轮改用已规范化的 BaseAddress、认证类型、用户名和账户标记。路径、查询、超时、密钥版本与 API Key 参数名不改变连接身份。没有修改连接凭证失效、目标变更守卫或发送时冻结路径检查。

- 单元验证：同连接不同路径/查询拥有同一指纹；主机、协议、端口、基础路径、用户名、认证类型和账户标记变化仍改变指纹；默认端口/主机大小写规范化、密钥版本与参数名变化不改变指纹。
- 真实适配器负向验证：即使指纹一致，向不同路径或查询的输出适配器提交旧冻结记录仍被拒绝，TCP listener 没有收到连接。既有待发/失败区记录阻止改路径/查询的 Host 测试保持通过。
- 开发检查点兼容边界：旧 endpoint 算法产生的记录不自动重写或发送，启动核对按“目标已变化”隔离，保留原指纹和正文。真实 RuntimeHost/SQLite 用例验证此行为；既有测试数据若有积压须先核查隔离区，而非当成已发送。不新增自动迁移策略。
- 回归先验证旧实现确实失败：`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~FingerprintUsesConnectionIdentity`，1 项失败（预期），日志 `/tmp/tenonedge-http-identity-red.log`。修正后 HTTP settings/output/target-change 定向 35 单元 + 21 集成通过，TRX 位于 `/tmp/tenonedge-http-identity`。随后旧记录隔离用例首次因属性名错误编译失败，修正为实际 StoredDelivery.Fingerprint 后独立 1/1 通过，日志 `/tmp/tenonedge-http-identity-legacy-r2.log`。

- 最终回归：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-identity-full' --results-directory /tmp/tenonedge-http-identity-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**685 单元 + 555 集成 = 1240 项通过**，0 失败/跳过，两份 TRX 的 Counters 已核验。环境 Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-http-identity-full.log`。三个变更 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include …` 退出 0（`/tmp/tenonedge-http-identity-format-final.log`），`git diff --check` 通过。

本轮不放开重复 connectionId，不宣称同连接多路径已实现。后续仍需一连接一发送者、路径独立映射及冻结报文路由的整体验证；T3/网页尚未开始。

### 2.37 T2-06：MQTT 同连接多路径、单发送者

继续既有授权。本轮实现 MQTT 同一 connectionId 下多个输出定义，不为每个定义创建发送者或客户端。HTTP 仍维持同连接单输出限制，待逐请求成功判定接线后再放开；不以 MQTT 的可共享判断直接套用 HTTP。

- IOutputTarget 增加默认拒绝的共享发送者判断；MQTT 只在 connectionId、规范化主机/端口/TLS、认证模式、超时、PING 间隔与实际 ClientId 全部相同时允许。判断使用与共享会话租约相同的连接参数，路径的 Topic/QoS、映射和输入引用不作为连接身份。
- RuntimeHost 每连接只构建一个 OutputSenderUnit；准备输出仍逐路径保存，投递上界按路径数量计算，启动隔离按 connectionId 去重。同 Broker 不同连接的 ClientId 冲突保护仍在。未支持的协议与参数冲突不会因 connectionId 一样而被错误合并。
- 分发器映射索引改为输出定义 RuntimeUnitId，不再按 connectionId 覆盖或重复键异常。各路径分别冻结 Topic/QoS、报文与独立 deliveryId；保留同 sourceMessageId。路径计数增加 `route/<inputKey>/<outputKind>/<outputKey>/<counter>`；旧 `path/<inputKey>/<connectionId>/<counter>` 保留为连接汇总，全局 activity 不重复累加。
- Host 在快照冻结前拒绝同连接协议或 MQTT 参数冲突；目标变更检查改为接收方/Topic 集合，而非 connectionId 唯一字典。有待发或失败记录时，已存在的接收方/Topic 不得消失；可增加路径，不因多 Topic 崩溃，不因节点排序变化误判接收方变化。

验证范围：
- 真实 Mosquitto：单输入的三条路径共用一个 connectionId，其中一条必填失败；另外两条独立映射为 42/84，分别实际发布到两个 Topic、QoS 1/0，由独立订阅者收到。检查只有一个 MQTT 发送者、一次真实 TCP 连接，确认后两条记录删除，失败路径独立计数，内存预算释放。
- RuntimeHost/SQLite：同一连接 32 条路径各产生一条不同编号/不同映射正文的待发记录；仅启动一个输入和一个 MQTT 发送者，预算不超过 64 MiB。发送连接器拒绝网络，本项不冒充 Broker 确认。33 条路径构建拒绝。
- 参数冲突七项（主机/端口/TLS/认证/超时/PING/ClientId）冻结前拒绝；多个 Topic 的增加、排序变化、修改/删除保护与失败区保护通过。
- 初次真实 Broker 用例编译被 xUnit2031 规则拒绝（Assert.Single 前的 Where），改用谓词重载后，`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~MappedOutputTests|FullyQualifiedName~OutputPipelineTests|FullyQualifiedName~MultipleMappedPathsUseOneSender|FullyQualifiedName~HttpTargetChangeTests' --logger 'trx;LogFilePrefix=shared-mqtt-r2' --results-directory /tmp/tenonedge-shared-mqtt-r2`：37 项通过，0 失败/跳过；日志 `/tmp/tenonedge-shared-mqtt-r2.log`。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=shared-mqtt-full' --results-directory /tmp/tenonedge-shared-mqtt-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**685 单元 + 566 集成 = 1251 项通过**，0 失败/跳过，两份 TRX Counters 已核验。环境 Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-shared-mqtt-full.log`。变更 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include $(cat /tmp/tenonedge-shared-mqtt-files)` 退出 0，`git diff --check` 通过。

边界：运行计划仍为明确输入→输出定义，图编译及 HTTP 同连接多路径未完成；MQTT 输入仍未实现，不能据本轮宣称 T2 全部完成或 T3 网页已可访问。

### 2.38 T2-06：HTTP 同连接多路径与冻结成功约定

继续既有授权。HTTP 路径只有基础地址、超时及 connectionId 一致才共用发送者/客户端；Topic/QoS 直接冻结的 MQTT 机制不够表达 HTTP 的逐路径成功判定，本轮补齐它后才放开 HTTP 同连接多输出。

- OutputMessage/DeliveryDraft/StoredDelivery 增加可空 ProtocolMetadata，保存协议专属的非秘密请求约定，UTF-8 上限 16 KiB；不是 HTTP 请求头，不保存认证秘密。唯一写者的 queue.db 迁移追加版本 5、protocol_metadata 列，旧记录为 null，不重写旧正文/指纹/配额。逻辑 ByteSize 包含该字段，HTTP 格式化预约相应增加；不修改配置库版本。
- HttpFrozenRequest 固定版本 1，冻结本条路径、可选 JSON Pointer 与标量成功值；配置时拒绝超限，发送时严格校验版本、字段、大小、路径对应及规则。每条记录读取自己的规则，与当前节点或排序第一条路径的规则无关。路径仍经过基础地址约束检查，凭证仍由当前连接解析、不会入库。未知/损坏的冻结约定在联网前拒绝。
- 共用发送者后仍按连接 seq 顺序发送、退避/暂停/配额也按连接。不能把同连接的多个路径当作互不影响的独立目标；不同连接隔离仍由 §2.35 覆盖。
- 旧开发检查点的兼容边界：v4→v5 保留缺少约定的旧记录。完整 endpoint 旧指纹仍按 §2.36 隔离；指纹虽匹配但缺少冻结约定时不猜测成功规则——路径不匹配则沿用目标隔离，路径匹配则暂停连接，待核查清理或迁移，不按当前默认 2xx 静默确认旧记录。未新增自动迁移/重写。

验证：
- 实际 RuntimeHost→SQLite→重启→TCP keep-alive POST：两路径同连接，分别冻结不同映射值、静态头、成功值；重启后把配置中的映射、头和成功值均改为 999/changed，待发记录仍按旧报文、旧头和旧成功值送达并删除。覆盖不同路径和相同路径两种场景（相同路径可有不同成功约定，不能仅按 URL 查当前规则）。一个发送者、同一 TCP 连接完成两次请求，停机预算归零。
- 非法元数据五项与旧记录缺少约定均无网络请求；过大的配置成功值冻结前拒绝。SQLite 验证 UTF-8 大小边界、配额字节、写者重启保持、v4 旧记录迁移后 null/正文/字节不变。
- 首轮 HTTP/队列定向 50 项通过（`/tmp/tenonedge-http-multipath`）；元数据存储补充定向 1 单元+3 集成通过（`/tmp/tenonedge-http-metadata-storage`）。全量 r1 单元 686 通过，集成 576/577；失败是实际进程测试仍硬编码 schema 4，观察到新 schema 5。修正精确预期为 5，未放宽断言；过程日志 `/tmp/tenonedge-http-multipath-full.log` 保留。

- 最终全量 r2：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=http-multipath-full-r2' --results-directory /tmp/tenonedge-http-multipath-full-r2 --blame-hang-timeout 5m --blame-hang-dump-type none`：**686 单元 + 577 集成 = 1263 项通过**，0 失败/跳过，两份 TRX Counters 已核验。环境 Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-http-multipath-full-r2.log`。变更 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include $(cat /tmp/tenonedge-http-multipath-files)` 退出 0，日志 `/tmp/tenonedge-http-multipath-format-final.log`；`git diff --check` 通过。

边界：HTTP/MQTT 现在支持明确 RuntimePlan 的同连接多路径，但图编译、MQTT 输入、Modbus 正式适配器与其余 T2 验收仍未完成，T3 网页尚未开始。本轮不是软件升级/旧队列自动迁移功能的交付。

### 2.39 T2-04/05/06：通用输出的三种报文格式

继续既有授权，落实总设计 §6.5 与功能需求 §5.5 的后端格式能力。HTTP/MQTT 输出配置新增可选 `payload`；省略/null 或 `{"mode":"envelope"}` 为标准信封，`{"mode":"values"}` 仅输出值对象，`{"mode":"custom","fields":[...]}` 为显式字段组装。标准信封仍是默认，纯函数测试证明默认字节不变。只发数值的采集时间/质量丢失提示仍须在 T3 页面实现。

- 自定义每字段指定 target，sourceField/constant/placeholder 三选一；支持固定 JSON 标量或 null，以及 `{messageId}`、`{sourceMessageId}`、`{collectedAt}` 三个精确白名单占位符。占位符在独立属性中显式选择，常量字符串中的花括号只当字面量，不解释表达式。例：`{"mode":"custom","fields":[{"target":"reading","sourceField":"voltage"},{"target":"id","placeholder":"{messageId}"}]}`。本实现组装一个字段对象，不引入脚本、任意字符串模板或 JSON 树变换语言。
- 字段最多 1000 个，名称最多 64 字符、不能重复；未知字段/模式、来源冲突、非法占位符、非有限数字常量与超限常量合计在配置阶段拒绝。required 来源缺失/bad 时拒绝本路径，可选缺失/bad 省略，绝不补 0 或沿用旧值。
- 使用 Utf8JsonWriter 与既有有界缓冲，不拼 JSON 文本；所有输出仍受 256 KiB 限制。来源 FieldValue 按既有数值规则写出，UInt64 最大值与 decimal 精度不经 double；常量的原始数字字面量也不重写成 double。配置编译后的常量独立持有，不依赖临时 JsonDocument。
- 自定义可重复引用来源或附加常量，小输入也能扩张成大输出，所以增加最小编码暂存声明。各路径串行复用该声明的最大值，不逐路径叠加 32 份编码工作区；冻结正文仍按路径累加。在途预算验证覆盖 32 条同连接自定义路径，峰值在 32—64 MiB 内并最终归零。
- 两协议在冻结快照前编译格式、在映射后生成正文；入库后重启/改格式不重新组装旧记录，HTTP 的 Idempotency-Key 不随正文模式改变。

验证过程：
- 纯函数初轮 15 项通过，补充静态常量合计上限后 16 项通过；覆盖默认信封、大整数/decimal、JSON 注入样例、白名单占位符、必填/可选、常量 null、坏配置、重复引用扩张与 UTF-8 转义超限。`/tmp/tenonedge-payload-limits` 为 16 单元+1 集成通过。
- 真实网络 r1 的 HTTP 两个新增模式测试失败：测试配置遗漏了 payload 属性，实际仍为默认信封；补上实际配置后 HTTP 重启重放（旧格式→新配置默认信封）与真实 Mosquitto 独立订阅共 8 项全部通过，日志 `/tmp/tenonedge-payload-wire-r2.log`、TRX `/tmp/tenonedge-payload-wire-r2`。不以只测纯函数冒充协议输出接线。
- SQLite/运行时还覆盖同连接上必填自定义组装失败仅拒绝该路径，另一条 values 路径正常入队，路径拒绝计数落盘；两种协议的非法 payload 在快照冻结前拒绝。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=payload-full' --results-directory /tmp/tenonedge-payload-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**702 单元 + 585 集成 = 1287 项通过**，0 失败/跳过，两份 TRX Counters 已核验。环境 Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-payload-full.log`。所有变更 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include $(cat /tmp/tenonedge-payload-files)` 退出 0（`/tmp/tenonedge-payload-format.log`），`git diff --check` 通过。

边界：这里只交付后端报文配置与发送，未交付网页提示、编辑器、预览页面；MQTT 输入与 Modbus 正式采集等剩余 T2 项仍未完成，未进入 T3。

### 2.40 T2-03 前置：输入持久接管回执

继续既有授权，先补齐 MQTT QoS 1 手动确认所依赖的运行时契约，不把轮询输入的“入内存通道即返回”当作 Broker 消息已被持久接管。正式 MQTT 输入适配器尚未接线，本节不是 AT-03/25 的 Broker 确认验收。

- 输入工厂可声明 UsesDurableHandoff，InputMessageWriter 增加 ProduceConfirmedAsync；仍先预约全局预算再解析，返回 Committed/Rejected/StorageFailed/Stopped。Committed 表示已提交的有效分支（其他分支可能被明确拒绝），Rejected 表示没有需要持久接管的投递；后两种绝不允许确认上游。回调 null 表示输入已分类计数的明确丢弃。
- BudgetedInputChannel 按消息对象身份保存回执，持久结果只有分发器完成入库或明确拒绝后才交出；每条声明持久接管的消息额外保守预约 1 KiB。调用方取消不提前释放已经转交运行时的报文/预约；最终由分发或停止清理释放回执索引与预算。入队失败则立即撤销索引，普通清理读取只能返回 Stopped，不能伪造提交。
- 声明持久接管的输入即使没有输出也启动分发器，返回明确 Rejected 并计 noDeliveryMessages；全部映射失败同样处理。配额/保护拒绝属于明确策略丢弃，返回 Rejected；有效记录已提交且另一些分支明确被拒绝时返回 Committed。
- 写入失败对可重投输入返回 StorageFailed，单独计 `input/<inputKey>/handoffFailed` 与 input.handoffFailed 事件，不记作轮询采样丢失；轮询/模拟输入的原 storageLostSourceMessages 语义不变。运行时不代替协议适配器发 ACK 或重连。

验证：
- 单元覆盖“进入通道与取走消息都不完成回执”、取消等待后运行时仍持有预算、满通道取消撤销索引、null 明确拒绝、停止清理不伪报提交、未声明工厂不能调用回执接口。
- 真实 SQLite/RuntimeHost：持有外部写事务时回执不成功；释放锁后只有已提交记录才返回 Committed；无输出、全部映射拒绝、配额拒绝不永久等待；一条有效分支和一条拒绝分支的源消息等待有效记录提交；SQLITE_ERROR 触发整批回滚后返回 StorageFailed，未记录轮询丢失，计数落盘、预算归零。输出连接器拒绝联网以保留记录，不冒充 MQTT 手动确认。
- 定向 r1 单元 18 通过，集成因 CA1869（测试重复创建 JsonSerializerOptions）未编译完成；改为静态共享选项。定向 r2：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~InputHandoffTests|FullyQualifiedName~InFlightBudgetTests' --logger 'trx;LogFilePrefix=handoff-r2' --results-directory /tmp/tenonedge-handoff-r2`，18 单元+5 集成通过，0 失败/跳过。后续补入部分分支提交与未声明工厂保护用例，见最终回归。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=handoff-full' --results-directory /tmp/tenonedge-handoff-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**707 单元 + 591 集成 = 1298 项通过**，0 失败/跳过，两份 TRX Counters 已核验。随后仅加强未声明工厂测试，确保生产回调真的没执行（避免两处同类型异常造成假阳性）；该用例独立复验 1/1 通过，TRX `/tmp/tenonedge-handoff-guard`。Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-handoff-full.log`。变更 C# 文件 format --verify-no-changes 退出 0（`/tmp/tenonedge-handoff-format.log`），加强后的测试文件另验格式退出 0（`/tmp/tenonedge-handoff-guard-format.log`）；`git diff --check` 通过。

边界：仍待 MQTT 共享会话/订阅协调、连接代次与按到达顺序确认、存储失败断连、QoS 0 背压策略、SessionPresent 与未确认重投的真实 Broker 验证。不可据本节宣称 QoS 1 可靠输入已实现。T2/T3 及网页目标仍未完成。

### 2.41 T2-03 前置：MQTT 会话解除对输出节点的依赖

继续既有授权。检查正式会话发现 MqttSession 构造依赖 MqttOutputTarget，输入订阅若复用它就必须伪造一个输出节点。本轮移除该依赖，不创建第二个客户端或第二套连接管理。

- 提取 MqttConnectionSettings，仅承载主机/端口/TLS、认证模式、超时、PING、ClientId 与连接 ID。MqttOutputSettings 继承这部分，仍自行校验 Topic/QoS、输入引用、映射与报文格式；不改变已有配置字段与默认值。
- 会话直接依赖连接参数、实际 ClientId、凭证解析、时钟和出站连接器；不读取任何输出节点的 Topic、映射或正文格式。MQTT 当前凭证、指纹复核、超时取消、显式 PING、TLS 与错误分类保持原语义。
- ConnectionIdentity 的规范化计算在冻结前的一致性检查和实际共享租约两处共用，避免两份字段清单漂移。主机大小写规范化，Topic/QoS 不进入连接身份；超时或 ClientId 改变租约身份但不改变接收方指纹。
- 新增真实 Mosquitto 用例：只创建连接参数和会话（没有输出定义/输出工厂），发布冻结消息，由独立订阅者接收并校验正文，实际只建一次 TCP 连接。既有多路径、认证轮换、超时、断网恢复与 TLS 拒绝用例继续执行。
- 定向：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~MqttOutputTests|FullyQualifiedName~ConflictingSharedMqttParameters' --logger 'trx;LogFilePrefix=mqtt-connection' --results-directory /tmp/tenonedge-mqtt-connection`：30 单元+22 集成通过，0 失败/跳过，日志 `/tmp/tenonedge-mqtt-connection.log`。随后补充连接身份独立于输出/主机大小写、超时身份与接收方指纹区别的单元用例。

- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-connection-full' --results-directory /tmp/tenonedge-mqtt-connection-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**708 单元 + 592 集成 = 1300 项通过**，0 失败/跳过，两份 TRX Counters 已核验。Linux x64 / SDK 10.0.401，日志 `/tmp/tenonedge-mqtt-connection-full.log`。变更 C# 文件 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include $(cat /tmp/tenonedge-mqtt-connection-files)` 退出 0（`/tmp/tenonedge-mqtt-connection-format.log`），`git diff --check` 通过。

边界与下一步：这仍是正式输入的前置工程，当前会话仍只发布、CleanSession=true。本轮没有提前声称订阅、持久会话或手动确认通过。下一步的关键是每连接在全部输入启动/失败后统一启用订阅，避免第一个输入启动时就抢先连接；不能让接收回调在持有共享串行执行器时等待自己的输出确认。MQTTnet 内部接收队列无界，必须保持设计规定的回调等待上限及资源保护。

### 2.42 T2-03 子项：持久 MQTT 会话与手动确认检查点

维护者再次要求“整理提交一下未提交的，然后继续”。本地整理 §2.36—§2.42 的共享输出、冻结报文、输入持久接管与 MQTT 会话前置，不推送，不将本检查点视为 T2-03 或网页完成。

- MQTT 输入接收器按过滤器合并订阅，使用 QoS 0/1、CleanSession=false；同一接收器在 SessionPresent=true 时保留订阅记录，丢失会话时重新订阅并报告。输出独立使用时仍为 CleanSession=true。
- 接收回调立即关闭自动确认；仅 Committed/Rejected 可确认，StorageFailed/Stopped 或 5 秒期限失败断开当前连接代次，不在回调内重连。不匹配、超大和默认忽略 retained 为显式丢弃。MaintainAsync 只表示连接维护成功，不表示输入持久提交。
- 真实 Mosquitto 两用例：单个 TCP 会话既订阅又发布；接管失败不确认、断连后 SessionPresent=true，Broker 以 DUP 重投，第二次成功接管后才确认。接管回调此处返回测试结果，**不是实际 SQLite 与 Broker 联合验收**。
- 修复新增测试将 string 传给 byte[] 发布参数导致的编译错误；定向 2/2 通过。
- Linux x64 / .NET SDK 10.0.401，全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=checkpoint' --results-directory /tmp/tenonedge-checkpoint-1302 --blame-hang-timeout 5m --blame-hang-dump-type none`。**708 单元 + 594 集成 = 1302 通过，0 失败/跳过**；两份 TRX Counters 已独立核对，日志 `/tmp/tenonedge-checkpoint-1302.log`。
- 首次格式验证发现新增接收器/会话的空白格式问题，使用 dotnet format whitespace 仅整理变更 C# 文件；最终验证见 `/tmp/tenonedge-checkpoint-format-final.log`。

剩余：确认期限/取消的确定性覆盖、SQLite 联合接管、Host 输入工厂与 JSON 解码、每连接全部输入启动/失败后的订阅屏障、跨进程订阅增删协调、MQTTnet 内部无界接收队列的资源限制。当前接收器仅保存进程内已订阅集合，尚未接入 Host，不声称正式输入完成。继续 T2-03，不提前创建 web/。

### 2.43 T2-03 继续：接管期限与迟到结果

§2.36—§2.42 已整理为本地提交 `071aee8`，未推送。继续增加真实 Mosquitto + FakeTimeProvider 用例：接管回调故意忽略取消，等待期间没有确认；推进 5 秒后报告 handoffTimeout 并断开连接，旧接管任务随后返回 Committed 也没有确认。此处仍是受控接管回调，不冒充 SQLite 联合验收。

命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MqttInputSessionTests --logger 'trx;LogFilePrefix=deadline' --results-directory /tmp/tenonedge-mqtt-deadline`，**3/3 通过，0 失败/跳过**（包含新增 1 项）；日志 `/tmp/tenonedge-mqtt-deadline.log`。本次仅新增测试，未重跑全量；全量基线仍为 §2.42 的 1302 项。后续继续 SQLite 联合接管与 Host 输入生命周期协调，T2-03 未完成。

### 2.44 T2-03 / AT-25 子场景：真实 Broker 重投与 SQLite 提交联合验证

上一轮完成本地提交和超时用例，属于有代码与验证证据的进展。本轮继续既有目标，不将 T2-03 缩减为会话原型。

- 增加真实 Mosquitto + RuntimeHost + queue.db 联合用例。测试输入工厂提供运行时持久接管上下文，由正式 MqttInputReceiver 调用 ProduceConfirmedAsync；实际分发器与写者负责提交，而非回调直接伪造 Committed。
- SQLite BEFORE INSERT 触发器制造实际写入错误：首次接管回滚、队列为空、没有确认，MQTT 会话断开。移除触发器后重连，SessionPresent=true，收到 DUP 重投；确认报告发生时另行读取 SQLite，已存在一条待发记录。两次接管、一次提交，handoffFailed=1，停止后在途预算归零。
- 出站 HTTP 连接器故意拒绝联网以保留记录用于核验。输入字段在测试桥接器中构造，**不代表 MQTT JSON 解码、正式 Host 输入工厂、端到端转发或真实设备已完成**。
- 定向命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~InputHandoffTests --logger 'trx;LogFilePrefix=broker-sqlite' --results-directory /tmp/tenonedge-broker-sqlite`：7/7 通过，0 失败/跳过。
- Linux x64 / SDK 10.0.401，全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=broker-sqlite-full' --results-directory /tmp/tenonedge-broker-sqlite-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**708 单元 + 596 集成 = 1304 项通过**，0 失败/跳过；两份 TRX Counters 已核对，日志 `/tmp/tenonedge-broker-sqlite-full.log`。格式初检仅报告 using 排序，整理后复验日志 `/tmp/tenonedge-broker-sqlite-format-final.log`。

下一步：正式 MQTT 输入需计划级连接/绑定准备与激活屏障；现有逐单元 Build 和串行连接租约不能直接在首个输入启动时联网，更不能让接收回调持有共享执行器等待投递。准备未激活版本不得修改运行版本的订阅。继续处理这些生命周期约束，再接入 Host；T2/T3 与实际网页访问仍未完成。

### 2.45 T2-03 子项：MQTT JSON 解码接入持久接管

上一轮 §2.44 已取得真实 Broker/SQLite 联合证据，本轮继续替换测试中的手工字段构造，新增正式适配层 MqttInputHandoff。此模块尚未注册为 Host 输入工厂。

- 调用 InputMessageWriter 先预约 48 MiB 解析峰值，再复制与解析 MQTT 正文；消息阶段预算覆盖字段与独立 raw JSON。正文限制 1 MiB、深度 64，严格 UTF-8 和单 JSON 文档，不输出异常正文。
- 复用 IJsonInputDecoder 的固定字段类型/质量与显式上游时间，采集接收时间在等待预算前记录；无效上游时间回退并计数。RawJsonPayload 独立持有正文副本，不依赖 MQTTnet 回调内存或已释放 JsonDocument。模块只返回持久接管结果，不确认、不重连、不占共享串行执行器。
- 无效正文/超限分类为 DataError，回调返回 null，经运行时明确 Rejected，允许接收器计数后确认；有效消息仍等待实际分发/提交。QoS 0 即满即丢策略尚未接入，不能用本持久接管模块冒充该策略完成。
- §2.44 真实 Broker 用例现在使用正式解码模块，不再手工构造 value=42。新增 8 个测试覆盖精确 Int64、源字节改写后 raw 仍有效、持久回执等待、非法文档/UTF-8/大小/深度、字段质量、上游时间与回退。测试中手动 Release 的用例只验解码与回执契约，真实数据库证据仍来自 Broker 联合用例。
- 初次编译发现 ProduceConfirmedAsync 返回 ValueTask 而非 Task，改为直接 await；上游时间用例初次使用不存在的 iso8601 枚举导致 2 项失败，按实际契约改为 rfc3339。未将失败记录当作通过。
- Linux x64 / SDK 10.0.401，最终命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-json-full' --results-directory /tmp/tenonedge-mqtt-json-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**708 单元 + 604 集成 = 1312 通过**，0 失败/跳过，TRX Counters 已核对；日志 `/tmp/tenonedge-mqtt-json-full.log`。变更 C# 格式验证日志 `/tmp/tenonedge-mqtt-json-format-final.log`，`git diff --check` 通过。

仍待：Host 输入配置/工厂、每连接激活屏障、QoS 0 背压、跨进程订阅协调与资源保护。§2.44—§2.45 本轮未提交，未推送，T2/T3 与网页目标保持未完成。

### 2.46 T2-03 / AT-25 子场景：QoS 0 非等待背压

上一轮已完成 JSON 解码与回归，本轮继续实现其不可重投分支，不让 QoS 0 借用 QoS 1 的等待入库语义。

- InputMessageWriter 新增单写者 TryProduce：检查通道容量，尝试预约全局解析/输出预算，成功后才同步调用解析器；满通道或预算不足立即 false，不挂等待任务、不建立额外队列。异常、取消、明确返回 null、入队失败均释放预约；成功移交的预算由原分发路径释放。关闭通道按现有 ChannelClosedException 契约处理。
- MqttInputHandoff 按消息实际 QoS 选择路径：QoS 0 非等待入队，容量不足计 CycleSkipped/overrun 并返回 Rejected；有效入队返回新增 Buffered（不是 Committed）。坏 JSON 仍明确 Rejected。QoS 1 保持 ProduceConfirmedAsync 持久接管。
- 接收器只允许 QoS 0 返回 Buffered，QoS 1 遇到 Buffered 仍断连且不确认，避免未来接线误用“已入内存”回执。真实 Mosquitto 重投用例增加 Buffered 失败分支验证这一保护。
- 新增容量/预算耗尽下立即返回、满通道不解析第二条正文、释放预算后恢复、没有持久回执、异常与精确等额预约释放测试。定向首次单元编译被 xUnit1051 拦截（未传测试取消令牌），补齐后执行全量；同期定向集成 10/10 通过，不把单元未编译写作通过。
- Linux x64 / SDK 10.0.401，最终命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=qos0-full' --results-directory /tmp/tenonedge-qos0-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**709 单元 + 607 集成 = 1316 项通过**，0 失败/跳过；两份 TRX Counters 已核对，日志 `/tmp/tenonedge-qos0-full.log`。变更 C# 格式复验退出 0（`/tmp/tenonedge-qos0-format-final.log`），`git diff --check` 通过。

边界：此处限制的是运行时输入通道与解析预算，MQTTnet 内部接收队列仍有未解决的资源边界；尚未接入正式 Host 输入工厂、连接激活屏障与订阅持久协调。不宣称 AT-25 全部通过或网页已实现。本轮改动未提交、未推送。

### 2.47 T2-03 前置：计划隔离的激活等待点

上一轮 QoS 0 背压有实现和回归证据，本轮推进正式 MQTT 输入所需的计划生命周期约束。

- RuntimeHost.Prepare 为每个 PreparedPlan 创建独立完成信号，传给其输入上下文；工厂重建实例时沿用相同上下文/信号。InputUnitContext.WaitForActivationAsync 允许共享连接类输入在启动后、联网前等待；调用者取消只结束自己的等待，不取消整个计划。独立测试装配没有计划时维持直接放行。
- ActivateAsync 在输入/分发器/发送者全部交给监督者安排启动或记录未启动之后放行。准备未激活的草稿始终不放行，Dispose 取消其信号；准备另一个草稿不影响现行计划。激活异常取消信号；目标隔离提交失败时在释放准备实例前先取消，避免释放逻辑等待激活。
- 新增 4 个 RuntimeHost 集成用例：两个相同定义的准备计划仍互相隔离；缺失输入模块不阻止正常输入放行；单个等待者取消不污染计划；写者停止导致隔离失败时不放行且 ActiveRevision 恢复为空。
- 本信号证明的是**计划启动安排结束**，不是每个协议单元已完成网络初始化。正式 MQTT 每连接仍需结合输入绑定的启动/故障状态协调订阅，且与输出共享连接的启动也必须遵守该条件；不能据本节宣称完整 MQTT 激活屏障或正式 Host 输入已完成。
- Linux x64 / SDK 10.0.401，全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=activation-full' --results-directory /tmp/tenonedge-activation-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**709 单元 + 611 集成 = 1320 项通过**，0 失败/跳过，两份 TRX Counters 已核对；日志 `/tmp/tenonedge-activation-full.log`。变更 C# format --verify-no-changes 退出 0（`/tmp/tenonedge-activation-format-final.log`），`git diff --check` 通过。

本轮未提交、未推送。继续计划范围内的 MQTT 输入/输出共同装配、订阅协调与内部队列资源限制；T2/T3 和真实网页访问目标尚未完成。

### 2.48 T2-03 子项：MQTT 输入配置及保留消息前置校验

上一轮增加计划激活等待机制，本轮继续为输入装配提供严格配置解析，不提前注册尚未具备共享连接协调的 Host 工厂。

- 新增 MqttInputSettings：继承共用连接参数，包含 TopicFilter、QoS、ProcessRetained、Fields、UpstreamTime；默认 QoS 1、不处理保留消息，字段限制 1—1000。未知属性和秘密 password 不静默忽略。
- 开启 ProcessRetained 必须提供带 path/format 的上游时间对象；具体字段/时间语义仍由现有 IJsonInputDecoderFactory 编译，未知时间格式不能绕过编译。解析器只校验结构，不把它当作整份配置已经可运行。
- 配置与实际接收器共用 Topic Filter 校验，保留协议通配符语义；按 UTF-8 字节限制长度，拒绝空值、NUL、错误通配位置和未配对代理字符。初次尝试 Utf16.IsValid 编译失败（当前 SDK 无该公开 API），已改用经验证可用的严格 UTF8Encoding，不假设库接口存在。
- 新增 25 个配置用例：正常通配/中文、QoS、连接参数、字段数量、保留消息/上游时间依赖、未知/秘密属性、独立 JSON 生命周期及 UTF-8 长度边界。定向 25/25 通过。
- Linux x64 / SDK 10.0.401，全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-settings-full' --results-directory /tmp/tenonedge-mqtt-settings-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 611 集成 = 1345 项通过**，0 失败/跳过；TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-settings-full.log`。变更 C# 格式复验退出 0，日志 `/tmp/tenonedge-mqtt-settings-format-final.log`；`git diff --check` 通过。

未完成项仍为：计划内 MQTT 输入/输出共同装配、每连接启动状态汇合、Host 接线、跨进程订阅协调与库内队列资源保护。不能把本配置解析子项算作正式 MQTT 输入或 AT-25 全项完成。本轮未提交、未推送。

### 2.49 T2-03 子项：MQTT 会话全部联网入口统一等待激活

上一轮已有严格输入配置，本轮把启动约束接到实际 MQTT 会话，而非仅让输入调用方等待。

- MqttSession 接受可选的激活等待委托，在创建客户端、解析凭证和发起网络连接前等待；MaintainAsync 与 SendAsync 共用入口。输出先被启动也不能绕过该等待点。调用方超时覆盖等待，取消单次发送不取消共用启动信号。输出独立场景不提供委托，维持既有行为。
- 新增真实 Mosquitto 用例：输入维护与输出发布各在屏障未放行时等待，实际 TCP 连接数为 0；放行后成功且只建一个连接。
- 新增共享租约 + MqttOutputAdapter + FakeTimeProvider 用例：等待启动的输出到 5 秒期限返回 Retry、不计毒消息、未建立 TCP，公共信号未取消；随后放行，原共享会话成功发送，只建立一个连接。回调不借用串行执行器，启动协调者也不能要求先进入该执行器才能报告准备状态。
- 定向：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MqttInputSessionTests --logger 'trx;LogFilePrefix=mqtt-gate' --results-directory /tmp/tenonedge-mqtt-gate`，7/7 通过，0 失败/跳过。
- Linux x64 / SDK 10.0.401，全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-gate-full' --results-directory /tmp/tenonedge-mqtt-gate-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 614 集成 = 1348 项通过**，0 失败/跳过，两份 TRX Counters 已核对。日志 `/tmp/tenonedge-mqtt-gate-full.log`；变更 C# 格式复验退出 0（`/tmp/tenonedge-mqtt-gate-format-final.log`），`git diff --check` 通过。

边界：本节使用受控启动信号验证真实会话，尚未由正式 Host 的计划内 MQTT 输入/输出装配器提供每连接汇合信号；因此不代表正式输入已接线或配置切换验收通过。下一步须把同一计划的订阅集合、输入上下文和输出会话组装在一起，且不能把准备中的新计划写入旧计划会话。本轮未提交、未推送。

### 2.50 T2-03 子项：计划范围的输入/输出共同连接组

上一轮会话的输入维护与输出发送已经共用激活等待，本轮增加 MqttConnectionGroup，将同一计划、同一连接的已编译订阅集合和输出借用归于同一会话。

- 连接组只持有不可变连接参数、稳定实际 ClientId、接收器、启动等待与既有服务；构造无网络 I/O，不自建连接管理。输入维护经 Acquire 借用，输出经正式 MqttOutputFactory / MqttOutputTarget 取得同组适配器，均进入现有 ISharedConnections 串行执行器。
- 输出工厂提供内部计划装配入口 InputGroups；Build 阶段就核对全部连接参数与实际 ClientId，不能延迟至首次发送才发现结构冲突。独立输出仍走原路径。同一发送者的兼容性也检查组对象身份，不将不同计划接收器混用。
- 组为本地租约身份增加计划随机标识；不改变 Broker ClientId、接收方指纹或冻结记录。新计划不能借入仍被旧计划占用的同一连接，也不能创建第二套同 ID 连接；旧引用释放后才可接管。此保护不代替 T3 配置切换的停止/排空步骤。
- 真实 Mosquitto：两个输入绑定、两个通过正式输出工厂构建的输出借用以及输入维护共用一条实际 TCP 连接，分别收到正确正文；不同组在旧租约未释放时被拒绝，释放后新组能连接。测试受控回调仅验证绑定路由与会话共享，不代表多输入 Host 端到端接线。
- 定向接收会话测试初版 9/9 通过；改用正式输出工厂后遇到 CA1861（重复常量数组），改为静态字段，最终全量如下。
- Linux x64 / SDK 10.0.401：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-group-full' --results-directory /tmp/tenonedge-mqtt-group-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 616 集成 = 1350 项通过**，0 失败/跳过；两份 TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-group-full.log`。变更 C# 格式复验退出 0（`/tmp/tenonedge-mqtt-group-format-final.log`），`git diff --check` 通过。

下一步：由正式计划装配创建输入上下文绑定和连接组，注册 MQTT 输入工厂/生命周期，接入 Host；还须补齐停机排空、订阅持久协调和内部队列资源边界。上述仍是 T2-03 子项，不宣称正式输入/网页目标完成。本轮未提交、未推送。

### 2.51 T2-03 / AT-03 子场景：计划级 MQTT 输入经真实队列转发

上一轮建立共同连接组，本轮新增 MqttPlanAdapters：一次 Prepare 独享的工厂对，把实际输入上下文、JSON 解码、持久接管、输入维护和输出借用组装在一起。尚未在 Host DI 注册，不把有状态的计划工厂作为进程级单例复用。

- 先构建全部输入，编译订阅/字段/时间并核对凭证与共享连接参数，再构建输出；输出解析到相应的惰性连接组。输出构建后禁止追加输入。监督者重建相同单元时复用原上下文与绑定；不同 Prepare 的上下文不能误用这一工厂。
- 输入 RunAsync 在进入共享串行执行器前报告启动。连接组同时等待计划激活信号与各输入启动/未启动释放信号，避免输出先占执行器导致输入无法报告启动。维护循环带连接期限和 1—30 秒指数退避；收到消息才更新成功读取状态。当前退避尚无抖动，正式注册前仍须补齐并验证故障行为。
- 停止后的新回调返回 Stopped，不产生确认；已进入持久接管的回调使用连接代次令牌，可继续等存储结果。尚须完成混合输入/输出连接停止和排空后的明确断连，不能据此宣称配置切换全部通过。
- 真实端到端用例通过 RuntimeHost 实例装配此工厂对：两个 MQTT 输入，实际 JSON 字段解码，实际 queue.db 分发/写入，正式 MQTT 输出，再由独立 Broker 客户端订阅接收。验证值 9223372036854775807 与 42、deviceKey、两次上游确认、待发记录清空、预算归零，网关只建立一个 TCP 连接；准备阶段连接数为 0。不再使用测试工厂手工发布 EdgeMessage。
- 初次编译按 CA1859 调整私有 Build 的具体返回类型；测试常量数组按 CA1861 提取静态字段。定向正式链路 1/1 通过，TRX `/tmp/tenonedge-plan-mqtt`。
- Linux x64 / SDK 10.0.401，全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=plan-mqtt-full' --results-directory /tmp/tenonedge-plan-mqtt-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 617 集成 = 1351 项通过**，0 失败/跳过，两份 TRX Counters 已核对；日志 `/tmp/tenonedge-plan-mqtt-full.log`。变更 C# 格式复验退出 0（`/tmp/tenonedge-plan-mqtt-format-final.log`），`git diff --check` 通过。

仍待：Runtime/Host 为每次 Prepare 创建新的工厂对、Host 配置/API 接线、失败输入的订阅协调、混合连接停机排空、跨进程订阅持久状态与内部队列资源限制。此处证明运行时的真实链路子场景，不代表 Host API、实际设备、AT-25 全项或网页验收。本轮未提交、未推送。

### 2.52 T2-03 前置：Runtime 每次 Prepare 创建独立协议工厂

上一轮已跑通两个正式 MQTT 输入经 SQLite 到共享输出。本轮新增 IRuntimePlanAdapterProvider / RuntimePlanAdapters 契约与 MqttPlanAdapterProvider，Runtime 每次 Prepare 新建协议工厂集合，监督者重建时沿用该计划工厂。不是请求作用域，不增加第二套 Runtime 或连接管理；同一种类重复声明在 Build 前拒绝，不静默覆盖原工厂。

- 真实 MQTT 端到端用例改为把进程级提供者交给 Runtime，不再由测试手工创建单次工厂对；运行过程中再次 Prepare 相同定义并放弃草稿，原两个订阅仍正常接收/转发，网关连接数仍为 1。新增两种重复注册拒绝用例。
- 该测试首次全量发现实际缺陷：OutputSenderUnit 构造时即调用 CreateAdapter/Acquire，准备新 MQTT 草稿因此与旧计划连接身份冲突。现改为惰性适配器，仅在 RunAsync 开始时创建；未激活草稿释放时不创建、不借用，也无需释放尚不存在的适配器。
- 原有“拒绝草稿时已构造适配器数为 1”的测试随后失败，它断言的是旧生命周期；按新约束改为准备及放弃都为 0，已激活发送者释放的既有用例保持执行。没有删除失败用例或将失败记为通过。
- 初次构建按 CA1859 将私有参数改为具体字典；第一次惰性修正用可空字段触发 CS8602，改为 Lazy 并在启动时显式求值。失败日志保留 `/tmp/tenonedge-plan-provider-full.log`、`/tmp/tenonedge-plan-provider-final.log`；定向修正后 7/7 通过（`/tmp/tenonedge-plan-provider-r3`）。
- Linux x64 / SDK 10.0.401，最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=plan-provider-r4' --results-directory /tmp/tenonedge-plan-provider-r4 --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 619 集成 = 1353 项通过**，0 失败/跳过，两份 TRX Counters 已核对，日志 `/tmp/tenonedge-plan-provider-r4.log`。
- 新纳入变更的 OutputPipelineTests 文件存在既有空白格式问题，按文件整理；格式最终复验日志 `/tmp/tenonedge-plan-provider-format-final2.log`。`git diff --check` 通过。

Host 尚未注册新的提供者或开放 input.mqtt 配置；正式注册时必须替换旧的独立 MQTT 输出工厂注册，不能重复声明。继续 Host 配置校验（含反馈路线）、诊断接线、混合连接停机及订阅恢复处理。本轮未提交、未推送，T2/T3 与网页访问目标仍未完成。

### 2.53 T2-03 前置：Host MQTT 输入冻结校验与反馈闭环拒绝

上一轮解决 Runtime 每计划装配与草稿隔离。本轮在 Host 运行快照冻结边界增加 MQTT 输入校验，尚未改变 DI 注册。

- input.mqtt 严格解析连接/订阅/字段/上游时间，编译字段和时间映射；密码与未知属性在冻结前拒绝。连接一致性检查扩展到 MQTT 输入和输出，不允许同一 ID 跨 HTTP/MQTT 或连接参数不同；同一已知 Broker 的不同连接不能显式配置相同 ClientId。
- 新增纯计算 MqttFeedbackGuard：按已知 Broker Recipient、实际展开的发布 Topic、MQTT 协议过滤器匹配构建输入间转发边，通过非递归拓扑检查拒绝直接和跨连接/跨 Broker 的已知反馈闭环。不同 Topic 正常转发不拦截；遵守 # 不匹配 $SYS 的协议规则。无 DNS/网络探测，不声称能识别不同地址/端口背后实际为同一 Broker 的别名。
- 新增 14 个 Host 文档解析测试：精确/通配/设备占位符自反馈、两 Broker 闭环与单向转发、非匹配主题、共享连接参数冲突、秘密/未知属性、保留消息的时间编译、显式 ClientId 冲突。定向初版 13/13 通过，补入 ClientId 用例后统一全量。
- Linux x64 / SDK 10.0.401，命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-validation-full' --results-directory /tmp/tenonedge-mqtt-validation-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 633 集成 = 1367 项通过**，0 失败/跳过；TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-validation-full.log`。变更 C# 格式复验退出 0（`/tmp/tenonedge-mqtt-validation-format-final.log`），`git diff --check` 通过。

接线前仍须处理：MqttCredentialStore 当前目标枚举只含输出，须让输入引用也维护凭证归属；诊断须计数而非每条确认写事件；以计划提供者替换旧 MQTT 输出注册。实际展开后的默认/显式 ClientId 组合仍须在计划装配核对，不能仅依赖本节的显式冲突检查。本轮未提交、未推送，正式 Host MQTT 输入与 T2/T3 网页目标未完成。

### 2.54 T2-03 前置：MQTT 输入凭证归属与实际 ClientId 冲突

上一轮完成冻结边界校验，本轮补齐正式 Host 注册前的身份规则。

- MqttCredentialStore 在版本提交事务中同时检查 input.mqtt / output.mqtt；仅输入引用某连接时，Broker 接收方变化同样默认作废旧秘密并写审计。输入输出共用连接时第一次作废后绑定已更新，后续引用不重复作废/审计。队列目标变更检查仍只枚举输出，不误把源连接当成输出投递目标。
- 扩展真实配置库/认证 API 用例：输出独占、输入独占、输入输出共享三种版本，分别验证正常提交与审计插入故障回滚；回滚保留原秘密和旧有效版本指针，成功只写一条作废审计。定向过滤共 10 项通过（包含同名 HTTP 凭证用例），0 失败/跳过，TRX `/tmp/tenonedge-mqtt-input-credentials`。
- MqttPlanAdapters 在实际 NodeId 默认 ClientId 展开后，统一登记输入和输出的 (Recipient, ClientId) → ConnectionId；拒绝默认与显式设置碰撞，而非仅比较 JSON 中是否显式同名。两个新增用例验证输入默认值分别与另一输入/输出显式值冲突，Prepare 产生结构失败、网络尝试为 0，TRX `/tmp/tenonedge-mqtt-clientid`。
- Linux x64 / SDK 10.0.401，全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=input-identity-full' --results-directory /tmp/tenonedge-input-identity-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 639 集成 = 1373 项通过**，0 失败/跳过，两份 TRX Counters 已核对；日志 `/tmp/tenonedge-input-identity-full.log`。变更 C# 格式复验退出 0（`/tmp/tenonedge-input-identity-format-final.log`），`git diff --check` 通过。

后续继续诊断计数接线与 Host 提供者注册/实际进程验证；混合连接停止、跨进程订阅持久协调、库内队列资源限制仍未完成。本轮未提交、未推送，T2-03 / T3 与实际网页目标保持未完成。

### 2.55 T2-03 / AT-03 子场景：产品 Host MQTT 输入接线与持久诊断

上一轮补齐凭证归属与实际 ClientId 校验，本轮正式替换 Host 的独立 MQTT 输出工厂注册，注册单例无状态 MqttPlanAdapterProvider；每次 Prepare 再创建输入/输出工厂对，仍使用同一 RuntimeHost、连接管理、凭证解析和 JSON 解码器。

- 接收器的固定诊断类型进入 DeliveryCache 原有计数累加器，由唯一写者合并持久化：`connection/<id>/mqtt/{subscribed,sessionLost,acknowledged,oversized,retainedIgnored,unmatched,handoffTimeout,handoffFailed}`。接口限制连接 ID 与类型，不接收任意报文/秘密作为计数键。
- 仅 sessionLost 作为状态变化写运行事件；确认、忽略等每消息结果只计数，不逐条写配置库事件。新增计数用例验证 100 次确认、保留消息忽略、未知类型拒绝、停止提交与 Runtime 重建后的持久值。
- 新增 WebApplicationFactory 产品宿主 + 真实 Mosquitto 用例：通过正式首次设置/会话/CSRF 和 MQTT 凭证 API 保存加密凭证，关闭引导宿主后使用同一数据目录启动种子计划；默认忽略 Broker 补发的 retained 值 7，实时值 9223372036854775807 经真实输入/队列/输出由独立订阅者接收。核验已删除输出记录、两次确认、订阅/忽略持久计数及运行状态 API，响应不含 Broker 密码。
- 此测试使用真实产品服务装配和真实网络，但宿主由测试工厂在测试进程内托管；不冒充独立产品进程、实物设备或完整会话恢复验收。通过服务枚举同时验证没有残留第二个独立 MQTT 输出注册。
- 定向产品宿主 1/1 通过（`/tmp/tenonedge-mqtt-host`）。Linux x64 / SDK 10.0.401，全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-host-full' --results-directory /tmp/tenonedge-mqtt-host-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 641 集成 = 1375 项通过**，0 失败/跳过；两份 TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-host-full.log`。变更 C# 格式复验日志 `/tmp/tenonedge-mqtt-host-format-final.log`，`git diff --check` 通过。

边界：input.mqtt 已能在 Host 运行快照中使用，但完整 T2-03 仍待混合连接停止/排空、故障成员订阅协调、跨进程订阅状态与 ClientId 旧会话清理、内部接收队列资源限制等验收。现有接收器的订阅记忆仅在进程内，不能声明重启不会重复订阅或 AT-25 全项通过；前端尚未创建。本轮未提交、未推送，继续原 T2/T3 与网页目标。

### 2.56 T2-03 / AT-25 子场景：接收静默与在途回调排空

上一轮已接入 Host，本轮增加 MqttInputReceiver.QuiesceAsync，作为后续混合连接停止/排空的接收器原语，尚未把它当作整个 Runtime 停机已经完成。

- 静默立即关闭接收入口；此后所有消息（包括 retained、超大、不匹配消息）在任何分类/处理前返回，保持 AutoAcknowledge=false。已进入回调的消息继续原有 5 秒接管/确认流程，只有全部已进入回调结束后排空任务才完成。
- 接收状态与活动回调计数只在短锁内变化，网络、解析、持久接管与确认都在锁外执行。不新增后台队列或脱离生命周期的任务。重复静默幂等，取消某次排空等待不会重新开放接收或取消其他等待者。
- 真实 Mosquitto 两场景（正常排空、取消等待后再次排空）：阻塞首条已接收消息的受控接管结果，进入静默后再发布一条；首条成功接管后确认并结束排空，连接仍存在，后续消息未进入处理/未确认。由拥有者关闭会话后，以相同 ClientId 重连，SessionPresent=true，并收到后续消息重投。此测试以受控回调结果验证协议顺序，不冒充 Runtime/SQLite 联合停机验证。
- 定向初版 1/1 通过；加入取消等待分支后执行全量。Linux x64 / SDK 10.0.401：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-quiesce-full' --results-directory /tmp/tenonedge-mqtt-quiesce-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 643 集成 = 1377 项通过**，0 失败/跳过；TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-quiesce-full.log`。变更 C# 格式复验日志 `/tmp/tenonedge-mqtt-quiesce-format-final.log`，`git diff --check` 通过。

下一步须由输入单元/连接组的停机拥有者调用静默、保留租约等待在途提交与确认，再断开混合会话并阻止旧输出重连；当前 Host 停机尚未调用本原语。跨进程订阅持久协调、旧 ClientId 清理和库内队列限制亦未完成。本轮未提交、未推送，T2/T3 与实际网页目标保持未完成。

### 2.57 T2-03 / AT-25 子场景：输入停机保留租约排空后关闭混合会话

上一轮接收器静默原语已有真实 Broker 证据，本轮将其接入实际 MQTT 输入运行单元与连接组。

- 输入停止令牌的短回调先关闭整个组的接收入口，不在取消回调里等待网络；运行循环结束时仍保留租约，在独立 5 秒期限内等待已接收回调完成，然后经共享执行器断开会话，最后释放输入租约。失败路径也释放取消注册和租约。
- 会话新增独立出站取消源：静默排空时取消在途发送/维护，但不因此抢先取消还在等待存储提交的接收代次；接收排空完成后才 Reset/断开。静默后的 Maintain/Send 返回 Retry，不重连、不把旧冻结记录当作已发送。
- 连接组首次静默计一次 `connection/<id>/mqtt/quiescing`，重复停止不重复计数。该类型加入 Host 已有持久计数白名单，不增加每消息事件日志。
- 新增真实 Mosquitto + RuntimeHost + SQLite 联合用例：外部写事务阻止入库，接收器已解码一条真实消息后请求 Runtime 停止；观察静默信号时停止任务未完成、没有确认。释放 SQLite 锁后成功提交并确认，停止正常完成，混合 MQTT 输出未把停止阶段的新记录发送出去，记录留在 queue.db 等待下次启动；网关连接数保持 1、预算归零、无 StopTimedOut。
- 定向联合用例 1/1 通过（`/tmp/tenonedge-mqtt-stop-sqlite`）；前期 MQTT 接收/产品 Host 回归 13/13 通过。Linux x64 / SDK 10.0.401，最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-stop-full' --results-directory /tmp/tenonedge-mqtt-stop-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**734 单元 + 644 集成 = 1378 项通过**，0 失败/跳过；两份 TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-stop-full.log`。变更 C# 格式复验日志 `/tmp/tenonedge-mqtt-stop-format-final.log`，`git diff --check` 通过。

边界：本节验证普通停止时 SQLite 受阻后恢复的混合连接路径，不等于强杀、永久网络/存储挂起、失败配置应用后恢复旧计划等全部场景通过。跨进程订阅状态/旧 ClientId 清理、故障成员协调、退避抖动和库内接收队列资源限制仍待处理，T3 配置切换尚未实施。本轮未提交、未推送，继续原 T2/T3 与实际网页目标。


### 2.58 T2-03 子项：本地检查点与 MQTT 输入退避抖动

维护者授权“整理提交一下未提交的，然后继续”。截至 §2.57 的输入接管、计划隔离、Host 接入和停机排空整理为本地提交 `adca485`；提交前核对原始 TRX：734 个单元测试、644 个集成测试通过，失败/跳过均为 0。未推送，不代表 T2 或网页验收完成。

随后补充 MQTT 输入维护循环的重试抖动：失败基准仍为 1、2、4、8、16、30 秒并封顶，每次随机缩短最多 10%；成功恢复固定一秒维护间隔，同时重置失败退避。状态由各输入运行实例持有，等待仍使用注入的 TimeProvider 和停止令牌。不引用 Runtime 内部实现、不新增依赖或连接管理器。

新增 5 个确定性单元用例：不同随机样本下的指数增长与范围、长期封顶不溢出、成功重置且不消耗随机数、实例状态隔离。这里验证的是单个输入维护循环的退避，不宣称共享连接所有调用者已经统一连接级重试调度，也不替代断网/多设备压力实测。

验证环境：Linux x64、SDK 10.0.401；实际命令与结果：

- `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~MqttInputRetryDelayTests`：5 通过，0 失败/跳过；日志 `/tmp/tenonedge-mqtt-jitter-unit.log`。
- `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-jitter' --results-directory /tmp/tenonedge-mqtt-jitter-full --blame-hang-timeout 5m --blame-hang-dump-type none`：739 单元 + 644 集成 = **1383 通过**，0 失败/跳过；日志 `/tmp/tenonedge-mqtt-jitter-full.log`，TRX 在结果目录。
- `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include src/TenonEdge.Adapters/Mqtt/MqttPlanAdapters.cs src/TenonEdge.Adapters/Mqtt/MqttInputRetryDelay.cs tests/UnitTests/Adapters/MqttInputRetryDelayTests.cs`：退出 0；日志 `/tmp/tenonedge-mqtt-jitter-format.log`。
- `git diff --check`：通过。

未完成边界保持：跨进程订阅集合持久化与协调、旧 ClientId 会话清理、MQTTnet 接收队列内存边界、故障成员订阅协调及异常停止场景仍需继续；T2-03、AT-25、T2 和可视化访问目标均未标为完成。


### 2.59 T2-03 / AT-25 子项：持久订阅日志与宿主重建协调

继续 §2.58，新增 `IMqttSubscriptionStore` 契约、Host 的 `MqttSubscriptionStore` 和 config.db schema 11 的 `mqtt_subscription` 表。表以规范化 Broker 接收方、实际 ClientId、Topic Filter 三个文本字段联合寻址，不分配雪花 ID，不保存密码，也不按计划版本隔离。Host 通过现有计划工厂/共享连接注入日志；Prepare 不读写订阅日志，激活后的订阅协调才访问。每次数据库操作使用独立短客户端，不跨网络等待持有事务。

订阅协议：

- 连接后重新读取日志。SessionPresent=1 时，已确认落盘且 Filter/QoS 未变的项不重复 SUBSCRIBE；新增/变更项重新订阅。SessionPresent=0 时清除旧集合并重新订阅，存在历史项时记录会话丢失事件。
- 对新版本不再使用的已知 Filter 先 UNSUBSCRIBE，收到成功 UNSUBACK 后才删除日志，再协调新增项；新增有限诊断计数 `unsubscribed`，不新增每消息事件。
- SUBSCRIBE **之前**持久化未确认意图，SUBACK 后才置为已确认。这样网络成功、本地确认提交失败时，日志仍保留可能已经安装的 Filter，下次可重新订阅或退订。未确认意图不会被当作成功订阅而跳过。
- UNSUBACK 后本地删除失败时，保留日志并断开，后续重试幂等退订。不宣称 Broker 与 SQLite 可以原子提交；应答/本地提交窗口中断后，重订可能重复回放保留消息，这是显式保留的不确定性，不宣称 exactly-once。

新增 10 个软件测试，另扩充产品 Host 用例：

- 数据库完整关闭/重开后，已确认与未确认项、Unicode/引号 Filter、Broker/ClientId 隔离、幂等删除保持；非法 QoS 更新不覆盖旧项；旧 schema 10 数据升级保留。
- 真实 Mosquitto + 重新打开 config.db + 新接收器：保留未变 Filter 不重复回放 retained，新增 Filter 安装，删除 Filter 退订后不再收到该主题的实时消息；用同一 Broker 的后续 barrier 消息确认接收顺序，不用固定 sleep 判定“没有消息”。
- QoS 变更实测新的接收 QoS；测试主动清除 Broker 会话后，SessionPresent=0 触发重订及丢失诊断。这是测试清理，不是产品旧 ClientId 清理实现。
- 用真实 SQLite 触发器分别拒绝订阅意图、SUBACK 后确认更新、UNSUBACK 后删除：意图失败时借用 Broker 持久会话验证没有安装未登记订阅；确认失败后分别验证重订和退订；删除失败后验证再次退订。触发器仅存在于测试临时库。
- 产品 Host 用 `WebApplicationFactory` 完整释放/重建两次，同一数据目录、稳定默认 ClientId、真实凭证与混合 MQTT 输入/输出；两次均能转发实际消息，累计 subscribed/retainedIgnored 均保持 1。此项是同一测试进程内的宿主重建，**不是独立 OS 进程或 SIGKILL 恢复测试**。

验证环境：Linux x64、SDK 10.0.401、真实 Mosquitto 测试夹具。命令与实际结果：

1. `dotnet build TenonEdge.sln -c Release --no-restore`：退出 0，0 警告/错误；日志 `/tmp/tenonedge-subscriptions-build.log`。
2. `dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~MqttSubscriptionStoreTests|FullyQualifiedName~MqttInputSessionTests|FullyQualifiedName~MqttHostInputTests'`：首轮 20 通过，0 失败/跳过；日志 `/tmp/tenonedge-subscriptions-focused.log`。后加的边界用例包含在最终全量运行中。
3. 首次全量回归：739 单元通过、集成 652 通过/1 失败，日志 `/tmp/tenonedge-subscriptions-full.log`。失败是新增文本联合主键表未登记到 `GatewayIdentityTests` 的非雪花主键表清单；已增加明确登记和三个 TEXT 主键字段的断言，没有放宽雪花 ID 表检查。
4. 后续全量回归：739 单元通过、集成 653 通过/1 失败，日志 `/tmp/tenonedge-subscriptions-final.log`。既有 `BrokerNetworkOutageBoundsTheQueueAndRecoveryDeliversFrozenBacklog` 发现网络冻结期间一条记录 FailureCount=1，预期为 0；这不是本轮订阅测试失败，但作为未决回归问题保留，**没有通过重跑将它标为已修复**。
5. 该断网用例隔离执行一次通过（`/tmp/tenonedge-subscriptions-outage-repeat.log`）；临时仅记录异常类型/堆栈后又连续 9 次通过（`/tmp/tenonedge-outage-debug-1.log` 至 `-9.log`），带临时诊断的全量 1393 项通过（`/tmp/tenonedge-subscriptions-debug-full.log`）。未捕获导致毒消息误计数的异常类型，根因仍待定位；临时 `[DEBUG-subscription-outage]` / sender 输出已全部移除，没有据猜测修改网络异常分类。
6. 移除临时诊断后的正式命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-subscriptions-clean' --results-directory /tmp/tenonedge-subscriptions-clean --blame-hang-timeout 5m --blame-hang-dump-type none`：**739 单元 + 654 集成 = 1393 通过**，0 失败/跳过；日志 `/tmp/tenonedge-subscriptions-clean.log`，TRX 计数已独立核对。该次全绿不消除第 4 项的偶发失败记录。
7. `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include $(git diff --name-only -- '*.cs') $(git ls-files --others --exclude-standard -- '*.cs')`：退出 0；日志 `/tmp/tenonedge-subscriptions-format-final.log`；`git diff --check` 通过。

仍未完成：独立进程强杀/重启与完整 AT-25；删除最后一个输入或整个连接时的退订、旧 ClientId 会话清理、故障成员动态协调；旧部署/外部创建但没有日志的订阅无法凭空重建清单。T5 恢复旧 config.db 时须处理日志相对 Broker 会话过期的问题，不能无条件信任备份中的 confirmed 标记。MQTTnet 无界接收队列与配置切换失败恢复等既有缺口保持。T2-03、T2 和可视化访问目标仍未完成；本轮不创建 web/，不推进 T4/T5/G1 实现。


### 2.60 T2-03 / AT-25 子场景：MQTT 输入独立进程强杀恢复

在 §2.59 的持久订阅实现上新增 `MqttInputRecoveryProcessTests`，这次不使用 `WebApplicationFactory` 代替进程重启：首次设置、保存 MQTT 凭证、运行种子计划和重启均通过独立 `dotnet` 宿主进程完成，使用真实 HTTP 管理 API、config.db/queue.db、产品输入/输出适配器和真实 Mosquitto。测试拥有的宿主由 `HostProcess.KillAsync` 在 Linux 上发送 SIGKILL；没有向产品加入退出开关或测试单元。

两个场景均使用稳定默认 ClientId、同一个 Broker 持久会话和混合输入/输出连接：

- **已确认并清空输出后强杀**：先验证保留值 7 被忽略、实时值 1 已转发，等待 SQLite 中 ACK 计数和输出删除落盘，再强杀宿主。确认进程退出后发布值 84；新进程补收 84，再正确转发实时 barrier 值 126。订阅/忽略保留消息的持久计数各保持 1，没有再收到保留值 7 或旧实时值 1。
- **已解码但提交前强杀**：同样完成上述基线，然后由测试持有 queue.db 的真实独占写事务，发布值 42。通过运行状态 API 等待输入完成第二次解码，确认投递表为空且 ACK 计数仍是基线值，持锁强杀宿主，再释放事务。退出后发布 84；重启进程依次收到 Broker 重投的 42、离线积存的 84 和新发布的 126，输出最终清空。重启前的空投递表证明 42 不是从旧 queue.db 记录重放，而是由上游持久会话重新接管。

**反向验证**：临时把 QoS 1 输入由 `ProduceConfirmedAsync` 改成 `ProduceAsync` 后直接返回 Committed（错误的“入内存即确认”）。运行同一进程测试，已确认场景通过，提交前强杀场景明确失败：重启后的第一条值预期 42、实际 84，证明提前确认会丢失未落盘输入，且本测试能发现。变异随后恢复；`MqttInputHandoff.cs` 与变异前无 diff，不保留测试旁路。

实际验证（Linux x64、SDK 10.0.401）：

1. 初次编译被 CA1859 阻止，尚未执行测试；将测试私有 PublishAsync 辅助方法的返回类型改为实际的 `Task<MqttClientPublishResult>`，未关闭分析器。
2. `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MqttInputRecoveryProcessTests --logger 'trx;LogFilePrefix=mqtt-input-process' --results-directory /tmp/tenonedge-mqtt-input-process`：**2 通过，0 失败/跳过**，日志 `/tmp/tenonedge-mqtt-input-process.log`，约 33 秒。
3. 错误变异下运行同一 filter（不加 TRX 参数）：**1 通过、1 失败**；日志 `/tmp/tenonedge-mqtt-input-process-mutation.log`，失败为 Expected 42 / Actual 84，不是编译失败或环境跳过。
4. 恢复后的 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-input-process-full' --results-directory /tmp/tenonedge-mqtt-input-process-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**739 单元 + 656 集成 = 1395 通过，0 失败/跳过**；日志 `/tmp/tenonedge-mqtt-input-process-full.log`，TRX 计数已独立核对。
5. 新文件的格式检查最初报告一处 if 后的换行，已仅调整空白；最终 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include tests/IntegrationTests/Processes/MqttInputRecoveryProcessTests.cs`：退出 0；日志 `/tmp/tenonedge-mqtt-input-process-format-final.log`；`git diff --check` 通过。

边界：这里只强杀网关进程，**Broker 一直存活**；不证明 Broker 强杀/重启或断电下的离线数据落盘，不覆盖磁盘满、已提交但上游尚未收到 ACK 的所有重复窗口，也不宣称 exactly-once。旧 ClientId 清理、删除最后一个输入/连接、故障成员协调、接收队列内存边界及完整配置切换仍未完成。§2.59 的既有断网误计毒消息偶发失败本轮全量运行未复现，但未定位、未宣称修复。T2-03 / AT-25 整体、T2 与可视化访问目标继续保持未完成。


### 2.61 T2 MQTT 重连：修复旧客户端仍在断开时再次 Connect 的竞态

针对 §2.59 记录的“断网期间毒消息次数增加”继续建立可重复反馈。先新增有限并发的 `RepeatedFrozenConnectionsDoNotCountNetworkFailuresAsPoison`：8 条独立连接各进行 10 轮 TCP 双向冻结，每轮验证两次失败发送只 Retry、不计毒消息。先后错开发送与保活的时间窗口（250–950 ms、950–1090 ms、1450–1820 ms），这些探针以及带诊断的全量运行均未重现原偶发失败；它们通过不能作为已经修复的依据。

随后用真实 Broker 和真实 MQTTnet 状态机缩小关闭窗口：

- 新增 `MqttDisconnectRaceTests`。第二个测试客户端以同一 ClientId 连接，使 Broker 关闭产品的原连接；测试只在原 TCP 流的首次释放处设置有界门闩，将异步清理窗口拉宽，不伪造 MQTTnet 的 IsConnected 或 ConnectAsync 返回。
- 已观察到原会话 IsConnected=false，但 MQTTnet 仍在 DisconnectCore 中。此时产品发送再次调用旧客户端的 ConnectAsync，稳定抛出 `System.InvalidOperationException`；临时堆栈明确指向 `MQTTnet.MqttClient.ConnectAsync → MqttSession.ExecuteAsync`。旧实现两次运行均因 CountsTowardPoison=true 失败，非超时/零用例/跳过。
- 现有检查错误地把“未连接”等同“旧客户端已完成断开，可以再次 Connect”。修正为：不再在旧对象上重连；发现未连接后先 Reset/释放旧传输，创建新的客户端及接收代次，然后连接。实际 ClientId、共享连接租约、接收器和持久订阅日志保持，不创建第二套运行时或连接管理。
- 不把所有 InvalidOperationException/未知本地异常改成网络错误，不放宽毒消息分类规则。回归要求新发送获得实际 Ack、CountsTowardPoison=false，并确认出站连接计数为 2（首次连接 + 新代次）。

实际命令与证据（Linux x64、SDK 10.0.401）：

1. 缩小窗口前的重复故障探针：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~RepeatedFrozenConnectionsDoNotCountNetworkFailuresAsPoison --logger 'console;verbosity=detailed'`。三组均 1 通过，日志 `/tmp/tenonedge-mqtt-poison-race.log`、`/tmp/tenonedge-mqtt-poison-ping-race.log`、`/tmp/tenonedge-mqtt-poison-cleanup-race.log`；最终用例保留第三组窗口。探针期间带临时诊断的全量通过，日志 `/tmp/tenonedge-mqtt-poison-full-1.log`，不据此宣称竞态消失。
2. 修复前：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MqttDisconnectRaceTests --logger 'console;verbosity=detailed'`：两次均 **1 失败**，日志 `/tmp/tenonedge-mqtt-disconnect-red.log` 与 `/tmp/tenonedge-mqtt-disconnect-red-repeat.log`；用例本身约 0.14 秒即可触发错误分类。
3. 修复后：同一 filter，加 `--logger 'trx;LogFilePrefix=mqtt-disconnect-fixed' --results-directory /tmp/tenonedge-mqtt-disconnect-fixed`：**1 通过，0 失败/跳过**；日志 `/tmp/tenonedge-mqtt-disconnect-fixed.log`。随后 `--no-build` 连续运行该 filter 5 次，均 1 通过，日志 `/tmp/tenonedge-mqtt-disconnect-repeat-1.log` 至 `-5.log`。
4. 移除所有临时诊断后的 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-disconnect-full' --results-directory /tmp/tenonedge-mqtt-disconnect-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**739 单元 + 658 集成 = 1397 通过，0 失败/跳过**；日志 `/tmp/tenonedge-mqtt-disconnect-full.log`，TRX 计数已独立核对，包含原断网队列恢复用例和 §2.60 的真实进程强杀用例。
5. 对本轮三个 C# 文件执行 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include src/TenonEdge.Adapters/Mqtt/MqttSession.cs tests/IntegrationTests/Runtime/MqttOutputTests.cs tests/IntegrationTests/Runtime/MqttDisconnectRaceTests.cs`：退出 0；日志 `/tmp/tenonedge-mqtt-disconnect-format-final.log`；`git diff --check` 通过。临时 `[DEBUG-mqtt-poison]` / sender 输出已移除，`OutputSenderUnit.cs` 无遗留改动。

对历史失败的归因边界：§2.59 的那次失败只有最终计数、没有原始异常堆栈，不能追溯证明它唯一由本竞态引起。本轮确认并修复的是一个能确定性产生相同误计的真实重连缺陷，并让原场景与完整回归通过；其他断开/取消边界仍须持续观察。T2-03、接收队列内存边界、旧 ClientId 清理、完整 AT-25 和可视化访问目标不因此标为完成。


### 2.62 T2-03 / V-04 子项：MQTT 单帧正文分配前的长度保护

开始处理接收内存边界时，回读 MQTTnet 5.2 的公开通道接口及实际接收路径：库先读固定头，再按 Remaining Length 分配正文；原有业务回调的 1 MiB 校验晚于这次分配。只限制后续排队条数不能防止单个超大声明先触发分配，因此本轮先补这层前置保护，**不把它当作接收队列总量已经有界**。

- 新增自有 `MqttFrameLimitChannel`，放在库的 TCP/TLS 明文通道与库帧解码器之间，只跟踪固定头、Remaining Length 和正文边界。初始两字节可合并读，其余长度字节逐个检查；超限时不把完整头交给库，不读取正文，直接终止本连接代次。正文内容不被当作下一帧头，零正文帧与分片/粘连均可处理；超限后锁定失败状态，不能继续读并重复计数。
- 新增 `MqttBoundedAdapterFactory`，通过 MQTTnet 的公开 `IMqttClientAdapterFactory` / `IMqttChannel` 扩展点装配，继续使用库的 `MqttTcpChannel`、TLS、编码器、通道适配器及包检查器。沿用出站 StreamProvider、原目标主机名与 TLS 信任设置；没有复制第三方协议栈、没有修改依赖版本或增加包。实际编译和测试仍是锁定的 MQTTnet **5.2.0.1603**。
- 保护用于产品 MQTT 会话的所有新客户端，包括混合输入/输出连接。首版只支持已声明的 MQTT 3.1.1 TCP/TLS；不趁机开放 WebSocket 或 MQTT 5。
- Remaining Length 上限为 **1,114,115 字节**：完整 1 MiB 载荷 + 65,535 字节 Topic + Topic 长度字段 2 字节 + QoS 1 PacketId 2 字节。固定头另最多 5 字节。业务层的 **1 MiB 载荷上限仍独立执行**，没有把业务载荷上限放宽到该帧上限。
- 新增有限诊断 `connection/<id>/mqtt/wireOversized`，经既有计数写者持久化，已测运行时重建后保留。业务回调中的 `oversized` 仍表示已解码出消息后按载荷上限拒绝；`wireOversized` 表示帧在正文分配前就被拒绝，**不 ACK、不读取整个超大正文**。Broker 侧消息大小限制仍是部署前提，否则超大 QoS 1 消息可能反复重投，不能声称已替代 Broker 限制。

新增验证：

- 13 个单元用例：单字节与合并读取、正文内类似帧头的字节、零长度控制帧、1–4 字节长度编码、超过四字节编码、精确上限、超限后的读取锁定/只计一次、取消与写入/传输元数据转发。
- 直接调用真实 MQTTnet 解码器：超限头触发长度拒绝且底层没有一次正文读取请求；另构造合法的最长 Topic + 完整 1 MiB QoS 1 载荷，确认解码器仍取得正确 Topic 长度、PacketId 和载荷长度，避免只测“拒绝”而误伤上限内合法消息。
- 5 个集成用例：脚本化 TCP/TLS 对端（不是实物或 Broker 容器）只发 CONNACK 和超限头，**完全不发所声明的正文**。TCP/TLS、头分片/完整发送四种组合均须主动断开、计一次超限且无应用消息回调；TLS 使用测试 CA 的显式信任链，没有关闭证书校验。另用实际 `MqttSession` 完成 CONNECT/SUBSCRIBE 后发送超限头，验证产品确实接入保护并报告 `wireOversized`，没有把保护留在独立探针里。

实际命令与结果（Linux x64、SDK 10.0.401）：

1. `dotnet build TenonEdge.sln -c Release --no-restore`：退出 0，0 警告/错误；日志 `/tmp/tenonedge-mqtt-frame-build.log`。
2. 初始单元构建被 CA1859 拒绝（未执行），已把私有读取辅助参数改为具体类型。随后一个测试对第三方异常包装的假设失败：MQTTnet 同步完成路径通过 Task.Result 产生 AggregateException，并包装为 MqttCommunicationException；异步路径可直接传播。回读实际源码后改为断言根异常必须为长度拒绝，同时保留“零正文读取”和计数断言，没有放宽为任意异常即通过。失败日志 `/tmp/tenonedge-mqtt-frame-unit-final.log`、`/tmp/tenonedge-mqtt-frame-unit-boundary.log`。
3. `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~MqttFrameLimitTests`：**13 通过，0 失败/跳过**；日志 `/tmp/tenonedge-mqtt-frame-unit-passed.log`。
4. `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MqttFrameLimitTests`：**5 通过，0 失败/跳过**；日志 `/tmp/tenonedge-mqtt-frame-integration.log`。
5. `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-frame-full' --results-directory /tmp/tenonedge-mqtt-frame-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**752 单元 + 663 集成 = 1415 通过，0 失败/跳过**；日志 `/tmp/tenonedge-mqtt-frame-full.log`，TRX 计数已独立核对；包括此前真实 Broker、重连竞态和独立进程强杀恢复回归。
6. `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include src/TenonEdge.Adapters/Mqtt/MqttFrameLimitChannel.cs src/TenonEdge.Adapters/Mqtt/MqttBoundedAdapterFactory.cs src/TenonEdge.Adapters/Mqtt/MqttSession.cs src/TenonEdge.Adapters/Mqtt/MqttInputReceiver.cs src/TenonEdge.Runtime/Deliveries/DeliveryCache.cs tests/UnitTests/Adapters/MqttFrameLimitTests.cs tests/IntegrationTests/Runtime/MqttFrameLimitTests.cs tests/IntegrationTests/Runtime/RuntimeCounterTests.cs`：退出 0；日志 `/tmp/tenonedge-mqtt-frame-format-final.log`；`git diff --check` 通过。

尚未解决：多个合法帧仍可能积存在 MQTTnet 内部接收队列。本轮只限制每帧分配前的声明长度，不证明跨连接/跨代次总字节额度、队列条数或进程 RSS 达标。下一步仍须把库接收队列纳入全局在途预算，验证 QoS 0 丢弃、QoS 1 不确认断连以及连接代次切换的额度释放；不得以本节替代该验收。旧 ClientId 清理、完整 T2-03 / AT-25、T2 及可视化访问目标仍未完成。

### 本地提交整理（2026-10-09，§2.59—§2.62）

维护者再次授权“整理提交一下未提交的，然后继续”。本次将持久订阅日志与独立进程恢复验证、断开/重连竞态修复、单帧分配前限长及其测试整理为一个本地检查点；各项共用 MQTT 会话装配，保留整体回归过的状态，不人为拆出未验证的中间态。不推送，不宣称 T2 或可视化访问已完成。

提交前在 Linux x64、SDK 10.0.401 上重新执行：

```sh
dotnet test TenonEdge.sln -c Release --no-restore \
  --logger 'trx;LogFilePrefix=mqtt-checkpoint' \
  --results-directory /tmp/tenonedge-mqtt-checkpoint \
  --blame-hang-timeout 5m --blame-hang-dump-type none
```

结果：**752 单元 + 663 集成 = 1415 通过，0 失败/跳过**。退出码 0，两个 TRX 的 Counters 已独立核对；日志 `/tmp/tenonedge-mqtt-checkpoint.log`。`git diff --check` 通过。继续点仍是 T2-03 接收队列/总原始内存有界，不把单帧限长当成全局预算验收。

### 2.63 T2-03 接收预算前置：固定头准入与 QoS 0 有界排空

§2.62 只解决单帧限长，不能直接在其原先的逐段透传头上实现“预算不足时跳过 QoS 0”：库可能已经拿到半个头，跳过正文会破坏后续帧边界。本轮先补预算策略所需的传输机制，尚未接入全局额度或宣布 MQTTnet 内部队列有界。

- `MqttFrameLimitChannel` 先用固定五字节缓冲读取完整固定头，限长与可选准入检查均在把任何头字节交给库之前完成；库仍负责 MQTT 正文解码，不复制第三方协议栈。
- 准入返回拒绝时，只允许 QoS 0 PUBLISH（含 retained），用最多 4096 字节的复用缓冲排空正文，再继续下一帧。QoS 1、控制帧不能被静默丢弃；策略须选择接受或抛错断连。拒绝/异常之前没有正文分配，异常后锁定本连接代次，避免把残留字节重新当头。
- 工厂支持传入准入策略，但产品会话目前仍不传预算策略，现有单帧限长保持生效。**不是产品 QoS 0 全局背压已完成**。下一步须把进程共享预算、入队至回调完成的所有权及断连清理接上；不能仅在解码之后计数，也不能在取消回调仍持有正文时提前释放额度。
- 新增八个单元场景：逐字节/合并输入的丢弃与随后真实库 PINGRESP 解码、QoS 1/控制帧禁止静默丢弃、准入异常不暴露头且不读正文、头/丢弃正文截断、单字节消费者准确取得头且每帧只准入一次。
- 新增脚本化 TCP 对端集成测试：真实 MQTTnet 客户端发 PINGREQ，对端先发一个被策略拒绝的 16 KiB QoS 0 PUBLISH 再发 PINGRESP；验证 Ping 完成、连接仍存活、无应用回调。它验证准入通道，不是 Broker/设备实测，也不是内存压力或全局预算验收。

验证环境：Linux x64、SDK 10.0.401、锁定 MQTTnet 5.2.0.1603，无依赖变更。

1. 首轮全量：760 单元通过；集成 662 通过、1 失败、0 跳过。失败为既有 `FluentModbusAcceptsATcpResponseFromAnotherUnit` 在同步网络读取时超时（约 403 ms），不是 MQTT 断言；保留 `/tmp/tenonedge-mqtt-admission-full.log` 及 `/tmp/tenonedge-mqtt-admission/*.trx`。没有据此宣称全量通过，也没有改 Modbus 用例、超时或断言。
2. 补齐 TCP 用例后执行 `dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~MqttFrameLimitTests|FullyQualifiedName~FluentModbusAcceptsATcpResponseFromAnotherUnit'`：7 通过、0 失败/跳过；日志 `/tmp/tenonedge-mqtt-admission-focused.log`。单次重跑通过不证明此前超时的根因已定位或修复。

3. 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-admission-final' --results-directory /tmp/tenonedge-mqtt-admission-final --blame-hang-timeout 5m --blame-hang-dump-type none`：**760 单元 + 664 集成 = 1424 通过，0 失败/跳过**，退出 0；日志 `/tmp/tenonedge-mqtt-admission-final.log`，两个 TRX 的 Counters 独立核对。此前 Modbus 超时仍保留为一次未定位的失败，不用本次通过抹去。
4. 格式检查发现新增集成用例一处换行问题，已修正；`git diff --check` 通过。

### 2.64 T2-03 接收预算基础：共享总额、独立分区与幂等租约

接入原始帧预算前复核 `BudgetedInputChannel`：其两阶段保守结果上界可能预约整个可用池。原始报文已经占据同一池时，若仍请求原来的总容量，会形成“原始报文等待自己的解析额度”的停滞。因此本轮补预算层的分区能力，尚未启用产品 MQTT 的接收分区。

- 新增 Abstractions 契约 `IInputBufferBudget`：非等待预约，额度不足返回 null；租约覆盖读取、排队、回调持有期间，取消或断连不能冒充所有权结束。接口不持有请求/数据库对象，不新增运行时实例。
- `InFlightBudget` 可在同一总容量内划出 `bufferCapacity`，余下为 `WorkingCapacity`；两者不互借。接收租约与普通预约共用锁、总量状态和峰值，合计不超过配置总额。原始额度以线程安全、幂等的租约释放，不会因旧代次重复释放而误释放新代次的额度。
- `BudgetedInputChannel` 的解析、输出上界截断、实际结果检查及直接写入均使用工作分区容量。超过工作容量的请求直接拒绝，不等待一个永远无法得到的容量；实际输出也不能借用原始分区。
- **启用边界**：现有构造默认 `bufferCapacity=0`，Host 尚未分配该分区、MQTT 尚未调用接口。测试中的 64 MiB 总额/8 MiB 原始分区只是验证参数，不是已定稿的产品默认值；不能把本轮当作接收队列已经受全局预算约束。
- 7 个新增测试覆盖：原始分区满时，真实输入 writer 仍能完成一次 48 MiB 解析预约（输出保守上界被截断到工作容量）；等待取消不释放原始额度；工作额度释放不能误释放原始额度；旧/新代次租约并存；并发重复释放；1000 次并发申请不超额；超限预约/实际结果及非法参数拒绝。保留既有预算测试。

下一步的所有权风险（仅源码核查，不当作运行验证）：MQTTnet 的 `AsyncQueue<MqttPublishPacket>.Dispose()` 不会清空非 IDisposable 元素；`MqttClient.Dispose()` 同步 Cleanup 也不等于接收/回调任务已经完成。`DisconnectAsync` 在断开已开始时可直接返回。因此不能在调用 Dispose/DisconnectAsync 后立即归还全部队列额度；必须证明旧代次排队与正在执行回调的原始数据引用都已结束，或改用明确拥有这些引用的接收路径。不得仅凭 IsConnected=false 判断可释放。

验证环境：Linux x64、SDK 10.0.401，无包版本变更。

1. 初始聚焦 `dotnet test tests/UnitTests -c Release --no-restore --filter 'FullyQualifiedName~InputBufferBudgetTests|FullyQualifiedName~InFlightBudgetTests'`：26 通过、0 失败/跳过（随后补了实际输出不能借用原始分区的第 7 项）；日志 `/tmp/tenonedge-buffer-budget-unit.log`。
2. 对本轮 C# 文件执行 `dotnet format TenonEdge.sln --no-restore --include ...`，退出 0；日志 `/tmp/tenonedge-buffer-budget-format.log`。

3. `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=buffer-budget' --results-directory /tmp/tenonedge-buffer-budget --blame-hang-timeout 5m --blame-hang-dump-type none`：**767 单元 + 664 集成 = 1431 通过，0 失败/跳过**，退出 0；日志 `/tmp/tenonedge-buffer-budget-full.log`，两个 TRX Counters 独立核对。`git diff --check` 通过。

后续接入候选：已核实公开 `IMqttChannelAdapter.ReceivePacketAsync` 可装饰，`MqttApplicationMessageReceivedEventArgs` 构造器及库的应用消息/PUBACK 工厂公开。可以在库解码后、其内部无界队列之前接管 PUBLISH，使用产品自己拥有的有界回调队列，其他控制帧继续交给原客户端；固定头准入仍在解码分配之前。这样有机会明确拥有解码中、排队、回调中的引用，避免依赖库 Dispose 的推测。**尚未实现/运行验证**，下一轮先验证这一接收路径及原有确认、保活与生命周期语义，不宣称已选定完成方案。

### 2.65 T2-03：产品 MQTT 有界接收队列与分配前共享预算

上一轮为分区和准入机制，本轮将它们接入产品路径，替代 **MQTT 输入连接**中库的无界 PUBLISH 排队路径；没有改包版本、复制第三方协议栈或创建第二套 Runtime。

- 新增 `MqttReceiveBuffer`：通过公开 `IMqttChannelAdapter` 装饰器在库解码后接管 PUBLISH，不把它交给高层客户端的内部队列。TCP/TLS、固定头/正文编解码、其他控制包和发送串行锁仍由 MQTTnet 提供；应用消息及 PUBACK 仍使用库的公开工厂。单连接代次只有一个自有消费者，按接收顺序手动 ACK。
- 固定头读全后、任何正文分配之前预约 `3 × Remaining Length + 4096` 字节，覆盖正文数组、最坏 Topic UTF-16 表示以及队列/包/回调的保守固定开销；每个输入连接最多 100 项，**含解码中、排队和正在处理的项**，不是只计 Channel.Count。
- Host 在既有 64 MiB 在途总额内给原始 PUBLISH 分配 8 MiB，解析/分发使用余下 56 MiB。全部产品输入上下文传递同一个 RuntimeHost.InFlight，不按连接另建额度。8 MiB 是当前内部实现的待调优参数，不是设备容量或 RSS 承诺。独立装配的 RuntimeOptions 默认原始分区为 0，便于既有无 MQTT 的测试/使用者；没有提供预算的独立 MqttSession 仍走原路径，不能据此宣称第三方独立装配也已受保护。
- QoS 0 满时不等待，用 §2.63 的小缓冲排空该帧，报告持久化 `bufferDropped`；紧随的 PINGRESP 可继续处理。QoS 1 使用最多 5 秒的可取消等待（共享额度由释放信号唤醒，不轮询），超时报告 `bufferFull`、不 ACK 并断连。最初实现直接断连，经复核发现积压突发可能反复在第一条提交前中止，故改为限时等待，并补大于窗口的突发顺序接管用例。
- QoS 1 等待发生在 TCP 头部，位于它后面的控制帧也会等待，最多到该期限或已有操作的更短取消期限；不声称压力下控制帧完全没有队头阻塞。排空 QoS 0 不阻塞后续控制帧的证据不能外推为 QoS 1 同样无阻塞。
- 每项租约从读取前一直保留到实际回调结束。断连时停止新预约/入队、关闭通道，解码中的预约由接收 finally 释放，队列由自有消费者清理；正在使用的原始数据不会因 Dispose/IsConnected=false 被提前归还。旧代次的租约继续占共享额度直到结束，不会误释放新代次额度。
- `MqttInputReceiver` 原先用 WaitAsync 实现截止时间，绑定忽略取消时会留下仍持有报文的任务。现在仍在截止时立即中止连接、不确认，但回调最终完成要等实际绑定结束，才能释放原始额度；迟到成功不发送 ACK。静默等待仍受调用方独立停止期限限制，不把无限等待移到宿主停止线程。
- 新增有限诊断 `bufferDropped`、`bufferFull`、`bufferFailed`，通过既有计数写者持久化，含重建后的读取断言。

验证：

- 7 个脚本化真实 TCP / MQTTnet 集成场景：条数/字节压力分别覆盖 QoS 0 与 QoS 1；慢回调期间 QoS 0 丢弃后 Ping 成功；QoS 1 仅发第二帧头、不发正文，超时断连；断连后仍执行的回调保留额度且没有进入库应用回调；手动确认延后且 PacketId 正确；20 条 QoS 1 大于单项窗口仍依次确认、无重连、峰值不超字节分区；正文读取中断后归还解码预约。
- 真实 Mosquitto 的既有产品 Host 接收/转发、持久订阅重启测试通过，增加 Host 原始分区 8 MiB / 总额 64 MiB 的装配断言；既有独立进程强杀恢复测试继续纳入全量回归。
- 原来的 5 秒接管超时/迟到完成测试增加启用真实预算的分支，检查断连后额度仍保留，实际绑定结束后额度为零且无迟到 ACK。
- 预算单元新增共享分区等待取消与另一连接释放后唤醒测试；取消不盗取既有租约。

实际命令与环境：Linux x64、SDK 10.0.401、MQTTnet 5.2.0.1603。

1. 初次构建因 CA1001 指出接收所有者未实现 IDisposable 而失败（未执行测试），已补生命周期。添加异步预算接口时一次修改未匹配已有格式，构建报告 CS0535，已补实现；未把这两次失败记为验证通过。
2. 初始自有队列聚焦 3 项通过（`/tmp/tenonedge-receive-buffer-tests.log`）；接入产品及限时等待后 `dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~MqttReceiveBufferTests|FullyQualifiedName~MqttHostInputTests|FullyQualifiedName~MqttInputSessionTests'`：26 通过、0 失败/跳过（当时尚未补最后两个队列场景和预算化迟到分支）；日志 `/tmp/tenonedge-buffer-product-wait2.log`。
3. 补突发与半帧断连后的全量：768 单元 + 671 集成 = 1439 通过，0 失败/跳过；`/tmp/tenonedge-buffer-product-full.log`，TRX `/tmp/tenonedge-buffer-product/`。

4. 补预算化迟到分支后，最终 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=buffer-product-final' --results-directory /tmp/tenonedge-buffer-product-final --blame-hang-timeout 5m --blame-hang-dump-type none`：**768 单元 + 672 集成 = 1440 通过，0 失败/跳过**，退出 0；日志 `/tmp/tenonedge-buffer-product-final.log`，两个 TRX Counters 已独立核对。

5. 对全部改动及新增 C# 文件执行 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...`，退出 0；日志 `/tmp/tenonedge-buffer-product-format-final.log`。`git diff --check` 通过。

边界：这些是字节预约/队列所有权及软件协议测试，不是全机 RSS 上限或长期压力验收。固定连接对象、TLS/Socket 缓冲、控制帧及排空用的小缓冲还需与运行计划规模一起做资源实测；输出专用连接收到非预期 PUBLISH 的保护、共享预算下大量连接公平性、TLS 压力、旧代次长时间不响应取消等仍需补场景。没有将 T2-08、V-04 完整范围或 T3 网页标为完成。

本地检查点：沿用维护者“整理提交后继续”的本地提交授权，将 §2.63—§2.65 的准入、预算分区、自有队列、产品装配和测试作为同一已回归状态提交；不推送，不将本检查点当作 T2 或可视化访问完成。

### 2.66 T2-03 / T2-08 子项：输出专用连接拒绝非预期 PUBLISH、TLS 接收压力

- 补 §2.65 的输出专用连接边界：它使用 CleanSession 且不订阅，现在从完整固定头识别非预期 PUBLISH，并在正文读取/分配前关闭连接。不让这些帧绕过输入预算进入库的无界队列；QoS 0/1 都拒绝。输入/输出共享连接仍使用已配置输入的有界接收器，没有误禁正常订阅。
- 新增两个真实 TCP 对端用例：实际 `MqttSession` 建连成功后，只发 QoS 0 或 QoS 1 PUBLISH 头，完全不发正文；必须在 3 秒内关闭、没有 ACK，随后同一产品会话可重新 CONNECT 成功且不计毒消息。保活间隔 15 秒，因此不能靠保活超时冒充头部拒绝。
- 先运行新用例：两个均因 3 秒读取等待取消而失败，日志 `/tmp/tenonedge-output-receive-red.log`。实现固定头拒绝后通过，没有放宽等待时间或改成任意异常即通过。
- 将 §2.65 的四个慢回调/条数与字节压力场景扩展为 TCP/TLS 两组，共八项。TLS 使用测试 CA 的显式信任链与正确服务器名/SAN，没有关闭验证；同样检查 QoS 0 丢弃后 Ping 成功、QoS 1 只发头时满预算超时断连、正在执行的回调保留额度、实际结束后释放及没有库应用回调。这是脚本化 TLS 对端软件测试，不是实物或长期 RSS 压测。

环境：Linux x64、SDK 10.0.401、锁定 MQTTnet 5.2.0.1603。

1. `dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~OutputOnlySessionRejectsUnsolicited|FullyQualifiedName~MqttReceiveBufferTests'`：**13 通过、0 失败/跳过**；日志 `/tmp/tenonedge-receive-tls-focused.log`。
2. `dotnet format TenonEdge.sln --no-restore --include src/TenonEdge.Adapters/Mqtt/MqttSession.cs tests/IntegrationTests/Runtime/MqttFrameLimitTests.cs tests/IntegrationTests/Runtime/MqttReceiveBufferTests.cs`：退出 0；日志 `/tmp/tenonedge-receive-tls-format.log`。

3. `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=receive-tls' --results-directory /tmp/tenonedge-receive-tls --blame-hang-timeout 5m --blame-hang-dump-type none`：**768 单元 + 678 集成 = 1446 通过，0 失败/跳过**，退出 0；日志 `/tmp/tenonedge-receive-tls-full.log`，两个 TRX Counters 独立核对。`git diff --check` 通过。

上述新增证据不代表 MQTT 全部连接生命周期、T2-08 长期资源验收或 T3 已完成。Modbus 选型及 TCP UnitId 冲突仍以维护者明确答复为准，未把建议当成批准；已异步提交建议，继续不受影响的任务，不因等待答复停止整个开发。

### 2.67 T2-07 已解码 LoRa 样例与真实 MQTT 软件链路

按总设计 §21.5—§21.6 先交付不依赖 Modbus 决策的 MQTT 软件子场景，不将 T2-06 的全部协议依赖视为已完成。

- 新增 `docs/lora-integration.md` 和 `samples/lora/`：两台人工土壤传感器的已解码事件子集、缺少解码对象/非法温度的负样本，以及可由实际后端读取的 `runtime-seed.json`。无真实设备数据、凭证、无线密钥，没有复制 ChirpStack 源码、安装其服务或新增协议依赖。
- 种子使用两条精确主题绑定两个逻辑设备，共享 MQTT 连接，扇入同一通用输出但不 join；明确 RFC 3339 上游时间、三个固定类型字段、湿度重命名、必填规则及输出主题。报文设备名不参与自动绑定，Body DevEUI 与错误主题的一致性检查不在本例中，文档已说明。
- 文档区分网关接收、上游时间和真实采样时间，说明上游 QoS 0 不能靠订阅 QoS 1 升级可靠性；列出解码、JSON、Broker 持久化、账号 ACL 和脱敏前提，不将样本或参考的预发布源码当作型号兼容验证。
- 新增 `LoraMqttSampleTests`，通过测试构建复制直接读取这五个仓库 JSON 文件，不在测试里另造一套业务配置。使用真实 Mosquitto 2.1.2 测试容器、实际产品 Host、首次设置/会话/凭证 API，以及独立 MQTT 客户端。
- LR-AT-01 软件子场景：已解码数值经真实输入、必填映射和发送缓存到独立接收端；核对标准信封的数值、类型、时间来源、时间、设备与主题。
- LR-AT-02 软件子场景：两台逻辑设备交替发布，分别检查 23.7/41.5/92 与 18.2/55.5/81，不串字段、设备或主题。
- LR-AT-03 软件子场景：只有 Base64 data/缺少 object，以及非法温度，均由显式必填规则拒绝本输出路径，不补 0 或旧值。持久计数精确为 2 条路径拒绝、2 次必填失败、3 项缺失、1 项类型转换失败；状态 API 也可读到拒绝计数。6 条输入共确认，只有 4 条合法输出提交并发送删除。用后续合法报文与计数作屏障，不靠“等一会没有报文”判定。

开发中失败与定位：首个完整测试在订阅就绪前超时（`/tmp/tenonedge-lora-sample-focused.log`）。缩小到启动状态和种子解析后，确认是样例将时间来源写成 `Upstream`，而已有协议固定字符串为 `upstream`，种子在冻结前即被拒绝，Host 保持 Idle；不是共享连接或 Broker 问题。修正样例字符串，没有修改生产解析器、放宽协议或延长超时。保留启动/解析失败日志 `/tmp/tenonedge-lora-sample-startup.log`、`/tmp/tenonedge-lora-sample-parse.log`；移除了临时数据库诊断读取，保留样例直接解析与 Active 状态断言作为清晰的失败入口。

环境：Linux x64、SDK 10.0.401、MQTTnet 5.2.0.1603、`eclipse-mosquitto:2.1.2-alpine`。

1. `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~LoraMqttSampleTests`：修正后 **1 通过、0 失败/跳过**，覆盖以上三个软件子场景；日志 `/tmp/tenonedge-lora-sample-green.log`。
2. `dotnet format TenonEdge.sln --no-restore --include tests/IntegrationTests/Runtime/LoraMqttSampleTests.cs`：退出 0；日志 `/tmp/tenonedge-lora-format.log`。五份样例 JSON 逐份解析有效，`git diff --check` 通过。

3. `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=lora-sample' --results-directory /tmp/tenonedge-lora-sample --blame-hang-timeout 5m --blame-hang-dump-type none`：**768 单元 + 679 集成 = 1447 通过，0 失败/跳过**，退出 0；日志 `/tmp/tenonedge-lora-full.log`，两个 TRX Counters 独立核对。

未覆盖：实际传感器上报周期与无线补传、真实 LoRaWAN 网络服务、LR-AT-06 实物链路、任何厂商型号兼容、Broker 断电持久化完整矩阵以及 T2-07/08 整体退出；不新增无线协议栈、设备自动发现、下行控制或网页。

### 2.68 T2-02 寄存器纯解码（2026-10-09）

- 新增独立于协议库的内部解码器：寄存器取位、16/32/64 位有符号/无符号整数、float32/float64；32/64 位覆盖四种字节序，64 位整数不经过 double，非有限值返回 `nonFinite`，缺词、多词及无效配置拒绝解码。
- AT-02 / V-05 子场景：58 项软件单元用例覆盖手写已知线序、超过 2^53 的整数、负数、位号 0–15、四种字节序下 NaN/正负无穷及 JSON 精度。没有网络请求，不代表正式协议适配、迟到帧、调度或实物验收通过。
- 环境：Linux x64、.NET SDK 10.0.401；沿用真实 Mosquitto 测试环境。
- 命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-codec' --results-directory /tmp/tenonedge-modbus-codec --blame-hang-timeout 5m --blame-hang-dump-type none`。
- 结果：826 单元 + 679 集成 = **1505 通过，0 失败、0 跳过**；日志 `/tmp/tenonedge-modbus-codec-full.log`，TRX 位于上述结果目录。初次局部格式校验发现测试 writer 的换行问题，已修正；未变更算法。
- 不修改 UnitId 范围、依赖选型或注册；TCP 0–255 / RTU over TCP 1–247 的建议仍待维护者确认，T2-02 不定稿。

### 2.69 T2-02 地址规范化子项（2026-10-09）

- 将协议偏移、显式功能码下的从 1 开始寄存器号、含前缀的 5/6 位手册编号分成三个内部入口，不猜测 `40001` 的输入模式。前缀 0/1/3/4 对应功能码 01/02/04/03，保留线圈编号的前导零。
- 校验零基偏移与整个点位宽度，包含最后合法地址、多寄存器跨界及整数溢出输入；拒绝非法前缀、零编号、非 ASCII 数字、符号和多余空格。
- 24 项新增地址用例；`dotnet test tests/UnitTests -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-address' --results-directory /tmp/tenonedge-modbus-address`：**850 单元通过，0 失败、0 跳过**。日志 `/tmp/tenonedge-modbus-address.log`，TRX Counters 已核对。该子项后未重跑集成测试，最近集成基线见 §2.68。
- `dotnet format TenonEdge.sln --verify-no-changes --no-restore --include src/TenonEdge.Adapters/Modbus/ModbusRegisterDecoder.cs tests/UnitTests/Adapters/ModbusRegisterDecoderTests.cs src/TenonEdge.Adapters/Modbus/ModbusAddress.cs tests/UnitTests/Adapters/ModbusAddressTests.cs` 与 `git diff --check` 通过。
- 环境同 §2.68。本项仅内部地址纯函数，尚未接入草稿 API/点表/读取块；不代表 AT-02 或 T2-02 完整通过。接下来继续连续读取块及调度，UnitId 与库选型仍不擅自定稿。

### 2.70 T2-02 连续读取块规划（2026-10-09）

- 新增单个 UnitId / 采集组的内部规划器，调用者只传启用点；按功能码和地址排序，仅合并相邻或重叠范围，不跨未配置地址。01/02 上限 2000 位、03/04 上限 125 寄存器，可设更小设备上限；上限以下仍不拆分一个多寄存器点，必要时两块重叠读取。
- 输出不可变读取块和原始点位序号/块内偏移；同地址取多个位不会丢点，排序不改变原点表，最高地址 65535 的末端用 int 计算，防止溢出。
- AT-02 / V-05 软件子场景：11 项用例覆盖四个功能码边界、地址空隙、重叠、设备上限、末地址、非法配置；200 组固定种子点表逐地址核对“无空隙、每点恰好归属一块、不拆点”；人工寄存器响应经实际解码器切片验证原字段身份和整数精度。没有网络从站或调度，不代表正式协议/实物验收通过。
- 首轮全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-planner' --results-directory /tmp/tenonedge-modbus-planner --blame-hang-timeout 5m --blame-hang-dump-type none`。861 单元通过；678 集成通过、1 失败（`NModbusLeavesAFollowingTcpFrameForTheNextRequest` 首次读取 300ms 超时），0 跳过。失败日志 `/tmp/tenonedge-modbus-planner-full.log` 与 TRX 保留，**该轮不算通过**。
- 对失败用例以 `dotnet test tests/IntegrationTests -c Release --no-build --no-restore --filter FullyQualifiedName~NModbusLeavesAFollowingTcpFrameForTheNextRequest` 连跑 10 次，10/10 通过（`/tmp/tenonedge-modbus-stale-repeat-1.log` 至 `-10.log`）；未稳定复现，根因尚未确认，不改库行为、不放宽超时、不宣称已修复。
- 初次局部格式校验发现 else 换行和 using 排序，已修正。局部 `dotnet format TenonEdge.sln --verify-no-changes --no-restore --include src/TenonEdge.Adapters/Modbus/ModbusReadPlanner.cs tests/UnitTests/Adapters/ModbusReadPlannerTests.cs` 与 `git diff --check` 通过。
- 格式检查结束后顺序重跑上述全量命令（结果前缀 `modbus-planner-repeat`，目录 `/tmp/tenonedge-modbus-planner-repeat`）：**861 单元 + 679 集成 = 1540 通过，0 失败、0 跳过**，TRX Counters 已核对。日志 `/tmp/tenonedge-modbus-planner-repeat.log`；这次通过不抹去前次偶发超时。环境沿用 §2.68。

### 2.71 T2-02 专用请求线程与取消所有权（2026-10-09）

- 新增内部连接级请求执行器：每实例一个专用线程执行阻塞式协议 I/O，不用线程池包装同步读取；候选队列容量为 1。输入准入仍由既有共享连接租约串行器负责，不建立第二套连接管理。
- 当前请求注册关闭连接的取消动作，排队请求取消不会关闭当前连接。取消后等待真实读取及取消回调退出，才完成请求/启动下一项；迟到的正常返回也不能作为成功结果。释放执行器关闭准入、取消当前项及排队项，实际读取不退出就保持释放未完成，避免用停止成功掩盖后台残留。
- 调用契约：read 与 abort 捕获同一代连接；read 不得自行换连接；abort 只关闭这个句柄且不得阻塞。此执行器尚未由正式 Modbus 会话注册，不宣称完整 AT-13 单实例生命周期通过。
- 6 项单元场景：固定非线程池线程、读取异常后的继续执行、排队取消隔离、主动取消后的真实退出屏障、停止排空/重复释放/停止后拒绝、满队列等待取消、忽略取消读取保留停止未完成（部分用例包含多个断言场景）。2 项回环 TCP 集成场景：对端不发响应时，通过请求取消或执行器释放关闭本次 socket，读取在 3 秒验证期限内退出；普通读超时设 10 秒，不能靠其超时冒充取消成功。
- 初次构建因测试等待没有传 TestContext 取消令牌而被 xUnit1051 拒绝；已补齐，没有禁用分析器。随后聚焦执行当时的 5 单元 + 2 集成全部通过，日志 `/tmp/tenonedge-modbus-executor-focused2.log`；停止不合作读的第 6 项单元随后加入全量验证。
- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-executor' --results-directory /tmp/tenonedge-modbus-executor --blame-hang-timeout 5m --blame-hang-dump-type none`：**867 单元 + 681 集成 = 1548 通过，0 失败、0 跳过**；TRX Counters 已核对，日志 `/tmp/tenonedge-modbus-executor-full.log`。本次文件经限定 `--include` 的 `dotnet format` 格式化，`git diff --check` 通过。
- 本项不包含 UnitId 范围、NModbus 正式依赖、帧匹配/静默排空、从站快速失败/退避与公平调度；这些仍须继续实现。环境沿用 §2.68。

### 2.72 T2-02 RTU 超时静默窗口子项（2026-10-09）

- 新增独立的 RTU 接收排空函数，固定 256 字节缓冲，使用注入 TimeProvider 的单次静默窗口；丢弃窗口内全部收到的字节，不因持续输入无限续期，不启动第二个并发读取。
- 为同时满足 §6.1 的静默要求及其他从站额外等待上界，采用保守复用规则：完整窗口未见字节才允许继续使用该连接；窗口中见到字节则返回“需立即更换连接”，不额外延长一个窗口。EOF 同样不得复用。实际重连由会话执行，本函数不设置连接退避、不改变其他从站状态；当前尚未接入正式会话。
- 等待底层 ReadAsync 实际结束，不能用 WhenAny 超时返回后留下一个会偷走下一应答的旧读取。外部取消必须向上传播，不能作为“已静默”吞掉；不合作 Stream 会使函数保持未完成，不能谎报满足停止期限。
- AT-02 / V-05 子场景：4 项单元覆盖连续输入的 500ms 固定窗口/缓冲、EOF/非法参数/取消、取消不合作读取的所有权、进行中的外部取消；2 项真实回环 TCP 测试覆盖完整旧 RTU 帧丢弃、静默后的连接复用及下一响应不被旧读取窃取。仍不是 NModbus 正式会话、全迟到帧矩阵或实物验证。
- 首轮聚焦测试 1 单元失败：误把 FakeTimeProvider 时间戳 0 当起点；改为读取测试开始时的时间戳，不改产品计时。日志 `/tmp/tenonedge-modbus-drain-focused.log` 保留。新增取消所有权测试后，`dotnet test TenonEdge.sln -c Release --no-restore --filter FullyQualifiedName~ModbusRtuDrainTests`：4 单元 + 2 集成通过（`/tmp/tenonedge-modbus-drain-focused2.log`）。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-drain' --results-directory /tmp/tenonedge-modbus-drain --blame-hang-timeout 5m --blame-hang-dump-type none`：**871 单元 + 683 集成 = 1554 通过，0 失败、0 跳过**；日志 `/tmp/tenonedge-modbus-drain-full.log`，TRX Counters 已核对。限定这三个 C# 文件的 `dotnet format --no-restore --include …` 完成，`git diff --check` 通过。
- 环境同 §2.68。UnitId 和协议库采用仍待维护者确认，不擅自定稿；后续继续会话接入与 UnitId 调度。

### 2.73 T2-02 专用线程的请求上下文隔离（2026-10-09）

- 复核 §2.71 时发现 `Thread.Start()` 会继承创建者 ExecutionContext；如果未来在配置应用或预览请求中创建会话，专用线程可能长期保留请求 AsyncLocal 等上下文，违反后端生命周期约束。
- 先增加测试：创建者设置 AsyncLocal 对象，专用线程读取必须为 null，创建者仍须保有原值。旧实现确定性失败（`Assert.Null` 得到 Object），日志 `/tmp/tenonedge-modbus-context-red.log`。然后改用当前 net10.0 引用程序集确实提供的 `Thread.UnsafeStart()`，不影响调用者上下文，不自行模拟线程池。
- `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~ModbusRequestExecutorTests`：7 项通过（`/tmp/tenonedge-modbus-context-green.log`）。测试中的 TestContext 取消令牌改为启动专用线程前显式捕获，不依赖被隔离掉的测试 AsyncLocal。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-context' --results-directory /tmp/tenonedge-modbus-context --blame-hang-timeout 5m --blame-hang-dump-type none`：**872 单元 + 683 集成 = 1555 通过，0 失败、0 跳过**；TRX Counters 已核对，日志 `/tmp/tenonedge-modbus-context-full.log`。两个改动 C# 文件限定 `--include` 的 `dotnet format --verify-no-changes --no-restore`、`git diff --check` 通过。
- 此为连接专用线程的工程约束验证，尚不代表 Host 已接入 Modbus 或完整 BE-AT-03 通过。环境同 §2.68。

### 2.74 T2-02 连接级超时事实与重连判定（2026-10-09）

- 新增连接私有的启用从站最近请求记录：只有所有启用从站的最近请求均为 timeout，或本次为连接级错误，才满足退避重连条件。未知/尚未请求的从站不视为超时；设备异常应答和其他读取错误不等同于超时。
- 同 UnitId 的多个采集组只占一个设备记录；不同物理连接互不共享。换连接清除旧连接的请求事实，重新收集；周期跳过和超时后未执行的块不允许调用 Record，避免伪造超时。
- 8 项单元测试覆盖单从站连续超时、尚未请求设备、成功/异常应答/读取错误清除本设备超时、连接错误不污染其他设备、重连后未知状态、重复组、跨连接隔离、非法输入。聚焦命令 `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~ModbusConnectionHealthTests`：8 通过，日志 `/tmp/tenonedge-modbus-health-focused.log`。
- 全部单元：`dotnet test tests/UnitTests -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-health' --results-directory /tmp/tenonedge-modbus-health`：**880 通过，0 失败、0 跳过**；TRX Counters 已核对，日志 `/tmp/tenonedge-modbus-health-unit.log`。本次不重跑集成，最近 683 项集成通过的基线见 §2.73。限定两个新增文件的 `dotnet format --verify-no-changes --no-restore --include …` 及 `git diff --check` 通过。
- 当前只实现内部调度判定，未接入网络会话，不宣称 AT-02 同连接多从站实测已通过；UnitId 产品范围及依赖采用仍待确认。环境同 §2.68。

### 2.75 T2-03 仅输出重连后恢复输入的持久会话验证（2026-10-09）

- 增加真实 Mosquitto + SQLite 订阅日志场景：原输入订阅并断开，上游 QoS 1 发布旧消息；独立观察客户端以同 ClientId/CleanSession=false 连回，实际收到旧积压但不确认，再断开。此正对照排除“旧积压本来不存在”的假通过。
- 随后由产品 MqttSession 以仅输出方式重新连接并真实发送至独立订阅端；再恢复输入，确认 SessionPresent=false、sessionLost/subscribed 各一次，旧本地订阅日志被重建；发布 fresh 作为有序屏障，只收到 fresh，未用固定 sleep 推断旧消息消失。
- 此为“仅输出会话确实重连”的会话层子场景；不等于删除最后一个输入后无需发送也会主动联网清理，不等于删除整个连接、修改 ClientId 或 Host 配置应用的旧会话清理已完成。那些仍需生命周期实现。
- 聚焦命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~OutputOnlyReconnectClearsFormerInputBacklogAndLaterInputRebuildsJournal`，1 项通过，日志 `/tmp/tenonedge-mqtt-last-input2.log`。初次构建被 CA1861 拒绝，改用 Assert.Single 后通过，没有关闭分析器。
- 变异检查：临时把产品输出连接的 `WithCleanSession(input is null)` 改为 `WithCleanSession(false)`，同一用例确定性失败（预期 Ack，实际 Retry），日志 `/tmp/tenonedge-mqtt-last-input-mutation.log`；finally 恢复产品文件，并用 git diff 确認该文件无变更。测试确实能发现关键会话策略退化，不只是成功连接检查。
- 恢复源码后全量重建：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-last-input' --results-directory /tmp/tenonedge-mqtt-last-input --blame-hang-timeout 5m --blame-hang-dump-type none`：**880 单元 + 684 集成 = 1564 通过，0 失败、0 跳过**；日志 `/tmp/tenonedge-mqtt-last-input-full.log`，TRX Counters 已核对。限定修改测试文件的 `dotnet format --verify-no-changes --no-restore --include …` 及 `git diff --check` 通过。
- 环境沿用 §2.68；本批不修改产品认证、订阅或会话策略，不宣称完整 AT-25 / T2-03 验收。

### 2.76 T2-07 / T2-08 Broker 进程重启与未保存窗口（2026-10-09）

- 扩展真实 Mosquitto 夹具：独占重启测试可启用持久化并选择固定端口，正常停止或 SIGKILL 后启动同一个容器，保留其数据卷和 Broker 身份。启动脚本允许再次启动，不依赖已从宿主临时目录删除的私钥副本。
- 四格矩阵使用正式 MqttSession + MqttInputReceiver + SQLite 订阅日志：持久化开/关 × 正常停止/SIGKILL。在 `autosave_interval 0` 且没有显式检查点的受控条件下，只有“持久化开启 + 正常停止”恢复 SessionPresent 和离线 QoS 1 积压；其余三格会话丢失，产品重订并记录 sessionLost，上游新消息恢复接收。以实际 barrier 消息核对顺序，不靠 sleep 判断消息丢失。
- 这是 **Broker 进程** 终止与恢复的软件证据，不是整机断电、文件系统掉电一致性或生产默认保存间隔承诺。SIGKILL 用例特意暴露尚未保存窗口的丢失，不宣称 QoS 1 等同 Broker 落盘。
- 首轮四格均在夹具身份检查处失败：Docker 的随机端口会在重启后重分配，未到协议验收。修正为重启测试显式映射空闲端口，仍检查重启前后端口相同；若端口被抢占则启动失败，不悄悄改变接收方。日志 `/tmp/tenonedge-broker-restart-focused.log` 保留。
- `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~BrokerRestartRestoresOnlySessionsAndBacklogThatWereCheckpointed`：4 项通过，日志 `/tmp/tenonedge-broker-restart-focused2.log`。
- 夹具清理修正：镜像 Config.Volumes 实际声明 `/mosquitto/data` 与 `/mosquitto/log`，原先 `docker rm -f` 会遗留匿名卷；改为 `docker rm -f -v`，只清除此测试容器关联的匿名卷。旧轮次已遗留且当前无法可靠追溯归属的卷未批量删除，不能继续沿用早期“测试卷均已删除”的笼统说法，也未执行全局 volume prune。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=broker-restart' --results-directory /tmp/tenonedge-broker-restart --blame-hang-timeout 5m --blame-hang-dump-type none`：**880 单元 + 688 集成 = 1568 通过，0 失败、0 跳过**；TRX Counters 已核对，日志 `/tmp/tenonedge-broker-restart-full.log`。全量前后 `docker volume ls -q | sort` 的名单 diff 为空（`/tmp/tenonedge-broker-volumes-before.txt`、`-after.txt`），本轮无新增遗留卷。修改文件限定 `--include` 的 `dotnet format --verify-no-changes --no-restore` 及 `git diff --check` 通过。
- 环境：Linux x64、SDK 10.0.401、Mosquitto 2.1.2-alpine；本批不改产品会话策略与首版部署要求。

### 2.77 T2-07 / T2-08 已保存检查点及订阅回滚风险（2026-10-09）

- 独占 Broker 夹具增加首次检查点：先确认不存在 mosquitto.db，发 SIGUSR1，等待非空最终文件出现，不把发信号成功当作写入完成。参考 mosquitto 笔记及 persist_write.c 的 mosquitto_write_file 路径，只借验证思路，不复制源码。
- §2.76 矩阵增加两格：保存包含离线消息的检查点后，再发布一条获 PUBACK 的新消息；正常停止保存两条，SIGKILL 仅恢复检查点内旧消息。6 格聚焦全部通过，日志 `/tmp/tenonedge-broker-checkpoint-focused.log`。仍仅是进程强杀，不是整机断电承诺。
- **已证实的可靠性缺口，不能记为验收通过**：新增 `KnownBrokerLimitation` 风险刻画用例，先订阅 A 并保存 Broker 检查点，再增加 B，取得 SUBACK、持久化本地确认日志，并实际验证 B 可以收消息；SIGKILL 后 Broker 回到只含 A 的检查点，重连仍返回 SessionPresent=true。正式接收器按现设计跳过本地已确认的 A/B，B 因此持续漏收，且没有 sessionLost 事件。以 B 消息后紧随 A 的 barrier 证明，不靠等待后“没收到”作结论。本地日志仍有两个 Confirmed 项，不能证明远端检查点也有两个订阅。
- 风险用例 `BrokerCanRestoreSessionPresentWithoutALaterLocallyConfirmedSubscription` 已真实运行并确认上述现象（`/tmp/tenonedge-broker-subscription-gap.log`）；测试通过仅意味着缺口被稳定复现，不意味着 AT-25 / V-04 / V-09 已通过。
- 待维护者决策：要消除该类订阅回滚漏收，须考虑每次建立新连接都重新协调订阅，与设计 §6.2“会话存在时不重复订阅未变更 Filter”冲突；MQTT 3.1.1 不提供查询订阅清单的接口。重新订阅可能重复投递 retained，特别影响已开启 ProcessRetained 的输入。仅异常断线重连补订无法覆盖网关离线期间 Broker 回滚，不能把它当完整修复。本批已向维护者说明，未擅自修改产品策略。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=broker-checkpoint' --results-directory /tmp/tenonedge-broker-checkpoint --blame-hang-timeout 5m --blame-hang-dump-type none`：880 单元 + 691 集成 = **1571 项测试断言通过，0 失败、0 跳过**。其中 1 项是上述已知缺口刻画，不能用这个总数宣称 MQTT 可靠性验收完成。TRX Counters 已核对，日志 `/tmp/tenonedge-broker-checkpoint-full.log`。初次格式检查发现两个换行问题、阻止全量执行；限定文件格式化修正后才执行上述全量，`git diff --check` 通过。
- 环境同 §2.76；本批未安装其他 Broker/插件，未把快照周期 0 当成生产推荐值。

### 2.78 T2-07 / V-09 Broker 离线积存条数与字节边界（2026-10-09）

- 独占 Mosquitto 配置可设置 max_queued_messages / max_queued_bytes；测试使用正式输入会话和 SQLite 订阅日志，在输入断开后发布 10 条 QoS 1 消息，逐条确认发布端收到成功 PUBACK，再恢复输入核对补收范围。
- 4 格实际结果：条数上限 1/3 时只补收最先的 1/3 条；字节上限 1/5、每条载荷 2 字节时同样补收最先的 1/3 条，其余新消息被 Broker 丢弃。字节检查针对已有积存，最后一条可跨过阈值，因此该参数不能被当成包含元数据的严格内存硬上限；本测试不推断 RSS。
- 会话仍返回 SessionPresent=true，不重订，也不产生 sessionLost；Broker 日志明确出现该 ClientId 的 outgoing messages dropped。恢复后实际新 barrier 到达作为顺序屏障，不使用固定等待证明丢失。发布端 PUBACK 不能证明网关已接管，Broker 外部丢弃也不能伪造网关接收计数。
- 聚焦命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~OfflineBrokerQueueDropsNewMessagesBeyondLimitDespitePublisherPubacks`：4 项通过，日志 `/tmp/tenonedge-broker-queue-focused3.log`；首轮构建缺少 ReadOnlySequence 扩展的 System.Buffers 引用而失败，补齐后条数两格及最终四格均通过，没有改 MQTTnet API/包版本。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=broker-queue' --results-directory /tmp/tenonedge-broker-queue --blame-hang-timeout 5m --blame-hang-dump-type none`：**880 单元 + 695 集成 = 1575 项测试断言通过，0 失败、0 跳过**，包含 §2.77 的 1 项已知缺口刻画，不能用总数抹去待解决的漏订阅风险。TRX Counters 已核对，日志 `/tmp/tenonedge-broker-queue-full.log`。限定修改文件的 `dotnet format --verify-no-changes --no-restore --include …` 和 `git diff --check` 通过。
- AT-27 软件子场景、V-09 的离线队列上限范围获得实测；这不是 Host 重启/配置应用全过程或完整 V-09 通过。环境同 §2.76，未新增生产 Broker 部署模板或改变默认产品配额。

### 2.79 T2-07 / V-09 设备 Topic 发布授权与只读网关账号（2026-10-09）

- 独占测试 Broker 增加文件 ACL 配置：两个独立测试设备账号 sensor-a/sensor-b 只能写各自 devices/<账号>/#；网关 probe-user 只读 devices/#。均为人工测试账号，不是生产密码或部署模板，不改变其他 Broker 用例的权限。
- 正式 MqttSession/MqttInputReceiver 作为网关接收端：两个设备各自合法发布均到达，双向跨设备伪装发布均不到达；只读网关自身发布也不到达。最后一条合法设备 barrier 作为接收顺序屏障，实际核对完整主题与载荷序列，而非 sleep 后检查空队列。
- 额外确认 MQTT 3.1.1 的边界：被 ACL 拒绝的 QoS 1 发布仍返回成功 PUBACK，产品发送结果因此为 Ack；它只表示 Broker 级确认，不证明 ACL 放行或终端接收。此行为须进入后续部署排障说明，不能把设备 ACL 错误冒充网关丢包。
- 聚焦命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~DeviceAccountsCannotSpoofEachOtherAndGatewayAccountIsReadOnly`：1 项通过，日志 `/tmp/tenonedge-broker-acl-focused.log`。匿名/错误密码场景仍由既有 MqttPublishProbeTests 覆盖；本项未实测设备账号的读取拒绝、ACL 热更新或会话过期。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=broker-acl' --results-directory /tmp/tenonedge-broker-acl --blame-hang-timeout 5m --blame-hang-dump-type none`：**880 单元 + 696 集成 = 1576 项测试断言通过，0 失败、0 跳过**，仍包含 §2.77 的已知缺口刻画，不代表该风险已解决。TRX Counters 已核对，日志 `/tmp/tenonedge-broker-acl-full.log`。限定修改文件的 `dotnet format --verify-no-changes --no-restore --include …` 与 `git diff --check` 通过。
- 适用范围为附带接入 Broker 的只读网关账号；外部 Broker 上需要输入/输出共用连接时，账号权限须与实际用途对应，不把此测试配置机械套到所有连接。环境同 §2.76。

### 2.80 T2-03 / T2-08 真实 Broker 载荷边界与原始缓冲释放（2026-10-09）

- QoS 0/1 各自经真实 Mosquitto → 正式 MqttSession → MqttReceiveBuffer → MqttInputReceiver，发送恰好 1 MiB、1 MiB + 1 字节和 1 字节屏障。绑定只接到第一条及屏障，超限条恰好计 oversized 一次；QoS 1 三条按处理策略均确认，QoS 0 无确认。
- 采用产品相同的 64 MiB 总额度 / 8 MiB 原始缓冲分区，验证峰值包含大载荷预约、从未超过原始分区、处理后全部释放；没有 bufferDropped/bufferFull、没有 wireOversized 或接管故障，连接仍可用且未重建。
- 这是 MQTT 载荷大小/确认/预约释放子场景，不是 JSON 解码、SQLite 接管或 RSS 验收：测试绑定返回 Rejected，使用任意二进制载荷验证 MQTT 层，不把它当成有效 JSON 入库证据。
- 聚焦命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~BrokerPayloadBoundaryIsCountedWithoutPoisoningBufferedConnection`：2 项通过，日志 `/tmp/tenonedge-mqtt-payload-boundary.log`。
- 会话类回归：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MqttInputSessionTests --logger 'trx;LogFilePrefix=mqtt-payload' --results-directory /tmp/tenonedge-mqtt-payload --blame-hang-timeout 5m --blame-hang-dump-type none`：**36 项断言通过，0 失败、0 跳过**，含 §2.77 已知缺口刻画；日志 `/tmp/tenonedge-mqtt-payload-session.log`，TRX Counters 已核对。只改测试，本轮未重跑整套解决方案，最近全量基线见 §2.79。限定修改文件的 `dotnet format --verify-no-changes --no-restore --include …` 和 `git diff --check` 通过。
- 环境同 §2.76；本批只新增测试，不改变 1 MiB 输入上限或原始缓冲算法。

### 2.81 T2 输出构建错误的非秘密边界（2026-10-09）

- 发现 MQTT 输出工厂把已捕获的 ArgumentException / JsonException.Message 原样公开，并把原始异常保留为 InnerException。未知 JSON 属性名和凭证提供者的异常说明因而可进入 RuntimeUnitBuildException，对外诊断或后续日志都可能回显不可信配置内容。
- 两项回归测试先确定性失败：未知属性名与凭证解析器异常中的人工敏感标记均出现在异常 ToString；日志 `/tmp/tenonedge-mqtt-build-red.log`。工厂改为固定安全说明，不保留这两类原始异常链；保留单元标识和 RuntimeUnitBuildException 类型，原来的配置拒绝语义不变。
- `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~MqttOutputTests`：33 项通过，日志 `/tmp/tenonedge-mqtt-build-green.log`。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=mqtt-build' --results-directory /tmp/tenonedge-mqtt-build --blame-hang-timeout 5m --blame-hang-dump-type none`：**882 单元 + 698 集成 = 1580 项测试断言通过，0 失败、0 跳过**；含 §2.77 已知缺口刻画，不代表订阅回滚风险已修复。TRX Counters 已核对，日志 `/tmp/tenonedge-mqtt-build-full.log`。限定两个修改文件的 `dotnet format --verify-no-changes --no-restore --include …` 与 `git diff --check` 通过。环境同 §2.76。
- 边界：只修复此工厂已经捕获的两类构建错误，不声称所有适配器、监督者未知异常及停止/释放日志已脱敏；§7 待处理 22 中的运行时统一异常策略仍须继续落实。测试标记完全人工，不含真实凭证。

### 2.82 T2 监督者故障、重建、停止与释放的安全诊断（2026-10-09）

- SupervisedUnit 不再把任意 Exception.Message 截断后写进状态/运行事件，也不再把原 Exception 对象交给故障、取消回调或释放日志。截断长度不是脱敏：异常链可能含地址、响应正文或凭证。
- 采用固定分类说明：重建失败、超时、输入输出失败、未知错误；保留单元 ID、时间、重启计数和退避期限。循环意外返回使用内部类型识别，保留可信的固定生命周期说明，不靠匹配异常文本判定安全性。没有增加正则秘密猜测或绕过异常监督。
- LogCapture 测试基础设施增加 ExceptionText，避免原来只检查格式化消息却漏掉 ILogger 的异常参数。4 个场景检查运行故障、重建、释放和停止回调，状态/事件均无人工敏感标记，日志不附带原异常对象；嵌套异常也被覆盖。最初运行/重建/释放 3 场景确定性失败（`/tmp/tenonedge-supervisor-red.log`），修正后当时 14 项监督者用例通过（`/tmp/tenonedge-supervisor-green.log`），随后补入停止场景。
- 既有测试中断言原始模拟异常文字的部分改为固定安全分类；重启次数、退避、停止期限、单实例及故障隔离断言不放宽。
- 首轮全量：882 单元通过，701 集成通过、1 失败；失败是 AckedRecordIsNotRepublishedWhenDeletionFailsAndSenderIsRebuilt 等待被移除的原始“删除未提交”文字超时，记录于 `/tmp/tenonedge-supervisor-full.log`。改为等待准确发送单元的 UnitFaulted 事件与真实 StorageFault 状态，保留重建、不重发、只计一次已发送和故障恢复断言。期间补 using 前的编译失败也保留（`/tmp/tenonedge-supervisor-targeted.log`）；补齐后 16 项相关用例通过（`/tmp/tenonedge-supervisor-targeted2.log`）。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=supervisor-safe-repeat' --results-directory /tmp/tenonedge-supervisor-safe-repeat --blame-hang-timeout 5m --blame-hang-dump-type none`：**882 单元 + 702 集成 = 1584 项测试断言通过，0 失败、0 跳过**；仍包含 §2.77 已知缺口刻画。日志 `/tmp/tenonedge-supervisor-full-repeat.log`，TRX Counters 已核对。四个改动 C# 文件限定 `--include` 的格式校验与 `git diff --check` 通过；环境同 §2.76。
- 边界：本批处理监督者；RuntimeHost 首次构建的未知异常和其他调用层仍须单独核查，不能将本节当作所有日志脱敏验收完成。§7 待处理 22 尚未整体关闭。

### 2.83 T2 首次构建失败的安全诊断（2026-10-09）

- RuntimeHost 对输入/输出工厂、字段映射解析等构建异常采用固定安全说明，不再公开任意 Message，未知输入构建错误日志不再附带原异常对象。RuntimeUnitBuildException 也不视为可信文本；单元标识仍通过 Failure.Id / 状态 / 事件目标保留。
- 运行时自己生成的固定验证说明使用私有异常类型，保留预算、缺少模块、引用与最多 32 条路径等诊断；不按异常类型 ArgumentException 或文本内容猜测是否含秘密。输入工厂标识不一致的说明也不回显工厂返回的任意标识。
- 新增 4 项人工敏感标记测试，覆盖输入/输出及预期/未知异常，检查准备失败原因、激活状态、事件和日志异常参数。先修正测试编译的 CA2201（不用裸 Exception）后，4 项均确定性红灯，日志 `/tmp/tenonedge-build-safe-red.log`；修复后与 MappedOutputTests 共 23 项通过，日志 `/tmp/tenonedge-build-safe-green.log`。
- 首轮全量 882 单元通过，集成 701 通过、5 失败：4 项旧契约测试要求 Reason 重复包含单元 ID，现改为检查固定安全说明，保留原有 Failure.Id 断言；另有 RepeatedFrozenConnectionsDoNotCountNetworkFailuresAsPoison 首次发送预期 Ack 实得 Retry，未修改该用例/协议代码，原因尚未确认。原始日志 `/tmp/tenonedge-build-safe-full.log`，不把该失败隐去。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=build-safe-repeat' --results-directory /tmp/tenonedge-build-safe-repeat --blame-hang-timeout 5m --blame-hang-dump-type none`：**882 单元 + 706 集成 = 1588 项测试断言通过，0 失败、0 跳过**；包含 §2.77 已知缺口刻画，不代表订阅回滚风险已解决。日志 `/tmp/tenonedge-build-safe-full-repeat.log`，TRX Counters 已核对。限定四个 C# 文件的格式检查通过（首轮发现 using 排序，修正后 `/tmp/tenonedge-build-safe-format2.log` 为空），`git diff --check` 通过；环境同 §2.76。
- 对首轮 MQTT 偶发失败另行单独复跑：`dotnet test tests/IntegrationTests -c Release --no-build --no-restore --filter FullyQualifiedName~RepeatedFrozenConnectionsDoNotCountNetworkFailuresAsPoison`，1 项通过（内部 8 路 × 10 轮），日志 `/tmp/tenonedge-build-safe-outage-repeat.log`；仍不据此判定偶发失败已修复。
- 边界：只处理单元首次构建的捕获路径，不声称计划提供者 Create、激活/停止、协调者和所有应用日志均已脱敏；§7 待处理 22 尚未整体关闭。无真实秘密、无协议策略调整。

### 2.84 T2 种子快照拒绝路径的安全诊断（2026-10-09）

- RuntimePlanDocument 原先把未知属性名、配置解析器 Message 和原异常链暴露给调用者；RuntimeCoordinator 会将其中的 Message 写入运行日志及 config.db 的 SeedRejected 事件。冻结前拒绝配置并不能防止这些诊断落盘泄露。
- 改为固定结构说明，保留可信的 units 索引、JSON 行号/字节位置，不回显未知属性名、非法配置值或解析器 JSON Path，不附带原异常链。重复标识、MQTT 反馈闭环仍拒绝并保留固定原因；未知属性与配置验证没有放宽。
- 红灯：5 项解析器单元测试 + 4 项真实进程内宿主测试均复现人工标记泄露，日志 `/tmp/tenonedge-seed-safe-red.log`。宿主场景包含顶层/单元未知属性、MQTT 输出与 HTTP 输入配置的未知属性；确认没有快照或有效版本，然后检查 SQLite 中实际持久化的拒绝事件及日志消息/异常链。
- 修复后聚焦命令：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~SeedDiagnosticsTests|FullyQualifiedName~RuntimePlanDocumentTests|FullyQualifiedName~RuntimeCoordinatorTests'`，20 单元 + 23 集成通过，零失败/跳过（`/tmp/tenonedge-seed-safe-green.log`）。随后增加一项非法 JSON 异常不保留原链的测试，随全量验证。
- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=seed-safe' --results-directory /tmp/tenonedge-seed-safe --blame-hang-timeout 5m --blame-hang-dump-type none`：**888 单元 + 710 集成 = 1598 项测试断言通过，0 失败、0 跳过**；仍含 §2.77 已知订阅回滚缺口刻画，不代表该风险已修复。TRX Counters 已核对，日志 `/tmp/tenonedge-seed-safe-full.log`。三个修改 C# 文件限定格式校验与 `git diff --check` 通过；Linux x64、SDK 10.0.401，其他环境同 §2.76。
- 边界：本节仅验证被拒绝种子内容的解析诊断；不声称种子路径、存储错误、计划提供者、停止与其他日志通道都已完成安全审查。没有改变导入、快照哈希、启动保护和协议行为，也不代表 T2 退出或网页验收。

### 2.85 T2 协调者版本存储错误的安全诊断（2026-10-09）

- RuntimeCoordinator 的启动版本存储错误原先把数据库异常 Message 放入运行状态与 RunFailed 事件，标记稳定/清除运行标记失败时也把完整 Exception 交给日志。现在使用固定安全说明，保留错误阶段和版本号，不附带数据库异常链。
- 新增三项真实宿主 + SQLite 故障注入：先正常导入/停止空计划，再分别以触发器拒绝启动写入 running_revision、写入 is_stable、停止清除 running_revision。错误包含完全人工的敏感标记，不含真实凭证。三项先确定性失败（`/tmp/tenonedge-storage-safe-red.log`），修复后检查协调者日志不携带异常对象，启动失败的状态及持久化 RunFailed 事件也不回显标记。
- 不修改事务、启动保护和失败语义：启动写标记失败仍为 RunFailed 且标记未写；稳定标记失败不伪报 IsStable；正常停机清标记失败仍保留 Running 指针供下次启动判定。
- 聚焦：`dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~RuntimeStorageDiagnosticsTests|FullyQualifiedName~RuntimeCoordinatorTests'`，22 项通过、零失败/跳过，日志 `/tmp/tenonedge-storage-safe-green.log`。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=storage-safe' --results-directory /tmp/tenonedge-storage-safe --blame-hang-timeout 5m --blame-hang-dump-type none`：**888 单元 + 713 集成 = 1601 项测试断言通过，0 失败、0 跳过**；仍包含 §2.77 已知订阅回滚缺口刻画。TRX Counters 已核对，日志 `/tmp/tenonedge-storage-safe-full.log`；两项 C# 文件的限定格式校验与 `git diff --check` 通过。环境同 §2.84。
- 边界：只处理协调者已捕获的三个版本存储错误通道，不声称停止兜底、发送缓存错误、种子文件 I/O 或整个系统日志均已脱敏。T2 协议完整验收与 T3 可视化目标仍未完成；Modbus 定稿及 MQTT 重订策略的确认未到达，未据此修改既有协议规则。

### 2.86 T2-07 / V-09 Broker ACL 在线重载（2026-10-09）

- 在独占 Mosquitto 2.1.2 测试实例中，让正式 MqttSession / MqttInputReceiver 持有 devices/# 订阅。sensor-a 同一发布连接顺序发送 private 与 barrier；依次验证初始都可读、SIGHUP 加入 private 拒绝规则后只收到 barrier、撤销拒绝规则后再次都可读。发布者仍收到成功 PUBACK，不能以此推断订阅者有读权限。
- 整个过程产品连接数为 1、subscribed 事件只有一次，两端保持连接；验证的是已有连接/已有订阅上的 ACL 即时生效，不是重新订阅后才生效。以同一发布连接上的有序屏障判定拒绝，不用睡眠后的“尚未收到”冒充拒绝证据。
- 测试夹具以原子替换 ACL 文件后发 SIGHUP；等待新增重载日志，再完成一个独立 MQTT 就绪握手，确认主循环已继续。只支持固定测试 ACL profile，不新增产品远程命令、部署模板或管理 API。
- 首次编译曾引用不存在的 SubscribeCount，修正为检查现有 subscribed 事件（`/tmp/tenonedge-acl-reload.log`）；聚焦命令 `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ReloadedReadAclAppliesToExistingSubscriptionWithoutReconnecting`，1 项通过（`/tmp/tenonedge-acl-reload2.log`）。
- 变异：临时取消拒绝规则后，同一用例因收到 denied-private 而确定性失败；编译成功、用例实际执行，日志 `/tmp/tenonedge-acl-reload-mutation.log`。变异已恢复后再进行全量回归。
- 全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=acl-reload' --results-directory /tmp/tenonedge-acl-reload --blame-hang-timeout 5m --blame-hang-dump-type none`：**888 单元 + 714 集成 = 1602 项测试断言通过，0 失败、0 跳过**；含 §2.77 已知缺口刻画。TRX Counters 已核对，日志 `/tmp/tenonedge-acl-reload-full.log`。环境同 §2.84，未增加包或镜像。首轮格式检查指出一处 if 换行，修正后两项 C# 文件限定格式检查与 `git diff --check` 通过（`/tmp/tenonedge-acl-reload-format2.log`）。
- 边界：只证明在线订阅的读权限收回/恢复；未证明设备账号读权限全矩阵、离线积存重连时的 ACL 重检、账号吊销或过期、实物接入。§2.77 订阅回滚风险与待确认决策没有改变。

### 2.87 T2-07 / V-09 离线积存的 ACL 撤权与恢复（2026-10-09）

- 通过正式 MqttSession 建立持久订阅并写本地订阅日志，释放网关客户端后，由 sensor-a 向 private 与 barrier 各发布一条非 retained 的 QoS 1 消息并获得 PUBACK。随后重载 ACL，再以相同 ClientId 和重新打开的本地订阅存储建立新会话。
- 两个对照场景：不撤权时，重连收到 queued-private、queued-barrier 两条，证明旧会话有离线积存；撤权时只收到 queued-barrier。两者 SessionPresent 均为 true，产品都没有重新订阅或报 sessionLost，因此不是 CleanSession 清空或重新建立订阅造成的差异。
- 同一发布连接的有序 barrier 是读取终点，不依赖固定睡眠。随后恢复 private 权限并发送 fresh-private / fresh-barrier，接收端恰好按顺序收到新消息，没有先收到曾被撤权丢弃的 queued-private。这里证明 Broker 侧授权隔离，不把 Broker 丢弃记作网关已接管或已入库。
- 初次编译 CA1861 提示常量数组断言，改为比较准确有序字符串后再执行，编译日志 `/tmp/tenonedge-acl-offline.log`。聚焦命令：`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~OfflineQueuedMessagesAreRecheckedAgainstReloadedReadAcl`：2 项通过、零失败/跳过（`/tmp/tenonedge-acl-offline2.log`）。
- 全量命令：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=acl-offline' --results-directory /tmp/tenonedge-acl-offline --blame-hang-timeout 5m --blame-hang-dump-type none`：**888 单元 + 716 集成 = 1604 项测试断言通过，0 失败、0 跳过**；仍含 §2.77 已知缺口刻画。日志 `/tmp/tenonedge-acl-offline-full.log`，TRX Counters 已核对。限定修改文件的格式校验和 `git diff --check` 通过；环境同 §2.84，无新包/镜像。
- 边界：未验证已经发出但尚未 PUBACK 的在途消息撤权、设备账号读权限全矩阵、密码吊销、会话过期或 Broker 真实断电；不改变 §2.77 订阅回滚缺口，不代表 T2-07 整体退出。选型与协议策略仍待维护者确认。

### 2.88 T2 协议决策确认与 MQTT 每次建连重订（2026-10-09）

- 维护者回复“都按推荐，继续”：确认 NModbus 3.0.83、Modbus TCP UnitId 0–255 / RTU over TCP 1–247；确认 MQTT 每次建立连接都重订当前需要主题，接受 ProcessRetained=true 时可能重复处理保留消息。总设计 §2.3/6.1/6.2 与功能需求已对齐；其他待定事项不据此视为获批。
- MqttInputReceiver 不再按本地 Confirmed 日志跳过当前主题。仍保留写订阅意图→SUBACK→本地确认与退订事务边界，SessionPresent=false 的会话丢失事件、QoS 1 按序确认、默认忽略 retained 均不变；连接健康维护而未重建时不会重复订阅。
- 将 §2.77 的已知缺口刻画改为必须恢复丢失订阅的回归测试：保存含 A 的 Broker 检查点，新增 B 并验证成功与本地确认，强杀回滚，SessionPresent=true 时要求重订 A/B，并实收 B 的消息。修复前确定性失败（期望 2 次订阅、实际 0），日志 `/tmp/tenonedge-resubscribe-red.log`。不再将“确认复现漏收”作为测试通过条件。
- 相关保留消息、进程重启和持久计数断言按已确认规则调整：显式处理 retained 会再次接收；默认忽略仍不生成投递，但忽略/确认计数增加。旧章节 §2.59—2.87 中“不重订”和“缺口未修复”描述保留为历史证据，以本节为当前策略。
- 聚焦 MQTT 会话/Host/真实进程重建：42 项通过，日志 `/tmp/tenonedge-resubscribe-green.log`。全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=resubscribe' --results-directory /tmp/tenonedge-resubscribe --blame-hang-timeout 5m --blame-hang-dump-type none`：**888 单元 + 716 集成 = 1604 项通过，0 失败、0 跳过**，日志 `/tmp/tenonedge-resubscribe-full.log`，TRX Counters 已核对。原 KnownBrokerLimitation 测试现已转换为恢复断言，不再按“复现缺陷”计通过。四项 C# 文件限定格式校验与 `git diff --check` 通过，环境同 §2.84。
- 边界：此修复恢复当前需要的订阅，不恢复 Broker 已丢失消息，也不保证识别 Broker 回滚复活、且本地已删除日志的历史订阅；后者仍可能额外积存，输入按当前绑定匹配并计数后确认。Modbus 正式适配、T2 完整退出和 T3 网页仍未完成。

### 2.89 T2-02 NModbus 正式只读传输层（2026-10-09）

- 在 §2.88 选型确认后，Adapters 正式引用 NModbus 3.0.83；使用实际包 API（CreateIpMaster / CreateRtuMaster + IStreamResource），没有复制参考仓源码。版本仍集中管理，Adapters/Host/两个测试工程锁文件随还原更新；`dotnet restore TenonEdge.sln --locked-mode` 通过（`/tmp/tenonedge-modbus-locked.log`）。包内 nuspec/许可证已核对，MIT 及版权登记在 reference-notes N-10；T5-07 仍需附带许可材料。
- ModbusReadClient 仅开放 FC01/02/03/04；TCP UnitId 0–255、RTU 1–247。接收由统一出站连接器取得的双向可超时流，在已有 ModbusRequestExecutor 专用线程上串行执行；不新增 DNS/代理通道，不使用库的线程池 Async 包装，不自行创建第二套连接。
- 库前的帧门禁使用固定 260 字节缓冲：验证 MBAP 协议号/长度、事务号、UnitId、功能码和字节数，RTU 另校验 CRC。旧 TCP 事务完整帧跳过，不重发当前请求；一代连接用尽事务号时要求更换连接，不在同一连接重复使用号码。未完成 TCP 头/正文保留到下一请求，旧帧补齐后跳过，避免将半帧剩余误当 MBAP；普通超时本身不擅自判定所有从站离线或强制重连。
- 请求总期限覆盖所有碎片与跳过旧帧的读取，每次实际读写按剩余时间设置同步超时。库内重试为 0；异常帧在交给库之前完成归属/CRC 验证，异常 02/05/06 等以结构化 Code 返回，不陷入 ACK 无限等候或 Busy 重发。取消关闭同代流并等待真实阻塞调用退出。
- 新测试：13 单元 + 24 集成 = 37 项聚焦通过（`dotnet test TenonEdge.sln -c Release --no-restore --filter FullyQualifiedName~ModbusReadClientTests`，`/tmp/tenonedge-modbus-client3.log`）。涵盖四功能码、跨字节位读取、边界 UnitId、真实本机从站已知寄存器、异常码、异常帧的错误 UnitId、CRC、旧帧的不同 UnitId/功能码、真实读取超时与取消，以及可控时钟的碎片总期限、TCP 半头/半正文跨请求、事务号用尽和写出前参数拒绝。既有独立模拟器增加 FC01/02 的已知交替位响应，03/04 行为不变。
- 超时用例把测试自身 WaitAsync 期限放在异常捕获之外，不能把测试等候超时误判为产品读取期限生效。真实通信为 Linux 回环 TCP 和模拟从站，不是实物串口服务器/厂商兼容性验收。
- 首轮全量单元通过，集成编译失败：将测试超时移出异常捕获后，xUnit Record.ExceptionAsync 返回 ValueTask，须先 AsTask 再 WaitAsync；已修正（`/tmp/tenonedge-modbus-client-full.log`）。首轮格式检查发现 if 换行，限定文件修正后校验通过。最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-client-repeat' --results-directory /tmp/tenonedge-modbus-client-repeat --blame-hang-timeout 5m --blame-hang-dump-type none`，**901 单元 + 740 集成 = 1641 项通过，0 失败、0 跳过**；TRX Counters 已核对，日志 `/tmp/tenonedge-modbus-client-full-repeat.log`。四个 C# 文件限定格式校验（`/tmp/tenonedge-modbus-client-format2.log`）与 `git diff --check` 通过，Linux x64 / SDK 10.0.401。
- 边界：传输已通过实际 NModbus 和统一出站连接器测试，但尚未注册 input.modbus。下一步为计划配置、共享会话、请求间隔、RTU 超时静默整合、UnitId 调度/快速失败/离线退避及消息生成；不得据此声称 T2-02 或 T2 完成。RTU 重用前仍须由会话执行 §2.72 的静默隔离，取消/坏 MBAP/事务耗尽需换连接。

### 2.90 T2-02 Modbus 共享会话与连接级故障恢复（2026-10-09）

- 新增内部 ModbusConnectionSettings / ModbusConnectionSession，将 §2.89 只读传输、专用执行器、连接健康事实及 RTU 静默隔离接到既有 SharedConnections 租约。构造不联网，参数及启用 UnitId 验证完成后才启动工作线程，避免无效构造泄漏线程；未增加运行时注册或第二套共享连接管理器。
- 连接总期限与读取期限分开；请求间隔使用单调时钟。排队、等待间隔和执行器未准入时的取消不关闭健康流；实际读取取消只关闭捕获的连接代，并等待真实 I/O 退出。连接器在取消后才返回的流也会释放。最后租约等待实际读取退出后才释放连接标识，期间不能建立第二实例。
- 每个实际请求返回 Outcome / WasRequested，退避期间不发送、不伪造从站超时。单从站超时保留 TCP 连接；所有启用从站最近实际请求均超时或连接错误才触发 1、2、4…最高 60 秒连接重试退避，成功恢复初始延迟。协议异常码、CRC 错误不是超时。RTU 单从站超时执行固定静默窗口；无字节可复用，有迟到字节则立即换连接而非额外退避。
- 新增设置/连接期限单元测试与真实回环 TCP/RTU 会话测试：同一物理连接的两个租约/UnitId、健康兄弟从站、全部超时后的无请求退避及恢复、连接错误指数退避和成功复位、协议异常/CRC、RTU 迟到隔离、读取取消后新连接、间隔取消、排队取消与最后释放期间拒绝第二实例。假时钟只控制间隔/退避/连接期限；协议超时使用真实 NetworkStream 与本机模拟从站，未声称实物验证。
- 聚焦命令：`dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~ModbusConnectionSettingsTests` 首轮 12 项通过（`/tmp/tenonedge-modbus-settings-target.log`）；随后补充 2 项取消后返回流释放测试，纳入最终全量。`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ModbusConnectionSessionTests` 10 项通过（`/tmp/tenonedge-modbus-session-target2.log`）。
- 环境 Linux x64 / SDK 10.0.401；`dotnet restore TenonEdge.sln --locked-mode` 通过（`/tmp/tenonedge-modbus-session-restore.log`）。四个新增 C# 文件执行限定 `dotnet format TenonEdge.sln --no-restore --include ...`，再用 `--verify-no-changes` 检查；日志 `/tmp/tenonedge-modbus-session-format-verify.log`。
- 最终代码首轮全量 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-session-final' --results-directory /tmp/tenonedge-modbus-session-final --blame-hang-timeout 5m --blame-hang-dump-type none`：915 单元通过，集成 749 通过 / 1 失败 / 0 跳过。失败为既有 `NModbusLeavesAFollowingTcpFrameForTheNextRequest` 的第一次正常读取（第 96 行）超时，尚未到重复帧断言；日志 `/tmp/tenonedge-modbus-session-final.log`，TRX 保留原失败。与 §2.70 已记录的 300ms 探针偶发超时类似，但本批未确定根因、未调整探针期限，不能声称修复。新增会话与设置测试在该轮全部通过。
- 上述探针单例复跑通过（`dotnet test tests/IntegrationTests -c Release --no-build --no-restore --filter FullyQualifiedName~NModbusLeavesAFollowingTcpFrameForTheNextRequest`，`/tmp/tenonedge-modbus-session-probe-repeat.log`）。相同最终代码完整复跑：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-session-repeat' --results-directory /tmp/tenonedge-modbus-session-repeat --blame-hang-timeout 5m --blame-hang-dump-type none`，**915 单元 + 750 集成 = 1665 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-session-repeat.log`）。限定格式验证与 `git diff --check` 通过；复跑成功不撤销上一条偶发失败记录。
- 边界：本节仅覆盖 AT-02/08/13、V-05 的连接会话子场景。尚未实现 Modbus 输入计划解析/工厂、UnitId 采集周期调度与快速失败、整周期失败离线退避、overrun、EdgeMessage 生成和宿主真实采集链路；不能标记 T2-02 或 T2 完成，尚无网页。

### 2.91 T2-02 Modbus 单周期快速失败与设备退避组件（2026-10-09）

- 新增 ModbusCycleReader：消费既有读取规划的块，经共享租约逐块串行准入；第一个超时或连接错误后，该次 UnitId 周期剩余块直接产生失败结果，WasRequested=false，不再次发送或记录连接健康事实。设备异常帧/CRC 等一般读取错误不冒充超时；部分成功保留本次成功块及各失败块的原因，不缓存上周期数据。
- 调度调用者传入本周期剩余时间，期限包含租约排队、请求间隔及网络读取。截止后等待实际 I/O 退出，返回一个 Overrun 标志和空结果，不把已完成的前半周期当作完整采样输出；停止取消仍向上传播，不转换成 overrun。已过期周期不联网，完整块表先校验再执行，非法后续块不会造成半次采集。
- 新增 ModbusDevicePollingState：实际完成的整周期全部失败累计至 3 次判离线（计数在阈值饱和）；任意成功读取清除连续失败/退避。设备退避与连接重连退避分开，初始采用正常周期的两倍、限制为 1—60 秒，后续失败翻倍至 60 秒；调用者仍须遵守正常周期，长周期不会被改为更频繁读取。overrun、未准入及连接退避导致的全未请求周期不作为设备离线或恢复证据。该状态由未来设备调度器按连接 + UnitId 共享，不能每采集组各建一份。
- 10 项状态单元测试：按周期而非按块累计、部分成功恢复、指数退避/上限/恢复后复位、无请求/overrun 不误判、协议错误计入实际整周期失败、设备隔离与初始间隔。命令 `dotnet test tests/UnitTests -c Release --no-restore --filter FullyQualifiedName~ModbusDevicePollingStateTests`，通过（`/tmp/tenonedge-modbus-cycle-unit.log`）。
- 回环 TCP/RTU 真实 NModbus 测试：一个沉默 UnitId 只发一个请求即快速失败，其余 UnitId 的三个块正常且共享同连接；设备异常后继续读取；周期截止丢弃此前成功块并等待阻塞读取退出，下一周期重新连接；停止取消、租约排队截止不影响当前操作；三次真实失败后仅沉默设备退避并在恢复后复位；过期周期与无效后续块不联网。首轮集成编译被 CA1861 拦下（`/tmp/tenonedge-modbus-cycle-target.log`），改为序列文本断言后聚焦 7 项通过（`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ModbusCycleReaderTests`，`/tmp/tenonedge-modbus-cycle-target2.log`）；后补 2 项边界测试纳入最终全量。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-cycle' --results-directory /tmp/tenonedge-modbus-cycle-results --blame-hang-timeout 5m --blame-hang-dump-type none`，**925 单元 + 759 集成 = 1684 项通过，0 失败、0 跳过**；TRX Counters 已核对，日志 `/tmp/tenonedge-modbus-cycle-full.log`。环境 Linux x64 / SDK 10.0.401。
- 限定新增四个 C# 文件的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...` 通过（`/tmp/tenonedge-modbus-cycle-format-verify.log`）；`dotnet restore TenonEdge.sln --locked-mode` 与 `git diff --check` 通过（还原日志 `/tmp/tenonedge-modbus-cycle-restore.log`），未增加依赖或更改锁文件。
- 范围仅为 AT-02/08/13、V-05 的单周期执行和退避组件；并非已注册 input.modbus。常驻固定周期循环、多采集组同 UnitId 协调、消息生成/预算预约、诊断计数/离线事件以及宿主端到端链路仍须后续接入。本节不声称 overrun 已出现在管理 API，不声称 T2 或实物验证完成。

### 2.92 T2-02 Modbus 输入配置与点表消息解码（2026-10-09）

- 新增 ModbusInputSettings / ModbusPointSettings：严格 JSON 成员检查，连接参数复用 §2.90 校验；TCP UnitId 0—255 / RTU 1—247，UnitId 与地址必须显式提供，不能把缺项静默当成合法的 0。周期默认 5 秒、最小 1 秒；读取默认 5 秒。运行计划只接受规范化零基偏移，手册编号仍由已有地址转换器在草稿/导入边界处理，不在采集时猜测。
- 点位校验功能码、类型、寄存器占用范围、四种字节序与位号；01/02 只接受 bool，寄存器取位用 bit + 0—15。禁用点位仍须结构合法但不进入读取规划；同组 key 不可重复。内部点表上限暂取 10000 项（与 CSV 行数上限对齐，不是容量承诺），运行组至少一个启用点；跨组同设备 key 唯一性仍由计划工厂接入时执行。
- 新增不可变 ModbusPointTable，将连续读取规划与点位原始序号关联，解码只使用本周期的块结果。部分失败：成功值进入 values，超时/异常/读取失败分别映射 readTimeout/deviceException/readError，失败字段不取历史值、不补零；非有限浮点为 nonFinite。整组通信失败或 overrun 返回 null，不能伪造正常消息。消息采集/接收时间均为本次网关读取完成时间，由调用者传入当时 Gateway/GatewayUnsynced 来源；uint64 保持无符号 64 位精度。
- 聚焦 37 单元 + 2 集成通过：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~ModbusInputSettingsTests|FullyQualifiedName~ModbusPointTableTests'`（`/tmp/tenonedge-modbus-points-target.log`）。单元覆盖严格/必填参数、禁用点校验、UnitId 边界、乱序/重叠地址身份、禁用地址间隙不合并、各失败质量、无旧值、uint64 最大值、无穷浮点、位与位输入、消息时间及 malformed success 拒绝。首轮单元编译失败为 xUnit 的 int/byte? 比较重载，显式 byte 后修正（`/tmp/tenonedge-modbus-points-unit.log`）；禁用点反例增加独立合法启用点，避免被“全禁用”检查掩盖。
- 两种帧格式的集成测试从真实 JSON 配置，经统一出站连接器、共享租约、NModbus、读取规划与周期层，到真实 EdgeMessage；对端为回环模拟从站。四功能码共同采集，手写独立期望 `0x1000100110021003` 核对大整数，寄存器取位与 bool 正确，超时块及快速失败的未请求块只进入 quality，确认没有发送后续地址请求。不据此声称真实设备/串口服务器兼容。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-points' --results-directory /tmp/tenonedge-modbus-points-results --blame-hang-timeout 5m --blame-hang-dump-type none`，**962 单元 + 761 集成 = 1723 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-points-full.log`）。Linux x64 / SDK 10.0.401。五个新增 C# 文件的限定 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...`（`/tmp/tenonedge-modbus-points-format-verify.log`）、`dotnet restore TenonEdge.sln --locked-mode`（`/tmp/tenonedge-modbus-points-restore.log`）与 `git diff --check` 通过，未新增依赖。
- 尚未注册 input.modbus；计划工厂、设备多组调度、全局在途预算、诊断事件与宿主端到端测试是下一步。此处没有前端，没有把组件集成测试当成 T2 完成。

### 2.93 T2-02 Modbus 计划工厂与预算化输入循环（2026-10-09）

- 新增 ModbusPlanAdapterProvider，每次 Prepare 创建独立输入工厂；Build 只校验/绑定，不联网、不取得租约、不启动线程。相同计划与上下文的监督重建返回新单元并复用绑定；拒绝跨计划复用、同连接物理参数不一致、同设备连接/UnitId 不一致、跨组重复点位 key，连接上限 256。失败构建在登记前退出，后续合法组仍可构建；激活后冻结新绑定。
- RunAsync 等待现有激活屏障后取得 SharedConnections 租约，同连接全部启用 UnitId 在创建会话时提供。输入使用既有 InputMessageWriter.ProduceAsync，先预约按点数估算的工作/消息额度（8192 + 点数 × 1024 字节），再读取/解码，正常周期不重叠、不补采；预算或租约等待超出周期计 overrun，停止取消等待实际 I/O 并释放租约与预算。不绕过出站连接器，不增加 Task.Run 阻塞读取或另一套运行时。
- 同设备组使用串行设备准入及共享退避；失败窗口以最短组周期划分，同一窗口不把多个失败组累计成多次离线证据。UnitId 首次读取超时后，窗口内同设备其他组快速失败，不再发送请求；其他 UnitId 不受该设备退避影响。非超时错误以各组最近实际周期结果保守判定，未知组不是失败；多组异步周期的完整验收仍须后续扩充。
- 接入既有读取完成/失败/overrun 上报，异常帧诊断仅带数值异常码，无外部正文。补充分离 ConnectionAttempted 与 WasRequested：实际建连失败可计当前设备周期失败，但退避期间没尝试连接不能伪造新失败；连接健康的请求事实仍仅由真正发送的请求维护。真实建连失败连续三次的离线状态及退避跳过已有测试。
- 8 项新增集成测试：TCP/RTU 激活前零连接、激活后真实值/时间来源/预算释放；沉默 UnitId 与健康 UnitId 共用一个物理连接；预算耗尽零网络且截止计一次 overrun；停止打断真实阻塞读取；无污染的跨组校验与监督重建；同 UnitId 多组每窗口只发一次超时请求并共享退避；实际建连拒绝与无尝试退避的诊断区别。测试通过真实运行单元与 BudgetedInputChannel，不是把工厂 Build 成功当成采集通过；仍为本机模拟从站，不是实物。
- 首轮构建修正嵌套 Unit 不能直接捕获外层主构造参数（改为显式 owner 服务字段）；首轮测试修正实际 InFlightBudget API 使用（TryReserve/Capacity/Release，而非不存在的 LimitBytes）。原始日志 `/tmp/tenonedge-modbus-factory-build.log`、`/tmp/tenonedge-modbus-factory-target.log`。预算截止用例先完成激活再同步进入 RunAsync，不靠固定 sleep 等待就绪。最终聚焦 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~ModbusInputTests|FullyQualifiedName~ModbusConnectionSettingsTests|FullyQualifiedName~ModbusDevicePollingStateTests'`，24 既有单元 + 8 新集成通过（`/tmp/tenonedge-modbus-factory-target4.log`）。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-factory' --results-directory /tmp/tenonedge-modbus-factory-results --blame-hang-timeout 5m --blame-hang-dump-type none`，**962 单元 + 769 集成 = 1731 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-factory-full.log`）。Linux x64 / SDK 10.0.401。五个本批 C# 文件限定 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...`（`/tmp/tenonedge-modbus-factory-format-verify.log`）、`dotnet restore TenonEdge.sln --locked-mode`（`/tmp/tenonedge-modbus-factory-restore.log`）与 `git diff --check` 通过；未新增依赖。
- 尚未在 Host 注册 provider 或开放 Modbus 种子校验；下一步接入 Host、跨协议连接冲突/全计划约束及真实输出链路。当前多组错误聚合、不同周期/多逻辑设备同 UnitId、长周期预算/压力、资源上限及真实离线恢复还须补齐完整验收，不能标记 T2-02 或 T2 完成。

### 2.94 T2-02 Modbus Host 注册、种子校验与真实 HTTP 输出链路（2026-10-09）

- Host 将 ModbusPlanAdapterProvider 注册为进程级无状态提供者，与 MQTT 提供者并存；每次 Prepare 仍产生独立工厂，复用同一个 SharedConnections/OutboundConnector/TimeProvider，不改变 Program.cs 或创建第二套 RuntimeHost。
- RuntimePlanDocument 在种子冻结前严格解析 input.modbus；校验跨组物理连接参数、设备连接/UnitId、设备内点位 key，拒绝 MQTT/HTTP/Modbus 混用连接编号，限制全部连接 500 / Modbus 256。外部未知属性和非法配置仍返回固定安全说明，不把原始配置或异常链写入诊断。合法不同 UnitId 与不同组可共用连接，按连接而不是按组计数。
- 新增 14 项单元测试：合法共享、5 类物理参数冲突、设备身份/点位冲突、跨协议冲突、256 边界、无效标识及秘密不回显。跨协议负例先单独解析另一协议的配置成功，再验证组合冲突，避免以无效 HTTP 字段配置制造假阳性。
- 新增 2 项产品 Host 集成测试：TCP UnitId 255 与 RTU UnitId 1；真实回环 Modbus 从站，首次设置后重启加载种子，完整采集—消息—SQLite 发送缓存—正式 HTTP POST—独立接收端 204 确认。独立已知值 `0x1000100110021003` 在最终 JSON 中仍为精确 uint64，标准信封的来源编号、设备与网关时间来源正确，确认后 DeletedRecords 增加，认证管理 status API 的采集完成计数增加。同一物理会话没有重复连接；未声称硬件实测。
- 聚焦命令 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~ModbusHostTests|FullyQualifiedName~ModbusPlanDocumentTests'`：14 单元 + 2 集成通过（`/tmp/tenonedge-modbus-host-target2.log`）。首轮集成编译被 CA1861 拦下，固定输入引用数组提取为 static readonly（`/tmp/tenonedge-modbus-host-target.log`）；核对 StandardEnvelope 后不再假设输出含内部 receivedAt 字段，时间相等的内部语义由 §2.92 已有测试覆盖。
- 首轮全量：976 单元通过，集成 769 通过 / 2 失败 / 0 跳过（`/tmp/tenonedge-modbus-host-full.log`）。均为注册新协议后既有夹具假设过时：RuntimeCoordinator 的“未注册模块”用例原先拿 input.modbus 作占位，改为明确未注册的 input.unregistered，保留构建失败后 effective 不变与 notEffective 快照断言；MQTT Host 用例原先断言提供者总共 1 个，改为分别断言 MQTT、Modbus 提供者各 1 个，保留凭证、重启、保留消息及无独立 MQTT 输出工厂等原断言。不通过删除用例或放宽业务结果掩盖失败。
- 最终全量复跑：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-host-repeat' --results-directory /tmp/tenonedge-modbus-host-repeat --blame-hang-timeout 5m --blame-hang-dump-type none`，**976 单元 + 771 集成 = 1747 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-host-repeat.log`）。Linux x64 / SDK 10.0.401。七个本批 C# 文件限定 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...`（`/tmp/tenonedge-modbus-host-format-verify.log`）、`dotnet restore TenonEdge.sln --locked-mode`（`/tmp/tenonedge-modbus-host-restore.log`）与 `git diff --check` 通过；无新增依赖。
- 新增 `docs/modbus-integration.md` 与 README 入口，说明实际种子格式、手工接入前提、地址/类型/出站策略、精度和诊断，以及运行中的周期边界；人工地址/点位不是客户配置。尚无网页；多组不同周期、同 UnitId 多逻辑设备、设备恢复、压力/资源及实物验收仍待后续，不标记 T2 完成。

### 2.95 T2-02 同物理 UnitId 的别名绑定、完整离线诊断与恢复（2026-10-09）

- 扩充运行单元反例，先在原实现执行：同逻辑设备两组第三次失败后，后续组被退避检查提前返回，等待其第 3 次失败报告超时；两个逻辑设备引用相同连接/UnitId 时，第一个窗口实际发送 2 次超时请求而非 1 次。两项均失败（`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~PhysicalUnitSharesFastFailure`，`/tmp/tenonedge-modbus-unit-sharing-red.log`），不是静态猜测。
- 工厂增加按 `(connectionId, UnitId)` 索引的共享设备调度对象；逻辑 deviceKey 仍独立校验连接身份，点位 key 唯一性按 `(deviceKey, pointKey)`，不同逻辑设备可使用相同字段名。物理 UnitId 的串行准入、最短周期窗口、快速失败和离线退避不再因逻辑别名而复制；不改变连接的引用计数或实际客户端数量。
- 本窗口快速失败的诊断先于后续周期退避检查，因此第三次整体失败时，同窗口的后续组也更新离线状态。实际连接失败与读取超时均可触发同 UnitId 本窗口后续组快速失败；纯连接退避的无尝试结果不能新造窗口失败或给其他 UnitId 增加失败事实。
- 增强集成测试：同设备两组与两个逻辑设备同 UnitId 均连续 3 窗口只发 3 次超时请求，两组均显示离线；退避期间健康 UnitId 继续，恢复后两组真实值重新进入预算通道、状态正常，并在下一秒恢复正常频率，始终一条物理连接。另验证慢组周期 5 秒超时，只抑制最短 1 秒窗口，健康快组下一秒即可读取，不能被慢周期压住 5 秒；连接被出站策略拒绝的两组同样完成 3 次失败离线诊断，只有实际连接尝试才增加连接次数。
- 聚焦全部 ModbusInputTests 10 项通过（`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ModbusInputTests`，`/tmp/tenonedge-modbus-unit-sharing-target2.log`）。使用可控调度时钟与真实回环 Modbus I/O；恢复后的返回值和请求数均实测，不是只改状态对象。未增加等待时长规避失败，也没有降低原协议断言。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-unit-sharing' --results-directory /tmp/tenonedge-modbus-unit-sharing-results --blame-hang-timeout 5m --blame-hang-dump-type none`，**976 单元 + 773 集成 = 1749 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-unit-sharing-full.log`）。Linux x64 / SDK 10.0.401。两文件限定 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include ...`（`/tmp/tenonedge-modbus-unit-sharing-format-verify.log`）、`dotnet restore TenonEdge.sln --locked-mode`（`/tmp/tenonedge-modbus-unit-sharing-restore.log`）与 `git diff --check` 通过，无新增依赖。
- 仍须进行完整多输出隔离、资源/压力和剩余协议故障回归；不把同 UnitId 修复视为 T2 全部完成，不宣称实物兼容或已有网页。

### 2.96 T2-06 Modbus 产品 Host 扇出隔离与扇入不合并（2026-10-09）

- 在 §2.94 的真实 Host 链路上新增一源四路径：正常原始值 HTTP 输出、重命名并转字符串的 HTTP 输出、真实已接收请求但暂不应答的目标、缺少必填来源字段而拒绝的映射路径。采集周期设为 60 秒，验证窗口只采一次；路径都使用既有产品工厂、映射器、持久队列和发送者，没有模拟输出适配器或修改产品代码来迎合测试。
- 故障目标按输出标识排在前面；独立 TCP 接收端读完其请求后用明确屏障暂停应答（产品请求期限 60 秒）。两个正常接收端在该请求仍未完成时已拿到报文，再显式关闭故障连接且不应答，观察真实 output.retry。这比仅等一次短超时后再查看正常结果更强，避免串行输出也误通过隔离验收。
- 核对：实际 Modbus 请求恰好 1 次、物理连接 1 条；两正常目标 sourceMessageId 相同但 deliveryId 不同；映射结果为精确 uint64 原文字符串，原路径仍为原始整数且未混入重命名字段。故障目标仍有 1 条已提交待发记录，冻结 Payload 与已经写到 TCP 的报文逐字节相同，来源编号一致而投递编号独立；网络失败不计毒消息。缺字段路径只有 dataRejected=1，没有待发记录；成功两条确认删除、共提交三条，输入在途额度归零且峰值不越预算。
- 另一个产品 Host 场景：两个不同 Modbus UnitId/设备经一个物理连接，扇入一个 HTTP 目标，独立接收端得到两条消息，分别是各自已知寄存器值与 deviceKey，sourceMessageId 不同、字段没有 join 或互相替换。恰好采集两次、提交两条并确认删除两条。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-fanout-final' --results-directory /tmp/tenonedge-modbus-fanout-final --blame-hang-timeout 5m --blame-hang-dump-type none`，**976 单元 + 775 集成 = 1751 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-fanout-final.log`）。Linux x64 / SDK 10.0.401。单文件限定 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include tests/IntegrationTests/Runtime/ModbusHostTests.cs`（`/tmp/tenonedge-modbus-fanout-format-verify.log`）、`dotnet restore TenonEdge.sln --locked-mode`（`/tmp/tenonedge-modbus-fanout-restore.log`）与 `git diff --check` 通过，无新增依赖；没有为已能通过的链路增加不必要产品分支。
- 聚焦 `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ModbusHostTests`，含已有两帧格式场景共 4 项通过（`/tmp/tenonedge-modbus-fanout-target3.log`）。本节是 GW-13 / AT-06/29 软件链路与故障路径证据，不是设备兼容或长期资源验收；故障目标为真实 TCP 故障注入，不是实物网络断线声明。RSS、规模和剩余协议回归仍继续。

### 2.97 T2-08 Modbus 连接上限、阻塞停止与一分钟资源观测（2026-10-09）

- 新增 Linux 专用、禁止与其他集成测试并行的 ModbusResourceTests（其他系统明确 skip，不算通过）。通过正式计划提供者、输入运行单元、SharedConnections、OutboundConnector、NModbus 和 BudgetedInputChannel，连接回环模拟从站；不是单独构造无 I/O 的线程计数探针。
- 最大连接场景：256 个不同连接编号、256 个运行输入，准备阶段无网络/工作线程；激活后实际 256 TCP 连接与 256 次读取、工作线程恰好增加 256。正常响应与全部不应答分别连续启动/停止 3 轮，复用同一共享连接管理器和连接编号，证实旧条目已释放、没有累加第二代工作线程。正常分支逐条核对已知寄存器；不应答分支读取期限 30 秒，实际请求已到达后立即停止，**取消任务与全部运行任务一起受 5 秒停止期限约束**，不等到读取自然超时。每轮工作线程回到基线、输入预算归零；不应答时峰值/在途占用 2359296 字节。
- 聚焦实测（`dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~MaximumConnectionPlanReadsAndStops --logger 'trx;LogFileName=resources.trx' --results-directory /tmp/tenonedge-modbus-resources-stop`）：2 项通过。正常三轮停止 21/5/3ms，不应答三轮停止 42/29/17ms；两类每轮均 256→0 个 Modbus 工作线程，预算归零。TRX 中保留 processThreads/RSS/fdCount/预算的逐轮原始输出（`/tmp/tenonedge-modbus-resources-stop/resources.trx`），不是理论估算。
- 连续轮询场景：真实系统时间，32 连接 × 64 连续点位、1 秒周期，61 次采样，共 **1952 次请求 / 124928 个解码字段 / 60055ms**。每轮校验所有消息字段数、首末已知寄存器值、设备身份、质量、工作线程数与预算范围；结束只有 32 个累计连接，不持续重连，停止后线程回基线且预算归零。聚焦两项（当时最大连接场景尚未拆为正常/沉默两参数）通过，日志 `/tmp/tenonedge-modbus-resources-soak.log`、TRX `/tmp/tenonedge-modbus-resources-soak/resources.trx`。
- 资源观测边界：上述独立运行的一分钟场景 RSS 约 99.83MB→113.07MB，停止后约 112.91MB；输入预算峰值 1695744 字节。256 连接聚焦场景采样 RSS 最大 126713856 字节。**这些是测试进程 + 模拟从站 + CLR 的观测值，不是产品 Host 单独 RSS，不是 64MiB 进程内存上限或整机容量承诺。** fdCount/CLR 线程没有完全回到冷启动基线（如沉默场景 115→122 个 FD），本批仅报告观测，没有据此声称全进程资源完全无增长。没有强制 GC 美化读数；一分钟测试不等于小时/天级长期稳定或 TLS/MQTT 全规模压力。
- 首轮编译被 CA1711 拦下（测试组标记类型不应以 Collection 结尾），改名 ModbusResourceTestGroup 后通过（`/tmp/tenonedge-modbus-resources-target.log`、`/tmp/tenonedge-modbus-resources-target2.log`）。产品实现无需额外分支或改变上限。
- 最终全量：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=modbus-resources' --results-directory /tmp/tenonedge-modbus-resources-full --blame-hang-timeout 5m --blame-hang-dump-type none`，**976 单元 + 778 集成 = 1754 项通过，0 失败、0 跳过**；TRX Counters 已核对（`/tmp/tenonedge-modbus-resources-full.log`）。Linux x64 / SDK 10.0.401，集成套件约 2 分 3 秒。单文件限定 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include tests/IntegrationTests/Runtime/ModbusResourceTests.cs`（`/tmp/tenonedge-modbus-resources-format-verify.log`）、`dotnet restore TenonEdge.sln --locked-mode`（`/tmp/tenonedge-modbus-resources-restore.log`）与 `git diff --check` 通过；无新增依赖。
- 证据覆盖 AT-08/13、V-05 与 T2-08 的连接数、线程所有权、真实阻塞取消及短时连续采集子场景；物理设备、全产品长期 RSS、完整 T2 退出复核与网页仍未完成。

### 2.98 T2 基础链路退出复核与 T3 准入边界（2026-10-09）

此次是既有证据复核，不是新增实物或性能验收。维护者已授权继续至可视化访问并按推荐决策推进；不把 Agent 自报当成维护者验收。§2.22 等较早条目中的“剩余/进行中”为当时快照，本节与 §1 汇总当前边界，保留历史失败记录。

| 任务 / 验收子集 | 已核对的软件交付与证据 | 不据此声称完成的部分 |
|---|---|---|
| T2-01 / AT-06/18、BE-AT-03 | SharedConnections 的串行、租约/释放、容量和唯一身份；MQTT/HTTP/Modbus 都走实际 IP connector，禁系统代理/HTTP 自动跳转；§2.22、§2.36—§2.38、§2.50、§2.90 | 显式出站代理仍不支持，不能宣称代理支持已验收；管理预览借用随 T3-04/07 联动 |
| T2-02 / AT-02/08/13、V-05 | NModbus 正式传输、地址/类型/字节序、超时/迟到帧/CRC、周期/UnitId、线程退出；ModbusHostTests 的 TCP/RTU→SQLite→独立 HTTP 接收端；§2.68—§2.74、§2.89—§2.97 | 模拟从站非厂商兼容，实物寄存器/网络故障未验证 |
| T2-03 / AT-03/25/30、V-04 | 产品 Host、真实 Mosquitto、按持久接管确认、QoS 0 丢弃、接收预算、强杀恢复；§2.40—§2.66、§2.75—§2.88 | 重订恢复已知期望 Filter，不恢复 Broker 已丢消息，也不能发现 Broker 回滚复活的未知历史 Filter；旧 ClientId 与配置切换随 T3-06 |
| T2-04 / AT-04/18/26、GW-12 | HttpInputTests.ProductHostGetsAndPostsThroughOneSharedAuthenticatedConnection 真实 GET/POST 共用 TCP、凭证 API、精确大整数/时间；HttpOutputTests 与响应测试覆盖四类结果、冻结/重试、慢响应/正文边界；§2.27—§2.33、§2.38—§2.39 | 客户端证书/每连接信任、连接测试与接收方编辑协同尚未交付，随 T3-04 补齐；不把密码/Token 软件测试等同全部认证方式 |
| T2-05/06 / AT-07/29/30、GW-13 | 纯函数映射、不可变原文、质量/时间、整数逐位保留；不同输出的映射失败和网络故障隔离；ModbusHostTests 实际扇出与独立扇入；§2.23—§2.26、§2.32—§2.39、§2.96 | 当前显式 RuntimePlan，不是可视化图编译、草稿或实时应用；这些按 T3-04—06 执行 |
| T2-07 / AT-27、V-09、LR-AT 软件子集 | 仓库 LoRa 人工已解码样例→真实 Broker→Host→独立接收端；Broker 账户 ACL 热重载、离线撤权、SIGKILL/检查点/积存边界；§2.67、§2.76—§2.87 | 无无线实物、无网络服务器联调、无断电零丢失承诺；Broker PUBACK 不等于终端业务成功 |
| T2-08 / 本阶段故障和资源子集 | 256 Modbus 连接三轮启停/实际阻塞停止，32×64 点一分钟连续采集；MQTT 载荷分配前保护/TLS压力/预算释放；§2.62—§2.66、§2.80、§2.97 | 一分钟不是小时/天级压力，测试进程 RSS 不是产品 Host RSS；FD/CLR 冷启动差异未归因为无泄漏 |

复核基线 `c0db632`，Linux x64 / SDK 10.0.401；重新读取 §2.97 的两个原始 TRX Counters：976 单元 + 778 集成 = **1754 实际执行且通过，0 失败/跳过**。命令和原始报告位置沿用该节，不把重新读报告计作再次运行。核对了当前 Host/HTTP/Modbus 测试源及计划 §5/6、FE-D01。

结论：三输入与 MQTT/HTTP 输出的**基础软件链路稳定，满足 T3 基础链路准入**；可以开始 T3-01、随后优先 T3-02/03 原型。不将 T2 各跨阶段 AT/V 整体标为通过，不丢弃上表缺口。正式阶段验收、最低浏览器、真实设备及长期资源仍待相应证据。此次不批准 T4/T5/G1，不发布或推送。

### 2.99 T3-01 .NET 静态资源与 SPA 路由边界（2026-10-09）

- 按 §2.98 的基础链路准入开始 T3-01；本子项只建立正式 Host 的静态托管，不用测试 HTML 冒充前端交付。`WebAssets` 从标准 WebRootFileProvider 读取公开构建产物，不读取数据目录、不启 Node 服务、不增加业务 API 或修改既有认证。
- 管道在路由、认证和授权之后处理静态文件；已有端点以及大小写不敏感的 `/api`、`/healthz`、`/readyz` 前缀不进入静态文件或 SPA。即使 WebRoot 错放了 API 同名文件也不公开它。
- 只有 GET/HEAD、接受 `text/html` 且无文件扩展名的导航才回退 `index.html`；`/assets` 缺失文件永不回退，写操作/JSON 查询维持原错误。index 使用 `Cache-Control: no-store`，静态响应带 `nosniff`，其他资源支持 ETag/304；不伪造永久缓存策略。
- 测试发现 ASP.NET 默认 StatusCodePages 在浏览器 `Accept: text/html` 下返回纯文本，不符合 API JSON 错误约定；改为显式执行 Problem result。保留真实 404/405/401，既有 Allow/认证质询仍由原管道提供，不回退成 200 页面。
- `WebAssetsTests` 使用临时目录里的明确标注测试壳，真实 Program/生产环境管道；19 个新增场景覆盖首页/深链、HEAD、静态 JS/条件请求、未知 API/脚本、保留路径、不允许的方法、未经认证以及没有构建时仍 404。原端点清单和 OpenAPI 不变。
- 首轮 19 项因测试夹具假定环境只注册一次而启动失败，改用 WebHostBuilder 的实际 HostingEnvironment，不改产品 DI；第二轮 6 通过/13 失败揭示上述 Accept 错误响应问题。原日志保留 `/tmp/tenonedge-web-assets-target.log`、`-target2.log`。修正后聚焦命令 `dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~WebAssetsTests|FullyQualifiedName~ErrorResponseTests|FullyQualifiedName~ManagementApiAccessTests|FullyQualifiedName~OpenApiDocumentTests' --logger 'trx;LogFileName=web-assets.trx' --results-directory /tmp/tenonedge-web-assets`：**31 通过，0 失败/跳过**（`/tmp/tenonedge-web-assets-target3.log`）。
- 全量回归：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=web-assets-full' --results-directory /tmp/tenonedge-web-assets-full --blame-hang-timeout 5m --blame-hang-dump-type none`：**976 单元 + 797 集成 = 1773 项通过，0 失败/跳过**，两个 TRX Counters 已核对；日志 `/tmp/tenonedge-web-assets-full.log`，Linux x64 / SDK 10.0.401。`dotnet restore TenonEdge.sln --locked-mode`、三个改动 C# 文件限定的 `dotnet format TenonEdge.sln --no-restore --verify-no-changes --include …` 与 `git diff --check` 通过，日志 `/tmp/tenonedge-web-assets-restore.log`、`/tmp/tenonedge-web-assets-format-verify.log`。无新增依赖。
- 前端来源准备：从 FE-D01 固定提交 `e16c87f213a5ba5e45964e9b67c792105ec74d26` 下载源码到 `/tmp/tenonedge-shadcn-source/`，再次核对 MIT / Copyright (c) 2024 Sat Naing 及 package.json；本机 Node 22.23.2、npm 10.9.8。仅核查，不照搬上游 Clerk/Axios/ESLint 依赖，不把上游版本范围视为本项目锁定版本。实际裁剪、来源清单与许可分发随下一 T3-01 子项提交。
- **未完成：** 尚无 `web/`、真实前端构建/登录、浏览器测试或可访问产品页面；临时测试壳不进入发布物。FE-AT-07 仅服务端路由边界子场景，不称为离线浏览器或最低版本验证。T3-01 继续，再先做点表/画布原型。

### 2.100 T3-01 裁剪前端、真实认证与普通 HTTP 浏览器（2026-10-09）

- 建立 `web/`：从 FE-D01 固定提交实际裁剪 Button/Input/Label/Card、Main 和主题，共 6 文件；许可、改动路径与实际 npm 生产许可证保存并随静态构建输出（`web/THIRD_PARTY.md`、`public/licenses/`、来源登记第 6 节）。删除未使用图表/外部字体变量；没有复制 Clerk、mock 用户/Token、社交登录、示例仪表盘或整个模板。导航只出现真实工作区/账号安全，不用未来页面的无效按钮充数。
- React 19.3、TanStack Router 代码路由、Query、openapi-fetch、Zustand 内存会话、react-i18next 中文、RHF/Zod 与 Tailwind 4；一套 npm 锁文件/精确直接版本，无 Axios/React Router/ESLint，无 Node 运行服务。Node 22.23.2，统一 npm 11.21.0（命令可经 `npx --yes npm@11.21.0`，没有修改全局 npm）。
- `openapi.json` 来自真实 Development Host 的管理文档；`api:generate` 保存文档并由 openapi-typescript 7.13.0 生成 `schema.d.ts`，未手写后端请求/响应结构。只对无秘密的本机临时 Host 取文档，没有保存会话 Cookie 或密码。API 基础路径、CSRF 头及角色来自真实契约。
- 真实首次设置、重置待设置/窗口关闭提示、账号登录、会话恢复、退出、自改密码、强制改临时密码入口均按实际 API 编写；Cookie 由 .NET HttpOnly 会话维护，CSRF 只在内存，不写 localStorage/sessionStorage。退出/401 清查询和会话，旧会话代次的迟到响应拒绝写回。错误在对应区域显示，登录错误不清空输入；写请求 `retry=false`，路由不预加载操作。
- 请求总期限 15 秒覆盖响应头和**整个正文接收**，调用方取消同样中止读取；使用普通 AbortController，不依赖 Chrome 111 不支持的 AbortSignal.any/timeout 或 HTTP 非安全上下文的 crypto.randomUUID。单元测试用真实 HTTP 服务发头和半截正文后保持连接，证实期限没有在收到头时提前清除。
- .NET Production Host 通过绝对 `--webroot` 指向构建产物；Playwright 启动临时数据目录的真实 .NET，不使用假认证服务。关闭后移除测试数据，不在仓库中生成真实配置/秘密。构建/测试命令与开发方式见 `web/README.md`。当前工作区明确提示“开发中，点表/路线尚未交付”，没有模拟采集数据。

实际发现与修正（不把失败步骤算通过）：

1. 直接尝试上游 TypeScript 6 被 openapi-typescript 的 `^5.x` peer 约束拒绝；改为 5.9.3，未使用 `--force` 或忽略 peer 检查。npm 10.9.8 多次出现 Arborist `edgesOut` 内部异常（日志 `/tmp/tenonedge-web-npm-install.log`、`-npm-pinned.log`），用兼容本机 Node 的 npm 11.21.0 正常解析同一组合。
2. 旧 Vite 8.0.8/Vitest 4.1.4 有高/中风险公告；按 npm audit 实际结果更新至 Vite 8.3.4/Vitest 4.1.11，重新安装为零已知漏洞，不执行强制整树升级。TypeScript 首次编译还发现测试登录体漏了契约必需字段，补齐测试，不放松生成类型。
3. Playwright 1.59.1 不支持本机 Ubuntu 26.04，安装浏览器失败；更新到 1.64.0 后正常使用 Chromium 156.0.8078.4。**这个现代 Chromium 不是最低浏览器验收。** 原日志 `/tmp/tenonedge-web-browser-install.log`，修正后 `-browser-install2.log`。

验证（本地 Linux x64；本批远端 CI 未运行）：

- `cd web && npx --yes npm@11.21.0 ci`：按锁文件干净重装成功、审计 0 漏洞（`/tmp/tenonedge-web-ci.log`）。`run format:check`、`run lint`、`run build` 通过；构建记录 `/tmp/tenonedge-web-build-final.log`。产物 JS 约 545kB / gzip 172kB，CSS 约 21.4kB；Vite 有 >500kB chunk 提醒，未隐藏，不称为性能优化完成。最低浏览器只设置构建 target，尚无实测。
- `npx --yes npm@11.21.0 test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**19 通过，0 失败/跳过**，7 项 API/期限测试及 12 项 CI 报告门禁（含零用例、跳过、失败、全局错误负向用例）；`/tmp/tenonedge-web-unit-report2.log`、`web/reports/unit.json`。
- `PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npx --yes npm@11.21.0 run test:e2e -- --reporter=list,json`：**2 通过，0 失败/跳过**；实际网卡 HTTP，明确断言 `isSecureContext=false`。第一场景完成首次设置→自动会话→/account 深链刷新→真实 CSRF 改密码→退出→新密码登录；核对 HttpOnly Cookie、精确 4 次 POST、未知 API 仍 JSON404、内存外存储为空、页面无跨源资源请求。阻断非同源浏览器请求仍成功，这是页面资源独立子场景，不冒充断网现场部署。第二场景注入登录500，确认保留输入且没有自动重试，随后实际错误密码返回401。日志 `/tmp/tenonedge-web-browser-last.log`（第二场景补为可独立初始化后复测），JSON `web/reports/browser.json`，截图 `web/test-results/workspace.png` 已人工查看。
- `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 通过；新增 Frontend CI 固定 Action SHA，执行 locked install、实际 Host 构建、静态检查、非 localhost 浏览器与非零/无跳过报告检查。只上传测试证据，不上传发行物；GitHub 作业尚未触发，不称为远端通过。

- 后端全量回归：`dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=web-foundation' --results-directory /tmp/tenonedge-web-foundation-dotnet --blame-hang-timeout 5m --blame-hang-dump-type none`：**976 单元 + 797 集成 = 1773 项通过，0 失败/跳过**（两个 TRX Counters 已核对，`/tmp/tenonedge-web-foundation-dotnet.log`）。未修改后端业务代码。品牌两文件与仓库原图 SHA-256 一致，源 MIT 文本 cmp 一致，`git diff --check` 通过。

边界：FE-AT-01/02/04/06/07 的上述子场景获软件证据，不表示 FE 全部完成。强制改临时密码/主机重置、只读角色浏览器矩阵、会话强制失效竞态和所有最低浏览器仍需补测；没有点表/画布/草稿/应用/回滚，不标“可独立使用网关”或总目标完成。下一步先 T3-02 点表与 T3-03 画布原型，继续真实配置链路，不止步于登录壳。

### 2.101 T3-02 点表交互原型与大表软件验证（2026-10-09—10）

范围核对：按 PLAN-01 T3-02、功能 §3.3、V-10、FE-AT-03/05/06 开展；前置 T3-01 真实认证基础见 §2.100。页面明确标注“本地交互原型，不保存、不读取设备、不改变运行配置；离开页面会丢失”。没有新增假点位 API、预置演示设备、保存成功提示或读取按钮，也没有绕过 T3-04/05/06 的草稿/编译/应用任务。

- `/prototypes/points` 懒加载页面，TanStack Table 8.21.3 管理行/选择，TanStack Virtual 3.14.14 固定行高虚拟化；9 个字段可直接编辑，保留原始无效字符串，不把错误静默改成 0。仅页面拥有的 Zustand store，不持久化到浏览器，不经查询刷新覆盖，退出/离开页面即释放。
- 支持选择部分或全部行、删除确认、批量改类型/字节序/单位/启用；Excel TSV 粘贴通过普通 `paste` 事件，也可用多行文本框。追加或精确覆盖选中行，覆盖数量不匹配不做部分修改，覆盖保持行身份；支持引号包裹的制表符/换行及双引号转义。多余列和字段错误按行显示，不污染合法邻行；引号结构损坏、超限则拒绝整个操作，保留原内容。最多 10,000 行；原型解析入口另有 2,097,152 UTF-16 字符单位的防护上限，不称为新获批产品容量。
- 对照正式 ModbusAddress / ModbusPointSettings / ModbusRegisterDecoder：功能码/类型、0—15 位号、四种字节序、字段长度、设备内 key、占用寄存器末地址都校验，停用行也参与。预留其他采集组 key 集合的纯函数校验入口，但尚无设备/采集组配置 API，不把单表冒充设备全量校验。
- 地址显式区分零基偏移、完整 5/6 位手册编号、无前缀的一基寄存器号；完整编号首位决定功能码，保留 `00001` 的零。表格同时显示偏移和手册编号。切换模式先按旧模式规范化再换算，不把偏移 40001 偷改成偏移 0；有坏地址则整个切换不应用。64 位字节序显示对应 ABCDEFGH/GHEFCDAB/BADCFEHG/HGFEDCBA。
- key 重复会标记所有冲突行；地址区间按功能码排序后线性扫描重叠，不做逐对 O(n²) 比较。重叠仅提示，允许同寄存器不同位。错误行标红、给出原因，摘要能跳到并聚焦首批错误行，即使它原本不在虚拟 DOM 内。

软件验证（Linux x64、Node 22.23.2 / npm 11.21.0、Chromium 156.0.8078.4）：

1. `cd web && npx --yes npm@11.21.0 ci`：锁文件安装成功，审计 0 漏洞（`/tmp/tenonedge-points-ci.log`）。`run format:check`、`run lint`、`run build` 通过；日志 `/tmp/tenonedge-points-format-check.log`、`-lint.log`、`-build-bit.log`。点表 chunk 约 84kB / gzip 23kB，主 chunk >500kB 提醒仍保留。生产许可聚合由 38 包变为 42 包，新增包为 MIT，见来源登记。
2. `npx --yes npm@11.21.0 test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**78 通过，0 失败/跳过**；新增 59 项含两类手册位数/前导零、65535 末端宽度、全部 16 个位号及零填充十进制位号、非法类型/非十进制地址写法、跨组与停用 key、重叠、引号 TSV、覆盖原子性、选中行批改和 10,000 行边界（`/tmp/tenonedge-points-unit-bit.log`、`web/reports/unit.json`）。
3. `PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npx --yes npm@11.21.0 run test:e2e -- --reporter=list,json`：**4 通过，0 失败/跳过**，包含既有 2 项认证回归（`/tmp/tenonedge-points-browser-bit.log`、`web/reports/browser.json`）。实际 .NET Production Host + 静态构建 + 网卡 HTTP，明确非安全上下文。点表场景从真实登录开始，操作期间没有任何写 API 请求。
   - 1,000 行中第 901 行坏 key，定位/修正不影响邻行；仅所选行批改单位；粘贴覆盖仍 1,000 行并保持选择；切换到手册编号后 `40901` 与偏移 900 一致。注入会话状态查询500后，局部错误、上次成功刷新时间和手动重试入口可见，key/单位/编辑页面保持；移除故障后手动 GET 重试成功，编辑仍在。该交互测试用浏览器测试时钟推进60秒轮询，**不采用它的时间读数作为性能证据**。
   - 独立的真实时钟场景先追加 1,000 行，再追加 9,000 行，达到 10,000；末行可编辑，当前只渲染 13 行（断言 <40），第 10,001 行操作被拒且不改变已有内容。此处是软件构造 `ClipboardEvent` 注入普通粘贴处理路径，**不是人工 Excel/OS 可信剪贴板实测**。
4. 真实时钟观测（`/tmp/tenonedge-points-browser-retry.log`）：1,000 行解析/应用 **10.4ms**、全表校验 **5.1ms**；再追加9,000行处理 **19.6ms**，10,000行末行编辑后的全表校验 **7.0ms**。CDP Runtime.getHeapUsage.usedSize：初始 **6,487,832**，1,000 行后 **11,622,548**，10,000 行及末行编辑后 **67,582,104** 字节。是当前浏览器页面堆观测，含框架、表格模型和未回收临时对象；没有强制 GC，不是进程 RSS、精确数据结构尺寸、最低配置承诺或长期泄漏判定。完整原始值在该次日志；后续重复运行数值可波动，性能阈值尚未确定。
5. 报告门禁 `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 通过；`git diff --check` 通过。截图 `web/test-results/points-1000.png` 和 `points-10000.png` 已查看。首轮浏览器 3 通过/1 超时，是测试用 getByLabel 精确匹配隐式 select 标签失败，页面可访问性快照的 combobox 名称正确；改用角色+可访问名称定位，未放松行为断言或延长期限（原日志 `/tmp/tenonedge-points-browser.log`，定向复测 `-browser2.log`）。

本批没有改后端代码；.NET 1773 项通过沿用 §2.100 证据，**本轮未重新跑后端全量**，不得把沿用记为新执行。远端 CI 未执行。最低 Chrome/Edge 111、Safari 16.4、Firefox128，人工 Excel 粘贴及完整键盘/辅助技术矩阵仍未验证；V-10 / FE-AT 不整体通过。CSV 导入导出、后端草稿、设备多组归属及保存/应用仍按 T3-04 后续任务实现。下一步 T3-03 画布原型，然后继续真实配置链路，不把此原型视为最终可用配置产品。

### 2.102 T3-03 本地画布原型与交互软件验证（2026-10-10）

前置沿用 T3-01 真实会话/静态托管与 T2 基本链路准入。按开发计划 T3-03、功能 §5、FE-AT-05 实现 `web/src/features/canvas/` 与 `/prototypes/canvas`，不新增 API、后端持久化、真实引用绑定、运行状态或外部输出。参考 Node-RED 笔记仅借鉴思路，未复制源码或安装运行时。React Flow 12.12.0 锁定及许可见 `docs/reference-notes.md`。

实现：

- 左侧分组节点库（8 种设计节点，TenonIoT 明示 T4 未实现）、中间端口连线画布、右侧名称属性和底部结构提示/只读自有格式预览；所有节点明确“未配置 / 未绑定”。支持拖入/点击添加、Shift 框选/多选、单个及整组移动、复制粘贴、节点与边删除、缩放/平移/适应画布/缩略图。
- 连接模型拒绝缺失端点、反向、自环、长环及重复边；支持分叉和独立合流，不实现 Join。上限 50 节点；粘贴越界时整体拒绝。复制仅保留所选节点内部边，全部编号重建；编号使用页面内序列，普通 HTTP 不依赖安全上下文 API。
- 不可变快照保存最近 **50 步**编辑，涵盖增删节点/边、名称和位置；整组或单个指针拖动只入历史一次。Ctrl/Cmd+Z/Y（及 Shift+Z）、C/V 和 Delete/Backspace 不截获文本输入的原生编辑。新编辑清空重做；复制与选择不入历史。
- 自有格式为 `schemaVersion / definition / layout`；定义与位置分离，选择、拖动中位置、测量尺寸、缩略图和视口状态只在 UI 层。React Flow 尺寸通知须保留在 UI 投影，否则缩略图节点为零宽；已修正并通过矩形数量/宽度断言。离开页面或注销释放本页原型，不放浏览器持久存储。

实际验证（Ubuntu Linux x64、Node 22.23.2 / npm 11.21.0，Chromium 156.0.8078.4）：

1. `npx --yes npm@11.21.0 ci`、`run build`、`run lint`、`run format:check` 通过；构建许可汇集 **61 个生产包**。构建日志 `/tmp/tenonedge-canvas-build.log`；主入口约 502 kB 的大小提示仍存在，没有提高阈值掩盖，画布按路由懒加载。
2. `npx --yes npm@11.21.0 test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**91 通过，0 失败/跳过**，其中画布新增 **13** 项（含非有限坐标参数化用例），既有 78 项回归通过。覆盖 50 步有界历史、分支重做、选择不入历史、位置引用隔离、连带删边与恢复、诱导子图复制/编号唯一、越界原子拒绝、实例隔离及拓扑规则。日志 `/tmp/tenonedge-canvas-unit-final.log`。
3. `PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npx --yes npm@11.21.0 run test:e2e -- --reporter=list,json`：**6 通过，0 失败/跳过**。实际 Production .NET Host + 静态构建 + 网卡普通 HTTP，没有模拟登录。日志 `/tmp/tenonedge-canvas-browser-final.log`、报告 `web/reports/browser.json`，画布截图 `web/test-results/canvas.png` 已查看。
   - 真实指针拖入 4 节点/连接 3 边；反向闭环被拒；名称修改、单点拖动与一步撤销、复制/删除及快捷键重做；Shift 框选 4 节点、组移动全部变更，一步撤销完整恢复；缩放/适应画布不改变文档。缩略图包含 4 个非零宽节点。
   - UI 连续修改名称 55 次，撤销 50 次到第 5 次结果，再重做 50 次恢复；输入框原生撤销/退格不触及图历史；Shift 多选复制产生 2 个副本，Delete 只删除所选副本。
   - 自有序列化没有 `selected / dragging / measured / positionAbsolute`；画布编辑期间没有写 API 请求；离开再进入内容为空。
4. 首轮 4 通过/1 超时是测试错误假定端口 CSS 类名；按实际组件改正。第二次单画布仍超时，揭示空图初始 `fitView` 在首次添加后自动放大，导致后续拖入节点覆盖端口：移除隐式初始适应，保留显式适应按钮，随后画布用例通过。截图又发现缩略图空白，修正 UI 尺寸投影并新增断言后重跑；最终全部 6 项通过。失败不作为通过证据。
5. `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 与 `git diff --check` 通过。测试宿主均已结束；未推送、未运行远端 CI。

本批后端没有改动，**没有重新执行 .NET 1773 项**，沿用 §2.100。此处只有本地拓扑提示，不是 T3-05 编译/来源字段验证/先饱和计数后路径展开或真实样本预览；不标 AT-07/13/29 整体通过。最低浏览器、完整触摸/辅助技术矩阵未验证。下一步 T3-04 草稿及真实配置 API/表单，之后 T3-05/06 编译与应用回滚；当前仍不能仅靠此原型完成网关配置，总目标不标完成。

### 2.103 T3-04 连接草稿子项：真实持久化与网页表单（2026-10-10）

本次是 T3-04 的连接草稿子项，不标整项完成。前置为 T3-02、T0 安全、T2 与已确认 D-05；沿维护者授权继续可视化配置。核对功能 §2/3、总设计 §7.4/10.1/10.2 及参考项目笔记后，自行实现，不复制参考项目源码、不新增依赖。

- 配置库显式迁移 **12**：`configuration_draft` 保存全局草稿版本，`connection_draft` 保存稳定雪花 ID、唯一业务 key 和类型化非秘密参数。连接 ID 纳入启动时发号下限登记。新增/修改/删除、草稿版本推进与审计在同一短事务中完成；旧版本写入返回 409，不静默合并或覆盖。
- 分组 Minimal API 的 `GET/POST /connections`、`PUT/DELETE /connections/{connectionId}` 已交付并更新实际 OpenAPI/生成客户端。所有角色可读；写操作要求配置人员或管理员并受既有 CSRF/会话保护。key 与协议类型创建后不变。连接参数复用 Modbus/MQTT/HTTP 适配器的连接校验；未知属性、URL 用户信息/查询及已识别秘密请求头不能保存到草稿。
- 删除在同一配置事务中检查有效、上一、待生效运行版本引用；待发、失败及隔离记录存在时拒绝删除。引用检查当前覆盖既有 schema 1 种子快照；后续设备/输入实体与 schema 2 快照须接入同一保护，不能跳过。未实现的引用类型不据此宣称通过。
- `/connections` 是真实表单而非原型：Modbus 帧格式/超时/间隔、MQTT TLS/ClientId/保活、HTTP 基础地址/非秘密请求头；支持名称/key 搜索、类型筛选、启停草稿、删除确认。显示“草稿，尚未应用”，不伪造联网状态。原有种子不会自动转换成草稿，空列表明确说明。
- 查询用 TanStack Query、编辑用页面内 Zustand，表单捕获打开时的草稿版本。查询 500、冲突或手动刷新都不覆盖输入；重新载入须显式确认丢弃修改。内部导航/离站对未保存编辑给出提醒。保存成功后重新查询实际数据库；不触发应用、采集或外发。

实际验证：

1. 环境沿用 Linux x64、SDK 10.0.401、Node 22.23.2/npm 11.21.0。`dotnet build TenonEdge.sln -c Release --no-restore` 通过，0 警告/错误；新增代码与测试的格式由 `dotnet format` 校正。格式门禁也发现之前提交中 13 个文件的换行/using 排序问题，只做自动格式规范化，不改变其业务逻辑，单独整理为本地提交 `05b4022`。完整 `dotnet format TenonEdge.sln --verify-no-changes --no-restore` 随后通过（`/tmp/tenonedge-draft-format-final.log`），格式化后再次 Release 构建通过（`/tmp/tenonedge-draft-final-build.log`）。
2. `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t3-draft-final' --results-directory artifacts/test-results/t3-draft --blame-hang-timeout 5m --blame-hang-dump-type none`：**992 单元 + 812 集成 = 1804 通过，0 失败/跳过**；新增 16 项连接参数单测和 15 项草稿集成子场景。日志 `/tmp/tenonedge-draft-full-final.log`；最终两份 TRX 另归集到 `artifacts/test-results/t3-draft-final/`，与初轮失败报告分开，原报告未删除。`python3 .github/scripts/verify-test-results.py artifacts/test-results/t3-draft-final --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests` 通过。
   - 参数规范化、协议互斥、key/超时/地址及秘密头拒绝；真实会话角色矩阵、匿名/CSRF 拒绝；同版本并发仅一次成功，重复 key/不可变标识/过期删除拒绝；审计失败整体回滚。
   - 独立数据目录重建宿主后读取相同 ID/版本/内容；更新/删除产生审计。保存不推进有效/上一/待生效指针（此用例从无运行版本开始，不冒充带真实设备在线的无扰动验证）。三类运行版本引用采用存储夹具，三类发送记录采用真实 queue.db 写者，删除均被拦截；夹具不发外部 HTTP/MQTT。
3. 从临时 Development Host 导出实际 API，`npm run api:generate` 更新 `web/openapi.json` 与 `src/api/schema.d.ts`；临时目录和宿主已清理。`npm run build`、`run lint`、`run format:check` 通过；主入口约 508 kB 的大小提示仍明确保留，未提高阈值掩盖，连接页按路由懒加载。
4. `npx --yes npm@11.21.0 test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**96 通过，0 失败/跳过**（新增 5 项表单/编辑状态子场景）。`PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npx --yes npm@11.21.0 run test:e2e -- --reporter=list,json`：**7 通过，0 失败/跳过**。实际 Production Host 和网卡 HTTP，完整回归认证、点表和画布；连接场景真实新建 Modbus、重载页面持久保留、第二编辑请求触发 409 且输入保留、GET500 后恢复不丢编辑、显式重载后更新、HTTP 头/路径保存和筛选删除。写请求仅连接 CRUD，运行版本号保持不变。日志 `/tmp/tenonedge-draft-web-unit.log`、`/tmp/tenonedge-draft-browser-final.log`；报告门禁通过，截图 `web/test-results/connections-draft.png` 已查看。
5. 初轮全量有 4 项失败：新增端点后访问/operationId 清单未更新（3 项）及升级测试硬编码旧 schema 11（1 项）；清单按实际授权逐项扩展、升级断言改为当前末次迁移版本，保留原有数据不改写的断言。后续定向核对又校正了路由库存的尾斜杠。浏览器首轮 6 通过/1 失败是 select 的 label 定位，改为其可访问 combobox 角色定位后通过；没有放宽业务断言。失败日志保留，不能算通过。

剩余：T3-04 的凭证/信任证书表单、连接测试、设备/来源/点位草稿、复制/CSV/字段推断仍未完成；现有凭证 API 仍是 T2 种子专用接口，本批不宣称已经具备草稿资源归属与完整凭证类型联动。接收方变更保存仅修改草稿，凭证重绑定/应用拦截随 T3-04 后续与 T3-06 完成。T3-05/06 的引用编译、受控应用与版本回滚未实现，网页仍不能独立完成整个网关配置。AT-18/FE-AT 仅上述子场景有软件证据，最低浏览器、真实设备和远端 CI 未验证。未推送或发版，总目标继续保持未完成。

### 2.104 T3-04 连接凭证子项：受管草稿绑定与真实网页保存（2026-10-10）

沿已授权 T3-04 继续，不标整项完成；不新增依赖、不复制参考项目源码。连接草稿非秘密参数与独立凭证继续分开保存。

- 已有 MQTT 密码及 HTTP Basic、Bearer、API Key 请求头/查询参数的真实凭证接口接入连接表单。只对已保存、未修改且版本仍匹配的 MQTT/HTTP 连接显示表单；只读角色仅查看状态。保存和清除分别二次确认，秘密不回显、不存浏览器持久存储，成功后清空输入。TanStack Mutation 变量只包含动作，不携带秘密。接收账户声明没有默认值；接收方改变时仅管理员可显式保留旧秘密。
- 受管连接的凭证写入在同一个配置事务内检查全局草稿版本、连接存在性、协议类型和规范化接收方；表单过期、删除或协议/目标不符时拒绝。新增两种 DELETE 凭证端点，检查凭证版本与有效/上一/待生效运行快照引用，清除与审计原子提交，且不推进草稿版本。身份作废与明确清除仍是两种行为。
- 删除无引用、无发送记录的连接草稿时，同事务删除其认证凭证并追加审计。新建草稿不占用已有种子凭证或保留运行快照的编号。引用检查兼容现有种子解析允许的数字及数字字符串 connectionId，覆盖三种保留版本指针。
- 修复 HTTP API Key 改参数名绕过账户声明的问题：参数名不是账户身份；存在待发/失败记录时，同类型秘密替换仍需声明（Basic 用户名确实改变除外）。MQTT 请求对象的 ToString 与 HTTP 一样明确省略秘密。
- 实际 Development Host 导出 OpenAPI 后重新生成客户端；更新端点授权清单与 operationId 清单。

实际验证：

1. Linux x64、.NET SDK 10.0.401、Node 22.23.2/npm 11.21.0、Chromium 156.0.8078.4。Release 构建随全量测试完成；完整格式门禁 `dotnet format TenonEdge.sln --verify-no-changes --no-restore` 通过（`/tmp/tenonedge-credential-format-verify.log`）。
2. 后端最终全量 **992 单元 + 829 集成 = 1821 通过，0 失败/跳过**；命令 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t3-credential-final' --results-directory artifacts/test-results/t3-credential-final --blame-hang-timeout 5m --blame-hang-dump-type none`，日志 `/tmp/tenonedge-credential-full-final.log`。TRX 门禁 `python3 .github/scripts/verify-test-results.py artifacts/test-results/t3-credential-final --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests` 通过。新增 17 项集成场景覆盖受管绑定、过期草稿、删除清理、角色/CSRF、审计失败回滚、保留版本禁止清除、账户声明及种子编号碰撞；引用采用配置库夹具，不冒充真实设备验证。
3. `npx --yes npm@11.21.0 run build`、`run lint`、`run format:check` 通过，仍保留主入口超过 500 kB 提示。Vitest `test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**99 通过，0 失败/跳过**；日志 `/tmp/tenonedge-credential-web-tests.log`、`/tmp/tenonedge-credential-web-build.log`。
4. `PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npx --yes npm@11.21.0 run test:e2e -- --reporter=list,json`：**9 通过，0 失败/跳过**。实际 Production Host/临时配置库、网卡 HTTP；除此前 7 项回归，新增 MQTT 保存/重载状态/不回显/无 localStorage 秘密/草稿版本不变/清除，以及 HTTP 四种认证方式逐项真实 PUT 和秘密清空。日志 `/tmp/tenonedge-credential-browser-final.log`；`node scripts/verify-results.mjs reports/unit.json reports/browser.json` 通过。已查看 `web/test-results/connection-credential.png`，页面显示已设置而非秘密。这不是对外 Broker/HTTP 的认证联调。

剩余与安全边界：旧 T1/T2 种子入口仍允许在不存在草稿时不带草稿版本预置凭证，未宣称完成旧入口资源归属迁移；现有待发/失败检查仍是凭证事务前的快照，活跃运行时跨库凭证轮换协调/隔离竞态尚须在 T3 收尾。客户端证书、信任证书、连接测试、设备/来源/点位持久草稿、复制/CSV/字段推断未完成；T3-05 编译与 T3-06 应用/回滚未实现。现有表单子场景通过不代表 AT-18 或完整可视化配置验收通过。未推送、未发版，远端 CI 与真实设备未验证，总目标仍未完成。

### 2.105 T3-04 设备基础草稿与连接引用保护（2026-10-10）

继续已授权 T3-04 的设备基础信息子项，前置与 §2.104 相同；按功能 §3.1/3.2、总设计 §10.2 实施。重新核对本机参考目录 SUMMARY 与 neuron 笔记，仅参考设备/分组/点位分工，不拷贝 LGPL 源码，不引入依赖。

- 配置库显式迁移 **13** 新增 `device_draft`，稳定 ID 纳入启动发号下限；全局唯一不可变 deviceKey、名称、单层分组、协议类型、连接引用、启停，以及 Modbus UnitId/地址写法。物理设备必须引用同类型已存连接，模拟设备不能携带连接或 Modbus 字段。TCP UnitId 0—255、RTU over TCP 1—247，规范化名称/分组并检查长度与控制字符。
- 设备 CRUD、全局草稿版本、引用校验和审计同一配置事务；只读可查、配置人员/管理员可写，沿既有会话与 CSRF。真实 Host API 与 OpenAPI 已更新。保存只改草稿，不构造运行单元、不外发。种子不会自动转换成设备草稿。
- 有设备引用时禁止删除连接（数据库外键与应用检查）；连接帧格式修改会重新校验引用设备，不能把 UnitId 0/255 的 TCP 设备变成非法 RTU 设备。三种保留运行快照引用的设备 key 不能被删除或被新草稿占用，既有源/输出输入引用按 schema 1 检查。
- 新增 `/devices` 真实页面，左侧名称/key 搜索、分组与草稿启用筛选，右侧设备基本信息及协议相关字段。按角色禁用修改，key 创建后禁改；查询在 TanStack Query、本地表单与捕获版本在 Zustand，GET500/409/刷新不覆盖输入，重新载入/离站丢弃须确认。设备与连接读取版本不一致时要求刷新，避免新建/打开时使用两个草稿版本；写入仍由后端原子校验。

实际验证：

1. 环境沿 §2.104。最终后端全量 **1004 单元 + 843 集成 = 1847 通过，0 失败/跳过**；命令 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t3-device' --results-directory artifacts/test-results/t3-device --blame-hang-timeout 5m --blame-hang-dump-type none`，日志 `/tmp/tenonedge-device-full.log`。TRX 门禁 `python3 .github/scripts/verify-test-results.py artifacts/test-results/t3-device --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests` 通过。新增 12 个参数单测与 14 项设备 CRUD/角色/CSRF、真实数据目录重建持久化、同版本并发、审计失败回滚、引用/跨实体草稿版本/帧格式联动的集成子场景；快照采用数据库夹具，不是实物采集验证。
2. `dotnet format TenonEdge.sln --verify-no-changes --no-restore` 通过（`/tmp/tenonedge-device-format-verify.log`）。`node /tmp/tenonedge-export-draft-api.mjs` 从临时 Development Host 导出实际文档并生成客户端，临时 Host/数据已清理；前端 build、lint、format:check 通过，主入口约 512 kB 提示未隐藏。Vitest `npx --yes npm@11.21.0 test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**103 通过，0 失败/跳过**，日志 `/tmp/tenonedge-device-web-tests.log`，构建日志 `/tmp/tenonedge-device-web-build2.log`。
3. 浏览器最终 **10 通过，0 失败/跳过**，命令沿 §2.104，日志 `/tmp/tenonedge-device-browser-final.log`；新增真实新建设备、重载持久化、连接删除被引用拦截、并发草稿冲突、GET500 保留编辑、显式重载更新与删除、运行版本不变场景。前端报告门禁 `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 通过；已查看截图 `web/test-results/device-draft.png`。
4. 初轮定向验证发现公开测试方法使用内部 enum 的编译错误及新增端点清单顺序不符，随后修正为整数参数转内部类型并按实际端点排序；第二轮分析器指出测试参数名称 pointer 不合约定，改为 referenceKind。修正后定向 12 单元 + 22 集成通过，没有放宽业务断言。前端第一次构建发现 OpenAPI long 接受 string/number，按既有安全整数 ID 约定显式 Number 转换；第二次构建通过。
5. 首轮浏览器 **9 通过/1 失败**：第 10 次新会话登录触发真实每分钟 10 次限流，设备场景尚未开始，不能算设备通过。失败页面明确为登录限流；配置类测试改为仅在进程内复用一个真实 Cookie 会话，不写磁盘、不改变产品限流，认证用例仍各自独立。原失败日志 `/tmp/tenonedge-device-browser.log` 保留。第二轮 9 通过/1 失败是 select 的精确 label 定位，依据实际可访问树改用 combobox 角色定位（与已有连接表单一致），未改变业务断言；独立设备场景随后 1 项通过，日志 `/tmp/tenonedge-device-browser-targeted.log`。

剩余：本批仅设备基础配置，不含数据来源/点位的持久化、设备连同来源与点位复制、CSV、字段推断、运行状态；这些仍须继续实现。来源/路线/平台映射持久化后必须补入设备变更与删除引用检查，返回引用方清单；未实现的对象不据此宣称安全验收完成。凭证的旧种子入口与活跃轮换边界仍见 §2.104，证书/信任/连接测试、T3-05 编译、T3-06 应用/回滚仍待完成。实际软件子场景不等于 AT-18/完整 T3 验收，不声称已具备可用采集路线。未推送、未发版、未进行实物验证。

### 2.106 T3-04 Modbus 来源与点表的真实持久化（2026-10-10）

继续 §2.105 后的来源/点位子项，本次贯通 Modbus；不把它当作全部协议来源、T3-04 或可视化配置目标完成。核对功能 §3.3、总设计 §10.2 与实际 Modbus 适配器/现有点表原型接口，复用本项目代码，无新增依赖。

- 显式迁移 **14** 新增 `input_draft`：来源拥有稳定 ID、设备内唯一不可变 key、名称、启停、Modbus 周期与点表；点位作为所属来源的类型化内容存储，内部地址永远为零基偏移。来源 ID 纳入启动发号下限。草稿允许空组/全停用点表，不伪造启用点位；能否生成运行单元须后续编译判断。
- `/devices/{deviceId}/inputs` GET/POST、`/{inputId}` PUT/DELETE 已交付。每次写入在同事务检查全局草稿版本、父设备存在、来源归属、来源 key、设备内跨来源字段 key 唯一性，以及正式 Modbus 单点校验；后端独立校验周期、功能码、地址占用、位号、类型和字节序，禁用点也不能带非法参数。单来源最多 10000 点，未擅自添加“全设备总共 10000 点”的限制。
- 与设备删除一起原子清理所属来源；设备存在来源时不能切成不相容协议。有效/上一/待生效运行快照中的来源引用阻止删除或新建占用相同 key。所有变更与审计/全局版本同事务；任何失败都不应用、不采集、不入队。
- 来源写端点在认证授权后、JSON 绑定前允许有界 **16 MiB** 请求体，满足 10000 点表保存；其他端点仍为 1 MiB，不扩大所有 API 上限。未知点位属性拒绝，凭证不进入点表。
- 设备详情增加“数据来源与点位”入口，新页面 `/devices/{deviceId}/inputs` 接入真实 API。表单/点表在 Zustand、查询在 TanStack Query；编辑版本被捕获，刷新或 409 不覆盖输入，重新载入/离站丢弃须确认。不同设备路由以设备 ID 重建本地编辑器，防止沿用上台设备表单。
- 现有点表编辑器通过受控 store 复用，原型路由仍保留明确的不保存说明；真实来源页使用不同说明。支持已有虚拟表格、粘贴、批量改值、地址转换、逐行错误与重叠提示；保存前转换为零基偏移。加载持久点表后重置本地行号分配器，追加不会与已加载行 ID 冲突。只读角色禁用编辑和粘贴；保存期间禁用点表，避免成功清空未提交的新改动。

实际验证：

1. 环境沿 §2.105，Release 构建完成。后端最终全量 **1010 单元 + 857 集成 = 1867 通过，0 失败/跳过**；命令 `dotnet test TenonEdge.sln -c Release --no-restore --logger 'trx;LogFilePrefix=t3-input-final' --results-directory artifacts/test-results/t3-input-final --blame-hang-timeout 5m --blame-hang-dump-type none`，日志 `/tmp/tenonedge-input-full-final.log`；`python3 .github/scripts/verify-test-results.py artifacts/test-results/t3-input-final --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests` 门禁通过。新增 6 个参数/边界单测、14 个集成场景，覆盖角色/CSRF、资源归属、设备内字段唯一、三种快照保护、来源/父设备删除审计回滚，以及真实数据目录重建后点表持久保留且运行指针不变。快照为存储夹具，不是实物采集。
2. 从实际临时 Development Host 导出 OpenAPI 并生成客户端，日志 `/tmp/tenonedge-input-api.log`；临时 Host/数据已清理。前端 build 通过（`/tmp/tenonedge-input-web-build-final.log`）；共享点表按路由拆分后主入口约 466 kB，此次构建无此前 500 kB 提示，未改警告阈值。Vitest `npx --yes npm@11.21.0 test -- --reporter=default --reporter=json --outputFile=reports/unit.json`：**106 通过，0 失败/跳过**，日志 `/tmp/tenonedge-input-web-tests.log`。
3. 浏览器最终 **11 通过，0 失败/跳过**，命令沿 §2.104，日志 `/tmp/tenonedge-input-browser-final2.log`。新增真实创建连接/设备/来源、粘贴两点、手册地址转换保存、重载后保留、追加行 ID 不冲突、非法行拒绝保存、跨来源重复 key 被后端拒绝、真实大于 1 MiB 的 10000 点 HTTP 保存与读取、版本冲突保留输入、来源删除及运行版本不变；另核对来源 16 MiB 与普通端点 1 MiB 的 413 边界。完整后端格式门禁 `dotnet format TenonEdge.sln --verify-no-changes --no-restore`、前端 typecheck/lint/format:check 与 `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 均通过。截图 `web/test-results/source-points.png` 已查看。这是软件 HTTP/数据库验证，不是 PLC 读取，也不是操作系统 Excel 剪贴板实测。
4. 初轮定向 2 项失败：operationId 清单排序及测试把 ArgumentOutOfRangeException 当成精确 ArgumentException，校正清单与精确异常类型后定向 6 单元 + 17 集成通过。首次前端构建暴露生成客户端的可选字段/数值字符串联合类型，显式规范化后通过；读取现有点表 ID 时发现分配器可能与已加载 ID 冲突，修正 load 并加回归。后补持久化/引用用例的首次全量编译发现 RuntimeSnapshotRecord 命名空间写错，改为实际 Storage 类型后重跑；旧日志 `/tmp/tenonedge-input-full.log` 不算通过。

剩余：MQTT、HTTP、模拟来源与字段表单尚未接入持久化；设备复制、CSV、字段推断、连接测试、客户端证书/信任表单仍未完成。来源/设备与路线草稿/平台映射的引用检查须随相应对象接入并给出引用方清单；当前仅既有运行快照保护。凭证边界继续见 §2.104。T3-05 编译与 T3-06 应用/回滚未完成，因此网页仍不能独立完成一条实际采集路线。未推送、未发版、未进行真实设备验证，总目标保持未完成。

### 2.107 T3-04 MQTT、HTTP 与模拟来源草稿（2026-10-10）

沿 §2.106 扩展既有来源表与页面，不新建重复草稿库、不启动协议会话。按功能 §3.4/3.5 对照真实适配器、JSON 定位与模拟输入代码，并复核本机参考项目笔记；只复用本项目校验，不复制第三方源码或新增依赖。

- 同一 `InputDraft` 增加 MQTT、HTTP、Simulator 类型化参数分支，只允许与所属设备匹配的一种。所有协议继续共享草稿版本、事务、设备内来源/字段 key 唯一、授权/CSRF 与审计；既有 Modbus 数据不迁移、不重写。
- MQTT 配置 Topic、QoS 0/1、保留消息处理与上游时间映射，复用正式适配器的订阅校验。JSON 字段包含 key、固定类型、JSON Pointer、显示名称和单位，元数据不混入运行解码器定义。上游时间明确声明 RFC 3339/Unix 秒/毫秒；缺少字段类型或时间格式不能被默认为 enum 零值。空字段表可存草稿，但后续编译仍须判定是否可运行。
- HTTP 保存相对路径/非秘密查询、周期及来源级非秘密头；复用正式地址/头校验，禁止逃离基础路径、切主机、识别出的秘密头/查询名及忽略大小写重复头。连接与来源头合并后的数量/字节限制同样检查；连接或设备改引用时重新校验所属来源，失败不推进版本。
- 模拟来源可选电表模板或自定义一层 JSON，设置周期和三种故障比例。通过正式模拟配置解析器做纯校验/字段描述，不执行模拟循环；模板字段与运行实现共用常量。自定义配置 JSON 以原文本保存，浏览器不先 JSON.parse，保留大于 JS 安全整数的值；拒绝重复/非法字段名、嵌套对象、null、未知模板和越界比例。自定义模拟数据是主动配置，明确告知会持久化；不与 MQTT/HTTP 的临时抓取样本混淆。
- 来源页按设备协议显示真实表单：JSON 字段可手工增删、编辑定位/类型/名称/单位；MQTT 保留处理、HTTP 路径/头、模拟模式/固定数据/故障比例均真实保存。上游时间有独立启用标记，明确启用的根值定位空字符串不会被误删。查询与本地 Zustand 编辑继续分开，错误不清空表单；只读不能改写。

实际验证：

1. 环境沿 §2.106（SDK 10.0.401、Linux x64、Node 22.23.2、npm 11.21.0、Chromium 156.0.8078.4）。Release 构建 0 警告/错误；最终 `dotnet test TenonEdge.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=t3-json-source-verified' --results-directory artifacts/test-results/t3-json-source-verified --blame-hang-timeout 5m --blame-hang-dump-type none` **1031 单元 + 863 集成 = 1894 项通过，0 失败/跳过**（`/tmp/tenonedge-json-source-full-verified.log`）；`verify-test-results.py` 两程序集非零门禁通过。此前定向 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~JsonSource|FullyQualifiedName~InputDraft|FullyQualifiedName~Simulator'` **53 单元 + 33 集成通过**（`/tmp/tenonedge-json-source-targeted2.log`），此后补入“缺少字段类型/时间格式拒绝”单元及 API 回归，已计入上述最终全量。
2. 新增后端用例包括 MQTT QoS/Topic/保留/时间约束、固定 JSON 定位、HTTP 路径与秘密拒绝、合并头容量重新校验、模拟原始整数与非法样本；三种来源分别经真实 API 保存、重建 Host 后读取同 ID/版本/字段，以及同设备跨来源字段碰撞拒绝。没有连接真实 Broker/HTTP/设备，不声称采集验收。
3. 前端字段声明、HTTP 重复头、模拟 JSON 原文及根值上游时间回归：`npm run test` **110 项通过**（`web/reports/unit.json`）。最终 `npm run build` 与全量 `npm run test:e2e -- --reporter=list,json` **12 项通过**（`/tmp/tenonedge-json-source-browser-verified.log`、`web/reports/browser.json`）；核看 MQTT/模拟来源截图，字段类型选择框最小宽度已修正。`dotnet format TenonEdge.sln --no-restore --verify-no-changes`、前端 typecheck/lint/format:check 与 `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 均通过。来源浏览器独立用例已通过一次（`/tmp/tenonedge-json-source-browser-targeted2.log`）：三种协议逐项真实保存、重载，MQTT 未配时间就启用保留时返回 400 且保留输入，时间格式与字段类型/名称/单位正确持久化，模拟 9007199254740993 原文保持，运行版本未变化。
4. 首次构建发现 RuntimeUnitBuildException 的命名空间缺失，按实际 Contracts 引入。初轮单测把 JSON Pointer 的字面量星号误当成非法通配表达式；实际定位器将它视为普通对象键，不展开数组，因此改测真正非法的 ~2 转义，未改变定位语义。首轮浏览器在模拟 textarea 的精确 label 定位超时，实际可访问树有正确 textbox，改用 textbox 角色定位后独立场景通过；没有放宽业务断言。

5. 后端中间全量（`/tmp/tenonedge-json-source-full-final.log`、`artifacts/test-results/t3-json-source-final/`）曾出现 **1029 单元通过、862 集成通过、1 集成失败**：既有 `NModbusReportsAnRtuCrcErrorImmediately` 预期 CRC 异常却在约 306 ms 收到网络读取超时。原 300 ms 超时与断言均未放宽，增加从站连接/请求/应答计数输出辅助后续定位；独立重复原用例 **10/10 通过**（`/tmp/tenonedge-json-source-crc-recheck.log`）。失败根因仍未确定，不把“负载/调度”猜测写成修复结论。一次先前重检链在 Release 构建成功后退出 143，未生成重复/全量日志，不计为已执行；上述十次为重新实际执行。失败 TRX 保留。

剩余：当前只有手工字段描述，粘贴样本字段推断与实时抓取尚未实现；设备复制、CSV、连接测试、客户端证书/信任表单仍待完成。JSON 字段表 1000 行浏览器性能尚未验证。草稿路线/平台映射的引用清单和保护须继续接入；凭证边界见 §2.104。T3-05 图编译/样本预览与 T3-06 应用/回滚未完成，因此四类来源草稿可保存仍不等于网页已经能启动完整采集路线。总目标保持未完成，未推送、未发版、无实物验证。


### 2.108 T3-04 粘贴样本字段推断（2026-10-10）

在 §2.107 的真实 MQTT/HTTP 来源表单中加入临时样本推断，按功能 §3.4/3.7、设计 §6.4 实现；复核本机参考笔记的数据原文/类型边界，只借鉴模式，无源码移植、无新增依赖/服务/API。

- 粘贴 JSON 后列出叶子字段、固定下标 JSON Pointer、推测类型和样本值；正确转义对象键中的 `~` 和 `/`。数字一律默认 `number`，添加后可在已有字段表中改为 `integer`；布尔、字符串固定类型。null 列出但不猜类型，提示手工定义；空对象/数组无叶子。
- 用户勾选后只追加字段描述，自动建议符合业务 key 规则且不与当前字段重名的 key，不覆盖已有描述；后端仍做整个设备范围内的唯一性检查。按完整样本拒绝同名对象成员（含转义等价名称），不沿用 JSON.parse 丢弃前一个成员的行为，避免与运行时 JSON Pointer 的歧义拒绝冲突。
- 样本仅在组件内存；不进入 Zustand 草稿、Query 缓存、请求、localStorage/sessionStorage、日志或配置库。添加、清除、关闭、重新载入或切换来源后清空；保存请求仅包含既有字段 DTO。模拟来源的主动固定配置不变。
- 浏览器通过原生 JSON.parse reviver 的 `context.source` 保留数字字面量，显示 64 位整数、长小数、负零及指数原文，不用 JS number 重新输出。旧浏览器不具备该能力时明确拒绝数值样本并提示升级/手工定义，不默默舍入。当前 Chromium 实测；其他浏览器未实测。
- 有界：UTF-8 1 MiB、64 层、1000 叶子/最终字段、4096 字符定位。超限或非法 JSON 不生成部分描述、不清除已有字段；长样本值显示截断至 256 字符，原文解析仍完整。界面明确这是粘贴样本，不伪装成实时抓取。

验证：

1. 环境沿 §2.107。本批只变更前端及说明，后端代码/Schema 未变；1894 项后端回归证据沿 §2.107，不记为本批重跑。
2. 前端 `npm run test -- --reporter=default --reporter=json --outputFile=reports/unit.json` 新增叶子/转义/数组/根值、数字原文、非法与上限、选择描述不含值、重复 key/原型键、旧引擎拒绝用例；最终 **117 项通过，0 失败/跳过**（`web/reports/unit.json`）；typecheck、lint（0 警告）、format:check 通过。
3. 浏览器在真实 MQTT 和 HTTP 来源表单粘贴带私有标记的样本，检查原文大整数、null 不可勾选、只选一项并修改类型、非法及重复成员不破坏已有字段；真实保存/重载后描述保持，监听所有页面请求没有样本标记，两种浏览器存储为空，未消费样本在重新载入时清空。最终 `npm run build` 通过（`/tmp/tenonedge-sample-build-verified.log`）；`PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npm run test:e2e -- --reporter=list,json` **12 项通过**（`/tmp/tenonedge-sample-browser-verified.log`、`web/reports/browser.json`）。`node scripts/verify-results.mjs reports/unit.json reports/browser.json` 非零/无跳过门禁通过。已核看 `sample-fields-mqtt.png` 的表单、候选值与选择状态；截图是人工样本，不是设备数据。

剩余：实时 MQTT/HTTP 抓取及超时/权限/连接复用仍待 T3-04/07；设备复制、CSV、连接测试、客户端证书/信任管理未完成。1000 行 JSON 字段编辑性能未实测；不把单测数量边界当作性能验收。T3-05 图编译/预览、T3-06 应用/回滚仍未完成，总目标保持进行中。未推送、未发版、无实物验证。


### 2.109 T3-04 设备与来源批量复制（2026-10-10）

按功能 §3.2/3.7、总设计 §5 和 §8.1 的 20 份限制扩展既有草稿；复核本机 iotgateway 笔记的保存即热应用反例，不沿用其行为、不复制源码。

- 新增分组 Minimal API `POST /devices/{deviceId}/copy`，使用 Configurator 策略、既有 Cookie/CSRF、明确 ExpectedVersion 和 1—20 份数量。缺设备 404、过期版本 409、非法数量 400；不自动重试过期批次。
- 同一个配置库事务内读取设备/所有来源，分配新的设备和来源 ID，复制四种协议的参数及点位/字段；更新一次全局草稿版本并写一条批次审计。任意副本插入失败或审计失败均回滚全部副本和版本，不写半个批次；无需新表或迁移。
- 副本默认设备停用；来源和点位原有启停标记、名称、分组、UnitId、地址写法、协议参数原样复制。显示名称保持原值，由新 key 区分副本。连接仅保留引用，不复制连接或凭证；独立来源行可分别编辑/删除，原设备不受影响。
- key 由原 key 的有界前缀加 `_copy_` 与雪花 ID 生成，最长 64 且符合业务 key 规则；实际检查草稿与 effective/previous/pending 保留快照的引用，冲突时重新分配。复制不编译/应用，不改变运行指针、不连接设备。
- 设备网页提供 1—20 份选择和复制按钮。有未保存设备修改时禁止复制并提示先保存/重新载入；过期版本保留当前表单/份数，刷新后须明确重新载入。副本重载后显示停用，提示启用前核对 UnitId、Topic/路径和共享连接。

验证：

1. 环境沿 §2.107。定向 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~DeviceDraftCopy|FullyQualifiedName~DeviceCopyKey|FullyQualifiedName~OpenApiDocument'` **3 单元 + 20 集成通过**（`/tmp/tenonedge-copy-targeted4.log`）。包括四协议重建 Host 后保留副本、独立来源行/删除隔离、20 份与并发过期拒绝、空设备、角色/匿名/CSRF、非法数量/缺设备、第二副本和审计注入失败的整批回滚，以及 64 字符 key/草稿与三种保留快照冲突。
2. `dotnet format TenonEdge.sln --no-restore --verify-no-changes`、Release 构建（0 警告/错误）通过；`dotnet test TenonEdge.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=t3-device-copy-verified' --results-directory artifacts/test-results/t3-device-copy-verified --blame-hang-timeout 5m --blame-hang-dump-type none` **1034 单元 + 879 集成 = 1913 项通过，0 失败/跳过**（`/tmp/tenonedge-copy-full-verified.log`）。`verify-test-results.py` 两程序集非零门禁通过。OpenAPI 与 TypeScript 类型由真实 Development Host 导出（`/tmp/tenonedge-copy-api.log`），新增 operationId/路由访问清单明确记录受保护的复制端点。
3. 浏览器实际构造 Modbus 设备和停用的 uint32 点位，验证脏表单禁用复制、版本冲突保留份数、复制两份并读取真实独立来源/参数、刷新后停用、运行版本仍为空。独立场景 **1 项通过**（`/tmp/tenonedge-copy-browser-targeted.log`），已核看 `device-copy.png`；最终 `PLAYWRIGHT_JSON_OUTPUT_NAME=reports/browser.json TENONEDGE_TEST_URL=http://192.168.0.88:18082 npm run test:e2e -- --reporter=list,json` **13 项通过**（`/tmp/tenonedge-copy-browser-verified.log`、`web/reports/browser.json`）；前端 `npm run test -- --reporter=default --reporter=json --outputFile=reports/unit.json` **117 项通过**（`/tmp/tenonedge-copy-web-test-verified.log`、`web/reports/unit.json`）。`npm run build`、typecheck/lint/format:check、`node scripts/verify-results.mjs reports/unit.json reports/browser.json` 全部通过。
4. 中间失败如实保留：新增 operationId 起初未放在契约清单排序位置；完整回归发现 Production/Development 端点保护清单漏增，导致 **1034 单元通过、877 集成通过、2 集成失败**（`artifacts/test-results/t3-device-copy/`、`/tmp/tenonedge-copy-full.log`），已按实际新端点与 Configurator 策略补齐，不放宽原断言。新增测试漏写 if 花括号被构建规范拦截，已修正。浏览器初轮误给 uint16 夹具填多寄存器字节序，真实 API 正确返回 400；改用需要该字节序的 uint32 夹具。随后精确 label 定位失败，可访问树存在正确 combobox，改按角色定位后独立场景通过；未更改产品验证或放宽超时。

剩余：最大点表设备一次复制 20 份的资源/延迟性能未实测；本批仅证明上述软件功能。T3-04 CSV、实时抓取/连接测试及证书信任表单仍待完成；T3-05 图编译/预览、T3-06 应用/回滚未完成。尚不能从网页独立启动完整采集路线，总目标保持进行中。未推送、未发版、无实物验证。


### 2.110 T3-04 设备点表 CSV 导入导出（2026-10-10）

按总设计 §6.1/§8.4、功能 §3.3/3.7 实现所选设备的真实草稿 CSV；详细列定义、手册编号和电子表格文本转义见 [点表 CSV](point-csv.md)。沿用 §2.109 的参考笔记取舍，不复制第三方源码，不新增 NuGet/npm 包。

- `GET/POST /devices/{deviceId}/points/csv` 接到现有管理根分组。GET 导出或提供模板（UTF-8 BOM/CRLF，显式地址写法）；POST 使用全局草稿版本、Configurator/CSRF、类型化模式/编码及 base64 原始字节。网页不先解码 GBK；服务端注册 CodePagesEncodingProvider，UTF-8/GBK 均用严格错误回退。
- 文件最多 5 MiB、10000 数据记录，单元格最多 4096 字符；JSON base64 请求体最多 8 MiB。支持引号、转义双引号、引号内换行、CRLF/LF、调整列顺序；拒绝未知/缺失/重复列和文件内重复点位 key。错误含物理起始行号，不回显文件内容。单点复用正式 Modbus 定义校验，不放宽停用点的地址/类型/字节序约束。
- CSV 描述设备内已有采集组（inputKey）的点位，不自动创建来源或修改周期。覆盖为按设备 key upsert、保留文件外点位，可将同 key 移到另一个已有组；增量只新增，已有 key 跳过但仍检查文件语法/字段/组引用。按 key 字典合并，避免每更新一行就线性扫描整个采集组；先计算最终结果，再检查每组 10000 点限制；整批更新来源行、版本与审计共用一次事务。任何行、组或审计失败均无部分提交；所有行跳过仍推进版本并记一次审计。
- 导出按设备写法显示零基偏移或完整手册编号；导入后均保存协议偏移，功能码与手册前缀须一致。name/unit 对公式起始字符、前导空白/控制/BOM 及单引号做可逆文本保护；原配置值不变。文件编辑时须保留地址/业务 key 的文本属性，避免电子表格自动转为数字或日期。
- 网页提供真实模板/文件下载、编码/模式选择和文件上传、行号错误、增改跳过计数；来源编辑打开时须先保存/关闭，不拿未保存点表冒充导出结果。失败保留文件，成功刷新全局版本相关查询；文件异步读取后检查会话代次，不把旧账号的文件发给新会话。按总设计 §10.3，只读用户不能导出/下载模板或导入；网页不展示 CSV 操作。保存不应用、不读取设备。

验证（软件子场景）：

1. 定向 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~PointCsv|FullyQualifiedName~OpenApiDocument|FullyQualifiedName~ManagementApiAccess'` **21 单元 + 15 集成通过**（`/tmp/tenonedge-csv-targeted3.log`）；此后增加边界与网卡异常回归；最终 `dotnet test TenonEdge.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=t3-point-csv-merge' --results-directory artifacts/test-results/t3-point-csv-merge --blame-hang-timeout 5m --blame-hang-dump-type none` **1059 单元 + 887 集成 = 1946 项通过，0 失败/跳过**（`/tmp/tenonedge-csv-merge-tests.log`），两程序集非零 TRX 门禁通过。Release 构建 0 警告/错误；`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 退出 0。覆盖引号/换行与物理行号、两种地址写法/四功能码、元数据可逆文本保护、GBK、非法定义、10000 行/5 MiB，以及版本并发、角色/CSRF、重启保存、组间移动/增量跳过、审计回滚；新增最终组边界用例包含完整 10000 点导出后覆盖再导入，不等于最大规模性能认证。
2. 前端 `npm run test -- --reporter=default --reporter=json --outputFile=reports/unit.json` **119 项通过**（`/tmp/tenonedge-csv-web-unit.log`）；新增用例核对 BOM/GBK 字节不经 JS 字符解码、base64 分块与 5 MiB 边界。真实 Development Host 导出 OpenAPI/类型（`/tmp/tenonedge-csv-api.log`）。
3. 浏览器独立 CSV 场景通过（`/tmp/tenonedge-csv-browser-boundaries.log`）：下载模板与真实带 BOM 点表、保留 00001/40001、重新导入导出文件、逐行错误整批不改且保留文件、增量/覆盖/跨组迁移、GBK 中文、刷新后导出和运行版本不变；实际 HTTP 同时验证 5 MiB+1 文件返回 400、超过 8 MiB 请求体返回 413，版本不变。最终全量 **14 项通过**（`/tmp/tenonedge-csv-merge-browser.log`、`web/reports/browser.json`），包含新增上传边界；`npm run build`、typecheck/lint/format:check 和 `node scripts/verify-results.mjs reports/unit.json reports/browser.json` 均通过。已核看 `device-csv.png` 的中文字段、导入计数与采集组数量。
4. 初次构建的 params 目标类型推断与代码分析规则已按实际编译器修正；定向 API 契约清单中 import operationId 排序漏调曾失败一次，已保持完整清单并修正顺序，无删除断言。提交前权限复核发现最初只保护导入；按 §10.3 的“导出同样需要权限”统一保护整个 CSV 分组，并补正只读导出/模板 403 用例及文档，不因点表不含凭证就允许只读导出。
5. 中间全量 **1057 单元通过、886 集成通过、1 集成失败**（`/tmp/tenonedge-csv-full.log`、`artifacts/test-results/t3-point-csv/`）：证书生成调用 `LinuxNetworkInterface.GetLinuxNetworkInterfaces()` 时，BCL 抛出 `ArgumentException`，参数 `address`，启动失败。不是 CSV 校验失败，也没有证据证明由何种网卡变化触发。补入与既有 NetworkInformationException 一致的可选 IP SAN 回退，仅捕获该明确地址异常；仍保留 localhost/主机名/回环 SAN，不改变远端 TLS 校验。注入异常单测验证回退及不吞掉其他参数/程序异常。无法枚举时证书不含可选本地非回环 IP；真实 OS 异常触发机制未复现。失败 TRX 保留，最终回归单独记录。

剩余：没有最大规模 CSV 导入的硬件性能验收、人工 Excel/GBK 编辑器兼容性或真实设备验证。T3-04 实时抓取、连接测试与证书信任表单仍待完成；T3-05 图编译/预览与 T3-06 应用/回滚未完成，因此仍不能仅通过网页启动完整采集路线。总目标保持进行中，未推送、未发版。

### 2.111 T3-04 真实测试的并发准入前置（2026-10-10）

本地检查点已整理：`c9492ed` 为 §2.110 中的证书地址枚举容错，`36e6bff` 为点表 CSV；均未推送。继续连接测试前，先按总设计 §8.7 实现 `PreviewOperationBudget`：默认全局 4 个、每会话 2 个，原子即时准入，超限返回空租约而不排队。释放幂等且线程安全，最后一个操作结束即移除会话分区；不持有请求、数据库事务或原始样本。允许显式构造上限，尚未提供管理参数 API。

- 宿主未来须传入已认证会话标识，不允许客户端任意指定分区；所有真实预览/连接测试/测试发送须共用一个宿主级实例。
- 额度由实际 I/O 与清理过程持有，不能因 HTTP 等待取消就提前释放。单测用仍待清理的操作验证这一点，不宣称已验证真实协议取消。
- 定向命令 `dotnet test tests/UnitTests/TenonEdge.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~PreviewOperationBudgetTests`：**5 项通过，0 失败/跳过**，日志 `/tmp/tenonedge-preview-budget-tests.log`。覆盖双重限额、会话隔离与轮换、并发竞争/重复释放、取消等待和非法参数。

追加验证：`dotnet test tests/UnitTests/TenonEdge.UnitTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=t3-preview-budget' --results-directory artifacts/test-results/t3-preview-budget` **1064 单元测试通过，0 失败/跳过**（`/tmp/tenonedge-preview-budget-full.log`）。`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 退出 0（`/tmp/tenonedge-preview-budget-format.log`）。本子项没有重跑集成或浏览器套件，不能将 §2.110 的运行次数计作本节新证据。

本节仅为 T3-04 的运行时准入基础：尚未接入 DI 或管理端点，不能算作网页连接测试已可用；实际出站、分阶段耗时/错误、凭证/TLS、运行连接所有权以及端到端限额验证继续实施。未修改现有运行时网络行为，未做真实设备验证。

### 2.112 T3-04 MQTT 真实连接测试 API 与网页（2026-10-10）

在 §2.111 的准入基础上接入真实 MQTT 连接测试，不将该子项算作全部连接测试或 T3-04 验收完成。参考 MQTTnet 笔记的 StreamProvider、CONNACK 和 TLS 边界；复用已安装库及自有有界通道，不复制第三方源码。

- `POST /connections/test/mqtt` 仅 Configurator/Administrator，要求本地会话与 CSRF；32 KiB 请求体。会话额度按服务端认证票据中的 CSRF 随机值的 SHA-256 分区，不接受请求传入分区；共用宿主单例 `PreviewOperationBudget`，全局 4、每会话 2，超限 429、不排队。租约覆盖真实客户端释放，不在 HTTP 取消等待时另行放开。
- 接收未保存的 MQTT 参数和显式认证选择：匿名、临时用户名/密码、已存凭证。已存凭证只按规范化接收方取一次快照，接收方不匹配/缺失/无法解密返回 409，绝不降级匿名；不要求保存草稿，不跨网络等待持有数据库事务。请求的 ToString 不包含秘密，响应只返回固定分类代码、CONNACK 数值和总耗时。
- 测试使用 MQTT 3.1.1、随机独立 ClientId、CleanSession，忽略正式 ClientId，不订阅、不发布、不创建发送记录；复用正式 `IOutboundConnector` 与 MQTTnet 默认 TLS 校验。上层设置总超时，返回前释放客户端。接收通道在分配正文前仅接受长度 2 的 CONNACK，意外 PUBLISH/其他控制报文立即断开，避免引入无界接收队列。
- 网页 MQTT 编辑器下新增显式测试区域，只对编辑角色显示；允许 key/名称未填时测试非秘密连接参数。临时秘密仅保留组件内存，不进入 Query mutation variables/持久存储；测试结束清空密码，组件卸载中止请求。显示该次测试目标和结果，不自动保存/应用；成功仅说明建立连接，不说明 Topic ACL 可读写。

定向后端 API/适配器/端点契约 **23 项通过，0 失败/跳过**（`/tmp/tenonedge-mqtt-test-api3.log`）；其后补充真实不受信任 TLS 和 3 个传输错误分类用例。第一轮全量 1064 单元 + 906 集成通过，TRX 门禁通过，但格式核对发现对象初始化换行和 using 排序未规范（`/tmp/tenonedge-mqtt-probe-format.log`）；已只格式化本次文件，继续最终回归。真实 Mosquitto 验证成功、错误/匿名凭证拒绝、独立 ClientId；TCP 软件夹具验证静默超时、取消关闭、意外 PUBLISH 拒绝。API 验证角色/CSRF、真实出站拒绝、已存接收方绑定、临时认证、全局/会话限额与草稿不变。最初测试方法公开了内部 UserRole 参数，编译失败；已改为字符串输入转换，不改生产可见性（`/tmp/tenonedge-mqtt-test-api.log` 保留）。

最终后端：`dotnet build TenonEdge.sln -c Release --no-restore` 0 警告/错误；`dotnet test TenonEdge.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=t3-mqtt-test-final' --results-directory artifacts/test-results/t3-mqtt-test-final --blame-hang-timeout 5m --blame-hang-dump-type none` **1064 单元 + 906 集成 = 1970 项通过，0 失败/跳过**（`/tmp/tenonedge-mqtt-test-final-full.log`），两程序集 TRX 门禁通过；`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 退出 0（`/tmp/tenonedge-mqtt-test-final-format.log`）。包括真实 Broker 旧 ClientId 持续 PING 及重新连接后的持久会话仍在。最后补齐 403/409/413/429 OpenAPI 响应元数据后，`dotnet test tests/IntegrationTests/TenonEdge.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~MqttConnectionTest|FullyQualifiedName~OpenApiDocument|FullyQualifiedName~ManagementApiAccess'` **27 项通过**（`/tmp/tenonedge-mqtt-test-contract-final.log`），重新导出 API/类型成功（`/tmp/tenonedge-mqtt-test-api-export-final.log`）。

浏览器独立场景 **1 项通过**（`/tmp/tenonedge-mqtt-test-browser.log`）：真实 Host 按当前策略拒绝回环目标，未保存表单无需 key/名称即可测试；密码清空，草稿版本/内容和浏览器持久存储不变。已查看 `mqtt-connection-test.png`，结果与目标可读、临时密码为空。前端最终全量 **119 Vitest + 15 Playwright 通过**（`/tmp/tenonedge-mqtt-test-web-unit.log`、`/tmp/tenonedge-mqtt-test-web-final.log`），构建、typecheck/lint/format:check 和报告非零/零跳过门禁均通过。真实 Broker 证据来自后端测试，不把浏览器的策略拒绝冒充成功接入。OpenAPI/类型从实际 Development Host 导出（`/tmp/tenonedge-mqtt-test-api-export.log`）。

剩余：目前只返回总耗时，功能 §2.4 的 DNS/TCP/TLS/认证/协议逐阶段耗时尚未实现；证书失败当前统一分类，尚未分不受信任/过期/主机名。网页信任/客户端证书、HTTP/Modbus 测试、实时抓取仍待继续；既有运行时凭证轮换隔离问题仍按 §2.104 跟踪。不是全功能连接测试验收，不是实物/现场网络兼容性验证。图编译与应用/回滚仍未完成，目标保持进行中。

### 2.113 T3-04 MQTT 分阶段诊断与证书失败解释（2026-10-10）

继续 §2.112 的真实测试，不新增协议或依赖，不创建第二套运行时。读取固定 MQTTnet 源码的 `MqttTcpChannel` TLS 默认回调与证书校验事件，确认默认接受条件为 `SslPolicyErrors.None`；本次仅添加解释，不复制源码或放宽信任条件。

- 出站连接器增加按调用观察解析/建连边界的重载，原调用仍走同一解析、IP 筛选和连接算法；不把观察到的域名再交给 Socket 解析。未实现阶段观察的连接器保留兼容调用，但其解析阶段/耗时显示未知，不编造成功。回调不含主机、地址或凭证内容，不是单例全局事件。
- MQTT 有界通道在真实 TCP/TLS Connect 完成后通知诊断，不能把证书验证回调发生就当作 TLS 完成。每次测试只保留固定五阶段结果；单调时钟测区间，失败终止当前阶段，后续为“未到达”，未启用 TLS 为“未启用”。冻结结果后忽略迟到回调。
- MQTT 3.1.1 认证与协议握手的可观察结果均来自同一个 CONNACK：返回同一 `timingGroup=mqtt.connack`，两个阶段共享实测区间，界面明确不可相加。只有成功 CONNACK 确认认证成功；认证拒绝为失败，超时/其他协议拒绝时认证为“未能确认”，不杜撰独立认证耗时或成功状态。
- TLS 回调仍严格要求 `SslPolicyErrors.None`；不关闭主机名、证书链或吊销检查，不导入系统信任。失败仅保留固定代码，不保留证书内容。多重错误按缺证书、主机名、吊销、有效期、信任链、吊销状态不可确认、其他错误确定首要原因；界面展示首要问题不表示其他问题已排除。证书有效期按 OS 校验时间解释，不用业务可注入时钟改写验证结果。
- API 返回阶段列表，OpenAPI/前端类型及网页阶段表同步；真实出站拒绝显示解析成功、TCP 失败、后续未到达，而非整体五阶段都失败。未记录临时密码、外部响应正文或证书正文。

验证：定向 `dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~MqttConnection|FullyQualifiedName~OutboundConnector|FullyQualifiedName~OpenApiDocument|FullyQualifiedName~ManagementApiAccess'` **5 单元 + 39 集成通过，0 失败/跳过**（`/tmp/tenonedge-mqtt-stages-targeted4.log`）；此后增加两个解释/阶段边界单测，最终完整结果见下。初次编译被 CA1068（CancellationToken 非末参）和 CA1861（循环内常量数组）拒绝，已调整参数顺序/静态测试常量，无禁用分析器（`/tmp/tenonedge-mqtt-stages-targeted*.log` 保留）。

真实本机 TLS 夹具使用每次生成的自签证书，通过真实 SslStream 握手分别得到不受信任、主机名不符、过期、尚未生效四种失败；均断开且服务器未读到 MQTT 应用字节，因此没有在 TLS 校验通过前发送测试凭证。这不是实物设备测试，不等于已完成客户端证书/自定义 CA 功能。真实 Mosquitto 与既有串行测试核对协议阶段成功/失败；注入时钟验证 5/7/11/13 ms 阶段边界以及 CONNACK 共享计时，不能把这些固定时间当作性能实测。

网页：从实际 Development Host 重新导出 OpenAPI/类型（`/tmp/tenonedge-mqtt-stages-api-final.log`），`npm run build`、lint、format:check 通过；**119 Vitest + 15 Playwright 通过**，非零/零跳过报告门禁通过（`/tmp/tenonedge-mqtt-stages-web-unit.log`、`/tmp/tenonedge-mqtt-stages-browser.log`）。浏览器核对真实策略拒绝的五阶段状态，已查看阶段表截图 `mqtt-connection-test.png`；不把未到达 TLS/认证/协议显示成已验证。

全量回归发现测试调度问题，未隐去失败：`artifacts/test-results/t3-mqtt-stages/` 与 `t3-mqtt-stages-final/` 两轮均为 **1071 单元通过，911 集成通过、1 集成失败**。分别在既有 `NModbusLeavesAFollowingTcpFrameForTheNextRequest`、`FluentModbusAcceptsATcpResponseFromAnotherUnit` 的首次读取超过 300 ms（不是待验证的后续旧帧断言失败）。第一个用例原样单独运行 1 次再连续 10 次均通过；Modbus 旧帧类与本次 MQTT 类的缩小组合 **35 项通过**（`/tmp/tenonedge-modbus-stale-diagnostic.log`、`/tmp/tenonedge-modbus-stale-repeat-*.log`、`/tmp/tenonedge-modbus-mqtt-cohort.log`）。两种探针直接调用候选库，不走本次增加观察回调的正式出站连接器。证据支持全量负载相关的时序窗口问题，但未取得精确线程调度链，不宣称已定位 OS/库内部原因。

为使协议比较不同时承担其他测试的调度压力，仅将该旧帧比较类放入 `DisableParallelization=true` 的 xUnit collection；保留全部断言、300 ms 超时、故障注入和候选库行为，不修改正式运行时、不全局关闭并行、不跳过用例。隔离后完整回归 **1071 单元 + 912 集成 = 1983 项通过，0 失败/跳过**，两程序集 TRX 门禁通过；之前失败 TRX 保留。实际命令为 `dotnet build TenonEdge.sln -c Release --no-restore`（0 警告/错误，`/tmp/tenonedge-mqtt-stages-isolation-build2.log`）和 `dotnet test TenonEdge.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=t3-mqtt-stages-isolated' --results-directory artifacts/test-results/t3-mqtt-stages-isolated --blame-hang-timeout 5m --blame-hang-dump-type none`（`/tmp/tenonedge-mqtt-stages-isolated-full.log`）。这是一次隔离后的全量通过，不是对任意负载稳定性的认证。collection 标识类最初以 Collection 结尾触发 CA1711，改名后构建通过；不抑制分析规则。`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 退出 0（`/tmp/tenonedge-mqtt-stages-isolated-format.log`）。

剩余：吊销、离线 CRL/OCSP、过期中间证书、有效受信任 TLS 的诊断展示尚未逐项实测；不将分类代码存在等同于这些场景通过。HTTP/Modbus 测试、客户端证书/信任表单、实时抓取继续实施；T3-05 图编译/预览、T3-06 应用/回滚仍未完成，完整网页配置目标尚未达到。

### 2.114 T3-04 HTTP 真实连接测试与共享会话串行化（2026-10-10）

沿用功能 §2.4、总设计 §5/§6.6/§8.7，不新增平台客户端或 Node 运行服务。回读 IoTEdge 笔记的 HTTP/秘密与“界面已有不等于实现”反例，只借取舍，不复制源码。核对实际 .NET 10.0.12 reference XML 的 `SocketsHttpHandler.PlaintextStreamFilter` 与 `PlaintextStream`，经真实网络测试确认可以在 TCP/TLS 完成后观察协议阶段，而非仅根据 API 名称推定行为。

- `POST /connections/test/http` 仅 Configurator/Administrator，本地会话/CSRF 与 MQTT 共用同一准入方法和宿主单例额度（全局 4、每会话 2）；请求体 128 KiB。未保存连接仅领取进程内唯一临时 ID，不插入草稿；已保存连接必须是 HTTP 草稿。匿名、临时四类凭证、已存凭证由显式认证模式选择；已存凭证按规范化完整基础地址绑定，接收方改变/凭证缺失返回 409，不降级匿名。
- 测试通过既有 `ISharedConnections` 的同编号/配置身份借用 `HttpConnectionSession`，与采集/输出排入同一串行执行器；不是旁路另建同编号会话。借用配置冲突、释放中或连接数上限返回 409。总期限包括串行等待；等待超时显示“本次未联网”，所有网络阶段保持未到达。
- 显式 HTTP 测试在串行租约内先释放该会话的空闲客户端，再建立本次测试客户端；测试结束后释放它并恢复一个尚未联网的普通客户端。任一时刻只有一个活动客户端，不中断同一执行器内的在途读取/发送；网页事前提示重建空闲客户端。这样 DNS/TCP/TLS 阶段是本次实测，不把复用旧连接伪装成重新握手。
- 只对规范化基础地址发送一次 HTTP/1.1 GET，复用正式 `OutboundHttpTransport`、出站校验、凭证格式与编码。禁用代理/Cookie/重定向，最多 64 KiB 响应头；`ResponseHeadersRead` 后只返回状态码，不读取/显示正文，关闭时也不排空正文。证书回调保持 `SslPolicyErrors.None`，不关闭现有校验。四类临时秘密仅在请求内存中使用，不记录在结果、审计或草稿中。
- 收到 2xx 时连接测试 Success 为 true；其他 HTTP 状态仍明确显示，协议阶段成功表示确实收到有效响应头，不表示业务成功。401/403 显示认证/授权拒绝；携带凭证但收到其他状态仍不能证明服务器校验了凭证，认证显示“未能确认”；未使用凭证且未被拒绝则“未启用”。复用的阶段结果/证书解释移入 Adapters/Diagnostics，MQTT 的认证语义保持不变。`ConnectionTestStep` 与 `ConnectionTestAuthentication` 为共用 OpenAPI 类型，既有 MQTT JSON 字段/枚举值不变。
- 网页支持未保存表单、临时 Basic/Bearer/API Key Header/Query、已存凭证、实际状态码和阶段表；HTTP/MQTT 共用阶段展示。秘密不进入 mutation variables 或浏览器持久存储，完成后清空；重新开启编辑器递增测试表单代次，放弃新建表单后也不会继承旧临时秘密/结果。

定向验证：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~HttpConnectionTest|FullyQualifiedName~MqttConnection|FullyQualifiedName~ConnectionCertificate|FullyQualifiedName~OpenApiDocument|FullyQualifiedName~ManagementApiAccess'` **7 单元 + 55 集成通过，0 失败/跳过**（`/tmp/tenonedge-http-test-targeted3.log`）。包括四类凭证线上编码/查询转义、200/302/401/403/500 状态、巨大但不发送的正文不阻塞返回、不跟随重定向、策略拒绝、超时/取消、共享队列等待与配置冲突。既有会话借用回归先真实采集，再测试，再采集；给测试工厂传入禁止联网的连接器仍成功，证明实际使用原会话而非另开工厂；观测旧 TCP 流关闭及后续采集成功。

真实本机 SslStream 夹具同时覆盖 HTTP/MQTT 的不受信任、主机名不符、过期、尚未生效，失败时服务器没有读到应用层凭证字节；不是外部真实设备证据。网页独立场景 **1 项通过**（`/tmp/tenonedge-http-test-browser.log`）：真实 .NET Host 连接 Playwright 进程中的回环 HTTP 服务器，看到一次 GET/Bearer、HTTP 204 与认证未确认；再返回 302 且不发送声明的巨大正文，网页仍及时返回、服务器没有第二个重定向请求；草稿和浏览器持久存储不变。完整浏览器回归也覆盖重新打开新建表单清除临时状态；已查看 `web/test-results/http-connection-test.png`，实际 HTTP 302 与五阶段结果展示正常。

完整回归：Linux x64、SDK 10.0.401 / .NET 10.0.12、Release；`dotnet build TenonEdge.sln -c Release --no-restore`、`dotnet test TenonEdge.sln -c Release --no-build --no-restore`（TRX：`artifacts/test-results/t3-http-test/`）及 TRX 非零/无跳过门禁通过，**1071 单元 + 934 集成 = 2005 项通过，0 失败/跳过**；`dotnet format TenonEdge.sln --verify-no-changes --no-restore` 通过。日志：`/tmp/tenonedge-http-test-final-build.log`、`/tmp/tenonedge-http-test-full.log`、`/tmp/tenonedge-http-test-final-format.log`。前端 Node 22.23.2 / npm 11.21.0，`test` **119 项通过**、`test:e2e` **16 项通过**，typecheck/lint/format:check 与非零/无跳过报告门禁均通过；报告 `web/reports/unit.json`、`web/reports/browser.json`，日志 `/tmp/tenonedge-http-test-web-unit.log`、`/tmp/tenonedge-http-test-web-full.log`。剩余：Modbus 连接测试/读取预览、实时抓取、信任及客户端证书表单仍待实现；TLS 吊销/离线状态等 §2.113 未验证项未借本轮改称通过。HTTP 并不自动验证业务响应规则或平台绑定。T3-05 图编译、T3-06 应用/回滚仍未完成，完整网页配置目标未达成。

### 2.115 T3-04 Modbus 连接测试：共享会话内核子场景（2026-10-10）

接续 §2.114，按功能 §2.4 只测到 TCP 建连，不发送 Modbus 读取/写入或探测报文；不是设备读取预览交付。回读参考总索引及 NModbus 笔记，仅借连接所有权/串行化约束，没有复制源码。

- `ModbusConnectionSession.TestConnectionAsync` 由同一个共享串行租约调用；新建传输使用正式出站连接器和连接超时。TCP / RTU over TCP 均可在随后正式读取中复用该流。
- 已有流返回 `connection.reused`，连接阶段标为 `reused`、没有新建连耗时；只表明借用现有流，**不证明远端此刻仍存活或从站可读**。TLS、认证、协议阶段明确跳过。
- 运行时连接退避中不绕过重连期限；等待租约时取消不关闭健康流。建连超时返回 timeout，调用者取消向上传播；地址解析、拒绝连接与出站拒绝单独分类。
- 本次仅实现并验证内部会话能力，没有注册 API 或新增网页按钮。后续 API 须处理测试先于运行时取得会话时的启用 UnitId 集合初始化，不能用占位 UnitId 污染正式采集的健康统计；准入额度与包含排队的总期限由 API/编排层补齐。设备真实读取、结果解码与网页仍未交付。

Linux x64、SDK 10.0.401 / .NET 10.0.12、Release 定向回归：`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~Modbus|FullyQualifiedName~ConnectionTestTrace|FullyQualifiedName~HttpConnectionTest|FullyQualifiedName~MqttConnectionTest' --logger trx --results-directory artifacts/test-results/t3-modbus-test-session`，**207 单元 + 154 集成 = 361 项通过，0 失败/跳过**；TRX 非零/无跳过门禁通过，日志 `/tmp/tenonedge-modbus-test-regression.log`。`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 通过（`/tmp/tenonedge-modbus-test-format-verify.log`），`git diff --check` 通过。本子项未重跑完整后端/前端套件，完整基线仍见 §2.114。首次会话定向测试 16 项通过，0 失败/跳过（`/tmp/tenonedge-modbus-test-session.log`）；此前一次构建被 CA1861 拒绝，调整测试常量集合写法后通过，不将失败尝试计作通过。真实网络子场景使用本机模拟从站，DNS/拒绝连接分类为注入错误测试，超时使用可控时钟；不宣称实物兼容。

### 2.116 T3-04 Modbus 未保存参数测试 API 与网页（2026-10-10）

接续 §2.115，补齐连接页测试入口；只建立 TCP 传输，不把建连当作设备读取验证。没有新增依赖、模拟产品数据或旁路连接。

- `POST /connections/test/modbus` 仅 Configurator/Administrator，使用本地会话、CSRF、128 KiB 请求体限制及 MQTT/HTTP 共用的全局 4 / 每会话 2 准入额度。未保存表单用临时唯一编号，不插入配置库；指定编号必须存在且为 Modbus 草稿，配置与现有共享会话不一致返回 409。
- `ModbusConnectionTest` 借用正式 `ISharedConnections` 串行队列。总连接期限包含排队，进入会话后只使用剩余预算；等待到期与网络超时分别报告。取消后等待真实操作退出，最后租约负责销毁会话，不后台遗留连接。
- 测试先创建会话时启用 UnitId 集合保持未绑定，而非填入假从站；正式运行单元取得租约后，在同一串行执行器中绑定完整集合，再进入读取循环。同集合重复绑定不清空健康历史，不同集合拒绝，未绑定时不能正式读取；不混合两套计划的成员。
- 网页支持 TCP / RTU over TCP 未保存参数，显示目标、总耗时与五阶段结果。已有流显示“复用现有连接，未重新检测远端存活”，协议读取明确“不执行”；关闭页面取消请求，新建编辑器清除旧结果。没有从连接页发送读取或写入报文。

定向验证 **36 单元 + 34 集成通过，0 失败/跳过**（`/tmp/tenonedge-modbus-api-targeted2.log`），涵盖角色/匿名/CSRF、非法参数、不存在草稿、运行参数冲突、共用额度、排队到期、未绑定会话接管与真实读取。初次编译提示 nullable 解引用，修正读取路径的非空流分析后通过；不把失败构建计作通过。新增同集合重复绑定保留超时历史回归随全量执行。完整回归：Linux x64、SDK 10.0.401 / .NET 10.0.12、Release，`dotnet build TenonEdge.sln -c Release --no-restore` 通过；`dotnet test TenonEdge.sln -c Release --no-build --no-restore --blame-hang-timeout 5m --blame-hang-dump-type none --logger trx --results-directory artifacts/test-results/t3-modbus-test-api` **1071 单元 + 951 集成 = 2022 项通过，0 失败/跳过**，TRX 非零/无跳过门禁通过。日志 `/tmp/tenonedge-modbus-api-build.log`、`/tmp/tenonedge-modbus-api-full.log`。`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 通过（`/tmp/tenonedge-modbus-api-format-verify.log`），`git diff --check` 通过。

前端 Node 22.23.2 / npm 11.21.0 / Chromium 156.0.8078.4，经 `http://192.168.0.88:18082`：build、typecheck、lint、format:check 通过；Vitest **119 项通过**，Playwright **17 项通过**（42.5 秒），报告非零/无跳过门禁通过；日志 `/tmp/tenonedge-modbus-api-web-build.log`、`/tmp/tenonedge-modbus-api-web-unit.log`、`/tmp/tenonedge-modbus-api-web-full.log`，报告 `web/reports/unit.json`、`web/reports/browser.json`。Modbus 浏览器场景在真实网关上临时放行本机 TCP 夹具，分别选择两种帧格式，观察到两次连接及关闭、**0 个协议字节**；草稿未改变，新建表单旧结果清空。策略恢复、夹具关闭；已查看 `web/test-results/modbus-connection-test.png`，结果表正确区分建连与不执行协议读取。

浏览器最初两次独立执行失败（`/tmp/tenonedge-modbus-api-browser.log`、`/tmp/tenonedge-modbus-api-browser2.log`）：trace 显示精确 `getByLabel` 在隐式标签的下拉框上超时，尚未发测试请求；第一次修正命令使用了错误相对路径，第二次仍运行旧定位。改用页面实际可访问名称的 `getByRole('combobox', ...)` 后，上述完整 17 项通过；未放宽断言/超时或重试。

剩余：设备页真实读取/字节序预览、实时抓取、证书表单，以及 T3-05 图编译和 T3-06 应用/回滚尚未完成；本机从站软件验证不代表实物兼容验收。

### 2.117 T3-07 Modbus 真实读取预览内核（2026-10-10）

按功能 §4.1、总设计 §5/§8.7/§10.1，在既有连接测试之后实现一次读取内核，不增加周期采集器或输出发送。API、设备页入口与点选字节序写草稿将在后续接入，本节不是网页预览完整验收。

- `ModbusReadPreview` 使用正式点表验证、连续块规划和解码器；每块经同编号/配置身份的共享串行租约执行，不另开并行连接。TCP UnitId 0—255、RTU over TCP 1—247；只允许 01/02/03/04。
- `ReadPreviewAsync` 与正式采集共用读取/取消/RTU 静默隔离代码，但预览草稿 UnitId 不加入正式采集成员，超时/成功不计作正式轮询健康样本。物理连接损坏仍关闭并退避；新物理连接仍清除旧代次的连接健康状态。预览期间实际请求间隔与读超时保持正式参数。
- 返回按块关联的采集时间、功能码、偏移/数量、是否请求、异常码及失败原因；点位包含零基偏移、1 基寄存器号、6 位完整手册编号、原始寄存器十六进制、寄存器取位二进制及解析值。32/64 位同时解码 ABCD/CDAB/BADC/DCBA；值按原文字符串附类型返回，超过 JavaScript 安全整数范围明确标记，NaN/Infinity 显示 nonFinite 而不是伪造值。
- 总期限默认 30 秒、校验范围 100 毫秒—60 秒，包含共享队列等待。到期保留本次已完成块，未完成块没有值；调用者取消向上传播，等待真实 I/O 退出后才释放租约。设备异常可继续其他块；一次超时/断连后不重复请求同组后续块，返回未执行的失败条目。
- 显示结果按逐行/逐块实际 UTF-8 JSON 字节计量，保留包装开销，最多 64 KiB；满后停止后续联网并标注 Truncated 和总点数。点表仍按正式上限校验（最多 10000 点），不会把截断结果当作完整路线样本。未登记 API 前，不绕过宿主既有并发额度；宿主准入及整台设备编排仍待接入。

定向 `dotnet test tests/IntegrationTests -c Release --no-restore --filter FullyQualifiedName~ModbusReadPreviewTests` **12 项通过，0 失败/跳过**（`/tmp/tenonedge-modbus-read-targeted2.log`）。真实本机 TCP/RTU 从站覆盖原始字、四种顺序、64 位精度、bit、coil、异常码 02、未知草稿 UnitId 与运行时共用连接且不改变成员、1 万点响应预算、非有限值、取消退出；可控时钟覆盖正在读取及排队期限。首轮 9 通过/1 失败（`/tmp/tenonedge-modbus-read-targeted.log`）：RTU 夹具错误地给会话使用未推进的 FakeTimeProvider，静默隔离不能到期，最终触发外层 30 秒期限；仅改该真实网络夹具为 System 时钟后通过，未改产品期限或断言。协议回归：Linux x64、SDK 10.0.401 / .NET 10.0.12，`dotnet test TenonEdge.sln -c Release --no-restore --filter 'FullyQualifiedName~Modbus' --logger trx --results-directory artifacts/test-results/t3-modbus-read-preview` **200 单元 + 138 集成 = 338 项通过，0 失败/跳过**，TRX 非零/无跳过门禁通过（`/tmp/tenonedge-modbus-read-regression.log`）。本内核子项未重跑完整前后端套件，完整基线仍见 §2.116。`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 与 `git diff --check` 通过（`/tmp/tenonedge-modbus-read-format-verify.log`）。

未验证：实物设备兼容；没有设备页或 API 的预览验收。整台设备多组汇总、连接层详细失败原因、网页采样结果与字节序回写仍需后续交付，图编译/应用目标仍未完成。

### 2.118 T3-07 Modbus 组/设备读取 API、网页与字节序采用（2026-10-10）

接续 §2.117，按功能 §4.1、BE 的事务/归属规范、FE-AT 的真数据要求接入设备来源页。没有新增 Node 运行服务或协议依赖；浏览器测试里的 TCP 从站仅是测试夹具。

- `POST /devices/{deviceId}/preview/modbus` 使用 Configurator/Administrator、本地会话/CSRF、16 MiB 点表请求体限制及既有全局 4 / 每会话 2 额度。短事务同时检查 expectedVersion、设备类型、InputId 的设备归属及对应连接，再返回纯快照；联网时不持有配置库事务。
- 请求 Source 提供未保存点表；否则 InputId 选择已保存的一组，省略两者读取全部启用且有启用点位的组。UnitId/连接来自同版本已保存设备，页面明确提示；没有隐式保存或应用。点表校验、空组、越界与过期版本均在读取前拒绝。
- 多组编排保持组边界（相邻地址也不跨组合并），外围租约保留同一个物理会话；整个请求共用 30 秒期限、64 KiB 显示容量。各组只领取剩余时间/字节预算；内部剩余期限可低至 1 毫秒，不重新领取 30 秒。全部组先校验再联网；后续组到期/容量不足时返回总组数与未完成标记，保留已完成组结果。
- 页面支持“整台设备真实读取”和“当前采集组真实读取”，后者使用当前未保存点表。展示原始寄存器、三种地址表示、类型附原文值、质量/失败块/异常码含义、时间与四种字节序。超出安全整数范围提示下游可映射字符串，不在浏览器 Number 转换业务值；仅小范围块索引等元数据转换为 Number。
- 已保存采集组可显式“采用字节序并保存当前组草稿”，复用原草稿版本检查与保存操作，不应用；同时保存当前组其他编辑的行为在按钮/说明中明确。采样后点表发生变化时禁止采用旧样本；新建组先保存再采用。关闭组件取消在途读取，结果只留内存、不进入浏览器存储。

定向集成 **29 项通过，0 失败/跳过**（`/tmp/tenonedge-modbus-preview-api-targeted2.log`）：实际从站线序、角色/匿名/CSRF、过期版本、错误 InputId 归属、非法点表、共用准入额度、未保存点表不改库、整台设备只建一条物理连接/两组不合并，以及多组共用期限与显示预算。首个 solution 过滤运行的 UnitTests 无匹配，不计为通过；有效集成结果为 26 项，新增多组用例后为上述 29 项。

浏览器独立场景 **1 项通过**（`/tmp/tenonedge-modbus-preview-browser.log`，8.9 秒）：真实 Host 按网页请求读取本机 TCP 从站，两组只建立一次连接；网页准确显示 `1152939101356429315` 原文与原始 `1000 1001 1002 1003`，预览后草稿未改变；选择 CDAB 后只有当前组草稿字节序改变，其他组不变，未追加网络读取，浏览器 local/sessionStorage 为空。实际查看 `web/test-results/modbus-read-preview.png`，多组与单组结果表显示正常。出站策略恢复，夹具连接关闭。最初前端构建发现生成 schema 的块索引为 number|string，显式转换小范围索引后构建通过；未转换业务数值。

完整后端：Linux x64、SDK 10.0.401 / .NET 10.0.12、Release，`dotnet build TenonEdge.sln -c Release --no-restore`（0 警告/错误）；`dotnet test TenonEdge.sln -c Release --no-build --no-restore --blame-hang-timeout 5m --blame-hang-dump-type none --logger trx --results-directory artifacts/test-results/t3-modbus-preview-api` **1071 单元 + 972 集成 = 2043 项通过，0 失败/跳过**，TRX 门禁通过；`dotnet format TenonEdge.sln --no-restore --verify-no-changes` 通过。日志 `/tmp/tenonedge-modbus-preview-api-build.log`、`/tmp/tenonedge-modbus-preview-api-full.log`、`/tmp/tenonedge-modbus-preview-api-format-verify.log`。

首次前端全量 **119 Vitest + 18 Playwright 通过**；检查时补充无预览结果前不反复序列化万点表、非法编辑用字段错误反馈，并新增浏览器“改变周期后禁止采用旧样本、恢复后才允许采用”断言，最终前端 **119 Vitest + 18 Playwright 通过，0 失败/跳过**（45.1 秒），报告非零/无跳过门禁通过。Node 22.23.2 / npm 11.21.0 / Chromium 156.0.8078.4；`npm run build`（含 tsc）、lint、format:check 通过；实际使用 `npx --yes npm@11.21.0`，浏览器地址 `http://192.168.0.88:18082`。日志 `/tmp/tenonedge-modbus-preview-web-build-final2.log`、`/tmp/tenonedge-modbus-preview-web-unit2.log`、`/tmp/tenonedge-modbus-preview-web-full2.log`；报告 `web/reports/unit.json`、`web/reports/browser.json`。`git diff --check` 通过。剩余：MQTT/HTTP 实时采样、预览连接层更细原因分类、真实设备兼容；T3-05 图编译与 T3-06 应用/回滚仍未完成，不能据本节宣称完整可视化配置目标达到。

## 3. 验收编号登记

| 编号 | 子场景 | 验证状态 | 证据 | 剩余 |
|---|---|---|---|---|
| BE-AT-01 | 已创建工程符合依赖方向；检查能发现违规（含经导入文件加入的引用）；无占位工程 | 通过（本地，T1-01 范围）：四个工程都已建立并受同一规则检查——Abstractions 锁文件中没有任何包；Runtime 只有 Microsoft.Data.Sqlite 与日志抽象（含传递依赖），不含 ASP.NET Core、ORM 与协议库；Adapters 不引用任何包 | F5，§4.3 实验 A，§2.10、§2.14 | 新增工程时由同一测试持续回归 |
| BE-AT-02 | 分组 Minimal API；ProblemDetails；operationId 稳定且唯一 | 通过（本地，T0-B 范围）：401（带质询头）、403、400 字段错误、404、405、409、429、500 均为 ProblemDetails，超出请求体上限返回 413（未核对响应体格式）；真实认证与角色授权；operationId 清单由测试固定 | F6，§2.7—§2.9 | 新增端点时由同一测试持续回归 |
| BE-AT-03 | 作用域与构建时校验；运行时宿主只创建一次；请求结束后不使用已释放服务；资源由明确的所有者释放 | T1-01 运行时部分通过（本地）：<br>• 作用域与构建时校验开启；<br>• 协调者是唯一的运行时后台服务，与按具体类型取得的是同一个实例，运行时宿主只有一个；<br>• 运行单元由监督者持有，同一标识同一时刻最多一个实例，停止超时的单元继续占用标识；<br>• 单元的上下文只含进程级对象；<br>• 写连接只归写者，读取用短连接，停机时关闭读连接池；<br>• 没有登记 IHttpContextAccessor | F6，§4.3 实验 D，§2.10、§2.14 | 连接共享、借用与租约：T2-01 |
| BE-AT-04 | 本地与 CI 使用同一 SDK 和测试运行器；locked restore；Release 构建 | 本地通过（干净副本、空缓存；locked restore 含 linux-x64 平台相关的 NuGet 资产，运行时包的版本由固定的 SDK 决定）；远端 CI 未执行 | F1—F3，§4.8 C6、C10、C16 | 以实际 CI 运行补证 |
| BE-AT-05 | xUnit 用例实际执行；隔离辅助；真实进程测试 | 通过（本地，T0-C 范围）：API 与存储测试各用独立数据目录；TLS、转发头、限流、重启、锁、日志脱敏有真实进程测试；MQTT 用真实 Broker（本机执行），Modbus 用进程内模拟从站（CI 执行）；发布物在干净容器中启动，挂卷与未挂卷两种情形都已自动验证 | F5、F6，§4.6，§4.8 | 真实磁盘写满未复现（以移走数据表模拟，§2.9）；真实 Broker 的 CI 场景随 MQTT 适配器（§6 决定 46） |
| BE-AT-06 | linux-x64 自包含发布在干净环境启动 | 通过（本地）：自包含发布物在未预装 .NET 的干净容器中启动；健康检查、首次设置、重启与换安装路径后的持久化、单实例、数据卷检查、低版本 SQLite 均按预期 | §2.13，§4.8 C7、C10、C16 | 远端 CI 未执行；正式安装包与镜像：T5-04 |
| BE-AT-07 | 阶段记录 | 本批已记录 | 本文 | 每批更新 |
| V-01 | 环境记录；自包含发布在没有 .NET 的干净容器中运行；后台服务未处理异常的默认行为 | 通过（本地）：已记录环境；linux-x64 自包含发布物在没有 .NET 的 runtime-deps 镜像中启动、服务并正常停止；后台服务未处理异常时宿主停止，进程退出码为 0 | §4.1，§2.10，§2.13 | 远端 CI 未执行；退出码改为非零：T1-01（§6 决定 45） |
| AT-10 | 单实例子项：同一数据目录的第二个实例被拒绝；NodeId 在重启后保留 | 通过（本地；自包含发布物中复验） | §2.4、§2.6、§2.13 | — |
| AT-10 | T1 子项：有效配置在重启后保留；提交前中断加载原版本；已提交的待发记录在写者重启后保留 | 部分通过（本地）：种子生成的有效版本在重启后照常激活；提交前中断的 pending 版本标为“未生效：进程中断”、按原版本启动；queue.db 中已提交的记录在写者重启后原样读回 | §2.14、§2.16 | 强制终止进程后的待发记录（FS-14）：T1-08；提交后中断加载新版本：T3-06 |
| V-06 | NoDrive 加 Microsoft.Data.Sqlite；逐物理连接设置并回读 PRAGMA；并发读写；版本化迁移；传递依赖清单与 D-09 试验 | 通过（开发环境与自包含发布物，本地）：回读为 wal 与 FULL；PRAGMA 挂载点可用，不需要改用长连接；发布物在干净容器中启动时逐连接回读核对通过；清单与结论已写入 `docs/reference-notes.md` | §2.5，§2.13 | D-09 待维护者确认 |
| V-08 | 密钥环持久化到数据目录；固定应用名；换安装路径、重建挂卷容器后可解密；容器中数据目录不在挂载卷上时拒绝启动运行时；合并密钥环；记录未配置密钥加密器的警告如何处理 | 通过（开发环境与自包含发布物，本地，含手动容器验证）：发布物换安装路径重启后，重启前的会话 Cookie 仍有效；数据目录不在卷上时就绪 503 | §2.6，§2.13，§4.6 B5，§6 决定 17 | 备份恢复中的密钥环合并流程：T5-02 |
| V-13 | 雪花 ID 生成器的来源记录 | 通过：许可允许复制（Apache-2.0），已记录 | `docs/reference-notes.md` §3 | — |
| AT-24 | 设置窗口 30 分钟，未初始化时重启重新开放；初始化后不再开放；初始化记审计 | 通过（本地）；系统信息接口返回首次设置的时间与来源 IP，窗口开放时记运行事件（T0-09） | §2.7、§2.9 | 首页显示属于前端：T3 |
| FS-09 | 两人同时提交首次设置只成功一次 | 通过（本地，8 个并发请求） | §2.7 | — |
| GW-01 | 首次访问时设置管理员；不存在默认账号 | 通过（本地，API 部分） | §2.7 | 网页部分在 T3 |
| AT-19 | 密钥子集：重启、更换安装路径、重建容器后仍可解密 | 通过（本地，最小版本） | §2.6 | 凭证业务随 T1；备份恢复部分属于 T5-02 |
| V-07 | 启动记录实际 SQLite 版本与编译选项；不低于 3.51.3；在自包含发布与 Docker 镜像中确认 | 通过（本地，开发环境与自包含发布物）：干净容器中实际加载 3.53.3；换入 3.49.1 的原生库时运行时不启动、就绪 503、日志说明原因；系统信息返回版本与编译选项 | §2.5、§2.9、§2.13 | 产品 Docker 镜像中确认：T5-04 |
| V-07 | 复验：queue.db 由 Runtime 打开时的实际版本与持久化设置 | 通过（本地，T1-03）：开发环境的真实宿主进程与干净容器中的自包含发布物都加载 3.53.3，与配置库是同一个原生库；写者的连接显式设置 WAL 与 synchronous=FULL 并回读；文件只有属主可读写 | §2.16、§4.10 D9 | 产品 Docker 镜像中确认：T5-04 |
| V-11 | 转发头只信任已登记的代理；`__Host-` Cookie；防 CSRF；按来源地址限流；PasswordHasher 格式与迭代次数 | 通过（本地，T0 子集）：未登记代理时伪造转发头无效，设置 ASPNETCORE_FORWARDEDHEADERS_ENABLED 也不改变；登记后只接受代理的转发头；两种协议的 Cookie 不能互换；限流按实际来源地址；哈希为 V3（HMAC-SHA512、10 万次） | §2.7、§2.8 | API 令牌部分：T3-09 |
| AT-32 | 管理访问：伪造转发头不生效；经 HTTP 创建或使用 API 令牌被拒；HTTP 与 HTTPS 的会话 Cookie 不能互换；“只允许 HTTPS”或恢复备份后全部会话失效 | 部分通过（本地，最小版本）：转发头、Cookie 互换两项通过；会话代次加一使全部会话失效的机制已测试 | §2.8 | API 令牌：T3-09；由“只允许 HTTPS”开关与恢复备份触发：T5-01、T5-02 |
| FS-10 | 伪造转发头；经 HTTP 创建 API 令牌 | 部分通过（本地）：伪造的转发头不改变协议判定、来源地址与限流 | §2.8 | 经 HTTP 创建令牌被拒：T3-09 |
| V-02 | 前端构建环境：工具版本与 Node.js 要求 | 资料核查完成（2026-09-30） | §2.13 | 锁定依赖与 CI 构建：T3 |
| V-04 | MQTTnet：3.1.1、持久会话、手动确认、发布超时、持续发布时的断线检测、TLS | T0 最小原型完成（本地，真实 Mosquitto 2.1.2）：与发布有关的结论已得出——QoS 1 的发布超时与断线检测、QoS 0 输出用 PINGREQ 发现断线（审查后补充）、TLS 证书校验 | §2.11，§4.8 C3、C12 | 保留消息、QoS 0 输入的内存、会话恢复与完整背压：T2-03 |
| V-05 | NModbus 与 FluentModbus 的比较 | 比较完成（本地；用例在 CI 中执行）：关键差异有可复现结果；审查发现两个用例的结果取决于时序（一个可能挂死，一个随机失败），已修正并在审查者复现问题的条件下复验；选型 NModbus 3.0.83 已于 2026-10-09 确认 | §2.12，§4.8 C2、C11，§4.9 | 全部类型与字节序、AT-02：T2-02 |
| V-09 | 附带 Broker 的版本、许可与配置项 | 资料核查（2026-09-30）；2.1.2 已在 T0-11 实际运行 | §2.11、§2.13 | 实测：T2-07；部署文档：T5-04 |
| V-10 | 点表粘贴与大表格 | 资料核查（同 S-09、S-10） | §2.13 | 原型：T3 |
| V-12 | JSON 数值的序列化 | 通过（本地） | §2.10 | — |
| AT-30 | 数值表示（契约层子集） | 部分通过（本地）：见 V-12 | §2.10 | 读取、映射、管理 API、预览与各类输出的端到端：T2—T4 |
| FS-19、FS-20 | 迟到应答；读取阻塞时停止（库层基础） | 已比较（本地）：两个库对迟到应答、释放连接打断读取的实际表现见 §2.12 | §2.12 | 适配器与运行单元层面的完整验证：T2-02 |
| AT-13 | 运行子集：新版本反复非正常退出时自动回滚一次或进入“运行失败”，不形成崩溃循环；正常重启与已稳定版本上的退出不计入；准备阶段失败时原配置不中断；停止超时不重复启动 | 通过（本地，T1-01 范围；启动保护用真实进程验证）：见 §2.14 的验收表 | §2.14 | 环、缺引用、未知节点、错误字段的配置校验与界面应用：T3；应用过程中的停止失败：T3-06 |
| FS-05 | 新版本启动后进程反复非正常退出 | 通过（本地，T1 部分）：以种子快照代替界面应用；进程被 SIGKILL 强制终止。自动回滚生成的版本仍反复退出时进入“运行失败”：输入不启动、发送缓存照常、就绪 503 | §2.14 | 界面上的重试与回滚：T3-06 |
| GW-07 | 模拟输入：无硬件可体验；可复现正常、缺字段及连接错误场景 | 通过（本地，后端） | §2.15 | 模拟输入的配置页面：T3 |
| AT-07 | 格式异常（输入侧子集） | 部分通过（本地）：非法值标为 bad（nonFinite）并从值中省略；连接错误不生成消息 | §2.15 | 必填字段、非法 JSON、溢出与超限：T2-05、T1-04 |
| AT-29 | 资源保护（输入通道子集） | 部分通过（本地）：每个输入的通道有界，满时丢弃并计 overrun，内存中最多容量那么多条 | §2.15 | 在途字节预算、调试缓存、路径上限与预览并发：T1-07、T3 |
| GW-14 | 有限持久化发送缓存（存储层子集） | 部分通过（本地）：发送前入库、分组提交后才算接管、只读已提交记录、重启保留、按 seq 先进先出、确认后删除的写入接口 | §2.16 | 确认后清理的发送者：T1-04；容量限制、过期与丢弃统计、失败区与暂停：T1-05—T1-07 |
| AT-09 | 输出断网与恢复（存储层子集） | 部分通过（本地）：报文与目标在入库时冻结（采集时间写在报文中，补传不变） | §2.16 | 断网积压与按策略补传：T1-04、T1-05、T1-08 |
| AT-12 | 缓存满/磁盘不足（写入失败子集） | 部分通过（本地）：SQLite 写入失败时整批显式回滚、如实报告、记“存储异常”，请求队列有界 | §2.16 | 配额、存储保护、磁盘接近写满时的管理界面、queue.db 损坏：T1-06—T1-08 |
| GW-11 / V-04 发布子集 | 通用 MQTT 正式输出 | 部分通过（本地）：真实 QoS 0/1 发布、独立订阅、PUBACK 超时不删除、显式 PING、认证轮换、TLS 拒绝不可信证书 | §2.17、§4.12 | 系统可信 TLS 成功发布本次未验证；输入会话为 T2-03 |
| AT-09 / GW-14 发送子集 | 提交—发送—确认—删除与正常停机恢复 | 部分通过（本地）：确认后删除失败不重发；缺失 PUBACK 保留；重启发送同一冻结报文；停止先排空通道 | §2.17、§4.12 | 完整故障/强制终止矩阵、容量与存储保护为 T1-05—T1-08 |
| D-05 / FS §2.3 | MQTT 当前凭证与接收方变化 | 通过（本地，本次 API 范围）：默认作废、管理员显式保留、事务审计、失败回滚、角色/CSRF、秘密不返回、重启解密 | §2.17、§4.12 | HTTP 基础路径、其他凭证类型、完整连接管理与网页在后续对应任务 |
| AT-14 子集 | 当前目标指纹校验 | 部分通过（本地）：不匹配的记录不发送、账户标记变化有审计 | §2.17 | 持久隔离与移出队头为 T1-06；完整迁移为 T3 |
| AT-26 / GW-14 | T1-05 发送策略与失败区 | 通过（本地，发送核心子集）：四类结果、连续拒绝阈值、毒消息探测、满额计数、重启与存储失败 | §2.18、§4.13 | 真实 HTTP：T2；完整手动恢复/网页：T3；平台设备分类：T4 |
| AT-14 / AT-26 | T1-06 身份隔离与暂停 | 通过（本地核心子集）：三类隔离、旧记录不阻塞新身份、手动暂停重启保持、恢复与权限审计 | §2.19、§4.14 | 网页、迁移及平台身份：T3/T4 |
| AT-12 / GW-14 | T1-06 配额与保留期 | 通过（本地核心子集）：目标条数/字节与全局字节上限、逐目标拒绝、隔离占用、过期计数、异常时间保留及事务回滚 | §2.19、§4.14 | 磁盘保护、资源压力与完整退出：T1-07/08 |
| V-14 | 系统时钟同步检测 | 后端子场景通过（Linux x64 原生及受限 Docker）：只读内核检测、三状态分类/API、单调缓存、状态变化事件及无人访问周期检测 | §2.20 | 诊断网页 T3；ARM64 未验证；T1-07 后端交付见 §2.20 |
| 其余 V、FS、AT、GW | — | 未执行 | — | 随所属任务登记 |

## 4. 验证记录

### 4.1 环境（本地）

- 日期：2026-09-29。
- 系统：Ubuntu 26.04 LTS，Linux 7.0.0-31-generic，x86_64。
- .NET：SDK 10.0.401（commit `e34a38d2ae`，MSBuild 18.9.11）；运行时 Microsoft.NETCore.App 与 Microsoft.AspNetCore.App 10.0.12。按官方 `releases.json`，.NET 10 为 LTS，支持到 2028-11-14。
- 测试运行器：VSTest（Microsoft.NET.Test.Sdk 18.10.1）、xUnit.net VSTest Adapter 4.0.0、xUnit.net v3 4.0.1（关闭 MTP）。
- 其他工具：Python 3.14.4（报告核对）；actionlint 1.7.12（只下载到会话临时目录用于检查，未安装到系统）。
- SDK 安装：本机原有 SDK 10.0.400（运行时 10.0.11）。本批从官方 `releases.json` 所列地址下载 `dotnet-sdk-10.0.401-linux-x64.tar.gz`，SHA-512 与元数据一致后并列解压到 `~/.dotnet`，未删除 10.0.400。首次运行新 SDK 时，SDK 自动生成了本机 ASP.NET Core HTTPS 开发证书（未设为受信任）。

### 4.2 本地执行记录

开发过程中的执行：

| # | 命令与条件 | 结果 |
|---|---|---|
| R1 | `dotnet restore TenonEdge.sln` | 退出码 0，生成三份锁文件 |
| R2 | `dotnet build TenonEdge.sln -c Release --no-restore --no-incremental` | 0 警告、0 错误 |
| R3 | 干净副本（`git ls-files --cached --others --exclude-standard` 列出的待提交文件）+ `GITHUB_ACTIONS=true` + 空 `NUGET_PACKAGES`：`dotnet restore TenonEdge.sln --locked-mode` | 退出码 0 |
| R4 | 同上，并清空 `NUGET_HTTP_CACHE_PATH` | 退出码 0，49 个包从 nuget.org 下载；NuGet Audit 取得 2026-09-30 漏洞库，未报告漏洞 |
| R5 | 干净副本：`dotnet format TenonEdge.sln --verify-no-changes --no-restore`；`dotnet build TenonEdge.sln --configuration Release --no-restore` | 均退出码 0；42 个文件无需格式调整，0 警告 |
| R6 | `dotnet list TenonEdge.sln package --vulnerable --include-transitive` | 三个工程均无已知漏洞 |
| R7 | `actionlint -no-color .github/workflows/backend-ci.yml`（修改工作流后再次执行） | 0 错误；shellcheck、pyflakes 未安装，相应检查未执行 |
| R8 | 以 Production 环境手动启动：`dotnet src/TenonEdge.Host/bin/Release/net10.0/TenonEdge.Host.dll --urls http://127.0.0.1:18080` | 实际监听 `http://127.0.0.1:18080`：<br>• `/healthz`、`/readyz` 为 200；<br>• API 文档为 401；<br>• 未知路径为 404；<br>• `POST /healthz` 为 405；<br>• 以上错误均为 ProblemDetails；<br>• SIGTERM 后输出 “Application is shutting down” 并退出 |
| R9 | 以 Development 环境手动启动（端口 18081） | API 文档为 200：OpenAPI 3.1.1，operationId 为 `getLiveness`、`getReadiness` |
| R10 | 在仓库根目录按启动说明执行，先用相对路径 `--contentRoot src/TenonEdge.Host/bin/Release/net10.0` | 启动失败：相对的内容根按程序所在目录解析，拼出的目录不存在，未处理异常使进程中止（退出码 134）。改用绝对路径（`"$PWD/src/TenonEdge.Host/bin/Release/net10.0"`）后，Production、Development 两种环境均按预期响应，SIGTERM 后退出码 0。启动说明因此使用绝对路径 |

最终复验：全部改动完成（含 §4.5 审查修正）后，按 CI 工作流的步骤和参数，在新的干净副本中重新执行（59 个待提交文件；`GITHUB_ACTIONS=true`；包缓存与 HTTP 缓存均为空）。

| # | 步骤（与 CI 相同的命令） | 结果 |
|---|---|---|
| F1 | `dotnet restore TenonEdge.sln --locked-mode` | 退出码 0；49 个包从 nuget.org 下载，与锁文件一致 |
| F2 | `dotnet format TenonEdge.sln --verify-no-changes --no-restore` | 退出码 0 |
| F3 | `dotnet build TenonEdge.sln --configuration Release --no-restore` | 退出码 0；0 警告、0 错误 |
| F4 | `dotnet test TenonEdge.sln --configuration Release --no-build --results-directory artifacts/test-results --logger "trx;LogFilePrefix=tenonedge" --logger "console;verbosity=normal"` | 退出码 0；生成 2 份 TRX |
| F5 | F4 中的 TenonEdge.UnitTests | 发现 23、执行 23、通过 23、失败 0、跳过 0 |
| F6 | F4 中的 TenonEdge.IntegrationTests | 发现 21、执行 21、通过 21、失败 0、跳过 0。其中真实进程测试：宿主报告系统分配的监听地址（本次为 `http://127.0.0.1:41019`），SIGTERM 后退出码 0 |
| F7 | `python3 .github/scripts/verify-test-results.py artifacts/test-results --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests` | 退出码 0，“全部通过” |

报告位置：CI 中为 `artifacts/test-results/*.trx` 与 `artifacts/environment/`，以工作流制品 `backend-test-results` 上传，并在 Job Summary 中输出核对表。本地复验的报告位于会话临时目录，不入库。

调试监听补充（2026-09-30，§6 决定 10）：F1—F7 之后新增开发启动配置，随后执行：

| # | 命令与条件 | 结果 |
|---|---|---|
| R11 | 在仓库根目录执行 `dotnet run --project src/TenonEdge.Host` | 使用 `Properties/launchSettings.json`：<br>• Development 环境，实际监听 `http://0.0.0.0:18080`，内容根为 `src/TenonEdge.Host`；<br>• 经 `192.168.0.88`、`127.0.0.1`、`172.17.0.1` 访问，`/healthz`、`/readyz`、API 文档均为 200，文档为 OpenAPI 3.1.1；<br>• 模拟 Ctrl+C（向进程组发送 SIGINT）后退出码 0，无残留进程 |
| R12 | 直接运行 Release 构建，带 `--urls http://0.0.0.0:18080`，Production、Development 各一次 | 均监听 `0.0.0.0:18080`。经网卡地址访问：<br>• `/healthz`、`/readyz` 为 200；<br>• API 文档在 Production 为 401 ProblemDetails，在 Development 为 200；<br>• SIGTERM 后退出码 0 |
| R13 | 直接运行，不带 `--urls`（Production） | 监听 ASP.NET Core 默认的 `http://localhost:5000`（`127.0.0.1` 与 `[::1]`），经网卡地址访问时连接被拒绝 |
| R14 | 检查构建输出与发布物：`dotnet run` 的 Debug 输出；`dotnet publish src/TenonEdge.Host -c Release --no-restore -o <会话临时目录>` | 均不含 `launchSettings.json` 或 `Properties` 目录 |
| R15 | 在工作区原位按 CI 步骤重跑：locked restore、格式检查、Release 构建、测试、报告核对 | 均退出码 0：<br>• 构建 0 警告、0 错误；<br>• 单元 23 个、集成 21 个全部通过，0 失败，0 跳过 |

R11—R13 的访问都从本机发起：目标为本机网卡地址时，数据经回环接口传递，只能证明网卡地址上可以建立连接。之后又从 Docker 容器（`172.17.0.7`，经 docker0 网卡进入主机，不走回环）访问两个网卡地址，健康检查与 API 文档均正常，宿主的请求日志记录了这些请求。本机网络条件：ufw 未启用（`ufw status` 为 inactive）；nftables 只有 Docker 的转发链，没有入站过滤；回复局域网客户端的路由走物理网卡 `eno1`，不经代理软件的 TUN 网卡。未从其他物理机器实测。

### 4.3 负向验证：检查能够失败

| 实验 | 临时改动 | 结果 | 恢复 |
|---|---|---|---|
| Z | 集成测试工程尚无用例时执行 `dotnet test` | 输出 “No test is available”，退出码 1 | 随后加入真实用例 |
| A | 临时新建 `src/TenonEdge.Runtime` 并引用 Host，执行还原 | 两个用例失败，退出码 1：<br>• `SourceProjectsFollowDependencyDirection`：报告“不允许引用 TenonEdge.Host”，以及依赖闭包中出现 `Microsoft.AspNetCore.OpenApi`；<br>• `SingleSolutionListsEveryProject`：报告该工程未列入解决方案 | 已删除，复验通过 |
| B | 注释管理 API 分组的 `RequireAuthorization()` | 三个用例失败：生产端点清单、API 文档 401、真实进程测试。`dotnet test` 退出码 1，核对脚本退出码 1 | 按备份恢复，SHA-256 与原文件一致 |
| D | 与 B 同时进行：`ValidateScopes=false` | `ScopedServiceCannotBeResolvedFromRootProvider` 失败 | 同 B |
| C | 临时加入一个 `Assert.Skip` 用例 | `dotnet test` 显示 “Passed!”，退出码 0；TRX 的 `notExecuted` 计数也是 0，跳过只表现为 `total` 大于 `executed`。核对脚本按 `total - executed` 判为不通过，退出码 1 | 已删除，复验通过 |
| E | 独立审查构造的两份运行中止报告（测试进程崩溃；超出会话时限） | 两份报告的用例计数都是 2/2 通过，但运行结果为 `Failed`，并有运行级错误。修正前核对脚本误判为通过；修正后判为不通过，退出码 1，并输出错误原文 | 只修改脚本，无需恢复 |
| F | 移除宿主对生命周期就绪条件的登记 | 修正前全部用例仍通过（独立审查发现）；新增的 `ReadinessConditionsIncludeHostLifecycle` 失败，退出码 1 | 按备份恢复，SHA-256 与原文件一致 |

结论：
- VSTest 不把跳过当失败，只看 `dotnet test` 的退出码不能拒绝跳过，CI 必须执行报告核对步骤。
- 运行中止时，用例计数可能仍全部显示通过，核对脚本同时检查运行结果与运行级错误。

### 4.4 远端 CI

未运行。工作流已编写（`.github/workflows/backend-ci.yml`，T0-13 增加了自包含发布与干净容器验证），推送后才会在 GitHub Actions 执行：工作流只在推送 main、拉取请求或手动触发时运行，main 上还没有该工作流，因此须经拉取请求。T0-A、T0-B 已在本地提交，推送未获授权。本地按 CI 步骤在干净副本中的执行（§4.2、§4.6 B17、§4.8 C6）不等于 GitHub Actions 已经通过。CI 使用 `ubuntu-24.04` 托管镜像，与本机 Ubuntu 26.04 不同，首次运行后须核对报告。

### 4.5 独立审查

实现完成后，由另一审查代理只读审查了本批全部改动：

- 结论：无高严重度问题。
- 两项中等问题已修正，见实验 E、F：
  - 核对脚本没有检查运行是否中止；
  - 就绪条件的登记缺少测试。
- 低严重度问题中，以下已修正：
  - 真实进程测试预选端口存在竞争，并在就绪前只检查一次 `/readyz`；
  - 进程提前退出时输出可能不完整；
  - 依赖规则只读工程文件，漏掉经导入文件加入的引用；
  - `.editorconfig` 中解决方案文件的换行符与实际不符；
  - CI 在 main 上也会取消进行中的运行；
  - V-01 剩余子项的阶段写错。
- 以下低严重度问题作为决定记录（§6）或留待后续：
  - 未知管理路径对匿名请求返回 404；
  - 401 不带质询头；
  - 漏洞告警作为错误。

### 4.6 T0-B 本地执行记录

环境同 §4.1，日期 2026-09-30。本机同时运行着其他仓库的构建和测试，个别命令耗时偏长，不影响结果。

| # | 命令与条件 | 结果 |
|---|---|---|
| B1 | T0-04 完成后：`dotnet build TenonEdge.sln -c Release`；`dotnet test TenonEdge.sln -c Release --no-build` | 构建 0 警告、0 错误；单元 23/23、集成 39/39 通过，0 失败，0 跳过。首次运行时一个真实进程用例失败：测试同时启动两个实例，恰好第二个先取得锁，第一个被拒。这说明锁本身正确，是测试写法有竞争；改为第一个实例就绪后再启动第二个，之后通过 |
| B2 | `dotnet run --project src/TenonEdge.Host`（调试启动配置） | 开发数据目录被创建，权限 700，锁文件权限 600；监听 `0.0.0.0:18080`，健康检查与 API 文档为 200；模拟 Ctrl+C 后退出码 0 |
| B3 | T0-05 完成后：`dotnet restore --locked-mode`、`dotnet format --verify-no-changes`、Release 构建与全部测试 | 均退出码 0：<br>• 构建 0 警告；<br>• 单元 33/33、集成 58/58 通过，0 失败，0 跳过；<br>• 实际加载 SQLite 3.53.3，6 个物理连接回读均为 wal、2、5000。<br>首次运行时两个问题已修正：<br>• 物理连接用例以事务占住连接，而 SqlSugar 的事务是 BEGIN IMMEDIATE，6 个事务互相等待写锁直至超时（见 §6 决定 12）；<br>• 迁移历史的比较误用了引用相等 |
| B4 | T0-06 完成后：Release 构建、`dotnet format --verify-no-changes`、全部测试 | 均退出码 0：构建 0 警告；单元 77/77、集成 76/76 通过，0 失败，0 跳过。开发中发现并修正的问题：<br>• WebApplication 在构建时就会创建服务器并读取其选项；监听配置原先注册为 Kestrel 的选项，会连带在取得数据目录锁之前打开配置库，11 个真实进程用例因此失败。改为在启动检查的最后一步加入监听端点；<br>• 挂载表中字段不足的行会使解析抛出异常，单元测试发现后改为按格式不符忽略 |
| B5 | 真实容器验证（手动，脚本在会话临时目录）：镜像 `mcr.microsoft.com/dotnet/aspnet:10.0`（`sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f`，运行时 10.0.12），以只读方式挂载 Release 构建输出 | • 挂载命名数据卷：就绪 200；首次生成证书，TLS 出示的指纹与日志一致；判定数据目录位于挂载点 /data（ext4）。<br>• 删除容器后用同一数据卷重建：就绪 200，NodeId 与证书指纹不变，日志显示证书为加载，没有解密失败。<br>• 不挂卷（数据目录在容器文件系统上）：存活 200、就绪 503，原因为位于挂载点 /（overlay）。<br>• 数据目录挂 tmpfs：就绪 503，原因为位于挂载点 /data（tmpfs）。<br>• 结束后没有残留容器或数据卷 |
| B7 | T0-07 完成后：Release 构建、格式检查、全部测试 | 均退出码 0：构建 0 警告；单元 100/100、集成 108/108 通过，0 失败，0 跳过 |
| B6 | `dotnet run --project src/TenonEdge.Host`（新的调试启动配置） | 监听 `http://0.0.0.0:18080` 与 `https://0.0.0.0:18443`；经 192.168.0.88 访问，两种协议下健康检查与 API 文档均为 200；首次生成密钥时出现预期的“未配置密钥加密器”警告（§6 决定 17）；模拟 Ctrl+C 后退出码 0，端口已释放 |
| B8 | T0-08 开发初期的一次全量测试（Debug；已加入防 CSRF 过滤器，集成用例仍为 108 个） | 单元 100/100 通过；集成 108 个中 `DataProtectionTests.CiphertextIsReadableAfterRestartWithADifferentContentRoot` 失败 1 个。当时的输出经过筛选，只留下用例名，没有保存失败原因。紧接着连续 10 次全量集成测试（各 108 个）全部通过，之后的运行也没有再出现。原因未查明，记为未解决的偶发失败。独立审查（§4.7）指出一个可能原因并在副本中复现：进程内宿主由 Program 的 RunAsync 与测试工厂两处释放，工厂可能在数据目录锁释放之前返回，紧接着在同一目录上启动的宿主会因锁仍被占用而启动失败（审查者将进程固定到单个 CPU 后，200 次中出现 1 次）。已让固定数据目录的测试工厂等到锁可以取得再返回（§6 决定 40）；该用例正是在同一目录上先后启动两个宿主。之后的重复运行见 B13、B18 |
| B9 | T0-08 开发期间：新增用例逐个运行 | `SessionSecurityTests` 6 个、`RequestSecurityProcessTests` 5 个通过；T0-08 完成时没有单独执行全量测试，全量结果见 B10 |
| B10 | T0-09 完成后（Debug）：`dotnet build TenonEdge.sln`、`dotnet test TenonEdge.sln --no-build` | 均退出码 0：构建 0 警告；单元 114/114、集成 146/146 通过，0 失败，0 跳过。随后新增审计保留期的启动检查用例，集成用例增至 147 个。开发中发现：枚举类型的查询参数由框架按大小写敏感解析，`level=warning` 返回 400，改为以字符串接收（§6 决定 35） |
| B11 | 变异实验：在工作区副本（会话临时目录，不含 bin、obj、.git）中逐个施加变异，构建后运行对应测试；每次结束恢复原文并核对 SHA-256 | 10 个变异全部被测试发现，恢复后 SHA-256 均一致；工作区本身未改动。逐项见下表 |
| B12 | 干净副本（`git ls-files --cached --others --exclude-standard` 列出的文件）+ `GITHUB_ACTIONS=true` + 空的 `NUGET_PACKAGES` 与 `NUGET_HTTP_CACHE_PATH`，按 CI 工作流的步骤执行：locked restore、`dotnet format --verify-no-changes`、Release 构建、带 TRX 的测试、报告核对；另执行 `dotnet list package --vulnerable --include-transitive` | 均退出码 0：<br>• 77 个包从 nuget.org 下载，与锁文件一致；<br>• 格式检查无需调整；构建 0 警告、0 错误；<br>• 单元 114/114、集成 147/147 通过，0 失败，0 跳过，核对脚本判为全部通过；<br>• 三个工程均无已知漏洞。<br>这是本地模拟，不等于 GitHub Actions 已经运行 |
| B13 | 在 B12 的副本中连续执行 10 次集成测试（Release，每次保存 TRX） | 10 次均为 147/147 通过；B8 的偶发失败没有再出现 |
| B14 | 按独立审查（§4.7）修正后（Debug）：构建与全部测试 | 均退出码 0：构建 0 警告；单元 119/119、集成 158/158 通过，0 失败，0 跳过。复核后再次调整（修改密码规则、保留期清理、日志提供程序清单），重新执行：单元 119/119、集成 164/164 通过 |
| B15 | 变异实验（同 B11 的做法，副本重新取自工作区）：逐个撤销审查后的修正 | 14 个变异全部被新增的测试发现，恢复后 SHA-256 均一致。逐项见下表：N1—N9 针对第一次修正，P1—P3、Q1—Q2 针对复核后的调整 |
| B16 | 手动验证启动时配置库被锁定：先正常启动一次再停止；另一个进程以 `BEGIN EXCLUSIVE` 占住 config.db 的写锁后再启动宿主（Debug 构建） | 约 31 秒后以退出码 4 结束，日志为“启动检查未通过，宿主退出（退出码 4）：配置库不可用：SQLite Error 5: 'database is locked'.”；第一次启动 SIGTERM 后退出码 0 |
| B17 | 修正后重新执行 B12 的全部步骤（新的干净副本与空的 NuGet 缓存） | 均退出码 0：77 个包重新下载；格式检查通过；构建 0 警告；单元 119/119、集成 158/158 通过，核对脚本判为全部通过；无已知漏洞。复核后的最终代码再执行一次（新的干净副本与空缓存）：同样全部退出码 0，构建 0 警告，单元 119/119、集成 164/164 通过，无已知漏洞。本地模拟，不等于 GitHub Actions 已经运行 |
| B18 | 在 B17 的副本中连续执行 10 次集成测试（Release，每次保存 TRX） | 10 次均为 158/158 通过；复核后的最终代码再连续执行 10 次，均为 164/164 通过 |

T0-B 负向验证（B11）：

| # | 变异 | 失败的测试 |
|---|---|---|
| M1 | 不再移除框架的转发头启动过滤器 | `ForgedForwardedHeadersAreIgnoredWithoutRegisteredProxies(frameworkVariableSet: True)` |
| M2 | 受信任代理的请求不看 `X-Forwarded-Proto`，改看连接本身 | `RegisteredProxyAloneDecidesTheProtocolAndTheClientAddress` |
| M3 | 防 CSRF 过滤器一律放行 | `CookieWritesWithoutTheMatchingCsrfTokenAreRejected` |
| M4 | 会话校验不再拒绝安全戳、代次、角色、协议不符的票据 | 会话失效与协议互换的 4 个用例 |
| M5 | 去掉 XmlKeyManager 日志类别的级别上限 | `SecretsNeverAppearInTheOutputEvenWithTraceLogging`（哨兵主密钥出现在输出中） |
| M6 | 写入失败的日志不限额 | `StorageWriteFallbackTests` 4 个、`RuntimeEventLogTests` 2 个 |
| M7 | 不登记保留期清理的后台服务 | `ExpiredRecordsArePurgedWhenTheServiceStarts` |
| M8 | 运行事件不再合并 | `RuntimeEventLogTests` 2 个、`RuntimeEventEndpointTests` 2 个 |
| M9 | 审计查询不再要求管理员角色 | 端点清单 2 个、`AnonymousGets401WithChallengeAndRolesWithoutPermissionGet403` |
| M10 | 审计保留期不设下限 | `ShorterOrInvalidAuditRetentionIsAConfigurationError(value: "179")`、`AuditRetentionShorterThan180DaysIsRejectedAtStartup` |

审查后修正的负向验证（B15）：

| # | 变异 | 失败的测试 |
|---|---|---|
| N1 | 登录不再按账号名排队 | `ConcurrentWrongPasswordsCannotGetPastTheLockoutLimit` |
| N2 | 修改密码时原密码错误不计入失败计数（针对第一次修正；该设计已被替换，现行规则由 P1、P2 检查） | `WrongCurrentPasswordsCountTowardTheSameLockoutAsFailedLogins`（该用例已随设计替换删除） |
| N3 | 不存在的账号名重新写入审计 | `UnknownAccountNamesAreNotWrittenToTheAudit` |
| N4 | 会话缓存不再核对读取期间的失效 | `InvalidationDuringALoadIsNotOverwrittenByTheStaleValue` |
| N5 | 设置窗口改回按系统时钟计算 | `SetupWindowTests` 2 个 |
| N6 | 日志规则改回只用完整类别名 | `SecretsNeverAppearInTheOutputEvenWithTraceLogging`（带通配符的规则把哨兵主密钥调了出来） |
| N7 | 不存在的账号名超出上限时不再移除 | `TrackedUnknownNamesStayWithinTheCapByEvictingTheOldestFailure` |
| N8 | 清理审计时不再核对 ID 中的逻辑时间戳（针对第一次修正；该条件已被“保存的时间可信”取代，现行规则由 Q1 检查） | `DataRetentionTests` 2 个 |
| N9 | 运行事件查询忽略 to 参数 | `EventsCanBeFilteredByWhenTheyOccurred` |
| P1 | 登录锁定期间不允许修改密码 | `LoginLockoutDoesNotStopTheOwnerFromChangingThePassword` |
| P2 | 原密码连续错误不再结束会话 | `RepeatedWrongCurrentPasswordsEndEverySessionOfTheAccount` |
| P3 | 限制规则的清单漏掉控制台 | `KeyManagerLoggingTests` 4 个 |
| Q1 | 清理审计时不再核对保存的时间是否可信 | `DataRetentionTests` 2 个 |
| Q2 | 清理运行事件时不再核对首次发生的时间是否可信 | `DataRetentionTests` 2 个 |

### 4.7 T0-B 独立审查

实现完成后，由两个审查代理分别只读审查了 T0-B 的全部改动（正确性与测试有效性、安全），审查者在会话临时目录的副本中运行了验证，没有改动工作区。

- 结论：无严重或高严重度问题；中等 7 项、低 13 项，部分重复。
- 已修正（均补充了测试，变异实验见 §4.6 B15）：
  - 并发的错误登录可以在锁定生效前多试密码（审查者用 30 个并发请求试出 21 次密码校验），并会延长锁定；
  - 修改密码时可以不受限制地猜原密码。第一次修正把原密码错误计入登录锁定，复核时审查者指出副作用：网络上的任何人都能维持登录锁定，从而使已登录的用户无法借改密码结束被窃取的会话。已改为单独计数，达到上限时结束该账号的全部会话，登录锁定不影响修改密码（§6 决定 36）；
  - 读取与失效并发时，旧安全戳可能留在会话缓存中；
  - 首次设置窗口按系统时钟计算，开机后校时会使窗口立刻关闭或被延长；
  - 带通配符的日志规则可以把 XmlKeyManager 调回 Trace，输出未加密的主密钥（审查者复现）；
  - 误输入账号栏的密码会以“不存在的账号名”写入审计（审查者复现）；
  - 不存在的账号名在全部未过期时不受 1 万条上限约束；
  - 保留期清理只看保存的时间：时钟落后期间写入的记录在时钟校正后会被立即删除。第一次修正加上了“ID 中的逻辑时间戳也须早于截止时间”，复核时审查者指出逻辑时间戳只是真实时间的下限，在构建之后很久才以错误时钟完成设置时仍会提前删除；已改为写入时时钟落后的记录不按保留期删除（§6 决定 32）；
  - 审计保留期没有上限，过大的值会使启动异常或清理每小时失败；
  - 启动时配置库被其他程序锁定，以未处理异常结束而不是退出码 4（修正后手动验证见 §4.6 B16）；
  - 登录计数写入失败的日志没有限额；
  - 运行事件查询缺少功能需求 §7.4 要求的按时间筛选；
  - 测试工厂释放后数据目录锁可能尚未释放（B8 的可能原因）；
  - 两个新用例在容器中运行时会因数据卷检查而失败，改为核对宿主实际登记的阻止原因；补充了“库中已有的最大 ID”作为 ID 下限的测试。
- 文档中不准确的表述已改正：锁定与计数上限、会话缓存、修改角色与停用账号的验证范围、日志规则、审计中的密码、“不无限写日志”的范围、响应时间、启动顺序。
- 响应时间：安全审查者在本机比较了已有账号与不存在的账号的登录失败耗时，未测到可区分的差异；正确性审查者指出已有账号多一次同步写盘，存储较慢时仍可能不同（未测量）。
- 修正后由同两位审查者复核（各自取工作区快照，在副本中重跑探针与变异）：确认上述修正生效；复核中又指出修改密码第一次修正的副作用与保留期清理的遗留问题，已再次修正（见上），其余意见记入 §7。
- 未修改、需要维护者决定或留给后续任务的项目见 §7。

### 4.8 T0-C 本地执行记录

环境同 §4.1，日期 2026-09-30。另用到 Docker 29.1.3，以及镜像 `eclipse-mosquitto:2.1.2-alpine`、`mcr.microsoft.com/dotnet/runtime-deps:10.0`（digest 见 §2.11、§2.13）。

| # | 命令与条件 | 结果 |
|---|---|---|
| C1 | T0-10 完成后：`dotnet build TenonEdge.sln`、`dotnet format TenonEdge.sln --verify-no-changes --no-restore`、全部测试 | 0 警告；单元 193/193、集成 179/179 通过，0 失败，0 跳过。开发中修正：<br>• 分析器 CA1720：四种输出类型的成员名与 C# 类型名相同，按设计用语保留，局部关闭并注明理由；<br>• Linux 上释放仍有读取的套接字时，设备一侧收到连接重置，测试原先只接受正常关闭（§6 决定 44）；<br>• 保留期清理的边界测试第一次把审计表改名，启动时的 ID 下限检查读不到该表，宿主按 T0-B 的规则以退出码 4 拒绝启动；改为用触发器只让清理失败 |
| C2 | T0-12：`dotnet test --filter Category=ModbusProbe` 连续 10 次，再用 `taskset -c 0` 限制在一个 CPU 上执行 3 次 | 当时每次 42/42 通过；限制 CPU 时每次约 21 秒。第一次运行时 38 个用例的结果与源码静态阅读的预期一致，另发现 FluentModbus 把后一帧当作数据返回（预期之外），改为固定这一行为并补充了确定性的用例。<br>**更正**：审查者在另外的时序下用同一命令复现了一个用例挂死、一个用例随机失败（§4.9），这里的“每次通过”只代表当时的时序；修正后的复验见 C11 |
| C3 | T0-11：`dotnet test --filter FullyQualifiedName~Probes.Mqtt`（本机 Docker）连续 5 次 | 每次 21/21 通过，约 25 秒；结束后没有残留容器。第一次运行时 5 个 TLS 用例失败：证书被拒时 MQTTnet 抛出的是 MqttCommunicationException，而不是预期的 MqttConnectingFailedException（拒绝发生在 TLS 握手，早于 MQTT 认证），按实测改正断言 |
| C4 | T0-C 全部完成后：Debug 构建、格式检查、全部测试（含 RealBroker） | 0 警告；单元 193/193、集成 242/242 通过，0 失败，0 跳过 |
| C5 | `dotnet publish src/TenonEdge.Host/TenonEdge.Host.csproj -c Release -r linux-x64 --self-contained` | 112 MB、344 个文件，含 `libe_sqlite3.so`，不含 SQL Server 驱动栈 |
| C6 | 干净副本 + `GITHUB_ACTIONS=true` + 空的 NuGet 包缓存与 HTTP 缓存，按 CI 的步骤执行 | 均退出码 0：<br>• locked restore 从 nuget.org 下载 88 个包；<br>• 格式检查与 Release 构建通过，0 警告；<br>• 测试排除 RealBroker：单元 193/193、集成 223/223，报告核对“全部通过”；<br>• 自包含发布，干净容器验证当时的 6 项全部通过（“数据目录不在卷上”一项随后加入，见 C7）；<br>• 四个工程均无已知漏洞。<br>第一次执行时会话临时目录的磁盘配额用尽，构建步骤失败（与代码无关）；删除旧的临时副本后重跑通过 |
| C7 | `bash .github/scripts/verify-published-host.sh`（本机，C5 的发布物） | 7 项全部通过；结束后没有残留容器与数据卷。第 6 项为本批补充：第一次运行时宿主按 T0-04 的规则以退出码 2 拒绝（数据目录不存在），改为先在容器的可写层创建目录 |
| C8 | actionlint 1.7.12 检查工作流；`bash -n` 检查验证脚本 | 均无错误；shellcheck 未安装，未执行 |
| C9 | T0-10 的变异实验：在干净副本中逐个改动，构建后运行相关测试，恢复后核对 SHA-256 | 14 个变异全部被测试发现，恢复后 SHA-256 均一致，见下表 |
| C10 | 审查修正后（2026-10-01）：干净副本 + `GITHUB_ACTIONS=true` + 空的 NuGet 包缓存与 HTTP 缓存，按修正后的 CI 步骤执行（干净镜像按 digest 固定） | 均退出码 0：<br>• locked restore 从 nuget.org 下载 88 个包；<br>• 格式检查与 Release 构建通过，0 警告；<br>• 测试排除 RealBroker：单元 202/202、集成 224/224，报告核对“全部通过”；<br>• 自包含发布 112 MB、344 个文件，SHA-256 清单 344 行；<br>• 干净容器验证 7 项全部通过；<br>• 四个工程均无已知漏洞。<br>结束后没有残留容器与数据卷 |
| C11 | 审查修正后（2026-10-01）：在审查者复现问题的条件下复验 Modbus 比较用例，均带 `--blame-hang-timeout 60s`：类别 10 次、只运行 `ModbusFrameErrorComparisonTests` 10 次、只运行异步超时用例 30 次、`taskset -c 0` 3 次、三个进程并行 3 轮 | 62 次全部通过，没有挂死，结束后没有残留进程 |
| C12 | 审查修正后：MQTT 验证代码（含新增的 PINGREQ 用例）连续 5 次（本机 Docker） | 每次 22/22 通过，约 26 秒；拔线后 PINGREQ 均在 1.0 秒超时；没有残留容器与临时目录 |
| C13 | 审查修正后的变异实验（R15—R21，方法同 C9） | 7 个全部被测试发现，恢复后 SHA-256 均一致；R20 第一次写成 `if (false)` 时被编译器以 CS0162 拦下，没有走到测试，改用运行时恒假的条件后由测试发现 |
| C14 | 审查修正后：Debug 构建、格式检查、全部测试（含 RealBroker） | 0 警告；单元 202/202、集成 244/244 通过，0 失败，0 跳过 |
| C15 | 复核意见修正后：验证脚本对真实发布物运行；再以启动即退出的假宿主运行；协议验证代码（Modbus 与 MQTT）运行一次 | 真实发布物 7 项全部通过，各项说明同时出现在控制台；假宿主时脚本退出码 1，控制台写明“没有映射端口”，证据目录保留该容器的输出，没有残留容器与数据卷；协议验证代码 64/64 通过 |
| C16 | 复核意见修正后（2026-10-01）：按 C10 的方式再执行一次（干净副本、空缓存、`GITHUB_ACTIONS=true`、干净镜像按 digest 固定） | 均退出码 0：locked restore 下载 88 个包；格式检查与 Release 构建通过，0 警告；单元 202/202、集成 224/224，报告核对“全部通过”；自包含发布 344 个文件，SHA-256 清单 344 行；干净容器验证 7 项全部通过；四个工程均无已知漏洞；结束后没有残留容器与数据卷 |

| # | 变异 | 失败的测试 |
|---|---|---|
| R1 | float32 改按双精度写出 | `FieldValueTests` 7 个 |
| R2 | 非有限的 double 不再被拒绝 | `NonFiniteDoublesCannotBecomeFieldValues` 3 个 |
| R3 | decimal 经 double 写出 | `FieldValueTests` 6 个 |
| R4 | 相等比较忽略 decimal 的小数位数 | `ValuesAreEqualOnlyWhenRepresentationAndTextMatch` |
| R5 | 规范化 JSON 不再排序属性 | `RuntimePlanTests` 2 个 |
| R6 | 规范化 JSON 接受重名属性 | `DuplicatePropertyNamesAreRejected` 3 个 |
| R7 | 运行计划接受重复的单元标识 | `PlanRejectsTwoUnitsWithTheSameIdentifier` |
| R8 | 业务标识的正则改回 `$`（接受末尾换行） | `InvalidKeysAreRejected`（`meter-1\n`） |
| R9 | 消息允许同一字段既有值又有原因码 | `AFieldCannotBeBothGoodAndBad` |
| R10 | 来自网关的采集时间不再须等于接收时间 | `GatewayTimeIsTheReceivedTime` |
| R11 | 时间来源的写出名称改用成员名 | `EnumNamesFollowTheDesign` |
| R12 | 测试适配器在构建时就连接设备 | `RuntimeUnitContractTests` 3 个 |
| R13 | 停止时不释放连接 | `RuntimeUnitContractTests` 2 个 |
| R14 | 保留期清理不在边界捕获异常 | `ARetentionFailureIsCaughtInsideTheServiceAndTheHostKeepsServing` |
| R15 | decimal 的负零不再按零保存 | `DecimalNegativeZeroIsStoredAsZero` |
| R16 | 时间来源去掉自带的字符串转换器 | `EnumNamesFollowTheDesign` |
| R17 | 消息不再校验原因码 | `UndefinedQualityCodesAreRejected` |
| R18 | 业务标识改回只允许一段 | `KeysScopedToADeviceAreAccepted`、`IdentifiersLongerThan64CharactersAreRejected` |
| R19 | 无效的配置 JSON 不再统一为参数错误 | `InvalidJsonContentIsAnArgumentError` |
| R20 | 后台服务检查忽略按具体类型登记的第二个实例 | `TheHostedServiceCheckCatchesASecondInstanceRegisteredByConcreteType` |
| R21 | 测试适配器停止之后仍可启动 | `AUnitStoppedBeforeItStartsCannotBeStarted` |

### 4.9 T0-C 独立审查

实现完成后，由一个审查代理只读审查了 T0-C 的全部改动（正确性、测试有效性、安全、CI 与文档），审查者在会话临时目录的副本中运行了验证，没有改动工作区。

- 结论：要求修改。高 3 项、中 6 项、低 10 项，另列出文档中与事实不符或夸大的表述 15 处。
- 已修正（变异实验见 §4.8 C13）：
  - 高：FluentModbus RTU over TCP 异步空转的用例会无限挂死。在测试线程上直接调用的异步读取从不让出线程，后面的取消执行不到。审查者不限 CPU 运行该类别 4 次全部挂死、只运行该测试类 8 次全部挂死，用 dotnet-stack 抓到测试线程停在库的读取循环中。改为在线程池线程上调用，文档补充“异步读取会占住调用线程空转”。
  - 高：FluentModbus 异步超时的用例断言 TimeoutException，实际的异常类型取决于时序（审查者单独运行 30 次失败 15 次，用 CI 的命令运行 11 次失败 2 次）。断言改为接受三种结果，文档改为“不能据异常类型判断是否超时”。
  - 高：CI 把完整的自包含发布物作为制品上传；仓库公开，等同于分发许可待核查的组件。删除上传，改为上传 SHA-256 清单（§6 决定 52）。
  - 中：运行单元的业务标识不能表示只在设备内唯一的 inputKey（§6 决定 51）。
  - 中：验证脚本的第 1 项在两个目录只缺一个时也判通过；改为逐项检查。
  - 中：CI 证据中的镜像 digest 在全新的 runner 上为空；改为先拉取、读取失败即终止，镜像按 digest 固定。
  - 中：宿主启动即退出时脚本不保存输出；改为清理前先保存每个容器的输出，读取端口失败时写明原因。
  - 中：“按具体类型登记的后台服务是同一个实例”的分支从未执行到；补充用构造的错误登记证明检查有效。同义反复的“作用域释放后单元照常运行”用例已删除，文档改为“由契约的形状保证”。
  - 中：V-04 的断线检测只覆盖 QoS 1；补充 QoS 0 的 PINGREQ 探测并实测（§6 决定 53）。
  - 低：decimal 负零的相等比较；三个枚举不配置转换器时写成整数（改为自带转换器）；消息不校验原因码；无效 UTF-16 与过深嵌套抛出的异常类型（统一为 ArgumentException）以及“与书写格式无关”的说法；测试适配器停止之后仍可启动（改为拒绝，并写明调用约定）；测试 Broker 的容器没有 `--rm` 与标签、docker 命令超时后进程不结束、测试私钥留在临时目录；断言比文档宽（NotAuthorized、对端关闭时的异常类型）；关于锁文件与运行时包的说法偏强；脚本的依赖清单不全。
- 维持现状并说明：
  - 依赖规则没有禁止 Host 引用协议库：Host 以后引用 Adapters，Adapters 引用协议库，Host 的依赖闭包中必然出现这些包，所以规则只禁止 Runtime 与 Abstractions；`Directory.Packages.props` 的注释已改为陈述现状，不再暗示有测试约束。
  - 测试进程被强制结束时可能留下 Broker 容器：没有在测试开始时自动清理（可能误删并行测试的容器），改为加标签并写明清理命令。
- 文档更正：§4.8 C2，§1 表与 V-05，§2.12 的异步表现，§2.10 的四处说法，§2.11 的观察窗与 QoS 0，§2.13 的 CI 上传与阶段结论，§6 决定 45 的退出码依据，按审查意见逐条改正。
- 复核：修正后由同一审查者复核，确认 19 条全部修正有效。
  - 按上次复现问题的条件重跑 49 次，均带 `--blame-hang-timeout`：不绑 CPU 运行类别 8 次、只运行该测试类 8 次、单独运行异步超时用例 30 次、`taskset -c 0` 运行类别 3 次。没有挂死、失败或残留进程；异步超时用例的三种结局都实际出现，均被新断言接受。用 CI 的命令运行 3 次，单元 202/202、集成 224/224。
  - 验证脚本第 1 项的三种构造场景都被判出；宿主启动即退出、镜像拉取失败两种情形按预期终止并留下原因。
  - 复核又提出 4 条低级问题，均已修正（§4.8 C15）：清理时只为尚未保存过输出的容器补存日志，改为删除之前一律重新保存；在命令替换中失败时说明只进摘要、不进控制台，改为同时写到标准错误；类注释中“异步读取占住调用线程”的结论注明证据来源（审查时抓到的线程栈）；测试 Broker 加 `--rm` 后启动失败的输出随容器消失，改为不加 `--rm`、启动失败时先取出输出。
  - 复核未重跑的部分：作者的变异实验 C13、三个进程并行的条件；空缓存下的 locked 还原（复核者用已有缓存）。

### 4.10 T1 第一批本地执行记录

环境同 §4.1，日期 2026-10-01；新增的包见 §5。

| # | 命令与条件 | 结果 |
|---|---|---|
| D1 | 本批代码完成后，第一次运行已有测试（`dotnet test TenonEdge.sln --filter "Category!=RealBroker"`） | 单元 202/202 通过；集成 225 个中 23 个失败，都由本批引起，改正后通过：<br>• `Program` 在 `RunAsync` 返回之后才读取后台服务，此时服务容器已释放，进程以 134 崩溃，21 个真实进程测试因此失败（§6 决定 62）；<br>• 停机时先关闭请求队列，写者在没有停止请求时返回，被监督者当作故障（§6 决定 71）；<br>• 两处测试本身的问题：契约测试假定了两个单元的连接顺序；雪花主键表的登记检查需要排除以版本号为主键的运行快照表 |
| D2 | 新增测试完成后：Debug 构建、格式检查、全部测试（含 RealBroker） | 0 警告；单元 292/292、集成 286/286 通过，0 失败，0 跳过 |
| D3 | 模拟输入与监督者的用例连续 30 次 | 30 次全部通过 |
| D4 | `taskset -c 0` 把运行时与契约用例（当时 58 个）限制在一个 CPU 上执行 5 次 | 3 次各有 1—2 个用例失败。查明 4 个原因并改正：<br>• 模拟输入先按时钟算出等待时长、再创建计时器，可控时钟在两步之间推进时会多等一个周期，测试偶发超时；改用固定周期的计时器（§6 决定 65）；<br>• 写者提交失败时先通知等待方、后更新状态，等待方立即查询时看不到“存储异常”（产品，§6 决定 69）；<br>• 测试工厂与 `Program` 两处停止宿主，协调者的停止被执行两次，后完成的那次清除标记的动作晚于测试对数据库的修改；改为协调者的停止只执行一次，测试工厂先等宿主发出“已停止”（§6 决定 58、72）；<br>• “已稳定”的用例在运行事件写入之前就断言（测试） |
| D5 | 改正之后：`taskset -c 0` 12 次；全部测试（含 RealBroker）3 次 | 全部通过；结束后没有残留容器与临时目录 |
| D6 | 变异实验 M1—M16：在干净副本中逐个改动，构建后运行相关测试，恢复后核对 SHA-256（方法同 §4.8 C9） | 第一轮 13 个被测试发现；M11 只表现为测试超时（写者打不开发送缓存时请求一直等待），M15 没有被发现（缺少停机最后一次提交的用例），M13 的改写被分析器 CA1822 拦下、没有走到测试。据此改正产品（打不开时不挂起）、补充两个用例并改写 M13 之后，16 个全部被发现，恢复后 SHA-256 均一致，见下表 |
| D7 | 补充之后：全部测试（含 RealBroker）；`taskset -c 0` 运行时与契约用例（60 个）15 次 | 单元 292/292、集成 288/288；单核第 2 次时新用例失败一次，原因同 D4 的第二条（打不开时先通知、后更新状态），改正后连续 10 次通过 |
| D8 | 真实进程：`StartupProtectionProcessTests`（2 个）、`ConfigStoreProcessTests` 的 queue.db 用例 | 通过；启动保护两个用例合计约 20 秒，共启动宿主 15 次，其中 10 次被强制终止 |
| D9 | CI 等效执行（2026-10-01）：干净副本 + `GITHUB_ACTIONS=true` + 空的 NuGet 包缓存与 HTTP 缓存，按 CI 的步骤执行，干净镜像按 digest 固定 | 第一次：还原、格式检查、Release 构建与测试都通过，自包含发布在 locked mode 下报 NU1004（被 Host 引用的三个工程的锁文件缺少 linux-x64 一段，§6 决定 74）。改正后第二次均退出码 0：<br>• locked restore 从 nuget.org 下载 89 个包；<br>• 格式检查与 Release 构建通过，0 警告；<br>• 测试排除 RealBroker：单元 292/292、集成 268/268，报告核对“全部通过”；<br>• 自包含发布 112 MB、350 个文件（新增 Runtime、Adapters、Abstractions 的程序集与符号文件），SHA-256 清单 350 行；<br>• 干净容器验证 7 项全部通过；发布物在没有 .NET 的容器中以“尚无有效配置”启动，打开 queue.db：SQLite 3.53.3、journal_mode=wal、synchronous=2（FULL）；<br>• 六个工程均无已知漏洞；结束后没有残留容器与数据卷。<br>这次运行早于“种子内容哈希改在提交或放弃时记下”及其用例（审查者指出），也早于审查意见的修正；最终工作区的 CI 等效执行见 D12 |
| D10 | 审查后的第二轮变异（M11 重做、M17—M24，方法同 D6） | 9 个全部被测试发现，恢复后 SHA-256 均一致，见下表；结束后没有残留的临时目录 |
| D11 | 审查意见修正之后：Debug 构建、格式检查、全部测试（含 RealBroker）；`taskset -c 0` 运行时与契约用例（70 个）8 次 | 第一次：格式检查发现一个测试文件的 using 顺序；单核第 3 次时新用例失败一次：取消回调出错的运行事件在宿主停止之后才写入。改为停止时在期限内同时等待循环退出与回调结束，回调出错因此在停止返回之前记下（之后又调整，见 D12）；M19 按新写法重做，仍被 3 个用例发现。改正后：0 警告、格式检查通过；单元 292/292、集成 300/300；单核 8 次全部通过；没有残留容器与临时目录 |
| D12 | 审查意见修正之后的 CI 等效执行（方法同 D9），以及随后的修正与复验 | 第一次：单元 292/292；集成 280 个中 `AFailedRebuildIsRetriedAfterBackoff` 失败一次（等待单元状态超时，停在“退避”）。原因：监督者先公布故障、后创建退避计时器，测试按状态推进可控时钟时，推进可能落在两步之间（§6 决定 79）。<br>修正：<br>• 改为先启动计时、再公布故障；<br>• 复查停止代码时发现，循环自行退出而回调仍阻塞的情况不报告停止超时，一并改为循环等取消回调执行完才结束（§6 决定 75）；<br>• 补充 2 个用例，变异 M19（第三次）、M25、M26 均被发现；运行时相关 49 个用例多核连续 15 次通过。<br>重做 CI 等效执行，均退出码 0：<br>• 89 个包；格式检查通过；构建 0 警告；<br>• 单元 292/292、集成 282/282（排除 RealBroker）；<br>• 发布物 112 MB、350 个文件；干净容器 7 项全部通过；<br>• 发布后的宿主打开 queue.db：SQLite 3.53.3、journal_mode=wal、synchronous=2；<br>• 没有已知漏洞的包。<br>之后在工作区：<br>• Debug 构建 0 警告、格式检查通过；<br>• 全部测试（含 RealBroker）：单元 292/292、集成 302/302；<br>• `taskset -c 0` 下运行时与契约用例（72 个）8 次全部通过；<br>• 没有残留容器。 |
| D13 | 复核意见修正之后（§4.11）：运行时与契约用例多核 10 次；变异 M19、M25—M30（方法同 D6，M26 按最终写法重新定义）；CI 等效执行（方法同 D9）；工作区全部测试与单核复验 | 均通过：<br>• 运行时与契约用例（74 个）多核连续 10 次通过；<br>• 7 个变异全部被发现，恢复后 SHA-256 均一致，见下表；<br>• CI 等效执行均退出码 0：89 个包；格式检查通过；构建 0 警告；单元 292/292、集成 284/284（排除 RealBroker），报告核对“全部通过”；发布物 112 MB、350 个文件；干净容器 7 项全部通过，发布后的宿主打开 queue.db：SQLite 3.53.3、journal_mode=wal、synchronous=2；没有已知漏洞的包；<br>• 工作区：Debug 构建 0 警告、格式检查通过；全部测试（含 RealBroker）单元 292/292、集成 304/304；`taskset -c 0` 下运行时与契约用例（74 个）8 次全部通过；<br>• 没有残留容器与临时目录 |
| D14 | 停止状态转换修正之后（§6 决定 80）：运行时与契约用例多核 10 次；变异 M31、M32；CI 等效执行（方法同 D9）；工作区全部测试与单核复验 | 均通过：<br>• 运行时与契约用例（74 个）多核连续 10 次通过；<br>• M31、M32 都被发现，恢复后 SHA-256 一致；<br>• CI 等效执行均退出码 0：89 个包；格式检查通过；构建 0 警告；单元 292/292、集成 284/284（排除 RealBroker），报告核对“全部通过”；发布物 112 MB、350 个文件；干净容器 7 项全部通过，发布后的宿主打开 queue.db：SQLite 3.53.3、journal_mode=wal、synchronous=2；没有已知漏洞的包；<br>• 工作区：Debug 构建 0 警告、格式检查通过；全部测试（含 RealBroker）单元 292/292、集成 304/304；`taskset -c 0` 下运行时与契约用例（74 个）8 次全部通过；<br>• 没有残留容器与临时目录。<br>这是本批最终代码的验证结果。 |

| # | 变异 | 失败的测试 |
|---|---|---|
| M1 | 单实例检查失效：上一个实例仍存活时也启动新实例 | `AUnitThatDoesNotStopKeepsItsIdentifierUntilItsLoopExits` |
| M2 | 上一个实例退出后不释放就构建下一个 | `InstancesNeverOverlapAndEachIsDisposedBeforeTheNextIsBuilt` |
| M3 | 已稳定的版本上的退出也计数 | `ALeftOverRunningMarkerIsCountedOnlyForAVersionThatNeverBecameStable`、`CrashesOfANewVersionRollBackOnceToTheLastStableVersion`（真实进程） |
| M4 | 上一版本从未稳定也自动回滚 | `APreviousVersionThatNeverBecameStableIsNotARollbackTarget` |
| M5 | 自动回滚生成的版本再次触发保护时继续回滚 | `CrashesOfANewVersionRollBackOnceToTheLastStableVersion`（真实进程） |
| M6 | 提交种子版本时不记下种子的内容哈希（按改正后的代码重做） | `RuntimeCoordinatorTests` 7 个 |
| M7 | 正常停止时不清除“运行中”标记 | `RuntimeCoordinatorTests` 2 个 |
| M8 | 提交成功后先通知等待方、后更新状态 | `AFailedCommitRollsBackTheWholeBatchAndIsReportedAsNotTakenOver` |
| M9 | 提交失败时报告成功（假装接管） | `AFailedCommitRollsBackTheWholeBatchAndIsReportedAsNotTakenOver` |
| M10 | seq 去掉 AUTOINCREMENT | `SeqsAreNotReusedAfterTheNewestRecordIsDeleted` |
| M11 | 写连接设为 synchronous=NORMAL | 第一轮：回读检查拒绝打开，等待的用例超时，没有直接失败；第二轮仍有竞争，记为“4 个以上直接失败”，审查者重做时挂起 15 分钟（§4.11 低-2）。测试的等待加期限后重做：`DeliveryCacheTests` 13 个直接失败 |
| M12 | 通道满时无限等待空位（没有截止） | `AFullChannelSkipsCyclesAndKeepsMemoryBounded` |
| M13 | 时间来源总是 gateway | `TheTimeSourceFollowsTheClockState(NotSynchronized)` |
| M14 | 非法值写成 0，而不是标为 bad | `FaultsAppearInTheConfiguredProportionsAndBehaveAsSpecified` |
| M15 | 停机时先关闭请求队列并让排队的请求失败，再停止写者 | 第一轮没有被发现；补充用例后 `StoppingCommitsQueuedRequestsAndRejectsLaterOnes` |
| M16 | 后台服务故障时退出码仍为 0 | `AFaultedBackgroundServiceGivesANonZeroExitCode` |
| M17 | 连续运行满退避上限后退避不从头计算（审查后补充） | `TheBackoffStartsOverAfterTheUnitRanForTheResetPeriod` |
| M18 | 落后多个周期时不补计被跳过的周期（审查后补充） | `ALoopHeldUpForSeveralPeriodsSkipsTheMissedCyclesAndCountsThem` |
| M19 | 停止时先等取消回调执行完、再按期限等待循环（审查前的写法；按最终写法重做） | `AnExceptionInACancellationCallbackIsRecordedAndDoesNotEscape`、`ABlockingCancellationCallbackIsBoundedByTheStopTimeout`、`ACancellationCallbackStillRunningAfterTheLoopExitedKeepsTheIdentifierAndDelaysDisposal`（停止 10 秒内不返回即判失败，不挂起）。经协调者的用例不再失败：运行时停机步骤的兜底捕获了逃出的异常，标记照常清除 |
| M20 | 先构建有效版本、后写“运行中”标记（审查前的顺序） | `TheRunningMarkerIsWrittenBeforeTheEffectiveVersionIsBuilt` |
| M21 | 种子导入的中断次数不设上限 | `ASeedWhoseImportKeepsGettingInterruptedIsNoLongerRetried` |
| M22 | 自动回滚的中断次数不设上限 | `AnAutomaticRollbackThatKeepsGettingInterruptedEndsInRunFailure` |
| M23 | 约束错误不按请求隔离，整批回滚 | `ARequestThatViolatesAConstraintIsRejectedAloneAndIsNotAStorageFault` |
| M24 | 启动检查不解析运行时的部署参数（审查前的写法） | `InvalidRuntimeDeploymentParametersStopTheHostWithTheConfigurationExitCode` 2 个（真实进程） |
| M25 | 先公布故障、后启动退避计时（D12 之前的顺序） | `TheBackoffTimerIsArmedBeforeTheFailureIsPublished` |
| M26 | 循环不等取消回调执行完就结束（只观察，不等待；按最终写法重新定义） | `ACancellationCallbackStillRunningAfterTheLoopExitedKeepsTheIdentifierAndDelaysDisposal` |
| M27 | 停止时先释放实例、后等取消回调（复核之前的顺序） | `ACancellationCallbackStillRunningAfterTheLoopExitedKeepsTheIdentifierAndDelaysDisposal` |
| M28 | 中断计数不区分未生效的原因 | `OnlyTheLatestConsecutiveInterruptionsOfASeedAreCounted` |
| M29 | 中断累计计数，不看是否连续 | `OnlyTheLatestConsecutiveInterruptionsOfASeedAreCounted` |
| M30 | 重新打开写连接失败时，已取出的本批不计入失败记录数 | `ABatchInFlightWhenTheStoreCannotBeReopenedIsCountedAsFailed` |
| M31 | 停止时只要循环未完成就改为“停止中”（第三次核对之前的写法） | `AUnitThatDoesNotStopKeepsItsIdentifierUntilItsLoopExits`（再次停止时状态退回“停止中”） |
| M32 | 每次停止超时都记录超时事件 | `AUnitThatDoesNotStopKeepsItsIdentifierUntilItsLoopExits`（超时事件变为 2 条） |

### 4.11 T1 第一批独立审查

由一个审查代理只读审查本批全部改动：正确性与并发、测试是否证明了声称的内容、文档与代码是否一致、范围与依赖方向、安全与运维。审查者在自己的副本中复跑了测试，补做了 4 个变异，没有改动工作区。审查对应约 09:55 的工作区。种子内容哈希改在提交或放弃时记下、被 Host 引用的工程声明 linux-x64 这两处改动在此之前完成，审查者按文件时间核对，确认它们已在审查范围内，并另做了补充核对（见下文）。

- 结论：修改后可以验收。中级 2 项、低级 6 项，没有高级问题。审查者复跑：单元 292/292、集成 269/269（排除 RealBroker）；运行时相关 73 个用例在 `taskset -c 0` 下连续 3 次通过。
- 已修正：
  - 中-1：单元取消回调抛出的异常会冒泡到宿主，使写者不停止、请求队列不关闭、“运行中”标记不清除；回调阻塞时停止期限不起作用。审查者用进程内测试单元实测了两种情况。改为不内联执行回调，监督循环等回调执行完才结束：出错记为停止故障，阻塞按期限判定“停止超时”；运行时的停机步骤与协调者另外各自兜底（§6 决定 75，经两次调整，见 §4.10 D11、D12）。补充 4 个用例（监督者 3 个、经协调者 1 个）。
  - 中-2：运行时的部署参数（种子路径不是绝对路径、稳定时间无效）在启动检查中没有被解析，进程以 134 崩溃，与文档写的退出码 2 不符（审查者实测）。改为在启动检查中与其他部署参数一起解析，补充 2 个真实进程用例：退出码 2、没有未处理异常、数据目录中不留文件。
  - 低-1：“连续运行满退避上限后退避从头计算”“落后多个周期时补计被跳过的周期”两项没有测试（审查者的变异没有被发现）。各补一个用例，变异 M17、M18 验证（§4.10）。
  - 低-2：写者打不开发送缓存时，`DeliveryCacheTests` 的部分等待没有期限，用例挂起而不是失败；§4.10 原先的 M11 记录与审查者重做的结果不符。所有等待都加了期限，CI 的测试步骤加挂起检测（§6 决定 78），M11 按改正后的测试重做并更正记录。
  - 低-3：三处说法过宽，已改正：
    - “每个物理连接都显式设置并回读”：只有写连接与初始化连接这样做，读连接不设置；
    - `IInputReadReporter` 的“不做 I/O、不阻塞”：状态转换时会同步写一条运行事件；
    - “各周期的结果只取决于周期序号”：应为“第 k 次读取的结果只取决于 k”。
  - 低-4：一个请求的数据错误使整批回滚，并被记为“存储异常”。改为每个请求一个保存点（§6 决定 77），补充 1 个用例。失败注入原用触发器中的 `RAISE(ABORT)`，SQLite 把它报为约束错误，改为触发器中的整数溢出（一般错误）。
  - 低-5：
    - 重新打开写连接失败时，已取出的本批不计入失败记录数，已改正；
    - 启动保护有两个缺口：构建有效版本的过程中崩溃不计数；导入或回滚在提交前反复中断时快照无限增长。改为先写“运行中”标记再构建，并给中断次数设上限（§6 决定 76），补充 3 个用例。
  - 低-6：
    - 写明标准信封在产品中的使用者是 T1-04；
    - 异常信息的脱敏列入 §7 第 22 项；
    - 默认种子改用小端读取，x64 上数值不变；
    - 遗留的临时目录已删除。
- 补充核对上述两处改动（审查者另行报告）：
  - 种子内容哈希：代码与文档一致，自动回滚不改 seed_hash。审查者指出新的低级风险：改动之后，准备阶段的确定性崩溃不再受“只导入一次”的限制，会形成无界的崩溃循环，配置库随之增长。这与低-5 的第二个缺口一同处理（§6 决定 76）。
  - linux-x64：三个锁文件都有该平台段，工程规则测试同样检查其中的依赖方向。审查者在最终工作区的干净副本中按 CI 的顺序执行（使用已有的 NuGet 缓存）：单元 292/292、集成 269/269，locked mode 下自包含发布没有 NU1004。
  - D9 的运行早于第一处改动：已在 D9 注明，最终工作区的 CI 等效执行见 D12。
- 修正复核（审查者在 10:55 的快照上复验；之后只有本文件有改动）：
  - 结论：2 个中级问题都已修正，按原复现条件不再出现：
    - 回调抛出异常：经运行时停止不再抛出，写者停止，迟到的请求报告“发送缓存已停止”，记一条停止故障；
    - 回调阻塞 3 秒、期限 200 毫秒：200 毫秒返回“停止超时”，单元仍占用标识；
    - 部署参数无效的两种情况：退出码 2，日志写明原因，数据目录为空，没有崩溃报告。
  - 其余低级问题都已处理。审查者重做的变异中，退避重置、补计、M11（13 个直接失败）、保存点隔离均被发现。审查者在快照上复跑：单元 292/292、集成 282/282（排除 RealBroker）；`taskset -c 0` 下 76 个用例 2 次通过；UnitSupervisorTests 多核 15 次通过。
  - 复核提出的新意见（均为低级），处理如下：
    - 中断计数的“进程中断”条件、重新打开失败时计入在途本批，这两项改正没有用例守住：各补 1 个用例，变异 M28、M30 被发现；
    - 中断计数是累计的，之后成功导入也不清零：改为只计最近一段连续的中断（§6 决定 76），变异 M29 被发现；
    - 提交或放弃时存储失败留下的 pending 也计为中断、被封顶种子的出路（只改格式不算改变）：写入 §2.14 与决定 76；
    - 写入结果不能以程序方式区分“数据错误被拒绝”与“存储失败”：列入 §7 第 23 项，T1-04 之前处理；
    - 停止时实例在等回调之前就被释放，仍在执行的回调可能与释放同时操作同一连接：改为先等回调、再释放，扩展了原有用例，变异 M27 被发现；
    - “构建或激活过程中崩溃都计入”对新导入与自动回滚不成立：已统一 §2.14 第 6 步、协调者与版本存储的注释。
  - 审查者在 10:3x 的快照上独立发现了 UnitSupervisorTests 的偶发失败（多核 15 次中 5 次），原因与 §4.10 D12 相同，已由决定 79 修正；在两步之间插入 30 毫秒时，有 4 个用例必然失败。
  - 复核未检查：RealBroker 类别、空缓存还原与干净容器（作者的 D12—D14 覆盖）；作者的 M17—M26 大多没有重做。
- 第三次核对（11:05 的快照，只看监督者的两处改动：先建计时器、锁内取消）：
  - 两处都成立。先后关系严密：令牌只在停止的锁内取消，`_cancellation` 在同一段锁内记下，循环看到取消之后要先拿锁才能读取，一定读到非空值。实测确认 CancelAsync 立即返回、回调在另一个线程执行；回调出错的情况重复 200 次，停止返回之前事件都已记下，状态与最后错误都一致。
  - 新的低级问题两项，都是原写法就有的：
    - 再次停止时状态退回“停止中”并重复记录超时事件；
    - 循环设为“已停止”与任务完成之间的窗口内期限到期时，状态永远停在“停止超时”。
  - 两项都已修正（§6 决定 80），前者有用例，变异 M31、M32 被发现；后者没有确定性用例。
- 最终核对（11:16:38 的快照，只看复核意见的处理）：
  - 没有中级以上问题。审查者在快照上复跑：单元 292/292、集成 284/284（排除 RealBroker），UnitSupervisorTests 多核连续 5 次通过。审查者重做的 M27、M29 都被发现；M30 只读了代码，确认用例有效。
  - 指出两处文档与事实不符：一是本节写的 D13 当时还没有写入（D13 在 CI 等效执行结束后补上）；二是本节当时没有第三次核对的结论。两处都已补齐。
  - 低级建议：契约应要求单元在 `RunAsync` 返回之前释放自己的取消注册；“回调与实例的释放不会同时执行”只对停止时正在运行的实例成立。前者写入契约（决定 80），后者改了措辞。
- 决定 80 的核对（11:33:19 的快照，之后没有再改代码）：没有中级以上问题。
  - 审查者复跑：单元 292/292、集成 284/284（排除 RealBroker），UnitSupervisorTests 多核连续 10 次通过。
  - 再次停止：等待期间保持“停止超时”，超时事件只有 1 条，放开后变为“已停止”。
  - 按复现原问题的条件，在“设为已停止”与任务完成之间插入 120 毫秒延迟：停止正常返回，最终状态为“已停止”，没有超时事件。
  - 代码、契约补充、§2.14 的状态规则、决定 80 与本节概述都与事实相符。审查者指出的两处文档问题已改正：本条原先写“没有再请审查者核对”；D14 当时还没有写入。
  - 本批最终代码的验证见 §4.10 D14。
- 审查者在 scratchpad 中复做 M11 时，有一个挂起的测试进程（PID 2594423，睡眠状态，不占 CPU），它的响应文件 `/tmp/tmpDBvUHj.tmp` 也一并保留。审查者尝试结束该进程时被本会话的权限设置拦截，于是交给维护者处理。作者没有替它结束这个进程（见报告）。
- 审查者未检查的部分：RealBroker 类别、自包含发布与干净容器验证（作者的 §4.10 D9、D12—D14 覆盖）；作者 16 个变异中的大部分；SQLITE_FULL、COMMIT 阶段失败、慢盘与断电的实际表现（只读了代码）；T1-04 之后分发器与写者的交互。

### 4.12 T1-04 本地执行记录（2026-10-08）

环境：Ubuntu 26.04、Linux x64、SDK 10.0.401、.NET/ASP.NET Core 10.0.12、SQLite 3.53.3；MQTTnet 5.2.0.1603。真实 Broker 为本机 Docker 中的 `eclipse-mosquitto:2.1.2-alpine`（digest `sha256:38c0da4f2ef84284d47b3b3eeea1cb3bdeabe81ee10caf0cd5c5ff61ee3ea408`），使用临时合成账号、临时证书和回环端口。干净容器基础镜像为 `mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da`。

实际命令（均在仓库根目录）：

```bash
dotnet restore TenonEdge.sln --force-evaluate --verbosity quiet
dotnet restore TenonEdge.sln --locked-mode --verbosity quiet
dotnet build TenonEdge.sln --no-restore -c Release --verbosity quiet
dotnet format TenonEdge.sln --verify-no-changes --no-restore --verbosity quiet
dotnet test TenonEdge.sln -c Release --no-build --blame-hang-timeout 5m --blame-hang-dump-type none --results-directory artifacts/test-results/t1-04-final-r5 --logger 'trx;LogFilePrefix=t1-04-final-r5' --logger 'console;verbosity=normal'
python3 .github/scripts/verify-test-results.py artifacts/test-results/t1-04-final-r5 --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests
dotnet publish src/TenonEdge.Host/TenonEdge.Host.csproj -c Release -r linux-x64 --self-contained --no-restore --output artifacts/publish/t1-04-final-r5-linux-x64 --verbosity quiet
bash .github/scripts/verify-published-host.sh artifacts/publish/t1-04-final-r5-linux-x64 artifacts/publish-check/t1-04-final-r5
git diff --check
```

执行过程：

- 首轮专项：单元 24/24；集成 42/42（缓存、分发、凭证及真实 MQTT）。随后补充种子提交/审计的原子作废、冻结前拒绝秘密、暂停期间使用非秘密身份、完整宿主到 Broker 的实际链路。
- 全量第一轮：单元 317/317；集成 330/335，5 处失败均为新增模块后的契约清单更新：未注册输出模块提示、两个端点清单断言、operationId 清单、凭证逻辑引用主键分类。报告保留于 `artifacts/test-results/t1-04-final`。
- 第二轮：单元 317/317；集成 333/335。发现分组路由的空后缀生成了多余尾斜杠，改为在 connections 分组中显式映射 mqtt-credential；没有放宽端点清单。报告保留于 `artifacts/test-results/t1-04-final-r2`。
- 第三轮：单元 317/317、集成 335/335；格式核查通过。删库约束失败现在记为存储故障，确认编号跨重建保留，测试同时断言没有被误记为丢弃。报告保留于 `artifacts/test-results/t1-04-final-r3`。
- 第四轮：单元 319/319、集成 335/335；补充 IPv6 的展开/压缩/方括号规范化断言。随后修正默认 ClientId，使用持久 NodeId 与 connectionId，并将完整宿主用例改为验证默认 ClientId。最终结果见下表。
- 初期格式检查曾报告新增文件的 using 顺序，已修正；构建中修正编译与风格诊断后执行最终检查。早期通过报告不替代最终结果。

| 记录 | 实际结果 | 证据 |
|---|---|---|
| E1 locked restore | 通过；真实重新生成锁文件后 locked mode 校验通过 | `artifacts/environment/t1-04/locked-restore.txt` |
| E2 Release build | 通过，0 警告、0 错误 | `artifacts/environment/t1-04/release-build.txt` |
| E3 format verify | 通过，无格式或风格改动 | `artifacts/environment/t1-04/format-check.txt` |
| E4 最终全量回归 | 单元 320/320、集成 335/335；总计 655，零失败、零跳过；含 28 个 RealBroker 分类用例（其中 8 个 T1-04 场景） | `artifacts/test-results/t1-04-final-r5`、`artifacts/environment/t1-04/full-tests.txt` |
| E5 TRX 核对 | 两个程序集报告均通过，执行数与总数一致 | `verify-test-results.py` 的上述命令输出 |
| E6 linux-x64 自包含发布 | 通过；发布物含 MQTTnet.dll，与锁定版本一致 | `artifacts/publish/t1-04-final-r5-linux-x64` |
| E7 干净容器复验 | 全部通过：无预装 .NET，健康/就绪 200，首次设置，SIGTERM 与换路径重启，第二实例拒绝，未挂载数据卷/低 SQLite 版本阻止运行 | `artifacts/publish-check/t1-04-final-r5` |
| E8 工作区核查 | `git diff --check` 通过；保留 T1 第一批未提交改动；停止在 T1-04 | 当前工作区 |

T1-04 代码与当前范围验证已完成，状态为待验收。没有阻塞本任务的失败或跳过；§2.17 的未覆盖项目不算通过。前四轮报告保留为过程证据，最终结论使用 E4 的 r5 报告。

证据：TRX 在 `artifacts/test-results/t1-04-final-r5`；命令输出在 `artifacts/environment/t1-04`；自包含发布物在 `artifacts/publish/t1-04-final-r5-linux-x64`，本机验证用，不发布；容器输出、发布物 SHA-256 清单及实际日志在 `artifacts/publish-check/t1-04-final-r5`。这些目录按工程约定被 Git 忽略，本次不提交生成物。GitHub Actions 未执行，CI 配置更新不算实际远端通过。

### 4.13 T1-05 本地执行记录（2026-10-08）

环境沿用 §4.12：Ubuntu 26.04 / Linux x64、SDK 10.0.401、Runtime 10.0.12、SQLite 3.53.3、Docker Mosquitto 2.1.2-alpine；所有故障注入在测试临时目录和测试 Broker 中进行。无实物设备验证。

过程记录（保留失败报告）：

- 专项 r1：43/44；失败为旧用例在初始化后替换 queue.db 路径时，新增清理过早打开并保留了写连接。改为每分钟清理时短暂打开连接，业务写连接仍由首次写入请求打开；保留原故障用例。
- 专项 r2：62/62，零失败、零跳过，含凭证失败区声明。之后补充探测后网络失败和 Retry-After 的实际等待用例。
- 全量 r1：单元 325/325、集成 359/361；失败为进程用例仍预期 schema 1，以及发送循环内部退避漏记超时运行事件。更新 schema 精确预期为 2，恢复 `output.retry` 事件，保留原 MQTT 超时事件断言并增加失败次数为 0 的断言。
- 全量 r2：单元 325/325、集成 361/361，共 686，零失败、零跳过；包含真实 Broker 场景。报告核查脚本通过，以本轮为最终代码回归证据。

命令：

```bash
dotnet format TenonEdge.sln --no-restore --verbosity quiet
dotnet build TenonEdge.sln --no-restore -c Release --verbosity quiet
dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~OutputFailureTests|FullyQualifiedName~DeliveryFailureStoreTests|FullyQualifiedName~OutputPipelineTests|FullyQualifiedName~DeliveryCacheTests|FullyQualifiedName~MqttCredentialTests' --results-directory artifacts/test-results/t1-05-targeted-r2 --logger 'trx;LogFilePrefix=t1-05' --verbosity quiet
dotnet test TenonEdge.sln -c Release --no-restore --blame-hang-timeout 5m --blame-hang-dump-type none --results-directory artifacts/test-results/t1-05-full-r2 --logger 'trx;LogFilePrefix=t1-05' --logger 'console;verbosity=minimal'
python3 .github/scripts/verify-test-results.py artifacts/test-results/t1-05-full-r2 --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests
dotnet restore TenonEdge.sln --locked-mode --verbosity quiet
dotnet format TenonEdge.sln --verify-no-changes --no-restore --verbosity quiet
dotnet publish src/TenonEdge.Host/TenonEdge.Host.csproj -c Release -r linux-x64 --self-contained --no-restore --output artifacts/publish/t1-05-linux-x64 --verbosity quiet
bash .github/scripts/verify-published-host.sh artifacts/publish/t1-05-linux-x64 artifacts/publish-check/t1-05
git diff --check
```

| 检查 | 结果 | 证据 |
|---|---|---|
| Release 构建 | 通过，0 警告/错误 | 本节命令及全量测试构建输出 |
| 单元/集成全量 | 686/686，零失败、零跳过；报告核查通过 | `artifacts/test-results/t1-05-full-r2` |
| locked restore / format verify / diff check | 通过 | 本节命令 |
| linux-x64 自包含发布 | 通过 | `artifacts/publish/t1-05-linux-x64` |
| 干净容器验证 | 全部通过：无预装 .NET、健康/就绪、首次设置、正常退出、换路径重启、单实例、无持久卷与低 SQLite 拒绝运行 | `artifacts/publish-check/t1-05/summary.txt` |

本次新增 31 个用例：退避单元测试 5 个，发送结果/失败区集成测试 25 个，失败区凭证声明场景 1 个。失败区上限用缩小参数做边界验证，不声明生产容量实测。GitHub Actions 未执行；测试报告及本地发布物由 Git 忽略，没有分发。


### 4.14 T1-06 本地执行记录（2026-10-08）

环境沿用 §4.13：Ubuntu 26.04 / Linux x64、SDK 10.0.401、Runtime 10.0.12、SQLite 3.53.3、Docker Mosquitto 2.1.2-alpine。故障注入仅在测试临时数据库与测试容器中进行，无真实客户配置或设备。

过程：专项 r1 54/54、r2 52/52（筛选范围不同），均零失败/跳过；专项 r3 在编译阶段发现新测试缺少命名空间导入和静态数组约定，未执行用例，不计通过。修正后全量 r1 单元 325/325、集成 387/387，总计 712，零失败、零跳过，报告核查通过。原有 MQTT 真实 Broker 场景未过滤。随后收尾复核补充“隔离初始化失败不退出宿主”的处理和回归用例，最终全量 r2 单元 325/325、集成 388/388，共 713，零失败/跳过，报告核查通过，以该轮为准。

```bash
dotnet build TenonEdge.sln --no-restore -c Release --verbosity quiet
dotnet format TenonEdge.sln --no-restore --verbosity quiet
dotnet test tests/IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~OutputControlTests|FullyQualifiedName~OutputFailureTests|FullyQualifiedName~DeliveryPolicyTests|FullyQualifiedName~ManagementApiAccessTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~GatewayIdentityTests' --results-directory artifacts/test-results/t1-06-targeted-r2 --logger 'trx;LogFilePrefix=t1-06' --verbosity quiet
dotnet test TenonEdge.sln -c Release --no-restore --blame-hang-timeout 5m --blame-hang-dump-type none --results-directory artifacts/test-results/t1-06-full-r2 --logger 'trx;LogFilePrefix=t1-06' --logger 'console;verbosity=minimal'
python3 .github/scripts/verify-test-results.py artifacts/test-results/t1-06-full-r2 --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests
dotnet restore TenonEdge.sln --locked-mode --verbosity quiet
dotnet format TenonEdge.sln --verify-no-changes --no-restore --verbosity quiet
dotnet publish src/TenonEdge.Host/TenonEdge.Host.csproj -c Release -r linux-x64 --self-contained --no-restore --output artifacts/publish/t1-06-final-r2-linux-x64 --verbosity quiet
bash .github/scripts/verify-published-host.sh artifacts/publish/t1-06-final-r2-linux-x64 artifacts/publish-check/t1-06-final-r2
git diff --check
```

| 检查 | 结果 | 证据 |
|---|---|---|
| Release 构建 | 通过，0 警告/错误 | 本节命令及全量构建输出 |
| 单元/集成全量与报告检查 | 最终 r2 713/713，零失败/跳过；报告核查通过 | `artifacts/test-results/t1-06-full-r2` |
| locked restore | 通过 | 本节命令 |
| format verify / 自包含发布 / 干净容器 / diff check | 全部通过；容器验证含首次设置、换路径重启、单实例与拒绝不支持环境 | `artifacts/publish/t1-06-final-r2-linux-x64`、`artifacts/publish-check/t1-06-final-r2` |

T1-05、T1-06 均留在工作区待验收；当前 HEAD 仍为 `c97521d`。未执行 GitHub Actions；本地测试与发布产物被 Git 忽略，没有发布或推送。

## 5. SDK 与依赖清单

| 组件 | 版本 | 用途 | 许可 | 来源 |
|---|---|---|---|---|
| .NET SDK | 10.0.401（运行时 10.0.12） | 构建与运行 | MIT | builds.dotnet.microsoft.com，SHA-512 已核对 |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | Host：OpenAPI 生成 | MIT | nuget.org |
| Microsoft.OpenApi | 2.12.0（传递依赖） | Host | MIT | nuget.org |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | 集成测试 | MIT | nuget.org |
| Microsoft.NET.Test.Sdk | 18.10.1 | VSTest | MIT | nuget.org |
| xunit.v3.mtp-off | 4.0.1 | 测试框架（关闭 MTP） | Apache-2.0 | nuget.org |
| xunit.runner.visualstudio | 4.0.0 | VSTest 适配器 | Apache-2.0 | nuget.org |
| SqlSugarCoreNoDrive | 5.1.4.221 | Host：配置库访问层（T0-05） | 待核查（D-09：NuGet 为 Apache-2.0 链接，GitHub 为 MIT） | nuget.org |
| Microsoft.Data.Sqlite | 10.0.12 | Host：配置库驱动（T0-05）；Runtime：发送缓存 queue.db（T1-03）。两者同一版本，加载同一个原生库 SQLite 3.53.3 | MIT（SQLitePCLRaw 为 Apache-2.0） | nuget.org |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12（传递依赖 Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12） | Runtime：日志（T1-01）。Host 与发布物中由 ASP.NET Core 共享框架提供，NuGet 依赖被剪除，不另外分发 | MIT | nuget.org |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | 单元与集成测试：可控时钟 FakeTimeProvider（T1） | MIT | nuget.org |
| Microsoft.NETCore.App.Runtime.linux-x64、Microsoft.AspNetCore.App.Runtime.linux-x64 | 10.0.12 | 自包含发布带入的运行时（T0-13） | MIT；包内另有 THIRD-PARTY-NOTICES.TXT | nuget.org |
| MQTTnet | 5.2.0.1603 | Adapters：正式 MQTT 输出（T1-04）；集成测试：T0-11/T1-04 | MIT | nuget.org |
| NModbus | 3.0.83 | 集成测试：T0-12 比较 | MIT | nuget.org |
| FluentModbus | 5.3.2（带入 System.IO.Ports 5.0.0 及其原生运行时包） | 集成测试：T0-12 比较 | MIT | nuget.org |
| 镜像 eclipse-mosquitto | 2.1.2-alpine（digest `sha256:38c0da4f…`） | T0-11 测试 Broker，不分发 | EPL-2.0 或 EDL-1.0 | Docker Hub |
| 镜像 mcr.microsoft.com/dotnet/runtime-deps | 10.0（digest `sha256:099f6f87…`） | T0-13 干净环境，不分发 | 镜像内各组件按其许可；产品基础镜像在 T5-04 选定并登记 | mcr.microsoft.com |

- T0-A 时生产发布物（Host）的依赖闭包只有 Microsoft.AspNetCore.OpenApi 与 Microsoft.OpenApi。T0-05 之后，随发布物分发的包共 10 个，另有 21 个包在还原图中但资产已排除（D-09），逐项见 `docs/reference-notes.md` §1。测试包不随发布物分发。完整的直接与传递依赖以三份 `packages.lock.json` 为准。
- T1 第一批新增本仓库的 Runtime、Adapters 两个程序集（Abstractions 随之进入发布物）；Host 的 Release 构建输出没有新增第三方程序集（§2.14）。Runtime 与 Adapters 的锁文件随工程建立。
- 第三方许可清单：`docs/reference-notes.md` 在 T0-05 建立，T0-13 补充了自包含发布带入的运行时（§1.3）与只用于测试的依赖、镜像和测试夹具（§4）。发行包附带的 `THIRD_PARTY_NOTICES.md` 与许可文本在 T5-07 整理。
- CI 中的 Action 均固定到提交：
  - `actions/checkout` v7.0.1（`3d3c42e5aac5ba805825da76410c181273ba90b1`）；
  - `actions/setup-dotnet` v6.0.0（`a98b56852c35b8e3190ac28c8c2271da59106c68`）；
  - `actions/upload-artifact` v7.0.1（`043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`）。

## 6. 实现决定

1—10 属于 T0-A，11—40 属于 T0-B，41 起属于 T0-C。

1. SDK 固定为 10.0.401，而不是本机原有的 10.0.400。10.0.12 是安全更新（官方元数据列出 6 个 CVE），也是开发准备 S-12 记录的最新补丁。以后升级同样通过修改 `global.json` 并重新验证完成。
2. 测试框架：xUnit.net v3 4.x 默认集成 Microsoft Testing Platform v2。官方说明，在 .NET 10 SDK 上使用 VSTest 需改用关闭 MTP 的包，因此统一采用 `xunit.v3.mtp-off` 与 `xunit.runner.visualstudio`，不混用两套运行器。
3. 分析与告警：
   - 启用 SDK 分析器的 Recommended 规则集，警告即错误。
   - IDE0005（多余 using）在构建中强制需要开启 XML 文档文件（roslyn#41640），因此只作提示。
   - NU1900（离线还原取不到漏洞库）不作为错误。
   - NU1901—NU1904（依赖存在已知漏洞）仍是错误。这是有意设置的安全门槛：漏洞库更新后，未改动的提交也可能构建失败，处理方式是升级依赖、更新锁文件并重新验证，不临时关闭审计。
4. OpenAPI 文档放在管理 API 分组内：`/api/edge-admin/v1/openapi/{documentName}.json`。按功能需求 §12.4，文档在登录后查看，非开发环境需认证；只在 Development 环境匿名开放，便于本地核对契约。
5. 认证或授权失败时只设置 401/403，由状态码页写入 ProblemDetails：不需要认证方案发起质询，也不跳转登录页。本批未注册任何认证方案，登录方式在 T0-07、T0-08 实现。匿名请求访问受保护前缀下不存在的路径时返回 404，而已存在的受保护路由返回 401。路由本身随开源代码与 OpenAPI 公开，不视为敏感信息，因此不为隐藏路由设置全局回退授权策略。
6. 健康端点用 Minimal API 实现并写入 OpenAPI，未使用 `MapHealthChecks`（后者的端点不会进入 OpenAPI）。判定仍使用框架的 `HealthCheckService`，其他组件以 `ready` 标签加入就绪条件。
7. 匿名例外清单化：测试逐项核对全部路由端点及其访问级别，新增匿名端点须同时修改测试清单；operationId 同样由测试固定清单。
8. 真实进程测试：
   - 从测试输出目录运行宿主（同一构建的副本）；
   - 以端口 0 监听，从启动日志读取系统分配的实际地址；
   - 轮询 `/readyz` 到 200 后再检查各端点；
   - 用 SIGTERM 验证正常停机。
   只在 Linux 上执行，其他平台记为跳过（CI 为 Linux）。测试进程被强行终止时，已启动的宿主进程可能残留：GitHub 托管运行器的每个作业使用新的虚拟机，结束后回收；在本地则需要手动结束。
9. 依赖规则检查的数据来源：工程与框架引用取自还原结果 `obj/project.assets.json`，包取自锁文件。这样导入文件或 SDK 隐式加入的引用也在检查范围内；未还原的工程直接报违规。
10. 调试监听地址（维护者 2026-09-30 要求）：开发启动配置监听 `0.0.0.0:18080`，便于从其他机器调试。
    - 只作用于 `dotnet run` 与 IDE 调试，不改变直接运行程序或发布物的监听行为；测试启动的宿主仍只监听 `127.0.0.1`。
    - 端口不用设计默认的 HTTP 8080：调试实例不应占用正式实例的默认端口；本机的 8080 也已被其他程序占用，再监听 `0.0.0.0:8080` 会失败。
    - Development 环境下 API 文档匿名开放，监听 `0.0.0.0` 后，同一网络中能访问该端口的主机都可以读取。本批只有健康检查与 API 文档，没有管理写接口；Development 环境不用于现场部署。
    - 正式的监听地址与端口（HTTP 8080、HTTPS 8443，可分别配置，可关闭 HTTP，总设计 §10.3）随 T0-06、T0-08 实现。进展：T0-06 已实现，调试启动配置随之改为 HTTP 18080、HTTPS 18443，都监听 0.0.0.0（决定 19）。

T0-B（文档未规定、由实现确定的部分，均可调整）：

11. 数据目录与单实例锁（T0-04）：
    - 部署参数名为 `TenonEdge:DataDirectory`。文档只要求“显式配置为绝对路径”，没有规定名称和默认值，因此不设默认值。
    - 非 Development 环境要求目录已经存在。路径写错时直接报错，而不是悄悄新建一套空数据：新数据会以未初始化状态重新开放首次设置窗口。
    - 锁文件为 `tenonedge.lock`，不写入 PID：锁被持有时，其他进程连文件内容也读不到；进程结束后留下的 PID 又会误导。
    - 退出码 2（部署参数或路径错误）、3（数据目录被占用）由本实现约定。
12. 配置库访问（T0-05）：
    - PRAGMA 挂载点用 CheckConnectionExecuted，V-06 的结论是可用，不需要改用长连接。SqlSugar 只在“日志事件”开关打开时触发连接事件：在连接配置中设置 AopEvents 即打开；它的部分维护操作会临时关闭这个开关，所以配置库不使用 CodeFirst、DbMaintenance 和 BackupDataBase（BackupDataBase 还会把 synchronous 改为 NORMAL）。
    - SqlSugar 经 Microsoft.Data.Sqlite 开始的事务是 BEGIN IMMEDIATE：一开始就取得写锁，写事务依次排队。只读查询不开事务；锁等待的上限为 busy_timeout 5 秒，外加命令超时 30 秒内的重试。
    - 同一异步流程内并发时必须用 CopyNew：共用同一客户端会共用一个连接，探针中出现过异常。
    - busy_timeout、命令超时和外键开关（连接串中开启）由本实现确定，文档未规定。时间一律以 UTC Unix 毫秒整数保存。
13. 迁移（T0-05）：
    - schema 版本以迁移历史表的最大版本号为准。SystemState 所列的“schema 版本”不另存一份，避免两处不一致。
    - 配置库无法打开、迁移失败或版本高于本程序时，整个宿主不启动（退出码 4），而不只是运行时不启动：账号也保存在配置库中，库不可用时管理界面同样无法使用，须按恢复流程处理（总设计 §8.5）。
14. D-09（未确认前按推荐方案）：排除 SQL Server 驱动栈共 21 个包的资产，同一开关也用于测试工程；包仍在锁文件中，漏洞审计照常覆盖。详见 `docs/reference-notes.md` §2。维护者改变 Q21 时，只影响配置库访问层。
15. 网关身份（T0-06）：NodeId、configStoreId 为随机 UUID，以 SystemState 为准。总设计把 NodeId 同时列在 SystemState 和“身份与本地秘密文件”中，没有规定以哪一处为准；只保存一处，避免两处不一致。两者在配置库创建后的第一次启动时生成（迁移之后立即生成）。
16. 雪花 ID 与下限（T0-06）：
    - SystemState 中的 ID 下限和配置版本号下限，含义统一为“新分配的值必须大于它”。总设计对 ID 写“大于下限”，对版本号写“不低于下限”，恢复时写入的又是“大于出现过的全部版本号”，这里取一致的严格大于。
    - “构建时间”取程序集元数据中的固定日期，不读取构建机时钟，以保持确定性构建；随版本发布更新，也可用构建属性覆盖。它只是下限，偏旧不影响唯一性。
    - 不可信时钟的告警在每次进入不可信状态时记录一次，避免刷屏。
17. 密钥环（T0-06，V-08）：接受 Linux 上密钥不加密落盘（总设计 §10.3）。框架在生成密钥时（首次启动，之后按默认约每 90 天轮换一次）记录“未配置密钥加密器”警告（XmlKeyManager 事件 35），这是预期的，不关闭该警告；访问控制依靠目录权限 700，含密钥的备份按秘密级别保管。认证 Cookie 与之后的凭证共用同一密钥环，以不同的用途字符串隔离。
18. 自签证书（T0-06，文档未规定参数）：
    - RSA 2048、SHA-256，有效期 825 天：部分客户端不接受更长的服务器证书，剩余不足 30 天时在启动时重签；
    - 使用者可选名称包含生成时本机的单播地址；
    - 私钥经 Data Protection 保护后保存；
    - 原文件无法解密时重新生成并告警，保证 HTTPS 可用，同时提示检查密钥环。
19. 监听（T0-06）：
    - 部署参数为 `TenonEdge:Http`、`TenonEdge:Https`，默认监听全部 IPv4 与 IPv6 地址。显式配置监听后，ASP.NET Core 的 urls 参数不再生效，启动说明改用上述参数。
    - 监听端点在启动检查的最后一步加入：WebApplication 构建时就会创建服务器，不能在它的选项配置中取证书，否则会在取得锁之前打开配置库。
    - 系统设置中的监听地址与 HTTPS 设置（SystemState）随 T3-09 实现。
    - 调试启动配置监听 0.0.0.0 的 18080 与 18443（维护者 2026-09-30 要求，见决定 10）。
20. 容器数据卷检查（T0-06，文档未规定检测方法）：
    - 以 /.dockerenv、/run/.containerenv、`DOTNET_RUNNING_IN_CONTAINER`、`container` 判断是否在容器中；
    - 从 /proc/self/mountinfo 取数据目录所在的最长挂载点；挂载点为根目录，或文件系统为 overlay、tmpfs 等时，视为不持久；
    - 不满足时只阻止运行时，管理界面仍可使用（与 SQLite 版本不足的处理一致）；
    - Kubernetes emptyDir 这类“挂载了、但随 Pod 删除”的卷无法据此识别，部署文档需要说明。
21. 首次设置接口（T0-07，文档未规定的细节）：
    - 已有管理员与窗口已关闭都返回 409，分别以 code `setup.alreadyCompleted`、`setup.windowClosed` 区分；字段错误统一用 ValidationProblem 的 errors 结构。
    - 设置成功后直接签发会话，对应功能需求“设置后自动登录”。
    - 窗口时长暂为固定的 30 分钟，作为运行参数可调整的部分随 T3-09 实现。
    - 重置待设置时，账号固定为被重置的账号；请求中给出其他账号时按字段错误拒绝。
22. 账号与登录（T0-07，文档未规定的细节）：
    - 账号名按小写规范化后比较，Admin 与 admin 视为同一账号。
    - 密码最长 128 位；弱口令表写在代码中（`PasswordPolicy`），不区分大小写比较；修改密码时新密码不能与原密码相同。
    - 第 5 次失败即返回“尝试次数过多”；锁定期间的尝试不延长锁定。同一账号名的尝试在进程内排队逐个处理（按账号名哈希分到 64 个队列），这两点在并发请求下同样成立（T0-B 审查后修正）。
    - 已有账号的计数保存在库中；不存在的账号名只在内存中计数，最多 1 万条，超出时先移除过期的，再移除最早失败的；重启后清零。攻击者可以用大量不同的账号名挤掉某个不存在账号名的计数，在约 1 万次带哈希的尝试之后据此分辨账号是否存在，属已知的有限风险。
    - 已停用或没有密码的账号，同样返回“账号或密码错误”，并执行等量的哈希校验。已有账号失败时另有一次计数写入（同步写盘），存储较慢时响应时间仍可能不同；本机测量未发现可区分的差异（§4.7）。
    - 登录与登出的审计单独写入，失败时改写到日志；首次设置、重置和修改密码的审计与业务写入在同一事务中，审计写不进去时操作一并失败。
23. 主机侧重置（T0-07）：
    - 命令形式为 `TenonEdge.Host reset-admin [--account <账号>]`；安装包提供的 `tenonedge` 命令名随 T5 打包。
    - 只能重置启用中的管理员。审计的“用户”留空，“对象”为被重置的账号，说明中记录执行命令的操作系统用户。
    - 以固定的程序目录读取配置，与当前工作目录无关；无法执行时退出码为 5。
24. 请求体上限（T0-07）：以 Kestrel 的全局上限实现默认 1 MiB。进程内测试使用的 TestServer 不执行这项限制，因此由真实进程测试验证。
25. 授权策略（T0-08）：策略名为 `role:administrator`、`role:configurator`，角色以字符串保存；只读状态查询（会话、运行事件、系统信息）只要求登录，与总设计 §10.1“除只读状态查询外，接口都声明所需角色”一致。
26. 会话失效（T0-08）：
    - 每个账号的存在、启用、安全戳与角色，以及全局会话代次，缓存在内存中；首次用到时从库中读取，修改这些字段的代码路径随即清除缓存。校验会话不写库。
    - 单实例锁保证只有一个进程写库；主机侧重置只能在服务停止时执行，服务启动时缓存为空。
    - 进程内读取与失效可能并发：每次失效给该账号的版本号加一，读取前后版本号不同则丢弃读到的值重新读取，避免提交之前读到的旧安全戳在失效之后写入缓存（T0-B 审查后修正）。
    - 会话代次保存在 SystemState 中，加一后写库再更新内存。
27. 防 CSRF（T0-08）：采用与会话绑定的令牌，而不是另设 Cookie 的双重提交：令牌写在会话票据中，登录、首次设置、修改密码和查看会话的响应都返回它；前端刷新页面后可从 `GET /auth/session` 重新取得。只校验以 Cookie 认证的写请求；API 令牌不走 Cookie，按总设计不做 CSRF 校验（T3-09）。
28. 转发头（T0-08）：只处理 `X-Forwarded-Proto` 与 `X-Forwarded-For`，都只取最后一跳（ForwardLimit 1，即只信任直接相连的已登记代理）；不处理 `X-Forwarded-Host` 与前缀。ASP.NET Core 自带的 `ASPNETCORE_FORWARDEDHEADERS_ENABLED` 被忽略并输出警告，避免部署时误开后信任任意来源的转发头。
29. 登录限流（T0-08）：固定窗口，每个来源地址每分钟 10 次，不排队；与账号锁定（§6 决定 22）相互独立。数值由本实现确定，文档只要求“按来源 IP 限流”。
30. 运行事件（T0-09）：
    - 合并的键为类型、对象与级别；窗口从第一次发生起算 5 分钟，使“5 分钟内断开 12 次”这样的计数成立；说明取最近一次。
    - 查询按首次发生（即 ID）排序并分页：合并会更新最近一次的时间，按它分页时翻页过程中顺序可能变化。
    - 运行时启动条件不满足时记为 `runtime.blocked` 错误事件（对象为原因代码）；总设计的事件清单未单列这一项，它属于“应用成败”一类的状态变化。
31. 降级记录（T0-09）：任务名中的“降级记录”按总设计 §3.3 理解为“存储受限时审计与运行事件写不进配置库的处理”：改写到标准输出，不阻断登录等正常操作，输出条数有上限。与业务写入在同一事务中的审计（首次设置、主机重置、修改密码）仍随操作一起失败，不改写到日志（§6 决定 22）。每分钟 20 条的上限由本实现确定。
32. 保留与清理（T0-09）：
    - 审计保留期先以部署参数 `TenonEdge:Audit:RetentionDays` 调整（默认 180，不接受更短的值，最长 36,500 天），系统设置中的调整随 T3-09；运行事件按最近一次发生计算 7 天。
    - 清理在启动后与之后每小时执行，分批删除，避免长时间占用写锁。
    - 只有保存的时间可信的记录才按保留期删除：保存的时间（运行事件取首次发生）不早于 ID 中的逻辑时间戳减 1 小时。写入时时钟落后的记录真实时间未知，不按保留期删除；代价是这类记录可能一直保留，符合“不少于六个月”的要求，运行事件仍受 1 万条上限约束（T0-B 审查后修正）。容差 1 小时由本实现确定：时钟正常时，两者只差读取时钟的先后与小幅校时。
33. 系统信息（T0-09）：
    - 功能需求 §10.1 规定三种角色都可查看，其中包括数据目录路径，按此实现；
    - 磁盘空间为数据目录所在文件系统的总空间与非特权进程可用的空间；
    - 时钟同步状态依赖 T1-07 的校时检测，本批不返回该字段，而不是返回“未知”；
    - 版本暂用程序集的 InformationalVersion，正式版本号规则随 T5-07（版本、构建与依赖记录）。
34. 日志（T0-09）：默认沿用 ASP.NET Core 的简单控制台格式；需要逐条 JSON 时使用框架自带的 `Logging:Console:FormatterName=json`，不引入第三方日志库。框架 `XmlKeyManager` 类别的最低级别固定为 Debug，规则的写法与依据见 §2.9 脱敏说明。
35. 运行事件查询的 `level` 参数（T0-09）：Minimal API 对枚举类型的查询参数调用不忽略大小写的 `Enum.TryParse`（aspnetcore v10.0.0 `ParameterBindingMethodCache`），而 API 返回的级别是小写字符串，`level=warning` 因此被拒绝。改为以字符串接收，只接受与返回值相同的 info、warning、error。
36. 修改密码的失败计数（T0-B 审查后补充）：
    - 原密码错误按账号在内存中单独计数，连续 5 次时更换安全戳、结束该账号的全部会话（包括当前会话，返回 401 `auth.sessionEnded`），并记审计。拿到会话的人因此最多猜几次原密码，会话随即失效；这个计数只能由已登录的会话触发，重启后清零。
    - 不与登录锁定合并：登录锁定可以由网络上的任何人触发并一直维持，合并后已登录的用户在锁定期间无法修改密码，也就无法借改密码结束被窃取的会话（复核时审查者指出，第一次修正即是如此）。
    - 计数只在修改成功或重启时清零，不随时间衰减：间隔很久的输错也会累计，到上限时只是需要重新登录；若随时间衰减，猜测者放慢速度即可绕过上限。
    - 接口与登录共用按来源地址的限流。写入新密码时核对密码哈希仍是校验时的那一个，期间被改过则按原密码不正确处理。
37. 不存在的账号名（T0-B 审查后补充）：登录失败时不写入审计，只记来源地址与“账号不存在”。符合账号名格式的字符串也可能是误输入的密码，按格式过滤不够，因此一律不记。
38. 首次设置窗口的计时（T0-B 审查后修正）：按单调计时计算 30 分钟；显示的关闭时间为“当前时间加剩余时长”，系统时钟被校正后仍与实际一致。
39. 启动时的 SQLite 错误（T0-B 审查后修正）：宿主与主机侧维护命令都把访问配置库时的 SQLite 错误（`SqliteException`）按配置库不可用处理，退出码 4，并输出一行原因。SqlSugar 自行包装后抛出的异常（若有）是否同样处理，未验证。
40. 测试基础设施（T0-B 审查后修正）：固定数据目录的进程内测试工厂在释放后等到数据目录锁可以取得再返回，见 §4.6 B8。
41. Abstractions 工程（T0-10）：本批建立，契约只有测试使用者，Host 暂不引用；Host 在 T1-01 编译运行计划、启动运行时宿主时才引用它，避免没有用途的引用（后端实施规范 §2）。Runtime、Adapters 工程仍未建立，随 T1 的实际代码创建。
42. 字段值的表示（T0-10）：用一个不可变类 `FieldValue` 表示四种输出类型，内部表示决定 JSON 文本，而不是用 JsonNode 或 object：
    - NaN 与无穷在创建时就被拒绝，调用方据此把字段判为 nonFinite，序列化路径上不再需要另做判断；
    - 相等比较与写出的文本一致：decimal 的 1.5 与 1.50 不相等，double 的 0 与 -0 不相等；
    - JSON 读取方向（按字段描述把 JSON 数解析为 integer 或 number、超出 decimal 范围时改用 double）与换算规则属于 T2-05。
43. 配置哈希（T0-10）：按规范化 JSON 计算，属性按序号排序、去掉空白、字符串统一转义；数值保留原文，因此 1 与 1.0 视为不同的配置；对象中有重名属性时拒绝。字符串转义沿用 System.Text.Json 的默认编码器，框架升级若改变转义规则，同一配置的哈希可能变化，后果只是一次“配置已变”的重启，不影响正确性。
44. 运行单元契约（T0-10）：
    - 只规定三件事：构建只做本地计算（不做网络 I/O，包括 DNS 解析）；启动后立即返回，连接在循环中建立；停止等待循环退出，期限到时单元仍视为存活。上下文只含 TimeProvider 与输入的有界通道，不含请求作用域的对象。
    - 测试适配器的停止方式是释放连接：Linux 上释放仍有读取未完成的套接字时，.NET 以连接重置（RST）中止连接，设备一侧看到“连接被重置”而不是正常关闭；测试把两种都视为连接已释放。
45. 后台服务的未处理异常（T0-10，V-01）：产品宿主保留 .NET 的默认 StopHost——运行单元的异常应在单元边界捕获（T1-01 的监督者），万一漏出，宿主停止，而不是让后台循环悄悄停在失败状态。实测宿主因此停止时等待停机的调用正常返回；`Program` 在 `RunAsync` 之后返回 0，由此推断进程以退出码 0 结束（审查者用相同结构的程序复现了这一点）。服务管理器按“失败时重启”配置时不会重启，也会被当作正常停止。T1-01 须据后台服务的 `ExecuteTask` 是否故障改用非零退出码，并与启动保护的“运行中”标记一起测试。
46. 协议验证代码（T0-11、T0-12）：放在 IntegrationTests 的 `Probes/` 目录，与正式代码分开，按类别隔离（后端实施规范 §6.1）：
    - ModbusProbe：进程内模拟从站，CI 执行；
    - MqttProbe：只检查库的默认值，CI 执行；
    - RealBroker：需要本机 Docker 与固定的 Mosquitto 镜像，本机执行并记录；CI 用过滤器排除，因此 CI 报告中既不出现这些用例，也不出现跳过。本机没有 Docker 或镜像时，这些用例写明原因后跳过。测试进程被强制结束时可能留下容器，按 `tenonedge.probe=mqtt` 标签清理。
    - 协议候选包（MQTTnet、NModbus、FluentModbus）只由测试工程引用；正式适配器按选型结论在 T1、T2 引用。
47. 测试 Broker（T0-11）：使用 Mosquitto 2.1.2（总设计 §3.1 的默认附带 Broker），按标签与 digest 记录；配置只用于验证（禁止匿名、密码认证、TLS），不是部署模板。部署模板与实测在 T2-07、T5-04。
48. 自包含发布与干净环境（T0-13）：
    - Host 声明 `RuntimeIdentifiers` 为 linux-x64，使平台资产进入锁文件，CI 以 locked mode 还原后发布；
    - “未预装 .NET 的干净 Linux”使用 `mcr.microsoft.com/dotnet/runtime-deps:10.0`：官方为自包含应用提供的基础镜像，只含 ICU、OpenSSL 等原生依赖，没有 .NET。标签会随补丁更新，验证脚本与 CI 按 digest 固定，换用新 digest 时一并更新本文的记录；
    - 不为此改用 InvariantGlobalization，ICU 由系统提供，与以后的产品镜像一致；产品镜像的基础镜像在 T5-04 选定并登记许可。
49. Modbus 选型（T0-12）：采用 NModbus 3.0.83（理由见 §2.12），维护者于 2026-10-09 回复“都按推荐，继续”确认；允许 T2-02 正式适配，TCP UnitId 0–255、RTU over TCP 1–247。若改选 FluentModbus，须先解决 §2.12 中三项无法在库外补救的缺陷。
50. 低版本 SQLite 的验证夹具（T0-13）：从 nuget.org 下载 SQLitePCLRaw.lib.e_sqlite3 2.1.11，核对包的 SHA-512 与 nuget.org 登记一致后，只把其中的原生库换入发布物的临时副本，验证“实际加载低版本时运行时不启动”；它不进入仓库或发布物。
51. 运行单元的业务标识（T0-C 审查后修正）：总设计中 inputKey 只在设备内唯一，路线以 deviceKey/inputKey 引用数据来源（§10.2、§10.5）。标识原先只允许一段，两台设备上同名的数据来源会在运行计划中被判为重复；改为允许以 / 分隔的最多 4 段，每段沿用总设计 §6.5 的标识规则。T1-01 由 Host 编译运行计划时按此生成标识。
52. CI 不上传测试发布物（T0-C 审查后修正）：仓库公开，上传的制品任何登录用户都能下载，等同于对外分发；而发布物含许可待核查的 SqlSugarCoreNoDrive（D-09），也没有附带许可声明，按总设计 §17.3 在处理之前不得分发。CI 只在作业中生成并验证发布物，上传验证证据与发布物的 SHA-256 清单。将来需要发布测试构建时，须先处理许可并取得维护者同意。
53. QoS 0 输出的断线检测（T0-C 审查后补充）：QoS 0 发布不等确认，持续发布时既不会超时，也会抑制库的保活，设计 §2.3 的“发布连续超时后断开重连”对它不适用。实测由调用方定期发送带超时的 PINGREQ 可以在超时后发现断线（§2.11），作为 T1-04 实现 QoS 0 输出的前提；周期与阈值在 T1-04 确定。设计文档未作修改，是否把这一要求写入总设计 §2.3 由维护者决定。

54. 运行单元契约（T1-01 调整 T0-10 的契约）：T0-10 的 `IRuntimeUnit` 由单元自己启动和管理循环（StartAsync/StopAsync），监督者看不到循环是否异常结束，无法“在单元边界捕获异常、记录最后错误并按退避重启”（总设计 §8.1）。改为单元只实现循环 `RunAsync(stoppingToken)`，由监督者在线程池线程上调用、等待它返回并捕获异常；停止即取消令牌，不响应取消的阻塞读取由单元在取消回调中释放连接来打断，期限与单实例规则不变。工厂与上下文改名为 `IInputUnitFactory`、`InputUnitContext`，因为本批的工厂只服务输入；输出单元需要发送缓存与凭证解析，随 T1-04 另行定义。T0-10 的契约测试改为经真实运行时执行，原有的验证点保留。
55. 运行时的后台服务：协调者（Host）是唯一的运行时后台服务，运行时 `RuntimeHost`（Runtime/Lifecycle）是宿主级单例，不是后台服务。版本元数据在 config.db 中，属于 Host；这样 Runtime 不需要回调 Host 去写配置库，也不需要宿主（Hosting）的包，只多一个日志抽象包。“只注册一个运行时宿主”的要求由协调者满足（BE-AT-03 测试核对）。
56. 种子快照（总设计 §12 T1“运行时从种子文件加载 RuntimePlan”）：
    - 由部署参数指向文件，格式为运行计划的文本形式（schemaVersion 1、units），按运行计划规范化后作为 schema 版本 1 的快照内容；T3 的配置模型快照使用新的 schema 版本，加载时按版本解析。
    - 导入按应用的前四步执行（冻结、准备、提交，宿主启动时没有正在运行的版本，不需要静默）。
    - 种子的内容哈希在该版本提交或因构建失败放弃时记下：没有生效的种子同样不再重复导入，避免每次启动都生成一个“未生效”版本；改正种子文件即生成新版本。冻结之后、提交之前进程退出时不记，下次启动丢弃中断的版本后重新导入同一份种子（最初在冻结时就记下，中断后须改动种子内容才能重试；自查时发现并改正，补充用例）。种子相当于草稿，自动回滚之后不会把引起回滚的内容原样再应用一次（总设计 §7.4 对草稿的要求）。
    - T3 有界面应用之后，种子快照只作为无界面部署与测试的入口，是否保留由届时的任务决定。
57. 启动判定的时机与顺序：在协调者的 StartAsync 中同步完成，宿主开始接收请求之前就绪状态已经确定。顺序为：丢弃中断的 pending → 处理“运行中”标记 → 导入种子 → 判定有效版本。标记先于种子处理，崩溃计在当时运行的版本上；新种子导入后，新版本从零计数。
58. “运行中”标记：保存在 config.db 的 SystemState（WAL + synchronous=FULL），在构建有效版本的运行单元之前写入（新导入或自动回滚生成的版本在提交之后、激活之前写入，见决定 76），正常停止时清除。后台服务故障时保留，按非正常退出计数。“运行失败”与启动条件不满足时不激活，也不写标记，进程在这些状态下退出不计数。协调者的停止只执行一次，重复或并发的调用等待同一次停止，标记只清除一次。
59. 启动保护的细节：
    - 上限为 3 次（总设计 §8.4），判定是纯函数（`StartupProtection`），有单元测试；
    - 自动回滚经冻结、准备、提交，准备失败时该版本标为“未生效”并进入“运行失败”；
    - “运行失败”不另外保存状态，每次启动按计数与来源重新判定，结果相同；
    - T1 没有待发记录的目标指纹，回滚前的总设计 §8.3 检查在 T1-06、T3-06 加入。
60. 稳定时间：默认 60 秒，部署参数 `TenonEdge:Runtime:StableAfterSeconds`（1—86400 秒），供测试与现场调整；管理员在界面上调整属于运行参数，随 T1-07、T3。
61. 构建失败的处理：导入种子与自动回滚按应用的规则，任何单元构建失败都放弃该版本；进程启动时加载有效版本，按总设计 §7.4 只停止构建失败的单元并告警，其余照常运行。
62. 后台服务故障的退出码：宿主停止之后、服务容器释放之前（ApplicationStopped），检查各后台服务的执行任务，有故障时进程以退出码 6 结束。最初写成在 RunAsync 返回之后读取服务，此时容器已释放，进程以 134 崩溃；真实进程测试发现后改正。产品中没有令后台服务故障的途径，进程级的退出码 6 未验证，只有单元测试。
63. 就绪：总设计 §10.1 写明“尚无有效配置时也视为就绪”，因此 Idle 与 Active 就绪；启动判定中、启动条件不满足、运行失败、停止中为 503。
64. 启动保护的进程测试由测试强制终止进程（SIGKILL），产品中不加入“令进程退出”的测试单元或故障注入开关。强制终止与崩溃、断电一样，进程没有机会执行停机逻辑。开发计划写的“测试单元终止进程”按此方式实现。
65. 模拟输入的语义：
    - 缺字段：“未携带”，既不在值中也不在质量中；
    - 非法值：浮点读到 NaN，按 nonFinite 标为 bad；
    - 连接错误：整组读取失败，不生成消息（总设计 §6.1）。
    - 序列用 SplitMix64 与整数步长，确保跨版本、跨平台可复现；每个周期消耗固定个数的随机数，序列不受通道是否满的影响。
    - 周期用固定周期的计时器：先按时钟算出等待时长、再创建计时器的写法，在两步之间时间推进时会多等一个周期，可控时钟的测试因此偶发失败。产品中这个窗口只有几微秒，但计时器的写法没有这个问题。
66. 时钟同步状态：检测方式在 T1-07 核实（V-14）。在此之前状态为“未知”，网关生成的时间记为 gateway；只有确认未同步时才记为 gateway-unsynced，不把“未知”当作“未同步”。
67. 标准信封：values 与 quality 总是写出（可为空对象），使接收方解析时字段始终存在；collectedAt 固定三位毫秒（截断），同一字段的文本形式不随数值变化。总设计 §9.4 的示例没有毫秒，两者都是 RFC 3339。
68. queue.db 表结构：
    - connectionId 为连接的内部 ID（整数），由 T1-04 从运行计划传入；路线 ID 与节点标签只作统计标签；
    - seq 用 AUTOINCREMENT，删除后不复用，发送者按 seq 游标读取时不会漏掉新记录（不用 AUTOINCREMENT 的变异由测试发现）；
    - 状态只允许 pending、failed、quarantined（总设计 §8.2 的待发、失败区、隔离），隔离原因不设约束，便于以后增加；
    - 表结构版本用 `PRAGMA user_version`；所属 configStoreId 存在 queue_meta 中，供 T1-06、T5-02 判断记录来源。
69. 写者：
    - 提交间隔 50 毫秒、每批最多 1000 条、请求队列 1024 个，都是待实测的初始值；
    - 一个批次失败时整批回滚，同批中本来能成功的请求也报告失败——它们确实没有被接管，调用方按“未接管”处理（MQTT QoS 1 输入不确认，由 Broker 重投）；
    - 先更新状态与事件，再通知等待的一方：调用方收到失败时，状态中已是“存储异常”（测试在单核下发现原先的顺序有竞争，已改正）；
    - 提交失败后重新打开写连接；
    - 打不开发送缓存时不让等待的一方挂起：已排队的请求报告“发送缓存不可用”，写者异常退出，由监督者按退避重启后再试（最多等一个退避间隔）。最初写者打不开时请求一直等待，变异实验 M11 因此只表现为测试超时，据此改正并补充用例。
70. 分发器不在本批：输入通道的消费者要把消息交给输出适配器生成冻结的报文，与 T1-04 的第一个输出一起实现，避免在没有真实输出时先定下输出适配器组装报文的接口。标准信封是与具体输出无关的纯函数，属于 T1-02 的“统一消息”，本批先实现并测试，产品中的使用者在 T1-04。在此之前，产品中模拟输入的通道写满后每个周期都计 overrun，这是过渡状态，不是缺陷。
71. 停机顺序中的写者：先停止输入，再停止写者。写者收到停止请求后不再等提交间隔，提交请求队列中已有的请求后退出；之后才关闭请求队列，迟到的请求报告失败，不让等待的一方挂起。最初先关闭请求队列，写者因读到“队列已完成”而在没有停止请求时返回，被监督者当作故障重启；真实进程测试发现后改正。停机时的最后一次提交另有用例（变异实验 M15 起初没有被发现，据此补充）。
72. 测试基础设施：`WebApplicationFactory` 配合顶层 `Program` 时，宿主由工厂与 `Program` 中的 `RunAsync` 两处停止和释放。负载高时，测试可能在协调者完成清除标记之前就继续执行（单核复现）。固定数据目录的测试工厂在释放前先等宿主发出“已停止”，协调者的停止只执行一次（决定 58）。产品进程只有 `Program` 一条停止路径，不受影响。
73. 测试用的可控时钟：周期、退避、截止时间的测试使用 FakeTimeProvider（Microsoft.Extensions.TimeProvider.Testing，只用于测试），不依赖真实等待；写者与协调者的测试用真实时间和很短的间隔，等待用条件轮询，不用固定的 sleep 判断结果。
74. 被 Host 引用的工程同样声明 linux-x64（扩展决定 48）：Host 按 linux-x64 自包含发布时，被引用的 Abstractions、Runtime、Adapters 也按该平台还原，而它们的锁文件原先没有这一平台，locked mode 下发布报 NU1004。T1 第一批的 CI 等效执行发现了这一点（§4.10 D9）。三个工程都声明 `RuntimeIdentifiers` 为 linux-x64，锁文件随之增加该平台一段；不影响普通构建。以后新增被 Host 引用的工程时同样处理。
75. 停止时的取消回调（审查中-1）：监督者请求停止时不再内联等待取消令牌的回调执行完（`CancelAsync` 返回的任务要等全部回调结束）。回调抛出的异常原先会穿过监督者、运行时与协调者，使写者不停止、请求队列不关闭、“运行中”标记不清除；回调阻塞时停止期限从回调结束后才开始计算。现在停止时在锁内发出取消（`CancelAsync` 不内联执行回调，回调在线程池上执行）并记下回调的执行；监督循环等回调执行完，才释放实例并结束（复核时审查者指出：先释放实例时，仍在执行的回调可能与释放同时操作同一连接）：出错时记为停止故障（日志与运行事件 `runtime.unitStopFailed`），不向上抛出；回调阻塞时循环不结束，停止按期限判定“停止超时”，单元仍占用其标识。停止本身只等待循环。修正经过两次调整：
    - 最初只单独观察回调、不等待它，单核测试中运行事件偶尔在宿主停止之后才写入（§4.10 D11）；
    - 随后改为在停止期限内同时等待循环与回调，复查时发现循环自行退出而回调仍阻塞时，会等满期限后报告“已停止”，不占用标识（§4.10 D12）；
    - 最终让循环等回调执行完才结束，三种情况各有用例。运行时的停机步骤与协调者另外各自捕获意外异常，保证后面的步骤与清除标记照常执行。
76. 启动保护的覆盖范围（审查低-5）：
    - “运行中”标记原先在启动判定开头就被消耗，到激活前才重新写入，加载有效版本时在构建运行单元的过程中崩溃（例如适配器解析配置时崩溃）不会被计数，会形成没有保护的崩溃循环。现在加载有效版本时先写标记、再构建。
    - 导入种子与自动回滚的构建发生在提交之前，崩溃时该版本还不是有效版本，不计入启动保护；每次重启又会冻结一个新版本。现在按“同一来源、同一内容（回滚时为同一目标）连续被标为‘未生效：进程中断’的次数”设上限 3：种子达到上限后不再导入（记下内容哈希与运行事件，原有效版本不变）；回滚达到上限后进入“运行失败”。快照行因此有上限，不随崩溃循环增长。
    - 计数的细节（复核意见）：
      - 按版本号从新到旧，只计最近一段连续的中断：同一内容之后有一次生效过、或因其他原因（例如构建失败）未生效的版本，之前的中断不再计入。最初是累计计数，零星的中断（例如断电）跨越很长时间累积到 3 次也会封顶。
      - 提交或放弃时配置库写入失败留下的 pending，启动时无法与进程退出区分，同样标为“进程中断”并计入。
      - 被封顶的种子只有内容改变才会再次导入：内容哈希按规范化之后的内容计算，只改格式不算改变。界面上的重试在 T3-06。
77. 写者的保存点（审查低-4）：一个请求的数据错误原先会使整批回滚，并被记为“存储异常”。现在每个请求在自己的保存点中执行：违反约束（SQLITE_CONSTRAINT，目前只可能是投递编号与已有记录重复）只回滚并拒绝该请求，报告“数据错误”，计入被拒绝的记录数（状态中的 RejectedRecords），同批其他请求照常提交；其他 SQLite 错误仍整批回滚，记“存储异常”。“同一草稿重试时是否把已入库的记录当作成功”属于 T1-04 分发器的重试语义，届时决定。
78. CI 的挂起检测（审查低-2）：测试步骤加 `--blame-hang-timeout 5m --blame-hang-dump-type none`，单个用例挂起时 5 分钟内终止并报告是哪个用例，不必等作业 30 分钟超时；不生成内存转储。本地的 CI 等效执行使用同样的参数。actionlint 1.7.12 检查通过。
79. 退避计时与公布故障的顺序（§4.10 D12）：监督者原先先公布故障（重启次数、状态“退避”、运行事件），再创建退避计时器。用可控时钟的测试看到状态变化就推进时钟，推进可能落在两步之间，计时器于是永远不到期；CI 等效执行中有一个用例因此失败一次。改为先启动退避计时、再公布故障：状态显示“退避”时计时已经开始，测试按状态推进时钟不再有竞争；实际重启时间也更接近公布的下次重启时间，不因记录事件的耗时而推迟。用例在记录故障事件的那一刻推进时钟，旧顺序下一定失败。
80. 停止时的状态转换与取消注册（审查者第三次核对，§4.11）：
    - 再次停止：原先每次停止都把状态改回“停止中”，到期后再改成“停止超时”，并重复记录超时事件。T3-06 先在应用中停止超时、再停机时会遇到。现在“停止中”只从运行中或退避转入，超时事件只在第一次转入“停止超时”时记录。用例与变异 M31、M32 覆盖。
    - 停止期限与循环结束的窗口：循环在锁内设为“已停止”之后，任务才真正完成。期限恰在其间到期时，原先按“任务是否完成”判断，会把已停止的单元改成“停止超时”并永远停在那里。现在超时分支在锁内按循环自己设的状态判断。窗口只有微秒级，没有确定性用例；审查者在副本中插入延迟复现了原写法的问题。
    - 契约：单元在停止令牌上注册的回调须在 `RunAsync` 返回之前释放。同一单元的各个实例共用一个停止令牌，故障实例遗留的注册会随重启累积，到停止时才执行，可能访问已经释放的资源。产品中的单元不直接注册回调，测试用的 TCP 单元用 `await using` 释放。

## 7. 待处理事项

1. 验收本批，并另行授权提交与推送，使 GitHub Actions 实际运行一次；用真实 CI 报告补充 §4.4。进展：维护者 2026-09-30 从其他机器访问调试实例后确认，指示进入 T0-B，并授权本地提交；T0-B 于同日验收并在本地提交为 `b2dec2d`。推送未获授权，GitHub Actions 仍未运行。
2. 其他开发机和 CI 须使用 SDK 10.0.401（`global.json` 禁止滚动到其他版本）。
3. D-09（SqlSugarCoreNoDrive 的传递依赖与许可）须在 T0-05 锁定配置库依赖前答复；未答复时按开发准备 §4“未确认前按”继续，并在此登记。
4. D-01、D-02（一次性设置码）宜在 T0-07 首次设置接口定稿前答复；未答复时按现行规则实现，并保留待调整状态。
5. T0-B 验收：维护者 2026-09-30 验收，指示进入 T0-C（T0-10—T0-13），并授权本地提交；推送未获授权，GitHub Actions 仍未运行。T1 未获授权。
6. D-01、D-02、D-09 仍待维护者确认，本批按“未确认前按”实现（§6 决定 14、21、23），确认结果不同时需要调整的范围见相应决定。
7. 偶发失败（§4.6 B8）：独立审查找到可能原因并已修正测试工厂，修正前后的重复运行均未再出现；如再出现，保存 TRX 中的失败详情后分析。
8. 安全审查提出、需要维护者决定的事项（本批未修改）：
   - DNS 重绑定：`AllowedHosts` 为 `*`（T0-A 起），首次设置窗口开放期间，同一网络中浏览器打开的恶意网页可以把自己的域名重新解析到网关地址，以同源请求调用匿名的首次设置接口创建管理员。审查者确认服务端接受任意 Host 头，完整利用链未演示。可选做法：按 Host 头白名单拒绝（IP 地址、localhost、本机名与配置的名称），或与 D-01 的一次性设置码一并处理。它会影响经域名或反向代理访问时的部署参数，因此不擅自修改。
   - 登出只删除本机 Cookie，被窃取的会话在空闲 2 小时之内一直可用，也没有绝对有效期；按总设计（无刷新令牌、滑动过期）属于既定行为，是否增加服务端吊销或绝对有效期待定。目前产品内结束被窃取会话的办法是修改密码（登录锁定期间同样可以），或由管理员强制下线（T3-09）。
   - 锁定期间的登录尝试仍逐条写入审计，按来源地址限流（每分钟 10 次）；攻击者使用大量 IPv6 地址时审计表会持续增长。可选做法：IPv6 按 /64 计入同一限流分区、合并重复的失败记录，或依靠 T1-07 的存储保护。
9. 其他留给后续任务的审查意见：
   - 自签证书只在启动时检查续期；开机时时钟错误会生成“尚未生效”的证书并覆盖原文件（T5-01 证书收尾）。
   - 保留期清理在时钟被错误调快时会提前删除审计（§2.9，与 T1-07 的校时检测一并处理）。
   - 修改密码时的哈希核对（§6 决定 36）没有单独的测试：目前能改密码的路径都经过同一队列，无法在测试中构造并发修改；T3-09 增加管理员重置密码时补测。
10. T0-C 验收：维护者 2026-10-01 验收，并授权本地提交；推送未获授权。T0-13 要求的“实际 CI 报告”须推送并在 GitHub Actions 运行后才有（§4.4）。
11. 进入 T1：维护者 2026-10-01 确认 D-07，并授权 T1 第一批（T1-01—T1-03）；总设计 §22、功能需求 §6.3 与开发准备 §1、§4、§7 中 D-07 的“待确认”标注已随 T1 第一批更新。D-05 已于 2026-10-08 确认，规则及本次落地见 §2.17。
12. Modbus 选型已于 2026-10-09 确认 NModbus 3.0.83，UnitId 范围及 MQTT 重订决策见 §2.88；正式适配及完整验收仍须执行。
13. T1-01：宿主因后台服务异常停止时进程退出码为 0，须改为非零并与启动保护一起测试（§6 决定 45）。进展：已改为退出码 6，有单元测试；产品中没有令后台服务故障的途径，进程级未验证（§6 决定 62）。启动保护本身已用真实进程验证（§2.14）。
14. 适配层要求：MQTT 见 §2.11（T1-04、T2-03；QoS 0 输出须定期发送带超时的 PINGREQ，§6 决定 53），Modbus 见 §2.12（T2-02）。
15. T5-04、T5-07：产品镜像去掉 `ASPNETCORE_HTTP_PORTS`；选定并登记基础镜像的许可；发行包附带 .NET 运行时与 ASP.NET Core 运行时的第三方声明（`docs/reference-notes.md` §1.3）；在产品镜像中确认 V-07。
16. 环境：本批拉取的镜像 `eclipse-mosquitto:2.1.2-alpine` 与 `mcr.microsoft.com/dotnet/runtime-deps:10.0` 保留在本机；测试与验证创建的容器和数据卷均已删除。审查者为抓取线程栈安装的 dotnet-stack 已随其临时目录删除，它的 NuGet 包仍在 `~/.nuget/packages` 中。
17. T0 验收之后，按开发准备的维护规则（“每个阶段结束时更新第 1、4、5、6 节”）更新 `docs/development-readiness.md`：第 5 节各验证项改为指向本文的结论，第 1 节写明进入 T1 的条件现状。本批不在验收之前改写该文件。进展：T0 于 2026-10-01 验收完毕，已随 T1 第一批更新开发准备第 1、4、5、6 节的状态（只改状态与指向，不改规则）。
18. T1 本地提交：维护者 2026-10-08 授权本地提交后继续，T1-01—T1-04 已提交为 `c97521d`。GitHub Actions 仍未运行过。
19. T1-04：D-05 已确认，分发器、正式 MQTT 输出与 QoS 0 显式 PING 已实现，证据见 §2.17、§4.12；等待本批验收。
20. 运行时留给后续任务的部分（§2.14—§2.16 的“未覆盖”）：
    - 应用的静默与切换、界面上的重试与回滚、版本历史与保留：T3-06；
    - 回滚前的目标变更检查与记录隔离：T1-06；
    - 运行状态 API、校时检测（V-14）、计数持久化、在途字节预算：T1-07；
    - 强制终止进程后的待发记录（FS-14）、queue.db 损坏（FS-16）：T1-08。
21. 环境：本批没有拉取新镜像；新增的 NuGet 包（Microsoft.Extensions.Logging.Abstractions 10.0.12、Microsoft.Extensions.TimeProvider.Testing 10.10.0）在本机缓存中。测试与变异实验留下的临时目录已删除（变异实验第一轮 M11 挂起的用例曾在 `/tmp/tenonedge-tests` 留下目录，审查者指出后清理）。审查者复做 M11 时有一个挂起的测试进程（`review-t1a/.../TenonEdge.IntegrationTests`），它尝试结束时被本会话的权限设置拦截，交由维护者处理，见 §4.11。
22. T2 之前：约定运行单元异常信息的脱敏方式。监督者把单元异常的说明（截断到 500 字）原样写入日志与运行事件；T1 的单元不含秘密，协议适配器的异常可能带地址、账户等信息（审查低-6）。
23. T1-04 前置复核已处理：`DeliveryWriteResult.Status` 区分提交、入库数据拒绝、存储失败与停止。插入约束错误只拒绝该请求，删除失败按存储故障回滚；分发器保留一组稳定编号只重试明确回滚的入库请求，见 §2.17。

24. 上次停止点为 T1-06，实现与验证见 §2.19、§4.14。此后维护者授权从 T1-07 继续至可视化访问，本轮证据见 §2.20—§2.23；尚未达到可视化访问目标。T1-01—T1-04 本地提交为 `c97521d`，后续至 §2.23 的改动已获授权整理为本地提交 `a67bfc9`，未推送；实际 GitHub Actions 报告仍缺失。

### 本地提交整理（2026-10-08，`a67bfc9`）

维护者授权“整理提交一下未提交的，然后继续”。将截至 §2.23 的 T1-05—T1-08、T2-01 子项及 T2-05 固定字段定位整理为本地检查点；因共享存储、注册和测试改动交织，保留为一个已整体回归的提交，不人为拆出不可构建的中间态。验证基线为 890 项通过；本地 Agent 会话文件不入库，未获推送授权，不推送。后续继续实现不等于本检查点宣称 T2 或网页验收完成。
