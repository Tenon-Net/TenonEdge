# TenonEdge 后端工程实施规范

- 编号：BE-D01。
- 整理日期：2026-09-29。
- 基线：`docs/tenonedge-design.md` V1.6，整理时仓库提交为 `6418336d16700ecebed4edc417aec1b0d2fc1c0e`。
- 状态：工程组织与实现约定已确认；尚未创建业务工程，未完成项目构建、依赖组合或运行验证。
- 用途：补充总设计 §3.2 的内部目录、§10.1 的 API 实现方式，以及 §12 中 T0 的依赖注册、构建与测试约定。运行机制、数据契约和产品行为仍在总设计及功能需求中定义，不在本文另建一套规则。

本次只补充后端工程约定，不改变前端 FE-D01、四工程依赖方向、T0—T5 顺序或首版范围。`development-readiness.md` 中已有待确认事项保持原状态；特别是 D-07 仍须在 T1 运行时实现前确认，D-05 须在连接与凭证 API 定稿前确认。本文不代表这些决策已被批准。

## 1. 架构与技术基线

保留单个 .NET 服务和四个后端工程：Host、Runtime、Adapters、Abstractions。Host 是程序装配入口；管理请求与持续采集分别组织，不把采集循环放进管理 API，不引入另一套 Admin 框架。

| 能力 | 工程约定 | 尚需验证的部分 |
|---|---|---|
| 平台与 Web | .NET 10、ASP.NET Core；管理 API 统一使用按功能分组的 Minimal API | 精确 SDK 与框架补丁在 T0 锁定；发布环境见 V-01 |
| 配置与队列 | config.db 使用既定 SqlSugarCoreNoDrive 与 Microsoft.Data.Sqlite 组合；queue.db 由 Runtime 直接使用 Microsoft.Data.Sqlite | V-06、V-07；D-09 的依赖与许可问题不在本次替代解决 |
| 协议适配 | MQTTnet；HTTP 使用 .NET HTTP 客户端能力；Modbus 候选仍是 NModbus 与 FluentModbus | V-04、V-05，按原阶段比较、包装和实测，不提前宣布选型通过 |
| 后台组织 | 一个 BackgroundService 运行时宿主，内部监督运行单元；有界 Channels 与 TimeProvider | 按总设计 §7、§8 分阶段验证 |
| 安全与 API 契约 | 既定本地 Cookie、固定三种角色、PasswordHasher、Data Protection、ProblemDetails、内置 OpenAPI | V-08、V-11 及现有安全验收 |
| 后端测试 | xUnit.net；API 集成测试使用 Microsoft.AspNetCore.Mvc.Testing 的 WebApplicationFactory | T0 锁定兼容的测试包与运行器组合，并确认测试被实际发现和执行 |

不为整理目录引入通用 CRUD 框架、泛型仓储、命令总线或额外应用层工程。不自动添加 MediatR、完整 Identity/EF Core、任务调度框架或分布式基础设施；确有需求时另行记录理由和范围。

## 2. 工程与目录

以下是目标组织方式，不是已经存在的文件清单。按当前批次创建确实有代码和测试的目录；没有实现的模块不建空工程、空接口或占位类。

```text
TenonEdge/
├── TenonEdge.sln
├── global.json                         # 固定实际验证的 SDK
├── Directory.Build.props               # 公共构建与编译约定
├── Directory.Packages.props            # NuGet 版本集中管理
├── .editorconfig
├── src/
│   ├── TenonEdge.Abstractions/
│   │   ├── Messages/                   # 消息、质量、投递结果等基础契约
│   │   ├── Plans/                      # 不可变 RuntimePlan 及其组成
│   │   └── Contracts/                  # 适配器、状态查询及必要的只读服务接口
│   ├── TenonEdge.Runtime/
│   │   ├── Lifecycle/                  # 宿主、监督者、单元启停与计划切换
│   │   ├── Connections/                # 连接共享、借用与资源所有权
│   │   ├── Scheduling/                 # 周期、截止时间、超时与退避
│   │   ├── Routing/                    # 纯图校验、编译与消息分发
│   │   ├── Transforms/                 # 字段映射、类型与数值换算
│   │   ├── Deliveries/                 # queue.db、写者、发送者与记录状态
│   │   └── Diagnostics/                # 有界状态、计数与事件输出
│   ├── TenonEdge.Adapters/
│   │   ├── Simulator/
│   │   ├── Modbus/
│   │   ├── Mqtt/
│   │   ├── Http/
│   │   └── TenonIoT/
│   └── TenonEdge.Host/
│       ├── Bootstrap/                  # 显式装配、初始化、启动检查
│       ├── Features/                   # 按管理 API 能力组织
│       │   ├── Auth/
│       │   ├── Users/
│       │   ├── Connections/
│       │   ├── Devices/
│       │   ├── Routes/
│       │   ├── Configuration/
│       │   ├── Deliveries/
│       │   ├── Runtime/
│       │   ├── PlatformLinks/
│       │   ├── Audit/
│       │   └── System/
│       ├── Security/                   # 认证、授权与凭证保护
│       ├── Storage/                    # config.db 实体与具体访问实现
│       │   └── Migrations/             # 显式、版本化的配置库迁移
│       └── Program.cs                  # 仅负责启动装配
├── tests/
│   ├── UnitTests/
│   ├── IntegrationTests/
│   └── TestPlatform/                   # T4 的测试接收端
├── .github/workflows/                  # T0 建立后端 CI
├── web/                                # 仍在 T3 创建
├── deploy/
├── samples/
└── docs/
```

特定功能目录内按需放置端点、请求响应模型和操作服务。例如 `Features/Connections/` 可以包含 `ConnectionsEndpoints.cs`、`ConnectionService.cs` 和 `Contracts.cs`；这些是建议命名，不是已经实现或必须机械创建的类。简单操作不额外制造只有一行转发代码的服务层。

- 不为每个工程重复建立 Domain/Application/Infrastructure 三套目录，不把全部服务混放进无法辨别职责的 Common 或 Utils。
- API 请求/响应模型属于 Host；配置库实体属于 Host/Storage；RuntimePlan、运行消息和跨工程接口属于 Abstractions。三者不共用一个可变数据库实体。
- Host 从配置实体生成不含 ORM 类型的配置描述，调用只接受基础契约的图校验/编译逻辑，最终把不可变 RuntimePlan 交给 Runtime。Runtime 中的纯编译逻辑不得因此反向读取 Host 的实体或配置数据库。
- 一个源消息的多路径处理按总设计执行，不因目录拆分复制采集实例。协议库具体类型留在 Adapters 内，不以 object、dynamic 或类型强转绕过 Abstractions 的边界。

项目引用方向保持：

```text
Host         -> Runtime / Adapters / Abstractions / 配置存储所需组件
Runtime      -> Abstractions / Microsoft.Data.Sqlite / 必要的 .NET 基础设施
Adapters     -> Abstractions / 必要协议库
Abstractions -> .NET 基础类型
```

新增引用须有具体用途。Runtime 和 Adapters 不引用 Host；Runtime 不直接引用协议库；Abstractions 不引用 ASP.NET Core、ORM 或协议库。T0 用简单的项目/程序集引用检查验证已创建工程，后续新增工程时扩展同一测试，不为此默认引入另一套架构分析平台。

## 3. 管理 API 的统一写法

### 3.1 端点组织

使用 ASP.NET Core 原生 Minimal API；不同时按模块各自引入 Controller、第三方端点框架或另一套响应封装。

- 管理入口仍为 `/api/edge-admin/v1`。使用 MapGroup 按总设计 §10.1 的能力分组，端点映射写在对应 Features 目录，通过显式映射入口在 Program.cs 装配；不把全部处理逻辑堆在 Program.cs。
- 端点负责绑定请求、调用用例和转换 HTTP 结果；业务校验、配置操作和事务边界放在相应操作服务或纯校验函数中；运行启停交给运行时协调者。
- 通过 TypedResults 或显式的响应类型元数据描述成功与失败结果，使用稳定、全局唯一的 operationId；由 Microsoft.AspNetCore.OpenApi 生成契约。重命名内部类不得无意改变客户端契约。
- 请求模型做格式、长度、大小与范围校验；跨对象引用、版本冲突、目标校验等按已有业务规则处理。字段错误结构统一记录在 OpenAPI 中，不在各模块随意使用不同格式。
- 错误继续使用 ProblemDetails 和正确的 HTTP 状态码，不返回 HTTP 200 包装业务失败，不把异常堆栈、连接秘密或内部数据库信息发给客户端。序列化、枚举与大整数按总设计 §6.4、§10.1 执行。

### 3.2 认证与操作边界

管理 API 默认要求认证；首次设置、登录和健康检查等匿名例外逐项显式列出并测试。固定角色对应的策略在 Security 中集中定义，涉及单独授权的操作逐个声明策略，不只保护列表和 CRUD。

Cookie 写操作的防 CSRF、API Token 的 HTTPS 限制及认证区分，完全沿用总设计 §10.3。采用 Minimal API 不代表这些行为天然已生效，须用集成测试验证。API 返回 401/403，不以跳转登录 HTML 冒充 API 响应；前端依契约处理。

普通查询、连接测试和预览使用受控的超时与取消令牌。应用、回滚等操作一旦由协调者接收并登记，生命周期由协调者管理，不能因浏览器断开而任意中止已经提交的步骤；请求取消可以结束等待，不等于撤回该操作。进度与结果使用总设计已有的配置操作接口约定，不另造任务平台。不得用未受监督的 Task.Run 或 async void 逃逸请求生命周期。

## 4. 依赖注入、生命周期与资源归属

只使用宿主统一的依赖注入容器。Host/Bootstrap 显式装配各模块，不在业务类内再次 BuildServiceProvider，也不扫描任意外部 DLL。实际注册方法在代码中实现，不把文档中的模块名称当成第三方库已经提供的 API。

| 对象 | 生命周期与持有者 | 约束 |
|---|---|---|
| 运行时宿主、监督者、连接管理器 | 宿主级单例 | 每个角色只有一个实际实例。需要通过接口查询同一宿主时复用该实例，不能因多次注册产生第二套采集任务 |
| 运行单元与协议会话 | 由运行时工厂按计划创建，由连接管理/监督者持有 | 不等同于 HTTP 请求作用域；借用者释放租约，不擅自释放共享客户端；同一单元 ID 不并存两个实例 |
| config.db 的 SqlSugarScope | 沿用总设计的单例约定 | 单例不意味着可以无保护共享事务或并发上下文；同一异步流程内并发按已验证的 CopyNew 策略隔离。具体打开、复制和释放行为由 V-06 验证 |
| 配置操作服务与数据库操作上下文 | HTTP 请求内按需使用 scoped；后台操作使用独立、短生命周期上下文 | 不把请求服务、事务、reader 或 HttpContext 保存在运行时单例中；操作完成及时释放，事务不跨外部网络等待 |
| queue.db 写者与连接 | 写者由 Runtime 单例管理，写连接仅供该写者使用 | 所有写操作进入同一有界写入路径；读操作使用短连接/短事务；不通过 Host 的配置 ORM 访问队列 |
| 凭证、绑定和运行参数的只读接口 | Host 提供线程安全实现，供 Runtime 持有 | 需要访问 scoped 存储时由实现内部创建并释放短作用域，只返回值对象；不得返回 ORM 客户端、活跃查询或 HttpContext |
| 无状态转换与校验器 | 可共享的无状态对象；需要状态时由运行单元拥有 | 不保存上一个请求或设备的可变数据，不用共享可变 JSON 作为多路径输出 |
| 密钥保护、时间与日志 | 复用已注册的框架服务 | 不自行创建第二套密钥环、全局日志容器或静态可变时钟 |

BackgroundService 通过 AddHostedService 注册时是单例，框架不会自动为它创建 scoped 作用域。需要 scoped 服务时，在 Host 实现的边界适配中使用 IServiceScopeFactory 创建并释放作用域；不要为每个点位建立作用域，也不把长时间运行的循环包在永不释放的作用域里。框架依据见第 8 节。

资源遵循“所有者最终释放”。所有 I/O 有超时，停止顺序沿用总设计 §8.1；对不支持取消的阻塞读取，由连接所有者执行受控关闭来打断，等待循环退出后完成最终清理。HTTP 客户端或 handler 不按每次轮询新建，按连接配置和凭证变化受控复用/替换。

T0 的开发与集成测试显式开启 ValidateScopes、ValidateOnBuild。它们用于发现注册和作用域错误，不能替代并发与资源泄漏测试；还要验证运行时实例唯一、短作用域释放，以及请求完成后后台工作不依赖已释放对象。

## 5. 构建、依赖与编码约定

| 文件或机制 | T0 要求 |
|---|---|
| global.json | 固定实际验证的完整 .NET 10 SDK 版本，allowPrerelease=false、rollForward=disable；本机与 CI 使用同一版本。升级通过显式变更重新验证，不自动跟随开发机已安装的更新版本 |
| Directory.Build.props | 统一 net10.0、可空引用检查、隐式 using 和确定性构建；语言版本跟随锁定 SDK/目标框架的受支持默认值，不使用 preview/latest 开关 |
| Directory.Packages.props | 启用 NuGet 集中版本管理；项目只引用实际需要的包，不给所有工程添加全部依赖。精确版本由 T0 核实，不复制资料中的“最新版本”当成验证结论 |
| packages.lock.json | 对需要还原的项目启用并提交锁文件；CI 使用 locked mode，依赖变化须连同锁文件和验证证据一起提交。发布目标的 RID/运行时资产也须纳入还原验证 |
| .editorconfig 与 SDK 分析器 | 统一格式和检查规则；自有代码的可空、编译和已启用分析器问题不得靠全局禁用消除；例外须局部说明原因 |
| NuGet 源与发布信息 | 公开构建不依赖私有源或客户凭据；记录直接与传递依赖、许可证、实际 SQLite 原生版本及目标 OS/架构，沿用总设计第 17 节 |

保持一份解决方案。初始目录沿用总设计的 TenonEdge.sln；不要同时维护内容不同的 .sln 和 .slnx，也不为文件格式转换重排工程。

方法和属性使用明确类型，异步 I/O 的取消令牌逐层传递；纯转换与校验不得隐藏网络或数据库操作。应用日志使用结构化字段和既定脱敏规则；统一异常转换在 Host 边界完成，不在每个 API 重复捕获所有异常。适配器错误仍映射为总设计中约定的运行/投递结果。

SDK、NuGet 包、容器基础镜像和测试工具的精确版本在 T0/对应交付阶段锁定。本文只确定管理方式，不编造版本兼容性或许可核查结果。

## 6. 测试组织与 CI

### 6.1 测试项目

- UnitTests：纯映射、数值、图校验/编译、身份和目标比较、策略与时间计算。采用 xUnit.net 与其断言；测试使用可控时间，不依赖随机数据或长时间 sleep。无需替身的代码不引入 Mock 接口。
- IntegrationTests：API 认证授权、防 CSRF、OpenAPI、配置迁移、密钥持久化、单实例锁和真实 SQLite 文件。API 使用 WebApplicationFactory；它的包名含 Mvc，不代表生产服务需要改用 Controller 或 Razor Pages。
- 协议与故障集成：同一测试工程中按类别隔离使用真实 Broker、模拟从站、HTTP 接收端和子进程的测试。环境不足须明确跳过原因，不能退化成假数据后仍标同一用例通过。
- TestPlatform：仍在 T4 按平台契约建立，不因测试目录规划而提前建设完整平台或发布进生产包。

每个测试或受控测试集合使用独立临时数据目录、数据库与动态端口。SQLite 的锁、WAL、重启和持久化行为使用磁盘临时文件，不用内存数据库代替；只有明确依赖共享夹具的用例才串行运行，不全局关闭并行来掩盖共享状态问题。

API 进程内测试不能证明真实 TLS、浏览器 Cookie 策略、DNS 出站校验、数据卷持久化或进程崩溃恢复。相关验收按 V-01、V-04、V-07、V-08、V-11 和既有 FS 场景启动真实进程/网络或容器；浏览器相关部分在 T3 完成，断电部分需真实硬件。

### 6.2 执行入口

首版后端统一使用 `dotnet test` 的 VSTest 路径，锁定与所选 xUnit.net 版本匹配的 `xunit.runner.visualstudio` 和 `Microsoft.NET.Test.Sdk`。不同时为不同项目无意混入两套测试运行器；今后改为 Microsoft.Testing.Platform 须统一迁移命令与 CI，并重新验证测试发现和报告。

T0 在 Linux CI 中落实以下顺序；这里是待实施要求，不是已经执行的命令记录：

1. 按 global.json 安装 SDK，记录环境，locked mode 还原依赖。
2. Release 构建及格式/分析检查，不在构建阶段静默更新锁文件。
3. 执行本阶段已有的单元、API 和存储集成测试，输出测试报告，核对实际发现的用例数量；零用例不能算通过。
4. 按 linux-x64 自包含方式发布最小宿主，在干净环境启动并检查健康端点；输出实际 SQLite 原生版本。后续有相应适配器时再接入真实 Broker 的 CI 场景。
5. 保存构建、测试及发布证据，失败时保留必要的脱敏日志；缺少硬件的测试单独标注，不能改成成功。

普通构建/测试流程不自动推送、发布版本或修改其他仓库。T0—T2 不创建前端工程，CI 不把尚不存在的 web/ 构建列为通过条件。T3 增加前端后沿 FE-D01 整合构建，不改写已验证的后端边界。

## 7. T0 执行与工程验收增补

本节与 `docs/development-readiness.md` 第 5、6、7 节一起执行；原 V/FS 编号及通过标准不变。本文补的是工程实施和证据组织，不新增进入 T0 的前置门槛，也不要求 T0 完成 T1—T5 的产品功能。

建议分三个批次：先交付可构建的最小宿主、测试入口和公共构建设置；再落实配置存储、身份密钥和最小认证的测试；最后汇总协议最小原型、依赖选择及进入 T1 的条件。每批按实际需要创建工程，不以空目录数量作为进度。

| 编号 | 工程验收 | 可观察的结果 | 阶段 |
|---|---|---|---|
| BE-AT-01 | 目录与引用 | 已创建工程符合第 2 节依赖方向；检查能发现 Runtime/Adapters 引用 Host 或 Abstractions 引用 ORM 的错误；无无用占位工程 | T0，后续持续 |
| BE-AT-02 | API 模板与权限 | 至少一组实际管理 API 使用分组 Minimal API；401/403、字段错误和 ProblemDetails 可测试；OpenAPI 有稳定且不重复的 operationId | T0，后续新增 API 复用 |
| BE-AT-03 | 生命周期 | 作用域验证开启；实际运行时宿主只创建一次；请求结束后不使用已释放服务；数据库与连接资源由明确的所有者释放 | T0 基础、T1/T2 完整 |
| BE-AT-04 | 构建与锁定 | 本地和 CI 使用同一 SDK/测试运行器，locked restore 与 Release 构建可复现；依赖版本变动不会被静默接受 | T0 |
| BE-AT-05 | 测试隔离与真实性 | xUnit 用例实际执行；API 与存储测试有独立数据目录；TLS、网络、重启等外部行为有对应真实环境测试或明确的未验证状态 | T0 基础，按 V/FS 所属阶段扩展 |
| BE-AT-06 | 独立发布 | linux-x64 自包含最小宿主在未预装 .NET 的干净环境可启动；持久化和健康检查按 V-01、V-07、V-08 留证据 | T0，T5 完整交付 |
| BE-AT-07 | 阶段记录 | implementation-status 记录提交、用例/命令、环境、结果和阻塞项；未执行、失败、跳过与通过分开，不将计划或调研探针写为项目测试通过 | 每批次 |

当前所有 BE-AT 均为“未验证”。T0 建立的 `docs/implementation-status.md` 同时索引 BE-AT 与原 V/FS 证据，不再创建第二套实现状态表。

T0 可以按现行默认开展可调整的基础实现和验证，这不代替维护者确认。进入 T1 仍须满足 development-readiness 第 1 节的条件；D-07 未确认不得定稿依赖其行为的运行时实现，D-05 在对应 API 定稿前处理。D-06、D-08 等 T4 事项不阻塞其他阶段。

## 8. 官方依据与核查范围

以下用于核对框架能力，不代表本项目已经运行验证。核查日期为 2026-09-29；实现时按锁定版本再次核实。生命周期和测试文档在网页访问不可用时阅读了官方仓库的对应 Markdown 源文档。

- [ASP.NET Core Minimal API 的路由分组与独立端点文件](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/route-handlers?view=aspnetcore-10.0)：MapGroup、端点元数据和独立映射入口。
- [.NET BackgroundService 使用 scoped 服务](https://github.com/dotnet/docs/blob/main/docs/core/extensions/scoped-service.md)：AddHostedService 的单例生命周期、显式作用域与释放。
- [ASP.NET Core 集成测试](https://github.com/dotnet/AspNetCore.Docs/blob/main/aspnetcore/test/integration-tests.md)：WebApplicationFactory、测试宿主和 xUnit 集成；不沿用示例中的 EF Core 作为本网关数据库方案。
- [.NET global.json](https://github.com/dotnet/docs/blob/main/docs/core/tools/global-json.md)：SDK 版本与 rollForward、allowPrerelease 的含义。
- [NuGet 集中版本管理](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)：Directory.Packages.props 与项目 PackageReference 的分工。

现有协议库、SqlSugar 和 SQLite 的来源及待验证行为继续引用 development-readiness §5，不在本文重新宣称兼容性或许可已核实。
