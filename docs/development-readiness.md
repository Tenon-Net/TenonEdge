# TenonEdge 开发准备

- 整理日期：2026-09-29。
- 基线：`docs/tenonedge-design.md` 1.6（含同日的开发准备补充）与 `docs/functional-spec.md`。核查开始时仓库 HEAD 为 `9c7e089`，工作区没有未提交修改，也还没有代码、测试或接口契约文件。
- 用途：记录开发准备的结论、已解决的问题、待维护者确认的事项、T0 验证清单、关键故障场景验收，以及各事项阻塞哪个阶段。规则本身只在设计文档和功能需求中定义，本文引用而不重复。
- 状态用语：“已定义”指规则已写入文档、尚未实现；“待实现”；“未验证”指验证尚未执行；“待确认”指等待维护者决定。本文中没有任何一项处于“已实现”或“已通过”。
- 维护：每个阶段结束时更新第 1、4、5、6 节。实现状态与测试证据记在 `docs/implementation-status.md`（T0 建立），本文只记录准备工作与决策。待确认事项得到答复后，先改设计文档正文和第 22 节，再在本文第 4 节标注“已确认”及日期。

## 1. 结论

- **可以进入 T0。** T0 本身就是完成依赖验证的阶段，第 5 节的验证是 T0 的工作内容，不是进入 T0 的前提。待确认事项中没有阻塞 T0 开始的；D-01、D-02 最好在 T0 的首次设置定稿前答复，D-09 在锁定依赖前答复，未答复时按现行规则或推荐方案实现，并在 T0 结束时列为待调整。
- **核心功能实现（T1 起）有条件进入**，需要同时满足：
  1. T0 中 V-01、V-06、V-07、V-08 有结论，且没有阻塞性的未通过项；V-04（MQTTnet）中与发布有关的结论（发布超时、断线检测、TLS 证书校验）须在 T1 实现 MQTT 输出之前得出，其余部分与 V-05（Modbus 库）在 T2 实现相应适配器之前得出。
  2. 维护者确认 D-07（应用改为“先提交、后激活”）。不确认时，T1 的运行时骨架要按原顺序另行设计启动恢复。
  3. D-05 在 T1 实现连接与凭证 API 之前答复。
- 其余待确认事项属于对应阶段（T4）的 P1，不阻塞 T1—T3。G1 需求不计入首版的开发前阻塞项。

## 2. 核查范围

- 逐项核查了本次提出的八个议题（第 3 节）。依据是当前工作区的文档；历史评审 `docs/reviews/2026-09-28-architecture-review.md` 只用于理解决策来由，没有改写。
- 外部资料只做了静态核查：阅读官方文档、源码和发布记录，没有编译、运行或连接任何设备、Broker 或平台（第 5 节）。唯一的例外是 S-17、S-19：核查人员在仓库之外的临时目录里用最小程序观察过 SQLite 的实际版本、连接池行为，以及 SqlSugar 与 Microsoft.Data.Sqlite 的组合。这些只是调研探针，不是项目测试，须在 V-06、V-07 中复现。
- 仓库中没有代码，因此没有“文档与代码不一致”的记录项。

优先级：

- **P0**：核心实现开始前必须确定，否则影响数据安全、运行边界或基础契约。
- **P1**：进入对应功能阶段前必须确定，不阻塞其他模块。
- **G1**：明确后置，不计入首版开发前的阻塞项。

## 3. 已解决的问题

| # | 议题 | 修改前的情况 | 处理 | 位置 | 状态 |
|---|---|---|---|---|---|
| 1 | 配置应用的激活边界 | 新计划先启动、稳定后才提交 effective，提交前就可能采集、入库和发送；草稿版本何时固定、回滚能否撤回数据、停止超时的单元如何处理都没有写明 | 六步流程与激活边界；单实例规则；启动保护；中断点表；回滚语义 | 设计 §7.4；功能 §6 | 已定义；D-07 待确认 |
| 2 | 全局资源预算与图展开 | 只有单条路线的节点数和多来源节点的上限；调试缓存只限制显示长度，没有存储截断和全局预算；路径数和单条消息的扇出没有上限 | 路径上限与饱和计数（伪代码）；调试缓存在捕获时截断，有对象与全局预算；在途字节、输入通道、预览并发、运行计划规模 | 设计 §7.2、§8.4、§8.7；功能 §4.3、§5.3 | 已定义 |
| 3 | 首次初始化与管理访问 | “首次访问设置”和“默认 HTTP”是已确认决定；没有写明 HTTP 提示不是防护；令牌可以经 HTTP 创建；转发头的信任范围和两种协议的 Cookie 隔离没有定义 | 保留两项决定，写明残余风险与最小保护；HTTPS 判定、Cookie 按协议分开、令牌创建仅限 HTTPS 直接修正；设置码列为 D-01、D-02；“默认只允许 HTTPS”和“备份只允许经 HTTPS”两项推荐已撤回（第 4 节） | 设计 §3.3、§10.3；功能 §1、§12.3 | 部分已定义，部分待确认 |
| 4 | 数据接管与缓存承诺 | 功能 §6.2、AT-25、AT-27 有无条件的“不丢失”；Broker 的积存上限、持久化间隔和会话过期没有作为前提；容量只按条数估算 | 四个阶段的口径、输入输出承诺、丢弃分类与计数持久化、故障矩阵、附带 Broker 的部署检查、容量估算 | 设计 §8.5、§8.6；功能 §6.2、§7.1、§8.3 | 已定义 |
| 5 | 平台字段同步与故障隔离 | 字段变化只有提示；随后平台返回的 422 会累计到“连续 20 条被拒”，暂停整个平台连接 | 字段结构比较与“字段待同步”；设备级错误 code 与目标级错误区分 | 设计 §9.3、§9.4；功能 §11.2 | 已定义；D-06、D-08 待确认 |
| 6 | 凭证变更、迁移与恢复 | 设计写“更新指纹”，功能写“选择新连接”，两处不一致；通用目标换凭证没有规则；恢复后旧队列没有规则 | 指纹组成与隔离记录；通用目标换凭证须声明账户；迁移分两种形式并定契约；备份各组成部分的版本、恢复标记与中断处理、恢复后的队列 | 设计 §8.3、§10.4；功能 §2.3、§8.4、§10.4 | 已定义；D-05 待确认 |
| 7 | 数据类型、时间与协议细节 | int64 只有风险提示；保留消息、重连后的积压、RTU 迟到应答、原因码都没有规则 | 四种输出类型与 64 位整数的无损表示；首版原因码；时间规则；保留消息与订阅协调；RTU 静默间隔与 Modbus 测试证据 | 设计 §6.1、§6.2、§6.4、§10.1 | 已定义 |
| 8 | 配置导入与引用一致性 | “按 key 合并”只对设备和点位成立；连接、数据来源、路线没有稳定标识；雪花 ID 的机器号固定，不能跨网关唯一 | 新增 connectionKey、inputKey、routeKey；预检与重映射；接收方参数变化时凭证须重新填写 | 设计 §10.2、§10.5；功能 §2.2、§3.1、§5.1、§10.2 | 已定义 |

核查中确认已经合理、只补了引用的内容：连接正常不代表传感器仍在上报（设计 §9.4、§21.4）；数据新鲜度提醒（GW-21）和运行事件推送（GW-24）留在 G1；心跳不进补传队列。

## 4. 待维护者确认的决策

以下事项会改变维护者已经确认的决定，或是需要取舍的产品行为。每项给出一个推荐方案；文档中已按“未确认前按”一列编写，确认后只需小范围修改。

| 编号 | 事项 | 推荐方案 | 影响与代价 | 未确认前按 | 阻塞阶段 | 级别 |
|---|---|---|---|---|---|---|
| D-01 | 首次设置是否需要一次性设置码 | Docker 与原生安装默认启用：设置码在主机上生成（安装脚本输出或 `tenonedge setup-code` 命令），网页设置时输入；整机保持“首次访问设置”（评审 Q19 的理由是整机现场看不到控制台），需要时可在出厂时生成设置码并贴标签 | 非整机部署多一步；整机若启用需增加出厂工序；改变了 Q19 对非整机部署的适用范围。设置码只解决“谁能初始化”，经 HTTP 提交时仍可能被截获 | 首次访问设置加设计 §10.3 的最小保护 | T0 首次设置接口定稿 | P1 |
| D-02 | 主机侧重置后是否必须输入设置码 | 采纳：重置命令生成一次性设置码，只在执行命令的终端显示一次，在设置窗口内有效，库中只存哈希，连续输错 5 次作废 | 执行重置的人本来就在主机终端前，不受 Q19 理由的限制，代价很小 | 重新开放窗口加最小保护 | T0 | P1 |
| D-05 | 修改连接的接收方参数后，已保存的凭证是否须重新填写 | 采纳：scheme、主机、端口、基础路径变化时凭证作废，须重新填写；管理员可以显式保留，记审计 | 合法修改地址时多填一次凭证；防止配置人员借修改地址把不可读回的秘密发往自己的服务器 | 保留凭证（配置导入已按推荐规则处理） | T1 连接与凭证 API | P1 |
| D-06 | “字段待同步”期间的数据 | 不入队、不补发、计数，与未绑定设备一致 | 该设备在同步前的数据缺失；备选“只发送未变化的字段”数据更连续，但要按字段裁剪报文，平台上各字段的更新时间会不一致 | 按推荐 | T4 | P1 |
| D-07 | 应用改为“先提交、后激活”，以启动保护代替“启动成功或稳定后才切换指针” | 确认 | 调整了评审 P1-2 已采纳的两点。一是指针切换时机：原方案先启动新计划、成功或稳定后才提交，新版本在提交前已经采集和发送；现在提交前只构建不运行。二是“全有或全无”的范围：原方案启动阶段任一单元失败都整体回滚；现在结构性失败在准备阶段发现，原配置不中断，提交之后的单元启动失败只降级该单元，不整体回滚。自动退回上一版本保留，但只在进程反复非正常退出、且上一生效版本曾稳定运行时，由启动保护触发一次；回滚被拦截或再次失败时进入“运行失败”，输入停止，须人工处理。功能需求第 6.3 节的相应条目已标“待确认” | 按推荐（文档已按此编写） | T1 运行时骨架 | P0 |
| D-08 | TenonIoT 的设备级错误 code 对“连续 20 条被拒”的计数透明（既不累加也不清零） | 确认 | 细化 2026-09-29 的补充决定，使一台设备的问题不会暂停整个平台连接；通用 HTTP 输出不变 | 按推荐 | T4 | P1 |
| D-09 | SqlSugarCoreNoDrive 带入的传递依赖与许可声明（S-19） | 保留 SqlSugar（评审 Q21 是维护者的选择）。T0 试验能否在不影响 SQLite 使用的前提下去掉 SQL Server 驱动栈；去不掉则接受，并把全部传递依赖及其许可列入许可清单。许可按 GitHub 的 MIT 与 NuGet 声明的 Apache-2.0 两份文本一并附带，同时向上游确认 | 这些传递包会增大发布包和镜像，也扩大漏洞扫描范围；两种许可都允许商业使用和再分发，但须按设计第 17 节记录。如维护者因此改变 Q21，只影响 config.db 的访问层，不影响 queue.db | 按推荐，许可状态记为“待核查” | T0 依赖锁定 | P1 |

D-03（新安装默认“只允许 HTTPS”）和 D-04（备份只允许经 HTTPS）的推荐已于 2026-09-29 讨论后撤回，维持原规则：默认 HTTP，备份在两种协议下都可用，理由见设计 §10.3。两个编号不再使用。

## 5. T0 验证清单

### 5.1 官方资料的静态核查

核查日期均为 2026-09-29。只阅读了官方文档、源码和发布记录，没有编译、运行或连接设备；S-17、S-19 中的调研探针是例外，见第 2 节。“已核实”只表示资料支持设计中的描述，或设计已据此修改，不代表运行验证通过；运行验证见 5.2 节，当前全部未执行。

| 编号 | 对象 | 核查结论与对设计的影响 | 来源（版本或提交） | 状态 |
|---|---|---|---|---|
| S-01 | MQTTnet 版本与发布记录 | 最新为 5.2.0（GitHub 与 NuGet 均为 2026-07-01），之后没有新发布。5.2.0 修复了“已取消或超时的发布收到迟到 PUBACK 时断开连接”，支持设计“不低于 5.2.0”的要求。尚未解决：持续发布时保活检测不到断线（issue #2244，修复未合并）；主干有一项未发布的 TLS 默认校验破坏性修复（#2254）。两点已写入设计 §2.3。许可 MIT | github.com/dotnet/MQTTnet：release v5.2.0（提交 14463d1）、issue #2244、v5.2.0...master 对比；nuget.org/packages/MQTTnet/5.2.0.1603 | 已核实（文档、源码） |
| S-02 | MQTTnet v5 客户端行为 | 默认协议为 5.0，用 WithProtocolVersion 指定 3.1.1；CleanSession 默认 true，ClientId 默认随机；5.0 下会话过期间隔默认 0，断开即结束会话，所以设计要求显式使用 3.1.1。ManagedClient 在 5.0.0 移除。AutoAcknowledge 默认 true，AcknowledgeAsync 重复调用会抛异常，处理器抛异常时该条不确认。PublishAsync 只受调用方的取消令牌控制，不使用选项中的超时；MQTT 5 的原因码放在结果中，IsSuccess 包括“没有匹配的订阅者”。连接结果有 IsSessionPresent。接收队列没有容量上限，逐条处理，断线时未处理的消息被丢弃（QoS 1 由 Broker 重投）。与设计 §2.3、§6.2、§8.2 一致 | MQTTnet v5.2.0 源码：Options/MqttClientOptions.cs、Receiving/MqttApplicationMessageReceivedEventArgs.cs、MqttClient.cs、Internal/AsyncQueue.cs、Publishing/MqttClientPublishResult.cs；Upgrading guide（wiki） | 已核实（源码） |
| S-03 | Mosquitto 版本与许可 | 最新稳定版 2.1.2（2026-02-09）；2.0 线最后一个打了标签并公告的是 2.0.22（2025-07-11），ChangeLog 另列 2.0.23 但没有标签和公告。双许可 EPL-2.0 或 EDL-1.0（BSD-3-Clause），与设计第 12 节 T5 一致 | mosquitto.org：ChangeLog、下载页、博客；v2.1.2 的 LICENSE.txt（提交 99fa50f） | 已核实（文档） |
| S-04 | Mosquitto 配置默认值与持久化 | persistence 默认关闭；传统持久化在退出、每 1800 秒（autosave_interval）和收到 SIGUSR1 时整库快照（写临时文件、fsync 后改名）；max_queued_messages 默认 1000（每个客户端，不含在途），满后丢弃新消息，每个客户端记一次日志；max_queued_bytes 默认不限；persistent_client_expiration 默认永不过期；max_inflight_messages 默认 20；queue_qos0_messages 默认关闭；定义了监听器时默认禁止匿名；2.1 起 max_packet_size 默认 2,000,000 字节。2.1 新增官方推荐的 persist-sqlite 插件：变更随时写入 SQLite（WAL，同步级别默认 normal，默认每 5 秒批量提交），2.1.2 起不能与 persistence true 同时使用。已写入设计 §8.5。丢失窗口是由源码推断的结论，须 V-09 实测 | mosquitto.org/man/mosquitto-conf-5.html（2.1）；v2.0.22 的 man/mosquitto.conf.5.xml、src/conf.c、src/database.c、src/persist_write.c；mosquitto.org/documentation/persistence/sqlite/；v2.1.2 的 plugins/persist-sqlite/init.c | 已核实（文档、源码） |
| S-05 | NanoMQ | MIT，最新 0.25.6（2026-08-19），支持 MQTT 3.1.1 与 5.0。“持久会话下离线 QoS 1 消息落盘”在官方文档中没有找到依据；维护者在 issue #1898 中说明启用 SQLite 且 clean_start=false 时会持久化，但文档没有写；#2320（离线 QoS 1 丢失，已关闭）和 #2412（客户端主动断开后会话被清理）是风险信号。设计 §3.1 已注明采用前须实测 | github.com/nanomq/nanomq：release 0.25.6、LICENSE.txt、issue #1898、#2320、#2412；nanomq.io 文档 | 关键能力未找到依据 |
| S-06 | MQTT 3.1.1 规范 | [MQTT-3.3.1-6/8/9]：新建订阅时补发保留消息且 RETAIN=1，按已建立订阅转发时 RETAIN=0；[MQTT-3.8.4-3]：相同的重复订阅会重发保留消息；[MQTT-4.6.0-2]：接收方按 PUBLISH 到达顺序发送 PUBACK；[MQTT-3.1.2-4/5]：CleanSession=0 时断开后保存会话，QoS 1、2 消息必须存入会话，QoS 0 可选；DUP 只表示可能是重投；3.1.1 没有会话过期的概念，由 Broker 的管理策略决定。与设计 §6.2 一致 | docs.oasis-open.org/mqtt/mqtt/v3.1.1/os/mqtt-v3.1.1-os.html（OASIS Standard，2014-10-29） | 已核实（文档） |
| S-07 | NModbus | 3.0.83（NuGet 2026-04-16），MIT，默认分支最后提交 2026-06-30。RTU over TCP 可用 FactoryExtensions.CreateRtuMaster 加 TcpClientAdapter 组合，仓库中没有示例或测试。发送前的清空缓冲对网络流无效，残留字节会保留。SocketAdapter 的读写超时映射相反，应使用 TcpClientAdapter。重试默认 3 次、间隔 250 毫秒；从站忙默认无限重试，除非设置 SlaveBusyUsesRetryCount。不设默认超时（NetworkStream 默认无限）。Async 方法不接受取消令牌。TCP 校验事务号，不符时抛异常并重发请求；RetryOnOldResponseThreshold 默认 0，即不跳过旧帧。RTU 校验地址、功能码和 CRC。预发布版 4.0.0-alpha010 的源码提交无法核实，不采用。已写入设计 §2.3、§6.1 | github.com/NModbus/NModbus（提交 baddfca；标签 v3.0.83 = d96e770）；nuget.org/packages/NModbus；learn.microsoft.com 上 .NET 10 的 NetworkStream.Flush 与 ReadTimeout 文档 | 已核实（源码） |
| S-08 | FluentModbus | 5.3.2（2025-03-28），MIT，默认分支最后提交 2026-05-19。ModbusRtuOverTcpClient 自 5.3.0 提供，没有测试、文档或示例。异步方法接受取消令牌，取消或超时会关闭连接。TCP 与 RTU over TCP 的读写超时默认无限（连接超时 1000 毫秒），只在客户端自行建立连接时生效。TCP 客户端不校验事务号；RTU over TCP 校验单元号和 CRC。已写入设计 §2.3 | github.com/Apollo3zehn/FluentModbus（提交 5f45dba；标签 v5.3.2 = 5e44710；release v5.3.0）；nuget.org/packages/FluentModbus | 已核实（源码） |
| S-09 | 前端工具链 | Tailwind CSS 4 的最低浏览器为 Chrome 111、Safari 16.4、Firefox 128，与设计一致（最新 4.3.3）。Vite 8 于 2026-03-12 发布，最新 8.3.1，要求 Node.js 20.19+ 或 22.12+；Node.js 20 已于 2026-04-30 结束维护，构建环境宜用 22 或 24 LTS。@xyflow/react 最新 12.12.0，MIT，peer 依赖 react ≥17，没有找到明确的 React 19 支持声明，配合 React 19 需要 zustand 4.5.6 及以上。React Flow UI 组件基于 shadcn/ui 与 Tailwind，组件本身没有单独标注许可，设计 §2.3 已要求复制前核实。TanStack Table 的 npm latest 已是 v9（9.2.4，2026-08），v8 最新 8.21.3；两者都不内置虚拟化，官方示例使用 TanStack Virtual。主版本在 T3 选定并记录理由，不因“最新”而默认选用。shadcn/ui 支持 Tailwind 4 与 React 19，MIT | tailwindcss.com/docs/compatibility；vite.dev/blog/announcing-vite8、registry.npmjs.org/vite；nodejs/Release 的 schedule.json；registry.npmjs.org/@xyflow/react、xyflow issue #5229、reactflow.dev/ui；registry.npmjs.org/@tanstack/react-table、tanstack.com/table/v8/docs/guide/virtualization；ui.shadcn.com/docs/tailwind-v4 | 已核实（文档）；React 19 声明与 UI 组件许可未找到明确依据 |
| S-10 | 浏览器剪贴板与安全上下文 | paste 事件的 clipboardData 在 MDN 和 W3C Clipboard API 中都没有安全上下文限制（这是由没有限制标注推断的，没有见到“可在 HTTP 下使用”的明文）；navigator.clipboard、crypto.randomUUID 只在安全上下文可用，crypto.getRandomValues 不受限；`http://localhost` 属于安全上下文，本机测试不能代表经网络的 HTTP 访问 | MDN：Element/paste_event、ClipboardEvent/clipboardData、Navigator/clipboard、Crypto/randomUUID、Secure_Contexts；w3.org/TR/clipboard-apis（2026-06-24 工作草案） | 已核实（文档）；粘贴一项为推断，须 V-03 实测 |
| S-11 | JSON 数值的互操作 | RFC 8259 第 6 节：只有 [-(2^53)+1, 2^53-1] 内的整数能保证各实现理解一致；RFC 7493 第 2.2 节：需要精确交换更大的数时建议编码为字符串；MDN：JSON.parse 会把数字转为 JavaScript 数字，可能丢精度。与设计 §6.4 一致 | rfc-editor.org 的 RFC 8259（2017-12）、RFC 7493（2015-03）；MDN Number/MAX_SAFE_INTEGER、JSON/parse | 已核实（文档） |
| S-12 | .NET 10 | 2025-11-11 正式发布，LTS，支持到 2028-11-14；最新补丁 10.0.12（2026-09-08，SDK 10.0.401） | dotnet.microsoft.com 支持策略页；dotnet/core 的 releases-index.json 与 release-notes/10.0 | 已核实（文档） |
| S-13 | ASP.NET Core Data Protection | Linux 默认密钥目录为 `$HOME/.aspnet/DataProtection-Keys`，没有可用目录时使用临时密钥，进程退出即丢失；非 Windows 默认不加密落盘。SetApplicationName 的值应在各次部署间保持一致，否则部署目录变化会使旧数据不可读。密钥默认 90 天轮换，框架不会自动删除旧密钥，官方建议不删除。容器中应把密钥放在数据卷上。官方说明它“并非主要用于机密数据的无限期保存”，但可以用于长期保护。没有公开 API 检测临时密钥（回退逻辑在内部类中，只有日志），设计 §10.3 已改为由网关检查数据目录是否在已挂载的卷上 | learn.microsoft.com 的 Data Protection 文档（default-settings、configuration/overview、introduction、implementation/key-management，aspnetcore-10.0）；dotnet/aspnetcore v10.0.0 的 XmlKeyManager.cs、DefaultKeyStorageDirectories.cs、LoggingExtensions.cs | 已核实（文档、源码）；检测接口未找到 |
| S-14 | 转发头中间件 | 默认只信任回环地址，ForwardLimit 为 1。.NET 10 起 KnownNetworks 标为过时，改用 KnownIPNetworks，KnownProxies 不变。信任列表都为空时不校验来源；设置 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true 会启用转发头并清空信任列表。设计 §10.3 已规定没有登记代理时不启用中间件，也不使用该环境变量 | learn.microsoft.com 的 proxy-load-balancer 与 .NET 10 破坏性变更 ipnetwork-knownnetworks-obsolete；dotnet/aspnetcore v10.0.0 的 ForwardedHeadersOptions.cs、ForwardedHeadersOptionsSetup.cs | 已核实（文档、源码） |
| S-15 | Cookie 前缀 | `__Host-` 须带 Secure、Path=/、不带 Domain；非安全来源（http）不能设置带 Secure 的 Cookie，所以经 HTTP 无法设置或覆盖 `__Host-` Cookie。“HTTP 不能覆盖已有 Secure Cookie”的规则见 RFC 6265bis 草案，尚不是正式 RFC | MDN Set-Cookie、Cookies 指南；draft-ietf-httpbis-rfc6265bis（2026-09-28） | 已核实（文档） |
| S-16 | PasswordHasher | Microsoft.Extensions.Identity.Core 可单独使用（最新 10.0.12，MIT），不依赖 EF Core。默认格式 V3：PBKDF2-HMAC-SHA512，128 位盐，256 位子密钥，迭代 100,000 次 | nuget.org/packages/Microsoft.Extensions.Identity.Core；learn.microsoft.com 的 PasswordHasherOptions.IterationCount；dotnet/aspnetcore v10.0.0 的 PasswordHasher.cs | 已核实（文档、源码） |
| S-17 | Microsoft.Data.Sqlite 与实际的 SQLite 版本 | 最新 10.0.12（2026-09-08），依赖 SQLitePCLRaw.bundle_e_sqlite3 2.1.12 及以上，对应 SQLite 3.53.3；10.0.0 至 10.0.10 依赖 2.1.11 及以上，对应 SQLite 3.49.1。连接默认进入连接池。所带原生库的编译选项把 synchronous 默认值设为 FULL（含 WAL 模式），官方文档没有说明 synchronous 是否按连接生效。核查时在临时目录中用最小程序观察到：两个版本分别加载 3.49.1 与 3.53.3；synchronous 按物理连接保存，连接池复用的物理连接保留原设置。这只是调研探针，不是项目测试 | nuget.org/packages/Microsoft.Data.Sqlite/10.0.12；SQLitePCL.raw 发布说明（v2.1.11、v2.1.12）；learn.microsoft.com 的连接字符串文档；dotnet/efcore v10.0.0 的 SqliteConnectionPool.cs | 已核实（文档、源码）；按连接生效的官方措辞未找到 |
| S-18 | SQLite 已知缺陷与持久性 | WAL 重置缺陷影响 3.7.0 至 3.51.2，在 WAL 模式下两个以上连接同时写入或执行检查点时可能损坏数据库，3.51.3 修复（另有 3.44.6、3.50.7 的回移版本）；当前最新 3.53.4。WAL 加 synchronous=NORMAL 在断电后可能回滚已提交的事务，FULL 满足 ACID。`sqlite_version()` 与 `PRAGMA compile_options` 可用。设计 §2.3 已要求实际加载的版本不低于 3.51.3 | sqlite.org：wal.html 第 11 节、changes.html、pragma.html（synchronous、compile_options）、lang_corefunc.html | 已核实（文档） |
| S-19 | SqlSugarCoreNoDrive | 最新 5.1.4.221（NuGet 2026-09-13）。不带 SQLite 驱动，SQLite 支持直接使用 Microsoft.Data.Sqlite 的类型；但依赖 SystemCommon，后者带入 Microsoft.Data.SqlClient、Azure.Identity、MSAL 等约 22 个传递包。许可：GitHub 的 LICENSE 为 MIT，NuGet 包声明为 Apache-2.0，两处不一致。连接检查事件 AopEvents.CheckConnectionExecuting/Executed 在每次检查连接时触发（自动关闭连接时每条命令一次），可作为 PRAGMA 挂载点，但须幂等。核查时在临时目录观察到 5.1.4.221 与 Microsoft.Data.Sqlite 10.0.12 在 .NET 10 上可以一起工作（调研探针，不是项目测试）。官方文档站未核对 | api.nuget.org 上 sqlsugarcorenodrive 与 systemcommon 的注册信息；github.com/DotNetNext/SqlSugar 的 LICENSE、SqliteProvider.cs、AdoProvider.cs、ConnectionConfig.cs（提交 01c1c70） | 已核实（源码）；与设计原描述不一致，见 D-09 |
| S-20 | System.Text.Json 的数值 | long、ulong、decimal 逐位写出，不经过 double（decimal 保留末尾的 0）；double、float 按最短往返格式写出，较大的值可能带指数（如 1E+21）；NaN 与无穷默认抛异常，只有开启 AllowNamedFloatingPointLiterals 才写成字符串。与设计 §6.4 一致（NaN 与无穷在序列化之前已判为 nonFinite） | learn.microsoft.com 的 Utf8JsonWriter.WriteNumberValue、JsonNumberHandling、标准数字格式字符串（net-10.0）；dotnet/runtime v10.0.0 的 Utf8JsonWriter.WriteValues.Double.cs、JsonWriterHelper.cs | 已核实（文档、源码）；NaN 默认抛异常一点文档未写明，依据源码 |
| S-21 | Chrome 对 HTTP 访问的默认询问 | 按官方计划，Chrome 154（2026 年 10 月）起默认开启“Always Use Secure Connections”的公网站点变体：访问 HTTP 公网站点前先询问。局域网 IP、单标签主机名、非唯一主机名（如 `staging.local`）和 `http://localhost` 不询问；用公共域名下的主机名访问 HTTP 时可能询问，用户的选择保留 15 天、再次访问时续期。企业可用 HttpAllowlist 策略免除指定主机；用 HttpsOnlyMode 策略强制严格模式（force_enabled）时，局域网 IP 也会询问。Google 表示今后会设法降低局域网站点采用 HTTPS 的门槛，没有给出时间表。结论：默认 HTTP、按局域网 IP 访问不受影响；部署文档说明用内网域名访问时的询问与免除办法（设计 §10.3） | Chrome Security Team，“HTTPS by default”（2025-10-28，security.googleblog.com，现跳转至 blog.google）；Chromium 文档 docs/security/ask-before-http/ask-before-http-adoption-guide.md（main 分支） | 已核实（文档）；按局域网 IP 访问不询问须在 V-03 中实测；非默认端口与公共域名的组合文档未写明 |

### 5.2 验证任务与通过标准

所有任务当前状态均为“未验证”。“阶段”列说明在哪个阶段执行运行验证；前端相关项按 `AGENTS.md` 在 T3 创建前端工程时执行，T0 只核对资料。

| 编号 | 对象 | 设计依赖的行为 | 验证步骤 | 通过标准 | 阶段 |
|---|---|---|---|---|---|
| V-01 | .NET 10 SDK 与运行时 | 后端 .NET 10、自包含发布、单个 RuntimeHost 捕获单元异常（设计 §3.1、§8.1） | 记录 `dotnet --info`；建最小宿主，自包含发布为 linux-x64，在没有 .NET 的干净容器中运行；验证后台服务未处理异常的默认行为 | 发布产物可运行；SDK、运行时版本和支持期写入 implementation-status | T0 |
| V-02 | 前端构建环境 | Node.js 只用于构建；Vite、React 19、Tailwind 4、shadcn/ui、React Flow、TanStack Table（设计 §3.1） | T0 记录各工具的当前版本与 Node.js 要求；T3 建工程时锁定依赖，在 CI 中执行 `npm ci` 与构建 | 构建可复现，产物由宿主提供，运行时不需要 Node.js | T0 资料；T3 运行 |
| V-03 | 最低支持浏览器 | Chrome/Edge 111、Safari 16.4、Firefox 128（设计 §3.1） | 在最低版本上冒烟：登录、画布拖拽连线、点表粘贴，分别经 HTTP 与 HTTPS。HTTP 须经网卡地址访问：`http://localhost` 属于安全上下文，测不出 HTTP 下的限制。另在当时最新的 Chrome（154 及以上）默认设置下，经局域网 IP 访问 HTTP 端口 | 功能可用；没有只在安全上下文可用的 API 报错；最新 Chrome 默认设置下经局域网 IP 访问不出现“访问 HTTP 前询问”（S-21） | T3 |
| V-04 | MQTTnet | 显式 3.1.1；CleanSession=false 与稳定 ClientId；SessionPresent；关闭自动确认后按序确认；PublishAsync 只靠取消令牌超时；MQTT 5 原因码在结果中；内部接收队列无界；v5 没有 ManagedClient（设计 §2.3、§6.2、§8.2） | 用真实 Mosquitto：断线期间发布 QoS 1，重连后 SessionPresent=1 且收到；不确认的消息重连后重投；Broker 暂停响应时 PublishAsync 按令牌超时；持续发布时拔掉网线，确认网关能发现断线并重连（issue #2244）；显式配置的 TLS 证书校验拒绝不受信任的证书、接受管理员信任的证书；QoS 0 高速输入加慢回调时观察内存；订阅时保留消息 RETAIN=1、已建立订阅转发时为 0、重复订阅会重发保留消息 | 各项行为与设计一致；不一致处回写设计文档 | T0 最小原型；T2 完整 |
| V-05 | Modbus 库比较（NModbus、FluentModbus） | Modbus TCP 与 RTU over TCP；显式超时；库内重试设为 0 或 1；停止时能打断阻塞读取；较旧事务号的应答被跳过；RTU 校验地址、功能码、CRC，残留字节被排空（设计 §2.3、§6.1） | 同一组测试：进程内从站模拟器，可注入延迟、从站忙与迟到应答；覆盖全部类型与字节序、超时耗时、停止延迟、迟到应答、线程占用。重点核对 5.1 节列出的差异：NModbus 事务号不符时重发请求、从站忙无限重试、网络流上清空缓冲无效；FluentModbus 不校验 TCP 事务号、RTU over TCP 客户端没有测试 | 选定的库加上适配层满足 AT-02 的用例，停止在停止超时内完成；记录选型理由、许可和适配层补齐的行为 | T0 比较；T2 完整 |
| V-06 | SqlSugarCoreNoDrive 与 Microsoft.Data.Sqlite | NoDrive 包加 Microsoft.Data.Sqlite 能工作；每个物理连接执行 PRAGMA（WAL、synchronous=FULL）；SqlSugarScope 单例与 CopyNew；CodeFirst 只做新增（设计 §2.3、§8.5） | 最小配置库集成测试：通过 AopEvents.CheckConnectionExecuting/Executed 幂等地设置 PRAGMA，连接池中每个物理连接回读 `PRAGMA journal_mode` 与 `PRAGMA synchronous`；并发读写；执行一次版本化迁移。列出 `dotnet list package --include-transitive` 的全部传递依赖及许可，试验能否去掉 SQL Server 驱动栈（D-09） | 回读结果为 wal 与 FULL；PRAGMA 挂载点确定，否则按设计改用长连接；传递依赖清单与 D-09 的结论写入 `docs/reference-notes.md` | T0 |
| V-07 | 实际加载的 SQLite 原生库 | queue.db 与 config.db 的持久化语义依赖实际加载的 SQLite 版本，不只看 NuGet 包版本（设计 §2.3、§8.5） | 启动时执行 `SELECT sqlite_version()` 与 `PRAGMA compile_options`，写日志并在系统信息中显示；记录 NuGet 包版本与原生库版本；在自包含发布与 Docker 镜像中分别确认 | 实际版本不低于 3.51.3（WAL 重置缺陷的修复版本，S-18）；人为加载较低版本时，按设计 §2.3 拒绝启动运行时 | T0 |
| V-08 | ASP.NET Core Data Protection | 密钥环显式持久化到数据目录；SetApplicationName；数据目录须在已挂载的卷上；容器重建、安装路径变化后仍可解密；恢复时合并密钥环（设计 §2.3、§10.3、§10.4） | 首次启动生成密钥文件；更换安装路径、重建容器（挂卷）后解密；容器中数据目录不在已挂载的卷上时被检测并拒绝启动运行时（框架没有检测接口，S-13）；合并两个密钥环目录后新旧密文都能解密；记录未配置密钥加密器时的警告如何处理 | AT-19 中与凭证有关的用例的最小版本通过 | T0 |
| V-09 | 附带 Broker（默认 Mosquitto） | 版本与许可；持久化、持久化间隔、积存上限、会话过期、在途窗口、报文大小等配置项和默认值（设计 §3.1、§8.5） | 按设计 §8.5 的检查表写配置模板；比较传统快照持久化与 2.1 的 persist-sqlite 插件（含其同步级别与提交间隔）；实测网关重启期间 QoS 1 补收、超过积存上限时的丢弃、Broker 被强制终止与断电后的丢失窗口、会话过期；如考虑 NanoMQ，按同一组用例实测 | 选定版本与持久化方式；实测数值写入部署文档；AT-27 的前提写明；许可按设计第 17 节登记 | T0 资料；T2 实测；T5 文档 |
| V-10 | 点表批量粘贴与大表格 | 从 Excel 粘贴多行（包括经 HTTP 访问时）、逐行校验标红、批量修改、大量点位（功能 §3.3） | T0 核对资料：非安全上下文中的粘贴事件、TanStack Table 的虚拟化方案；T3 原型：粘贴 1,000 行、渲染 10,000 行、测量校验耗时 | 原型在最低版本浏览器上完成粘贴与逐行校验，出错行标红不影响其他行；耗时与内存记录在案，阈值实测后再定 | T0 资料；T3 原型 |
| V-11 | ASP.NET Core 安全组件 | 转发头只信任已登记的代理；`__Host-` Cookie；防 CSRF；按来源地址限流；单独使用 Identity.Core 的 PasswordHasher（设计 §3.3、§10.3） | 集成测试：未登记代理时不启用转发头中间件，伪造转发头无效；设置 ASPNETCORE_FORWARDEDHEADERS_ENABLED 也不改变这一行为；登记代理后只接受其转发头；两种协议的 Cookie 不能互换；限流按实际来源地址计算；记录密码哈希的格式与迭代次数（S-16） | AT-32 的最小版本通过 | T0 |
| V-12 | JSON 数值的序列化 | long、ulong、decimal 逐位写出；double、float 按最短往返格式；NaN 与无穷不写入 JSON（设计 §6.4） | 单元测试：2^53+1、ulong 最大值、decimal 高精度、float32 的 23.7；NaN 的处理 | 输出文本逐位符合设计 §6.4 | T0 |
| V-13 | 雪花 ID 生成器的来源 | 拷贝 TenonAdmin 的 SnowflakeIdGenerator 源码（设计 §10.1） | 记录来源仓库、提交、路径和许可，按设计第 17 节登记 | 许可允许复制，`docs/reference-notes.md` 有记录 | T0 |
| V-14 | 系统时钟同步检测 | 诊断页显示时钟是否同步（设计 §8.1，检测方式在 T1 核实） | 比较可用的检测方式在 Docker 与原生部署下是否可用 | 两种部署下都能给出“已同步、未同步、未知”之一 | T1 |

## 6. 关键故障场景验收

“证据类型”区分软件测试（含故障注入）、模拟设备、真实 Broker 与真实设备；没有执行的一律记为未验证，不以模拟结果代替实物结果。当前全部为未验证。

| 编号 | 场景 | 可观察的通过标准 | 证据类型 | 对应验收 | 阶段 |
|---|---|---|---|---|---|
| FS-01 | 应用时引用了无法解密的凭证 | 准备阶段失败；原配置的输入计数连续，没有中断；该版本标为“未生效”并显示原因 | 集成测试 | AT-13、AT-28 | T3 |
| FS-02 | 应用时某个输入停不下来 | 应用失败；已停止的输入按原版本恢复；卡住的单元显示“停止超时”；同一单元始终不超过一个实例 | 集成测试（测试适配器） | AT-13 | T3 |
| FS-03 | 提交前终止进程 | 重启后加载原版本；历史中标“未生效：进程中断”；queue.db 中没有新版本号生成的记录 | 故障注入 | AT-10、AT-28 | T3 |
| FS-04 | 提交后终止进程 | 重启后加载新版本 | 故障注入 | AT-10 | T3 |
| FS-05 | 新版本启动后进程反复非正常退出 | 达到启动保护上限后，上一生效版本曾稳定则自动回滚到它（新版本号，操作人为“系统”），否则直接进入“运行失败”；自动回滚的版本仍反复退出时同样进入“运行失败”：输入不启动，发送继续，`/readyz` 返回 503，页面说明原因。正常重启、已稳定版本上的退出不计入 | 故障注入 | AT-13 | T1、T3 |
| FS-06 | 回滚会改变仍有待发数据的连接的接收方 | 回滚被拦截，并给出处理办法 | 集成测试 | AT-14、AT-28 | T3 |
| FS-07 | 先扇出再扇入的路线使路径数超限 | 校验阶段拒绝并指出数据来源；校验耗时不随路径数成倍增长 | 单元测试 | AT-29 | T3 |
| FS-08 | 持续输入 1 MiB 报文 | 调试缓存与在途字节不超过预算；记录显示截断标记和原始大小 | 压力测试 | AT-29 | T3 |
| FS-09 | 两人同时提交首次设置 | 只成功一次；首页显示首次设置的时间和来源 IP；初始化后重启不再开放设置 | 集成测试 | AT-24 | T0 |
| FS-10 | 伪造转发头；经 HTTP 创建 API 令牌 | 伪造的转发头不改变协议判定和限流；经 HTTP 创建令牌被拒绝 | 集成测试 | AT-32 | T0 |
| FS-11 | 网关重启期间设备以 QoS 1 发布，数量在 Broker 积存上限内 | 恢复后全部补收 | 真实 Broker | AT-25、AT-27 | T2 |
| FS-12 | 积存超过 Broker 的上限 | Broker 丢弃超出部分，网关侧没有计数，Broker 日志可见；部署文档写明 | 真实 Broker | AT-27 | T2、T5 |
| FS-13 | Broker 被强制终止 | 实测的丢失窗口与持久化间隔相符，写入部署文档 | 真实 Broker | AT-27 | T5 |
| FS-14 | 发送过程中强制终止网关 | 已提交的记录保留；接收端统计的重复条数不超过“最后一个提交间隔内已确认的条数 + 在途上限” | 故障注入 | AT-10、AT-11 | T1 |
| FS-15 | 整机断电 | 已接管的记录保留（前提：存储如实执行 fsync） | 真实设备（HW-05） | HW-05 | 有硬件时 |
| FS-16 | queue.db 损坏 | 移到一旁并新建，记运行事件；配置不受影响 | 故障注入 | AT-12 | T1 |
| FS-17 | 重新订阅后 Broker 补发保留消息 | 不生成投递，计入“保留消息已忽略” | 真实 Broker | AT-25、AT-30 | T2 |
| FS-18 | 超过 2^53 的 int64、uint64 端到端 | 读取、映射、管理 API、预览与各类输出逐位一致 | 集成测试 | AT-30 | T2—T4 |
| FS-19 | RTU over TCP 的迟到应答；Modbus TCP 较旧事务号的应答 | 下一个请求不会取走旧应答；同一连接上的其他从站每个周期多出的等待不超过“读取超时 + 静默间隔” | 模拟从站 | AT-02 | T2 |
| FS-20 | Modbus 读取阻塞时停止单元 | 在停止超时内退出，不产生第二个实例 | 模拟从站 | AT-13 | T2 |
| FS-21 | 已绑定设备新增字段后应用 | 只有该设备停止向平台发送且计数可见；其他设备照常，连接不暂停；同步后恢复 | TestPlatform | AT-31 | T4 |
| FS-22 | 平台返回 401 | 整个目标暂停，记录保持待发；更换凭证后恢复 | TestPlatform | AT-26 | T4 |
| FS-23 | 迁移过程中终止进程 | 要么全部生效，要么全部不生效；审计补记实际结果 | 故障注入 | AT-14 | T3 |
| FS-24 | 迁移到 GatewayId 不同的平台连接 | 被拒绝 | TestPlatform | AT-14 | T4 |
| FS-25 | 恢复同一网关的旧备份；恢复另一台网关的备份 | 前者指纹相符的照常发送、不符的被隔离；后者原有记录全部被隔离 | 集成测试 | AT-19 | T5 |
| FS-26 | 恢复过程中断电；在每个阶段之间终止进程 | 下次启动完成恢复或回退到恢复前的状态，`previous/` 中始终是恢复前的内容；界面发起的恢复在进程退出后由服务管理器自动重启 | 故障注入 | AT-19 | T5 |
| FS-27 | 从另一台网关导入配置 | 按业务标识匹配，内部 ID 重新分配，凭证需要填写；文件有错误时草稿不变 | 集成测试 | AT-17 | T3 |
| FS-28 | 通用目标有待发数据时更换 Token，并声明更换了账户 | 旧记录被隔离，新记录用新凭证发送 | 集成测试 | AT-14 | T2、T3 |

## 7. 事项与阶段

| 阶段 | 开始前须确定 | 本阶段内完成的验证 | 不阻塞本阶段 |
|---|---|---|---|
| T0 | 无；D-01、D-02 宜在首次设置定稿前答复，D-09 在依赖锁定前答复（均为 P1） | V-01、V-06、V-07、V-08、V-11、V-12、V-13；V-04、V-05 的资料核查与最小原型；V-02、V-09、V-10 的资料核查；FS-09、FS-10 | 其余待确认事项 |
| T1 | D-07（P0）；D-05（P1，连接与凭证 API 之前）；V-04 中与发布有关的结论 | V-14；FS-05、FS-14、FS-16 | D-06、D-08 |
| T2 | V-04 其余部分、V-05 有结论 | V-04、V-05 完整验证；V-09 实测；FS-11、FS-12（实测部分）、FS-17—FS-20、FS-28 | 同上 |
| T3 | 无新增 | V-02、V-03、V-10；FS-01—FS-08、FS-23、FS-27、FS-28（配置界面部分） | 同上 |
| T4 | D-06、D-08（P1） | FS-21、FS-22、FS-24 | — |
| T5 | 无 | V-09 与 S-21 写入部署文档；FS-12（部署文档部分）、FS-13、FS-25、FS-26；有硬件时 FS-15 | — |
| G1 | 不计入首版 | — | GW-20—GW-26，包括数据新鲜度提醒（GW-21）、运行事件推送（GW-24）；以上游事件 ID 派生投递编号；多设备自动分流 |

### 7.1 G1 能力的接口边界与建议顺序

以下只说明首版已经留好的接口和以后的建议顺序，不构成首版任务；进入实施前仍按设计文档第 4 节以实际需求确认。

| 能力 | 首版已留好的接口 | 以后的边界 | 建议顺序 |
|---|---|---|---|
| 数据新鲜度提醒（GW-21） | 推送类设备只显示最后接收时间（设计 §6.4、§9.4、§21.4）；轮询类已有离线判定 | 设备增加“预期上报周期”；按 receivedAt 判为正常、过期或未知，只影响状态显示和运行事件，不改变数据处理与投递 | 1（LoRa 等长周期上报场景最先需要） |
| 运行事件推送（GW-24） | 运行事件已持久化，状态变化合并计数（设计 §3.3） | 经已有 MQTT/HTTP 连接以固定格式发出，与遥测分开配额；不做告警规则 | 2 |
| 以上游事件 ID 派生投递编号 | 投递编号已是记录字段；输入侧重投的去重边界已写明（设计 §8.5） | 输入配置一个上游事件 ID 的 JSON Pointer，按“事件 ID + 目标 + 路径”派生投递编号 | 3（先实测 LoRa 上游的重复情况） |
| 按变化上报（GW-22） | 平台契约已预留“未携带”字段语义（设计 §9.4） | 映射之后按路径保存变化状态，周期补发全量 | 按需求 |
| 多设备自动分流、HTTP 推送接收（GW-20、GW-21） | 状态按 deviceKey 统计，连接可共享 | 显式的未知设备策略；宿主提供统一的入站端点 | 按需求 |
