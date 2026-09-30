# TenonEdge 实现状态与验证证据

- 用途：任务状态与验证证据的统一入口（PLAN-01 §1、`AGENTS.md`）。任务清单和开发准备不另外维护动态完成表；以后建立 Issue 时沿用同一任务 ID，并链接本文。
- 建立：T0-01，2026-09-29。
- 状态用语：代码状态分为未开始、进行中、待验收、完成、阻塞；验证状态分为未执行、通过、失败、跳过、不适用，并注明是本地结果还是远端 CI 结果。跳过须写原因。Agent 自报不等于验收人认可，本批任务在验收前一律记为“待验收”。
- 基线：分支 `main`，提交 `27db103`（任务拆分提交）。T0-A 经维护者 2026-09-30 授权，在本地分支 `t0-foundation` 上单独提交，未推送；提交号在后续更新中补记。

## 1. 批次与任务状态

| 批次 | 任务 | 代码状态 | 验证状态 | 说明 |
|---|---|---|---|---|
| T0-A | T0-01 工程与公共构建设置 | 完成 | 本地通过；远端 CI 未执行 | §2.1 |
| T0-A | T0-02 测试底座与初始 CI | 完成 | 本地通过；远端 CI 未执行（未推送） | §2.2 |
| T0-A | T0-03 最小宿主、健康检查与 API 模板 | 完成 | 本地通过；远端 CI 未执行 | §2.3 |
| T0-B | T0-04—T0-09 | 未开始 | 未执行 | 维护者 2026-09-30 授权 |
| T0-C | T0-10—T0-13 | 未开始 | 未执行 | 未获授权 |
| T1—T5、G1 | 全部 | 未开始 | 未执行 | — |

## 2. T0-A 实现内容

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
- HTTPS 8443、HTTP/HTTPS 监听地址与端口的正式配置，以及请求协议判定：T0-06、T0-08。目前只有 HTTP：直接运行程序时按 `--urls` 参数监听，未指定时使用 ASP.NET Core 的默认地址；`dotnet run` 与 IDE 调试按开发启动配置监听。
- 管理 API 请求体默认 1 MiB 上限（总设计 §8.7）：随第一个带请求体的接口（T0-07）实现。

## 3. 验收编号登记

| 编号 | 子场景 | 验证状态 | 证据 | 剩余 |
|---|---|---|---|---|
| BE-AT-01 | 已创建工程符合依赖方向；检查能发现违规（含经导入文件加入的引用）；无占位工程 | 通过（本地，T0-A 范围） | F5，§4.3 实验 A | 新增工程时由同一测试持续回归 |
| BE-AT-02 | 分组 Minimal API；ProblemDetails；operationId 稳定且唯一 | 部分通过（本地）：401/404/405/500 与 operationId 部分 | F6 | 403、字段错误、真实认证：T0-07、T0-08 |
| BE-AT-03 | 作用域与构建时校验开启 | 子项通过（本地） | F6，§4.3 实验 D | 运行时宿主实例唯一、资源释放：T0-10、T1 |
| BE-AT-04 | 本地与 CI 使用同一 SDK 和测试运行器；locked restore；Release 构建 | 本地通过；远端 CI 未执行 | F1—F3 | 以实际 CI 运行补证 |
| BE-AT-05 | xUnit 用例实际执行；隔离辅助；真实进程测试 | 通过（本地，T0-A 范围） | F5、F6 | 存储、TLS、重启等随 T0-04 以后的任务扩展 |
| BE-AT-06 | linux-x64 自包含发布在干净环境启动 | 未执行 | — | T0-13 |
| BE-AT-07 | 阶段记录 | 本批已记录 | 本文 | 每批更新 |
| V-01 | 环境记录；最小宿主真实进程启动与正常停机 | 部分通过：已记录环境；框架依赖方式的最小宿主在本机以真实进程启动，SIGTERM 后退出码 0 | §4.1，F6（`HostProcessTests`），R8 | 仍属 T0，且进入 T1 前须有结论：<br>• 自包含发布、无 .NET 的干净容器：T0-13；<br>• 后台服务未处理异常的默认行为：T0-10 或 T0-13 |
| 其余 V、FS、AT、GW | — | 未执行 | — | 不在 T0-A 范围 |

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

- 生产发布物（Host）的依赖闭包只有前两个包；测试包不随发布物分发。完整的直接与传递依赖以三份 `packages.lock.json` 为准：Host 直接 1 个、传递 1 个；UnitTests 直接 3 个、传递 14 个；IntegrationTests 直接 4 个、传递 44 个。
- 正式的第三方许可清单（`docs/reference-notes.md`、`THIRD_PARTY_NOTICES.md`）按计划在 T0-13、T5-07 整理，本批未建立。
- CI 中的 Action 均固定到提交：
  - `actions/checkout` v7.0.1（`3d3c42e5aac5ba805825da76410c181273ba90b1`）；
  - `actions/setup-dotnet` v6.0.0（`a98b56852c35b8e3190ac28c8c2271da59106c68`）；
  - `actions/upload-artifact` v7.0.1（`043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`）。

## 6. 本批的实现决定

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
    - 正式的监听地址与端口（HTTP 8080、HTTPS 8443，可分别配置，可关闭 HTTP，总设计 §10.3）随 T0-06、T0-08 实现。

## 7. 进入 T0-B 前需处理

1. 验收本批，并另行授权提交与推送，使 GitHub Actions 实际运行一次；用真实 CI 报告补充 §4.4。进展：维护者 2026-09-30 从其他机器访问调试实例后确认，指示进入 T0-B，并授权本地提交；推送未获授权，GitHub Actions 仍未运行。
2. 其他开发机和 CI 须使用 SDK 10.0.401（`global.json` 禁止滚动到其他版本）。
3. D-09（SqlSugarCoreNoDrive 的传递依赖与许可）须在 T0-05 锁定配置库依赖前答复；未答复时按开发准备 §4“未确认前按”继续，并在此登记。
4. D-01、D-02（一次性设置码）宜在 T0-07 首次设置接口定稿前答复；未答复时按现行规则实现，并保留待调整状态。
