# TenonEdge 第三方来源与许可记录

- 用途：按总设计 §17.2、§17.3 记录实际采用的第三方组件：通过包管理器的依赖、复制或修改的源码。只参考思路的项目仍在 `docs/reference-*` 与参考项目笔记中说明，不在本表重复。
- 建立：T0-05，2026-09-30。每次新增依赖或复制代码时更新；许可或交付义务未明确的分发组件，其状态写“待核查”，并阻止对应发行包发布（§17.3）。
- 本文整理证据、发现缺口，不代替专业许可审查；“已记录”只表示许可声明已从包元数据中取得并登记，不表示已完成法律审查。
- 发布包附带的 `THIRD_PARTY_NOTICES.md` 与许可文本在 T0-13、T5-07 整理，本文不是该文件。

## 1. 宿主的 NuGet 依赖（V-06、D-09）

- 依据：`src/TenonEdge.Host/packages.lock.json` 列出的全部直接与传递依赖（31 个）；许可取自本机 NuGet 缓存中各包 nuspec 的 license 元素，核查日期 2026-09-30。
- 分发范围按 Host 的实际构建输出判定：`src/TenonEdge.Host/bin/Release/net10.0` 只含下表“随发布”的程序集（9 个 DLL）与 `runtimes/*/native` 下的 e_sqlite3 原生库。
- 测试工程的依赖（xUnit.net、VSTest、Mvc.Testing 等）不随产品分发，见两个测试工程的锁文件，不在此逐项登记。

### 1.1 随发布物分发

| 编号 | 组件/项目 | 版本或提交 | 路径/包名与来源 | 采用方式 | 许可证/授权依据 | 是否修改 | 是否随包/整机分发 | 应附声明/源码等材料 | 状态与核查记录 |
|---|---|---|---|---|---|---|---|---|---|
| N-01 | Microsoft.AspNetCore.OpenApi | 10.0.12 | nuget.org，直接依赖 | 依赖 | MIT（nuspec 许可表达式） | 否 | 是 | 版权与 MIT 许可文本 | 已记录 |
| N-02 | Microsoft.OpenApi | 2.12.0 | nuget.org，N-01 的传递依赖 | 依赖 | MIT | 否 | 是 | 版权与 MIT 许可文本 | 已记录 |
| N-03 | Microsoft.Data.Sqlite | 10.0.12 | nuget.org，直接依赖（配置库驱动） | 依赖 | MIT | 否 | 是 | 版权与 MIT 许可文本 | 已记录 |
| N-04 | Microsoft.Data.Sqlite.Core | 10.0.12 | nuget.org，N-03 的传递依赖 | 依赖 | MIT | 否 | 是 | 版权与 MIT 许可文本 | 已记录 |
| N-05 | SQLitePCLRaw.bundle_e_sqlite3、core、provider.e_sqlite3、lib.e_sqlite3 | 2.1.12 | nuget.org，N-04 的传递依赖 | 依赖 | Apache-2.0（nuspec）；所带原生库为 SQLite 3.53.3（SQLite 本身为公有领域） | 否 | 是（lib.e_sqlite3 为各平台原生库） | Apache-2.0 许可文本；是否带 NOTICE 待核查 | 已记录；NOTICE 待核查 |
| N-06 | SqlSugarCoreNoDrive（程序集 SqlSugar 5.1.4.221） | 5.1.4.221 | nuget.org，直接依赖（配置库访问层） | 依赖 | 待核查：nuspec 只有指向 Apache-2.0 的 licenseUrl，没有许可表达式；GitHub 仓库 LICENSE 为 MIT（S-19） | 否 | 是 | 按 D-09 推荐：MIT 与 Apache-2.0 两份文本一并附带，并向上游确认 | 待核查（D-09 未确认） |
| N-07 | Newtonsoft.Json | 13.0.2 | nuget.org，N-06 的传递依赖 | 依赖 | MIT | 否 | 是 | 版权与 MIT 许可文本 | 已记录 |

### 1.2 在还原图中、但资产已排除（不分发）

SqlSugarCoreNoDrive 依赖 SystemCommon 8.5.0，后者带入 SQL Server 驱动栈及其身份认证依赖。下列包以直接引用加 `ExcludeAssets="all"` 排除全部资产（`Directory.Build.targets`；其中 Microsoft.Bcl.AsyncInterfaces 只在 Host 中排除），不进入构建输出和发布物；它们仍在锁文件中，NuGet 漏洞审计照常覆盖。

| 编号 | 组件/项目 | 版本 | 许可证/授权依据（nuspec） | 是否随包分发 | 状态与核查记录 |
|---|---|---|---|---|---|
| X-01 | SystemCommon | 8.5.0 | 待核查：只有指向 Apache-2.0 的 licenseUrl；包内唯一的程序集是较旧的 SqlSugar.dll 5.1.4.169，没有公开源码 | 否 | 已排除；D-09 |
| X-02 | Microsoft.Data.SqlClient | 5.2.2 | MIT | 否 | 已排除 |
| X-03 | Microsoft.Data.SqlClient.SNI.runtime | 5.2.0 | 包内 LICENSE.txt（微软软件许可条款） | 否 | 已排除 |
| X-04 | Microsoft.SqlServer.Server | 1.0.0 | MIT | 否 | 已排除 |
| X-05 | Azure.Core、Azure.Identity | 1.38.0、1.11.4 | MIT | 否 | 已排除 |
| X-06 | Microsoft.Identity.Client、Microsoft.Identity.Client.Extensions.Msal | 4.61.3 | MIT | 否 | 已排除 |
| X-07 | Microsoft.IdentityModel.Abstractions、JsonWebTokens、Logging、Tokens；System.IdentityModel.Tokens.Jwt | 7.6.3 | MIT | 否 | 已排除 |
| X-08 | Microsoft.IdentityModel.Protocols、Protocols.OpenIdConnect | 6.35.0 | MIT | 否 | 已排除 |
| X-09 | System.ClientModel、System.Memory.Data | 1.0.0、1.0.2 | MIT | 否 | 已排除（只由 Azure.Core 引用） |
| X-10 | System.Runtime.Caching、System.Configuration.ConfigurationManager、System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | 否 | 已排除（只由 SqlClient 引用） |
| X-11 | Microsoft.Bcl.AsyncInterfaces | 1.1.1 | MIT | 否 | Host 中已排除（只由 Azure.Core 引用）；测试工程中 xUnit 使用其 6.0.0 版本 |

## 2. D-09 试验记录

- 事项：SqlSugarCoreNoDrive 带入的传递依赖与许可声明（`docs/development-readiness.md` §4 D-09）。维护者尚未答复；按“未确认前按推荐”实施：保留 SqlSugar，试验去掉 SQL Server 驱动栈，许可状态记为“待核查”。本记录不代表 D-09 已获确认。
- 依据的事实：
  - SqlSugar.dll 的程序集引用中，各数据库驱动（Microsoft.Data.SqlClient、MySqlConnector、Npgsql、Oracle.ManagedDataAccess、DM.DmProvider、Kdbndp、Oscar.Data.SqlClient）只在使用对应数据库时加载；NoDrive 包本身就不带其中大多数驱动。
  - SystemCommon 8.5.0 的依赖为 Microsoft.Data.SqlClient 5.2.2、System.IdentityModel.Tokens.Jwt 7.6.3 等；包内唯一的程序集是 SqlSugar.dll 5.1.4.169，构建时被 SqlSugarCoreNoDrive 自带的 5.1.4.221 取代（两者 SHA-256 不同，输出与 SqlSugarCoreNoDrive 包内文件一致）。
- 试验（2026-09-30，会话临时目录中的最小程序，之后在项目中复现）：
  1. 默认组合：framework-dependent 发布 27 个 DLL、约 50 MB（含各平台原生库），其中有 SqlClient、Azure.Identity、MSAL、IdentityModel 等 13 个程序集。
  2. 只排除 SQL Server 驱动与身份认证组件（14 个包）：SQLite 读写、事务、并发和 PRAGMA 回读均正常，发布降为 14 个 DLL、约 39 MB。
  3. 同时排除 SystemCommon：结果与第 2 步相同。
- 项目中的做法：排除第 1.2 节的 21 个包（第 2 步的 14 个，加上 SystemCommon，以及只被它们引用的 6 个包）。Host 构建输出降为 9 个 DLL；测试工程设置同一开关，进程内测试与发布物使用同一组程序集；真实进程测试使用 Host 自己的依赖清单（deps.json）运行。全部测试通过，见 `docs/implementation-status.md` §4.6。
- 影响与残余风险：
  - 若将来使用 SqlSugar 的 SQL Server 等其他数据库功能，会因缺少程序集而失败；配置库只用 SQLite，这是预期的边界。
  - 被排除的包仍在锁文件中：它们出现漏洞公告时，构建会按 NU1901—NU1904 失败，需要升级版本或随 SqlSugar 升级一并处理。
  - 升级 SqlSugarCoreNoDrive 时，须同步核对 `Directory.Packages.props` 中这组包的版本；版本低于新依赖要求时，还原会报 NU1605 降级错误，不会被静默接受。
  - 许可：SqlSugarCoreNoDrive 的许可声明仍不一致，状态保持“待核查”；发行包发布前须按 D-09 的答复处理。

## 3. 复制或修改的源码

| 编号 | 组件/项目 | 版本或提交 | 路径/包名与来源 | 采用方式 | 许可证/授权依据 | 是否修改 | 是否随包/整机分发 | 应附声明/源码等材料 | 状态与核查记录 |
|---|---|---|---|---|---|---|---|---|---|
| S-01 | TenonAdmin 的雪花 ID 生成器（总设计 §10.1，V-13） | 提交 `e6473d4107a2f402fce4c05337559167be8f8a63`（最后修改该文件的提交，已在 origin/main；文件 blob `c741634f669ce5795eb16bfe0e54c7779befa1af`，与核查时的工作区一致） | https://github.com/Tenon-Net/TenonAdmin 的 `backend/src/TenonAdmin.Core/Ids/SnowflakeIdGenerator.cs` → 本仓库 `src/TenonEdge.Host/Storage/Ids/SnowflakeIdGenerator.cs` | 移植（复制并修改源码） | Apache-2.0（TenonAdmin 仓库 LICENSE；仓库没有 NOTICE 文件）；与本仓库许可相同 | 是，见下方修改说明 | 是（编译进 TenonEdge.Host.dll） | 文件头保留来源与修改声明（Apache-2.0 第 4 条 b 项）；发布时附 Apache-2.0 许可文本 | 已记录，核查日期 2026-09-30 |

S-01 的修改说明（按总设计 §10.1，边缘设备开机时时钟可能不可靠）：

- 保留：41 位毫秒、6 位机器号、6 位序列的位布局，ID 恒小于 2^53；纪元 2020-02-20 02:20:02 UTC；进程内加锁发号。
- 机器号固定为 0，删除构造参数以及原实现中的机器号文件锁与库表租约（`WorkerIdLease` 等未复制）；唯一性依赖数据目录的单实例锁。
- 启动时以 SystemState 的 ID 下限、各雪花主键表中已有的最大 ID 与本版本的构建时间下限确定起点，新 ID 大于前两者，时间戳不早于构建时间下限。
- 时钟落后时沿用逻辑时间戳，不再在小幅回拨时自旋、大幅回拨时抛出异常；序列用尽时逻辑时间戳加 1 毫秒，不等待时钟。
- 时钟读数达到 2^41 毫秒时视为不可信，沿用逻辑时间戳并告警（在时钟恢复可信之前只告警一次）；只有逻辑时间戳本身达到 2^41 毫秒时才拒绝发号。
- 删除与 TenonAdmin 种子数据相关的 `CurrentFloor`、`MinId` 和 `IIdGenerator` 接口；类型改为 internal，日志改用 LoggerMessage。
- 公开 `TimestampShift`：按保留期清理审计记录与运行事件时，在 SQL 中由 ID 换算逻辑时间，判断保存的时间是否可信（T0-09）。
- 测试：原实现的并发不重复测试改写后保留（`ParallelBurstIdsAreUnique`），其余测试按上述规则重写，见 `tests/UnitTests/Storage/SnowflakeIdGeneratorTests.cs`。
