# TenonEdge 实现状态与验证证据

- 用途：任务状态与验证证据的统一入口（PLAN-01 §1、`AGENTS.md`）。任务清单和开发准备不另外维护动态完成表；以后建立 Issue 时沿用同一任务 ID，并链接本文。
- 建立：T0-01，2026-09-29。
- 状态用语：代码状态分为未开始、进行中、待验收、完成、阻塞；验证状态分为未执行、通过、失败、跳过、不适用，并注明是本地结果还是远端 CI 结果。跳过须写原因。Agent 自报不等于验收人认可，本批任务在验收前一律记为“待验收”。
- 基线：分支 `main`，提交 `27db103`（任务拆分提交）。T0-A 经维护者 2026-09-30 授权，在本地分支 `t0-foundation` 上单独提交为 `0ff530a`，未推送。T0-B 经维护者 2026-09-30 验收，并授权在同一分支上单独提交，未推送。

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
| T0-C | T0-10—T0-13 | 未开始 | 未执行 | 2026-09-30 获授权 |
| T1—T5、G1 | 全部 | 未开始 | 未执行 | — |

## 2. 实现内容

2.1—2.3 为 T0-A，2.4 起为 T0-B。

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

## 3. 验收编号登记

| 编号 | 子场景 | 验证状态 | 证据 | 剩余 |
|---|---|---|---|---|
| BE-AT-01 | 已创建工程符合依赖方向；检查能发现违规（含经导入文件加入的引用）；无占位工程 | 通过（本地，T0-A 范围） | F5，§4.3 实验 A | 新增工程时由同一测试持续回归 |
| BE-AT-02 | 分组 Minimal API；ProblemDetails；operationId 稳定且唯一 | 通过（本地，T0-B 范围）：401（带质询头）、403、400 字段错误、404、405、409、429、500 均为 ProblemDetails，超出请求体上限返回 413（未核对响应体格式）；真实认证与角色授权；operationId 清单由测试固定 | F6，§2.7—§2.9 | 新增端点时由同一测试持续回归 |
| BE-AT-03 | 作用域与构建时校验开启 | 子项通过（本地） | F6，§4.3 实验 D | 运行时宿主实例唯一、资源释放：T0-10、T1 |
| BE-AT-04 | 本地与 CI 使用同一 SDK 和测试运行器；locked restore；Release 构建 | 本地通过；远端 CI 未执行 | F1—F3 | 以实际 CI 运行补证 |
| BE-AT-05 | xUnit 用例实际执行；隔离辅助；真实进程测试 | 通过（本地，T0-B 范围）：API 与存储测试各用独立数据目录；TLS、转发头、限流、重启、锁、日志脱敏有真实进程测试；容器挂卷为手动验证 | F5、F6，§4.6 | 真实磁盘写满未复现（以移走数据表模拟，§2.9）；容器验证纳入自动化：T0-13 |
| BE-AT-06 | linux-x64 自包含发布在干净环境启动 | 未执行 | — | T0-13 |
| BE-AT-07 | 阶段记录 | 本批已记录 | 本文 | 每批更新 |
| V-01 | 环境记录；最小宿主真实进程启动与正常停机 | 部分通过：已记录环境；框架依赖方式的最小宿主在本机以真实进程启动，SIGTERM 后退出码 0 | §4.1，F6（`HostProcessTests`），R8 | 仍属 T0，且进入 T1 前须有结论：<br>• 自包含发布、无 .NET 的干净容器：T0-13；<br>• 后台服务未处理异常的默认行为：T0-10 或 T0-13 |
| AT-10 | 单实例子项：同一数据目录的第二个实例被拒绝；NodeId 在重启后保留 | 通过（本地） | §2.4、§2.6 | 完整的队列重启验证属于 T1 |
| V-06 | NoDrive 加 Microsoft.Data.Sqlite；逐物理连接设置并回读 PRAGMA；并发读写；版本化迁移；传递依赖清单与 D-09 试验 | 通过（开发环境，本地）：回读为 wal 与 FULL；PRAGMA 挂载点可用，不需要改用长连接；清单与结论已写入 `docs/reference-notes.md` | §2.5 | 发布环境复验：T0-13；D-09 待维护者确认 |
| V-08 | 密钥环持久化到数据目录；固定应用名；换安装路径、重建挂卷容器后可解密；容器中数据目录不在挂载卷上时拒绝启动运行时；合并密钥环；记录未配置密钥加密器的警告如何处理 | 通过（开发环境，本地，含手动容器验证） | §2.6，§4.6 B5，§6 决定 17 | 发布物复验：T0-13 |
| V-13 | 雪花 ID 生成器的来源记录 | 通过：许可允许复制（Apache-2.0），已记录 | `docs/reference-notes.md` §3 | — |
| AT-24 | 设置窗口 30 分钟，未初始化时重启重新开放；初始化后不再开放；初始化记审计 | 通过（本地）；系统信息接口返回首次设置的时间与来源 IP，窗口开放时记运行事件（T0-09） | §2.7、§2.9 | 首页显示属于前端：T3 |
| FS-09 | 两人同时提交首次设置只成功一次 | 通过（本地，8 个并发请求） | §2.7 | — |
| GW-01 | 首次访问时设置管理员；不存在默认账号 | 通过（本地，API 部分） | §2.7 | 网页部分在 T3 |
| AT-19 | 密钥子集：重启、更换安装路径、重建容器后仍可解密 | 通过（本地，最小版本） | §2.6 | 凭证业务随 T1；备份恢复部分属于 T5-02 |
| V-07 | 启动记录实际 SQLite 版本与编译选项；不低于 3.51.3 | 部分通过（开发环境，本地）：3.53.3，启动日志有记录；低于下限时的阻止路径已测试；系统信息接口返回版本与编译选项（T0-09） | §2.5、§2.9 | 自包含发布与 Docker 镜像中确认、真实加载低版本原生库：T0-13 |
| V-11 | 转发头只信任已登记的代理；`__Host-` Cookie；防 CSRF；按来源地址限流；PasswordHasher 格式与迭代次数 | 通过（本地，T0 子集）：未登记代理时伪造转发头无效，设置 ASPNETCORE_FORWARDEDHEADERS_ENABLED 也不改变；登记后只接受代理的转发头；两种协议的 Cookie 不能互换；限流按实际来源地址；哈希为 V3（HMAC-SHA512、10 万次） | §2.7、§2.8 | API 令牌部分：T3-09 |
| AT-32 | 管理访问：伪造转发头不生效；经 HTTP 创建或使用 API 令牌被拒；HTTP 与 HTTPS 的会话 Cookie 不能互换；“只允许 HTTPS”或恢复备份后全部会话失效 | 部分通过（本地，最小版本）：转发头、Cookie 互换两项通过；会话代次加一使全部会话失效的机制已测试 | §2.8 | API 令牌：T3-09；由“只允许 HTTPS”开关与恢复备份触发：T5-01、T5-02 |
| FS-10 | 伪造转发头；经 HTTP 创建 API 令牌 | 部分通过（本地）：伪造的转发头不改变协议判定、来源地址与限流 | §2.8 | 经 HTTP 创建令牌被拒：T3-09 |
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

未运行。工作流已编写（`.github/workflows/backend-ci.yml`），提交并推送后才会在 GitHub Actions 执行；本批未获提交或推送授权。本地逐步模拟不等于 GitHub Actions 已经通过。CI 使用 `ubuntu-24.04` 托管镜像，与本机 Ubuntu 26.04 不同，首次运行后须核对报告。

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
| Microsoft.Data.Sqlite | 10.0.12 | Host：SQLite 驱动（T0-05），带 SQLite 3.53.3 | MIT（SQLitePCLRaw 为 Apache-2.0） | nuget.org |

- T0-A 时生产发布物（Host）的依赖闭包只有 Microsoft.AspNetCore.OpenApi 与 Microsoft.OpenApi。T0-05 之后，随发布物分发的包共 10 个，另有 21 个包在还原图中但资产已排除（D-09），逐项见 `docs/reference-notes.md` §1。测试包不随发布物分发。完整的直接与传递依赖以三份 `packages.lock.json` 为准。
- 正式的第三方许可清单（`docs/reference-notes.md`、`THIRD_PARTY_NOTICES.md`）按计划在 T0-13、T5-07 整理，本批未建立。
- CI 中的 Action 均固定到提交：
  - `actions/checkout` v7.0.1（`3d3c42e5aac5ba805825da76410c181273ba90b1`）；
  - `actions/setup-dotnet` v6.0.0（`a98b56852c35b8e3190ac28c8c2271da59106c68`）；
  - `actions/upload-artifact` v7.0.1（`043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`）。

## 6. 实现决定

1—10 属于 T0-A，11 起属于 T0-B。

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
    - 版本暂用程序集的 InformationalVersion，正式版本号规则随 T0-12、T0-13。
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

## 7. 待处理事项

1. 验收本批，并另行授权提交与推送，使 GitHub Actions 实际运行一次；用真实 CI 报告补充 §4.4。进展：维护者 2026-09-30 从其他机器访问调试实例后确认，指示进入 T0-B，并授权本地提交；推送未获授权，GitHub Actions 仍未运行。
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
