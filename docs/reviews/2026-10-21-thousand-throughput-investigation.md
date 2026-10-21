# QA-PERF-01：千条吞吐定位与候选回归证据

本轮承接实施状态 §2.323，目标仍为整合后的稳定千条与 T3 收口，不进入 T4/T5/G1。只操作 `t3-product-integration` 独立工作树；两个原工作树、人工试用服务和数据未修改。当前产品仍为待验收候选；本轮完整回归及实际浏览器复核结果见文末，不把绿色当作历史偶发问题已修复或T3已验收。

证据根 `P=/home/shiny/.cache/tenonedge-product-integration/evidence/20261021/thousand-loop`。原整合版正式千条失败为 **770.583/s**、来源 overrun **17157**，没有进入 503/恢复阶段，见上一集成报告。本轮所有失败及撤回试验保留，不覆盖原证据。

## 定位与已撤回试验

仓库外 `P/tools/short-output-loop.mjs` 是缩小反馈环：1000 个真实产品模拟来源、原持久队列及单目标 loopback HTTP，独立 ext4 临时数据和自有 Host，默认热身 5 秒、测量 10 秒；不是正式性能或硬件验收。它核对吞吐、来源跳过、策略/存储丢弃、重复、精确大整数和正常退出。

| 试验 | 收到/s | 待发变化 | overrun | 结论 |
|---|---:|---|---|---|
| 原实现 `baseline-01` | 736.801 | 2765 → 3793 | 1166 → 2797 | 复现失败 |
| 预算 FIFO 直接交接 `admission-short-01` | 735.650 | 继续积压 | 非零 | 无改善，代码和专项测试已撤回 |
| 格式化后归还未用预约 `workspace-short-01` | 755.689 | 4656 → 6939 | 0 → 0 | 消除本次来源跳过，但输出仍失败 |
| 去掉显式读事务 `workspace-read-short-01` | 777.696 | 继续积压 | 0 | 对照不足以支持收益，已恢复原读事务 |
| 最后分段计时 `timing-read-detail-01` | 744.231 | 4781 → 7171 | 0 → 0 | 真实 exit1，保留失败 |

读事务的计时对照分别约 764.083/s、764.178/s，累计 10000 次队头读取均约 3.9 秒，不以单次小幅变化宣称改善。FIFO 交接、无显式读事务试验补丁分别保存为 `admission-trial.patch`、`read-transaction-trial.patch`。

CPU 采样为 `1000-cpu.nettrace`、`1000-cpu.speedscope.json` 及两个 top 文本，仅附加精确自有 Host PID/startTicks；采样包含等待和原生代码，不将样本百分比当整机 CPU 占比。边界计时把成本分成实时身份、控制、队头读取、凭证版本和 HTTP 准备/网络；队头读取中主要成本落在查询执行，而不是打开连接或 COMMIT。含启动/JIT 的微秒均值只用于定位，不是 SLA。

临时 `[DEBUG-tk]` 探针已从 `DeliveryStore`、`OutputSenderUnit`、`HttpConnectionSession` 完全删除；最后补丁为 `read-detail-timing.patch`。之后重新构建整个 solution，正式脚本不使用探针版本的程序集。

## 候选及实验边界

1. **格式化后的保守预约缩减**：全部输出路径冻结后，归还已经不再使用的最坏情况映射/编码工作区；源消息、冻结记录、写入暂存和回执仍计费到原写者结果。冻结记录按 `4 * ByteSize + 4096 + 128 * headerCount` 保守计费，不增加原预约，不改变持久队列配额。重复调用不重复归还；取消等待不释放未知写者结果，重建沿用原冻结 ID 和所有权。这不是内存入队即成功，也不是 SQLite 持久性证明。
2. **发送者独占预编译队头查询（候选，工程边界已获确认）**：一个运行循环拥有一个读连接和查询命令，退出或故障时释放；每次仍开启并结束短读事务，只读取当前已提交队头，不预取正文、不跨 HTTP 等待保留快照。管理屏障、毒消息探测、身份/暂停/凭证实时检查和唯一发送者不变。连接页缓存目标设为 64 KiB，避免多目标时采用默认的大缓存目标；这不是整个原生内存的硬上限。

第二项最初涉及后端规范 §4“短连接/短事务”的澄清，未确认时仅作为实验、不提交。**维护者于2026-10-21明确选择“发送者独占读连接，保持短事务”**，现已同步总设计§8/§22及后端规范§4；该批准不同于配置库独立客户端工厂，不代表吞吐/T3已经验收。原 `ReadPending` 管理/分页入口继续使用原短连接和短事务。

预编译查询早期原型的独立缩小反馈环：

| 证据 | 热身/测量秒 | 收到/s | 待发变化 | overrun |
|---|---|---:|---|---|
| `prepared-head-short-01` | 5 / 10 | 1024.179 | 3985 → 3538 | 0 → 0 |
| `prepared-head-short-02` | 30 / 30 | 1063.279 | 2860 → 710 | 0 → 0 |

两次坏报文、重复和策略/存储故障均为零，Host 正常退出。它们早于后续 64 KiB 页缓存目标及释放保护的版本，不以此证明当前最终版本稳定千条，也不把积压下降写成已清空。

## 当前验证证据

实际执行：

```sh
dotnet build TenonEdge.sln -c Release --no-restore
dotnet test tests/UnitTests/TenonEdge.UnitTests.csproj -c Release --no-build \
  --filter 'FullyQualifiedName~InputFormattingRetentionTests|FullyQualifiedName~DispatcherPublicationTests|FullyQualifiedName~InFlightBudget|FullyQualifiedName~BudgetedInput' \
  --logger trx --results-directory "$P/workspace-and-head-tests/unit"
dotnet test tests/IntegrationTests/TenonEdge.IntegrationTests.csproj -c Release --no-build \
  --filter 'FullyQualifiedName~DeliveryCacheTests.PreparedHead|FullyQualifiedName~OutputAcknowledgementPipelineTests|FullyQualifiedName~OutputFailureTests' \
  --logger trx --results-directory "$P/workspace-and-head-tests/integration"
dotnet test tests/UnitTests/TenonEdge.UnitTests.csproj -c Release --no-build \
  --logger 'trx;LogFileName=unit.trx' --results-directory "$P/candidate-unit-full"
python3 .github/scripts/verify-test-results.py "$P/candidate-unit-full" --require TenonEdge.UnitTests
```

SDK 10.0.401；重新构建整个 solution 成功、0 warning/error，避免 Integration 使用旧运行库。定向单元 **43/43**、定向集成 **33/33**；当前候选完整单元 **2094/2094**，全部 0 失败/跳过，TRX 核对脚本通过。新增真实 SQLite 集成核对可见 COMMIT/不可见未提交、回滚、目标隔离、复用后空队头/新队头，以及读取后可完成 WAL TRUNCATE；单元覆盖重复释放、其他所有者、预算降低、大估算、四种持久交接结果及取消后分发器重建。预算修正的原四项红测试见 `workspace-red/red.trx`，不能把新的绿色当作所有场景已完成。

当前完整集成和浏览器回归尚未完成。新冻结 `prepared-candidate-frozen-01.json` 含 2117 个源码/测试/运行产物文件；它是未提交实验候选，不替代上一集成版本的冻结。正式千条脚本已真实结束，exit1；执行后 2117 个冻结文件全部未变（`prepared-candidate-frozen-01-verified.json`）：

```sh
TENONEDGE_TEST_WEBROOT="$PWD/web/dist" \
TENONEDGE_QA_DATA_ROOT=/home/shiny/.cache/tenonedge-product-integration/workloads \
TENONEDGE_QA_INPUTS=1000 TENONEDGE_QA_OUTAGE_SECONDS=3 \
TENONEDGE_QA_RESULTS="$P/prepared-candidate-1000ps-01" \
node web/scripts/product-performance.mjs
```

该次 **失败**：60 秒稳态收到 **1023.986/s**，待发 **5764→4294**，p95/p99 **6129/6350 ms**；3 秒 503 后待发 **7585**。持续输入下再观察 60 秒仍未清空，断言最后一次读到 7139，最终诊断为 **7418**；不是恢复成功，未进入后续 15 秒观察。来源 overrun 0、队列策略丢弃 0、坏报文 0，Host exit0，报告仍为 `passed:false`。正常稳态有改善但恢复余量不足，不能把 95% 稳态门禁单独当作整项通过。

原脚本仍要求 60 秒稳态、3 秒 503、持续输入下最多 60 秒首次清空和后续 15 秒观察，不增大配额、不改变 95% 原门禁。下一步继续区分积压时配额聚合查询、元数据查询与网络成本，不能只靠延长恢复时限或延长热身洗绿。仍需正式低档、完整后端回归、较长期稳态/恢复以及剩余 T3 退出核对；实际 Chrome/Edge111、Safari16.4、Excel 来源、远端 CI、实物/掉电和维护者验收没有新增通过证据。

## 积压时配额覆盖索引的单变量试验

在独立真实 SQLite 库放入 8000 条 1024 字节正文记录，对同一配额 SQL 各运行 50 次。`quota-index-probe/probe.trx` **1/1**，不是吞吐验收；临时测量代码已移至仓库外 `tools/QuotaIndexProbe.cs`，不留在产品测试套件。

| 查询 | 原索引平均 ms | 覆盖索引平均 ms |
|---|---:|---:|
| 目标 COUNT/SUM | 6.424432 | 0.808168 |
| 全局 SUM | 5.169876 | 0.678734 |

原计划使用 `ix_delivery_connection_state_seq` / `ix_delivery_state_created`，覆盖后均为 `USING COVERING INDEX`，避免配额汇总反复加载正文页。新增两项固定查询计划回归在旧实现真实 **2/2 失败**（`quota-index-red`）；随后只追加 migration10：`delivery(state, connection_id, byte_size)`。不跨事务缓存配额，不修改 FIFO/配额/写者边界；旧迁移不改写。

首轮定向为 **52 通过/1 失败**（`quota-index-green`）：旧 schema2 测试通过剥除当前结构构造历史库，遗漏删除新增索引，删除 `byte_size` 时被 SQLite 拒绝。修正七个历史结构夹具先去除 migration10 索引，保留全部原数据/迁移断言。随后同一 53 项 **53/53**、0 失败/跳过（`quota-index-green-02`）；整个 solution 重新 Release 构建、0 warning/error，新候选完整 Unit **2094/2094**，核对脚本通过（`index-candidate-unit-full`）。完整集成仍未执行。

追加索引后的正式千条对照使用相同命令和原门禁，证据 `index-candidate-1000ps-01/performance.json`，**仍 exit1 / passed:false**：

- 60 秒稳态 **1061.456/s**，待发 **5838→2055**，p95/p99 **5404/5690 ms**；改善不等于正式通过。
- 3 秒 503 后待发 **5015**，持续输入下再等 60 秒仍未清空，断言最后读到 7203，最终 **7091**；未进入后续 15 秒观察。
- 来源 overrun、策略/存储丢弃、重复接收及坏报文均为 0，Host exit0。新冻结 `index-candidate-frozen-01.json` 的 2117 个文件在正式运行期间均未变，另有 `*-verified.json` 核对。

**schema10 仍为实验候选，尚未提交或部署**。旧容量经验档案仅适用于 queue9，当前选择器对 schema10 正确返回未校准；不得直接把旧档案扩大到新版本。若保留该索引，必须重测容量矩阵、登记新档案并恢复相关端点/浏览器回归，不能把当前完整 Unit 绿色当作容量功能已完成。

恢复阶段比单独稳态更差，下一反馈环对照相同来源下“无额外观测”与“完成一次输出 API 后等待 250 ms 再观测”；诊断页面也必须在负载下可用，不通过降低正式脚本观测频率换取通过。

仓库外 `tools/observer-output-loop.mjs` 已真实结束，exit0，原始报告 `observer-loop-01/short-loop.json`。热身 30 秒后，无额外观测的 30 秒收到 **1067.931/s**、待发 **2739→455**；随后带观测的 30 秒收到 **1009.675/s**、待发降至 **19**，输出 API 的 p50/p95 为 **48.151/77.778 ms**，重复/坏报文 0、Host exit0。它是诊断反馈环通过，不是正式 503 恢复通过。

**该先后对照不足以证明吞吐差来自 API**：后一阶段的积压接近清空，接收速率回到实际生成率本就是合理现象，不能把前一阶段清除更多旧积压的速度当作无观测稳态上限。下一步应分别测量原输出 API 的快照加载/解析、队列汇总和运行状态成本，并在可比积压下核对，而不是据这次差值直接引入缓存或减少安全读取。

## 输出诊断边界实测与已验证目录复用候选

`observer-timing-01.log` / `observer-boundary-summary.json` 对原输出端点取得 **98 次**分段样本（真实 1000 来源及队列）。缩小反馈环本身 exit0，但不是故障恢复或长期性能验收。各段微秒 p50/p95 为：

| 边界 | p50 µs | p95 µs |
|---|---:|---:|
| 数据库加载快照 | 1517.3 | 2149.9 |
| 完整快照解析/执行视图 | 36793.4 | 72013.2 |
| 队列统计 | 18952.15 | 33787.6 |
| 运行状态快照 | 1060.5 | 1941.4 |
| 其他状态/控制/速率整理 | 653.5 | 962.8 |
| 端点内部总计 | 61448.25 | 111397.3 |

这是同一请求边界的成本测量，不将各段分位数直接相加或当作 SLA。探针补丁保存在 `observer-boundary-timing.patch`，`[DEBUG-ob]` 已全部从源代码删除，随后重新构建整个 solution。

据此新增 `RuntimeOutputCatalog`：隶属于原每 Host 单例 `RuntimeVersionStore`，最多保留一个已验证版本的不可变输出目录（连接名/key/kind/运行单元 ID），不持有快照正文、数据库客户端或事务。**每次 API 仍从数据库读取完整当前快照，并执行原大小与实际内容 SHA-256 校验**；只有 revision、schema 和经核对的 hash 均相同时才复用目录。缺失、损坏、版本改变或 schema 不符不能返回旧目录。安全、凭证、暂停、队列和运行状态不在该目录中，继续按原实时查询；保留原执行视图对旧完整快照的 queue-only 输出恢复，不用草稿替代。

先抽出原目录计算（尚不复用）跑新增回归：`output-catalog-red-03/red.trx` **1 失败/6 通过**，失败为同一已验证版本重复解析；此前两次夹具构建错误保留在 `output-catalog-red` / `output-catalog-red-02`，不是产品性能证明。加入单条目录复用和每次完整性校验后，`output-catalog-green` 定向 Unit **30/30**、API Integration **5/5**、0 失败/跳过，Release 全 solution 构建 0 warning/error。覆盖内容/hash/schema/超限损坏、同 revision 内容改变、新版本替换、跨 Host 隔离、32 个并发读取、完整快照冻结名称和旧 queue-only 目标；原 API 回归继续核对三角色真实数量/暂停、未应用草稿不替换名称，以及缓存预热后快照损坏/队列不可读。

当前候选完整 Unit **2103/2103**，TRX 核对通过（`catalog-candidate-unit-full`）。在新冻结 `catalog-candidate-frozen-01.json` 上重新执行原正式千条脚本，证据前缀 `catalog-candidate-1000ps-01`，实际 **exit1 / passed:false**，2119 个冻结文件在该次运行期间全部未变：

- 60 秒稳态 **1050.621/s**，待发 **6202→3106**，p95/p99 **5975/6283 ms**。
- 3 秒 503 后待发 **6030**；持续输入下 60 秒仍未清空，断言末值 **9960**、最终 **10000**（达到原条数配额），后续 15 秒未执行。
- 本次新增来源 overrun **22624**、配额拒绝 **26911**，`noPolicyLoss:false`；存储丢失、重复接收及坏报文仍 0，Host exit0。必须保留这个更差的失败，不以目录局部回归通过或均值较旧基线高宣称千条通过，也不无证据归因共享环境。

恢复阶段原发送循环的独立分段诊断已结束（`catalog-recovery-timing-01/performance.json`，exit1）：稳态 1048.571/s，待发 5475→2650；3 秒 503 后 5517，恢复 60 秒仍未清空，最终 4145。此次来源 overrun、配额拒绝、存储损失、重复及坏报文均为 0，Host exit0；没有重现上一轮达到配额上限的严重恶化，但不能据此认定已修复或归因环境。带探针诊断不是正式性能通过。

`recovery-sender-timing-summary.json` 按每 1000 次发送汇总：早段 6000—35000 次平均 1007.391 ms，晚段 110000—139000 次平均 993.223 ms；两次实时指纹读取分别 215.074/216.209 ms，两次控制读取 139.251/141.328 ms，队头 83.308/80.175 ms，凭证版本 67.572/71.521 ms，SendAsync 451.172/430.286 ms，未覆盖提交/等待等 51.014/53.704 ms。热路径自身已约 1 ms/条，故障恢复余量不足；此处 SendAsync 还包含适配器自己的实时身份检查，不能把它全部当网络成本。

已保存 `recovery-sender-timing.patch`，从 `outputsender-before-recovery-timing.cs` 恢复产品候选（未还原 HEAD、未丢弃预编译读取），确认源码无 `[DEBUG-tk]` / `[DEBUG-ob]` / `[DEBUG-rc]`。之后全 solution Release 构建成功、0 warning/error（`post-recovery-timing-clean-build.log`）；原恢复诊断和清理构建会话均已结束。

## 真实配置库读取分解

仓库外 `tools/ConfigReadCostProbe.cs` 使用原 `TestConfigStore.Initialize()`、真实 config.db 迁移/WAL/FULL 和统一独立客户端工厂，对相同参数化 HTTP 非秘密身份投影分段计时。每种模式预热 500 次、五轮各 4000 次，正反交替顺序；不是产品吞吐、并发或持久性验收。首次执行 `config-read-cost-01` 因夹具校验和误写为 21 而非 22 失败，未改产品。修正夹具后 `config-read-cost-02` **1/1**，0 失败/跳过。临时代码已移出项目测试目录。

| 模式 | 五轮每次总耗时中位数 µs |
|---|---:|
| 只创建/释放独立客户端 | 2.839 |
| 创建、打开、关闭，不执行查询 | 8.220 |
| 原 SqlSugar GetDataReader | 31.861 |
| 同一原路径提前 Ado.Open | 28.649 |
| Ado.Open 后直接 DbCommand（仅对照） | 22.453 |

初轮 JIT 后时间明显下降，原始每轮/分段/分配结果完整保留在日志和 TRX，不把中位数解释为运行负载下 SLA。客户端创建不是主要成本；提前打开连接在后几轮没有稳定收益。直接命令对照绕过 SqlSugar 的 SQL 执行级 AOP，仅诊断，**未用于产品**，也未手工伪造事件。

下一单变量候选：让发送前指纹与凭证版本由同一次实时元数据读取生成，仍保留循环开始、发送前及适配器内检查，暂停/信任仍实时读取，不持有跨发送的安全缓存或配置库客户端。先补同代值、新调用重读、删除/创建、TLS 信任变化及原发送者集成回归，再运行原性能反馈环；不能仅据少一次查询宣称性能改善。

已在原 `IOutputTarget` 增加默认兼容的 `ReadSendIdentity()`，默认仍调用既有两属性；HTTP 原目标重写为一次实时 `DescribeHttp`，保留当前 TLS 信任版本及错误传播。原发送者在既有发送前核对位置取得该短命非秘密值，之后仍执行第二次手动暂停读取，适配器仍再次核对当前身份及秘密。没有跨发送缓存、额外数据库连接、SQL AOP 旁路或第二发送者；其他扩展沿用原默认属性行为。

先仅添加默认合同及回归（未修改 HTTP 或发送者调用）时，`send-identity-red-02` 单元 **3/3 失败**、原队列/发送者集成 **2/2 失败**：两次读取可配成旧指纹/新版本，原发送者尚未使用新入口。它们锁定新增合同及接入点，不声称原产品已经观察到错误投递。此前 `send-identity-red` 是测试 CA1861/CA1859 构建错误，单独保留，不算产品红例。实现后 `send-identity-green` 定向 Unit **44/44**、Integration **36/36**，0 失败/跳过；包含同代指纹/版本、新调用重读、匿名创建/删除、TLS 信任换代/不可读，以及暂停恰在元数据读取时提交后仍不发送、显式恢复才发送。原确认批处理、默认扩展目标、暂停/凭证轮换和身份隔离回归未删除。Release 全 solution 构建 0 warning/error；性能和完整回归仍待新候选执行。

随后修正两处测试 `if` 的格式（首次格式检查失败日志保留），重新 Release 全 solution 构建、0 warning/error，定向空白验证通过；新候选完整 Unit **2106/2106**，0 失败/跳过，TRX 核对通过（`send-identity-unit-full`）。冻结 `send-identity-candidate-frozen-01.json` 共 **2136** 个源码/测试/产物文件，以下两次负载期间未变，结束后逐文件核对通过：

- `send-identity-short-01`：30 秒热身、30 秒测量，接收 1027.361/s，待发 1245→248，overrun/策略损失/存储损失/重复/坏报文均 0、Host exit0；仅缩小反馈环。
- `send-identity-candidate-1000ps-01`：原正式命令和门禁，**首次完整 exit0 / passed:true**。60 秒接收 **1079.712/s**，待发 **4832→80**；该速率含清除热身积压，不称为无积压稳态峰值。3 秒 503 后待发 **3150**，持续输入下 **40.889 秒**首次读到零待发。后续 15 秒接收 **998.911/s**、阶段末待发 **11**，稍后最终快照 **181**，不能写成持续空队列。来源 overrun、配额/其他策略丢弃、存储损失、重复及坏报文均为 0，Host exit0，实际接收 139204 条。
- 初始 60 秒采集到接收的 p95/p99 为 **4708/5290 ms**（含旧积压），恢复后 15 秒为 **717/806 ms**。采样 Host RSS 最大 **439.184 MiB**、CPU 总时间/观察时间约 **2.424 个核**；这些仅为本机软件负载观测，不是整机上界或远端 HTTP SLA。

这是**一次短软件场景完整通过**，不是长期稳定千条、生产高性能或 T3 整体完成。此前所有正式失败仍有效保留；还须重复及较长稳态/恢复、50/500 回归、新索引的 queue10 容量重新校准和完整后端/受影响浏览器回归。当前先按新实际 schema10 重新测九组容量矩阵，选择器仍拒绝复用旧 queue9 档案，不提前登记新档案。

## schema10 存量迁移与容量独立重测

`schema10-capacity-matrix-02` 已结束：迁移 **3/3**、九组容量矩阵 **9/9**，均 0 失败/跳过；后者实际 8.1055 分钟。新迁移回归使用原写者准备待发/失败/隔离记录，退回 schema9 后两次按产品初始化迁移，逐字段比较八张表的全部行（含冻结元数据、二进制正文、计数与 sqlite_sequence），核对覆盖索引唯一存在。此前 `schema10-capacity-matrix-01` 仅为新增测试引用不存在属性的构建错误，不当作产品失败或通过。

实际回读 ext4、SQLite 3.53.3、schema10、WAL/FULL、page4096、checkpoint1000、autoVacuum0；唯一原写者、50 ms 分组提交、每组三轮插入/删除/checkpoint。九组最大增长 k 和原模型 k 详见[容量校准文档](../capacity-calibration.md#queue10-覆盖索引后的独立重测2026-10-21)及 `schema10-capacity-summary.json`。所有有限样本均不超过原经验模型；这不是安全上界、长期稳态、硬件或故障保证。

先将 Unit/API 的新档案期望改为 queue10，旧选择器不变：`schema10-profile-red` 实际 Unit **9 失败/12 通过**、API **1 失败/2 通过**，均无跳过。之后才据新测量登记 `linux-ext4-sqlite-3.53.3-queue10-v1`；公式、大小范围和 PRAGMA 条件不变，旧 schema9 及未来 schema11 继续拒绝。补齐历史迁移夹具最终版本断言及网页档案期望，不改历史源版本或保护门禁。

首次 `schema10-profile-green` Release 构建成功，但全 solution 空白校验发现此前新增两份测试的 switch/初始化格式问题而退出2，后续测试未运行，不当作绿色证据。仅格式化 `DispatcherPublicationTests.cs` / `RuntimeOutputCatalogTests.cs` 后，在独立 `schema10-profile-green-02` 目录重跑：

- `dotnet format whitespace TenonEdge.sln --verify-no-changes --no-restore` 通过；`dotnet build TenonEdge.sln -c Release --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false` 为0 warning/error。
- 完整 Unit **2106/2106**；关联 Integration **103/103**，均0失败/跳过。后者的实际过滤条件为 `(FullyQualifiedName~DeliveryCacheTests|FullyQualifiedName~DeliveryManualRetryTests|FullyQualifiedName~DeliveryFailureStoreTests|FullyQualifiedName~DeliveryMigrationTests|FullyQualifiedName~DeliveryResponseSummaryTests|FullyQualifiedName~DeliveryMigrationHoldTests|FullyQualifiedName~RuntimeOutputEndpointTests)&FullyQualifiedName!~MeasuresCapacityAmplificationAcrossCommitSizesAndRepeatedDeletion`，明确不重复计入前述8分钟容量矩阵。命令为 `dotnet test <相应工程> -c Release --no-build --no-restore --logger 'trx;LogFileName=<unit或integration>.trx' --results-directory <证据子目录>`，Integration另加上述filter；`verified.json`核对非零、执行/通过/总数相同及失败/跳过计数。
- `web/` 中固定 `npx --yes npm@11.21.0` 执行 `run typecheck`、`run lint`、`run format:check`、`run build`、`run api:check` 均通过。完整 Vitest **62文件、836/836**，JSON=`web-unit.json`，不是只运行容量组件。
- `PLAYWRIGHT_JSON_OUTPUT_NAME=<证据目录>/browser-outputs.json npx --yes npm@11.21.0 run test:e2e -- tests/outputs.spec.ts --workers=1 --reporter=list,json` **2/2**，Chromium156、约1.2分钟、0失败/跳过/重试。真实Host/HTTP对端分别位于tmpfs和ext4：前者仍诚实报告未校准，后者展示queue10档案/动态系数；继续核对积压、速率、退避、暂停持久化/重启、恢复及只读元数据。`node scripts/verify-results.mjs <web-unit.json> <browser-outputs.json>`通过。不把tmpfs浏览器场景算作tmpfs校准。

上述完整Unit和Web绿色仍不替代当前完整Integration、全量浏览器或最低版浏览器。随后冻结 `queue10-candidate-frozen-01.json` 的2136个源码/测试/产物，开始串行50/500/1000重复及600秒稳态复测，未据启动命令记录性能通过。

## 同一冻结候选的分档重复与600秒负载

`tools/run-queue10-performance.sh` 位于仓库外证据目录，已真实结束 **exit0**；总日志 `queue10-performance-matrix.log`，汇总 `queue10-performance-matrix-summary.json`。以下五次顺序运行，没有与本任务的构建/重型回归并行，2136个冻结文件逐次核对均未变。普通HTTP仍单请求在途，唯一队列写者/发送者、FIFO、WAL/FULL、配额及实时身份/暂停检查未放宽。

每次独立环境变量与命令保存在各证据目录的 `command.sh`，例如：

```sh
ROOT=/home/shiny/.cache/tenonedge-product-integration/worktree
P=/home/shiny/.cache/tenonedge-product-integration/evidence/20261021/thousand-loop
TENONEDGE_TEST_WEBROOT="$ROOT/web/dist" \
TENONEDGE_QA_DATA_ROOT=/home/shiny/.cache/tenonedge-product-integration/workloads \
TENONEDGE_QA_INPUTS=1000 TENONEDGE_QA_OUTAGE_SECONDS=3 \
TENONEDGE_QA_RESULTS="$P/queue10-standard-1000ps-01" \
node "$ROOT/web/scripts/product-performance.mjs"
```

600秒场景使用仓库外 `tools/long-performance.mjs`，精确差异见 `long-performance-harness.patch`：只更换外部执行所需的导入/根目录、稳态60→600秒、恢复后15→60秒及报告标签；**持续输入下60秒清空门禁、250ms恢复观测和全部损失/重复门禁不变**。其余四次使用原脚本；50档中断15秒，高档中断3秒，不混为相同故障持续时间。

| 证据前缀 | 稳态秒 | 接收条/s | 稳态前→末待发 | 故障后待发 | 持续输入下首次清空s | 恢复后条/s | 观察阶段末/最终待发 | 原门禁 |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| queue10-standard-50ps-01 | 60 | 50.070 | 19→19 | 753 | 17.229 | 49.964 | 0/0 | 通过 |
| queue10-standard-500ps-01 | 60 | 503.300 | 425→246 | 1694 | 7.366 | 499.632 | 0/0 | 通过 |
| queue10-standard-1000ps-01 | 60 | 1065.643 | 4456→530 | 3463 | 33.292 | 997.996 | 85/12 | 通过 |
| queue10-standard-1000ps-02 | 60 | 1009.118 | 696→132 | 3098 | 21.940 | 991.686 | 115/115 | 通过 |
| queue10-long-1000ps-01 | 600 | 1004.708 | 3359→537 | 3472 | 58.019 | 995.080 | 337/284 | 通过 |

五次均来源overrun、策略/存储丢弃、重复接收ID、坏报文为0，Host正常exit0；原始 `performance.json` 与 `exit-code` 一一对应，`frozen-verification.json` 逐次核对。速率大于名义输入包含旧积压排空，不是额外生成，也不能作为无积压吞吐上限。不同时间快照的瞬时待发未强行统一为0；首次清空不等于持续空队列。

600秒场景接收602839条，整个运行接收741107条；该阶段采集到接收p50/p95/p99为481/784/1619ms，含最初热身积压，恢复后60秒为527/821/995ms。采样Host RSS最大469.777MiB，整段约739秒的CPU均值约2.042个核；不含Node接收端和浏览器，不是内存上限、ARM/现场硬件容量或远端HTTP SLA。共享20逻辑核主机负载记录于报告，不无证据将差异归因环境。

**58.019秒恢复已接近60秒门禁，恢复余量仍偏紧。** 本轮证明相同本机软件场景下多次千条及一次10分钟稳态/故障恢复通过，不宣称生产长期稳定、24小时soak、实物或T3整体完成。此前红色结果均保留，不能用本轮通过抹去。

之后在同一冻结版本开始当前完整Integration及Chromium完整浏览器序列，命令与日志为 `tools/run-queue10-full-regression.sh` / `queue10-full-regression-01/`。完整Integration已真实结束：**2836通过、1失败、0跳过，13.8717分钟**，不是全量绿色。唯一失败为 `ConfigStoreProcessTests.QueueDatabaseUsesTheSameSqliteWithWalAndFullSynchronousWrites`，该真实子进程/磁盘用例的 `PRAGMA user_version` 断言仍为9L，实际回读10；这是此前扫描只匹配 `SchemaVersion` 而遗漏的直接SQL断言。该用例到达版本断言前已核对实际原生库、WAL/FULL、文件权限和正常停止，不能据此说版本断言已经通过。原TRX、执行退出码、逐项审计及失败日志全部保留。

完整后端结束后2136个冻结文件核对仍无变化。同一冻结版本的Chromium **188/188完整通过，39.0分钟、0失败/跳过/不稳定重试**，`browser-current.json` / `browser-current.log`、前端报告核对和 `after-browser-current-frozen.json` 均保留。整条 `run-queue10-full-regression.sh` 仍exit1，因为后端存在上述1项失败，不能把浏览器绿色改写成完整序列通过。

浏览器执行期间维护者明确批准发送者独占读连接/短事务约定，文档已按原边界澄清；为保持证据，实验源码注释在本轮结束后再同步。既有真实SQLite回归 `PreparedHeadReaderSeesOnlyCurrentCommittedState` 核对未提交变更不可见、提交后立即换代、目标隔离及游标；`PreparedHeadReaderDoesNotPinWalBetweenReadsAndCanBeDisposedTwice` 核对读取间隙checkpoint(TRUNCATE)成功、删除/新记录可见、重复释放及释放后拒绝。相关生命周期/管理屏障/身份与暂停回归继续保留，不因批准而省略测试；其他T3退出条件仍未核销。

浏览器结束后才修改两处：真实进程测试9L→10L，及已批准读取器的实验标签→发送者所有权注释；**没有产品行为变更**。在重建之前逐文件回比原2136文件清单，确认只存在这两个精确文本差异，逆向替换后SHA-256与原清单相同，证据 `queue10-final-validation-02/pre-rebuild-source-delta.json`。随后重新全格式校验/构建/完整Unit，冻结第二份候选并开始完整Integration、Chromium111及系统Google Chrome关联组、第二次600秒千条；这轮正在执行，不把旧二进制的冻结证据当作新产物未变或先记通过。

第二轮后端已结束：`dotnet format TenonEdge.sln --verify-no-changes --no-restore`通过，全solution Release构建0 warning/error；完整Unit **2106/2106**、完整Integration **2837/2837、13.6434分钟**，0失败/跳过/未执行。Integration仍用原 `--blame-hang-timeout 5m --blame-hang-dump-type none`，未增加期限或删除失败用例；修正旧版本断言后完整重跑，不是把定向1项补算为绿色。TRX分别在 `queue10-final-validation-02/unit/` / `integration/`，`backend-audit.json`核对总数/执行/通过/逐项outcome和退出码。新 `queue10-candidate-frozen-02.json` 的2136个源码/测试/产物在整轮后端结束后未变；重新 `api:check`通过。

两组关联浏览器现均结束并通过：归档 **Chromium111.0.5563.0为9/9、3.1分钟**，实际 **Google Chrome152.0.7977.82为9/9、3.0分钟**，0失败/跳过/重试。真实配置了 `TENONEDGE_TEST_URL=http://192.168.0.88:18082`，共享Host及两角色Modbus→HTTP路线通过网卡HTTP访问（独立输出诊断Host仍为回环）；覆盖首次设置/认证与离线资产、1000行/10000行点表、拖放/复制/50步画布历史、两角色无JSON路线/只读边界/关闭浏览器和Host重启，以及tmpfs/ext4两种输出容量/暂停/恢复。实际命令：

```sh
TENONEDGE_TEST_URL=http://192.168.0.88:18082 \
TENONEDGE_TEST_EXECUTABLE=<该轮独立二进制> \
PLAYWRIGHT_JSON_OUTPUT_NAME=<证据目录>/<该轮>.json \
npx --yes npm@11.21.0 run test:e2e -- \
  tests/auth.spec.ts:5 tests/auth.spec.ts:226 tests/auth.spec.ts:325 \
  tests/auth.spec.ts:372 tests/auth.spec.ts:522 tests/outputs.spec.ts \
  tests/gateway-workflow.spec.ts --workers=1 --reporter=list,json
```

二进制分别为 `/tmp/tenonedge-t3/minimum-browser/chrome-linux/chrome` 和 `/usr/bin/google-chrome`，版本文件/JSON/log/报告核对和逐轮冻结核对均在 `queue10-final-validation-02/`，2136文件未变。这是两组关联回归，不是两套188项全量；不将Chromium111当作实际Chrome111/Edge111或Safari16.4，也不以系统Chrome152代替最低Chrome111。

## 第二次600秒恢复复测失败：稳定千条仍未关闭

上述格式/构建、完整后端和关联浏览器后，以新冻结候选再次执行相同仓库外600秒脚本，原60秒恢复/250ms观测及全部损失门禁不变。`queue10-long-1000ps-02` **实际exit1、passed:false**：

- 600秒收到601281条、**1002.092/s**，待发 **2114→898**；采集到接收p50/p95/p99为461/759/915ms，含热身积压，不称无积压峰值。
- 3秒503后待发 **3838**；持续输入下恢复60秒仍未读到零，断言末值 **393**、稍后最终快照 **314**。没有进入后续60秒观察；不能把上一轮58.019秒成功移植到本轮，也不以接近清空改判通过。
- 来源overrun、策略/存储丢弃、重复接收ID和坏报文均0，Host正常exit0；整个运行接收683000条。采样Host RSS最大454.477MiB，约681秒CPU均值2.247个核。这些不核销恢复失败，也不足以将差异归因环境。
- 该段已真实结束，之后再次逐文件核对新冻结清单2136文件均无变化。`performance.json`、`run.log`、实际退出码、`summary.json`及`frozen-verification.json`均保留；整条第二轮验证序列因这个性能失败exit1，不能标整体通过。

**当前功能软件回归绿色，但稳定千条/故障恢复仍未达成。** 下一反馈环将600秒稳态先缩为120秒，保留同一1000来源/3秒503/60秒恢复/250ms既有请求，并只记录已有恢复查询的时间、待发和接收数，不增加数据库/管理API请求。外部脚本 `tools/recovery-minimize-120s.mjs` 与差异 `recovery-minimize-120s-harness.patch` 保留；这是缩小故障复现的诊断，不是新的600秒通过证据，也不改正式门禁。

`queue10-recovery-minimize-120s-01` 已结束exit1，**再次真实复现相同恢复失败**：120秒1037.327/s、热身待发4787→284；3秒503后3270，恢复60秒最后1150、最终1090，没有进入后续观察。203次原查询的最小待发1126，不是仅有几条在途记录或错过瞬时零值；来源overrun/策略与存储丢弃/重复/坏报文0、Host exit0、冻结2136文件不变。

恢复时间线 `recovery-summary.json` / `performance.json`：首次观察到接收发生于恢复后3.743秒，此时待发7251；8.451秒时最高7512，之后逐10秒期末为6924、5905、4736、3426、2358、1150。原退避使恢复初始继续累积，而后接收约1100/s仍不足以在本轮门禁内排空；这只是本次观测，不将全部成本或不稳定归于退避/环境，也不为通过而缩短故障策略。

在用户可见说明后，下一步按以下可证伪假设排序剖析：①退避消耗恢复窗口并放大初始债务（预测保留策略时先停发/增长再排空，已有时间线可核对）；②实时身份/控制读取及指纹派生主导热路径（预测采样集中于这些方法）；③HTTP构造、分配/GC或诊断查询主导（预测采样集中于相应边界，而非身份）。尚不据此改产品。

仓库外 `tools/recovery-profile-120s.mjs` 在同一缩小反馈环中，于恢复后6秒对脚本精确识别且持续校验PID/startTicks的**自有Host**启动20秒 `dotnet-trace 10.0.745401`、`dotnet-sampled-thread-time`、64MiB缓冲及Speedscope输出。异步启动，不阻塞Node接收端，不匹配或附加人工服务；只采样方法，不采集正文/秘密。工具首次未设DOTNET_ROOT启动失败，验证其实际路径后只对工具设置DOTNET_ROOT及LatestMajor，不改变产品运行时。

新诊断 `queue10-recovery-profile-120s-01` 已结束：剖析工具exit0，nettrace与Speedscope转换完成；工作负载仍exit1，120秒1042.929/s、稳态末770、503后3705，恢复60秒最后1504/最终1393，未进入后续观察。overrun/策略与存储丢弃/重复/坏报文0、Host0，2136文件冻结不变。带剖析开销的结果不作为正式容量证明，不用它与无探针运行直接比较速度或归因。

**剖析类型限制：** 实际工具 `list-profiles` 明确 `dotnet-sampled-thread-time` 是约100Hz的.NET线程栈**墙钟时间**采样，不能当成内核CPU采样。42个线程、791个帧、789062个事件的原始聚合保存在 `profile-summary.json` / `profile-top-*.txt`；`CPU_TIME`标签子集另在 `profile-cpu-markers-summary.json` / `profile-cpu-top-*.txt`，即使名称带CPU也不等于总进程CPU百分比。最终器、GC轮询和等待帧的高墙钟占比不证明GC、锁或SQLite是CPU根因，`UNMANAGED_CODE_TIME`也不能直接等同空闲或归为某个本地调用。原始文件名中的cpu只是证据名，不能扩大指标含义。

真正内核CPU采样需该工具的 `collect-linux --profile cpu-sampling`。只读核对本机为uid1000、`perf_event_paranoid=4`、无有效capability，工具帮助要求管理员权限；没有修改宿主内核设置、提权或采集其他进程。因此本轮不宣布CPU根因已定位。后续可在同一缩小红反馈环对原发送循环的身份/控制、队头、发送与确认边界做有界聚合计时，并结合恢复时间线测补发余量；临时探针须保存差异、删除并重建/核对原候选，不改退避、监控频率、配额或安全检查来换通过。当前没有新增产品修复，稳定千条和T3仍未完成。


## 有界边界计时与单条 SELECT 复验（2026-10-21 接续）

接续确认发送者独占读连接的批准后，使用同一个120秒反馈环，未更改1000来源、3秒503、60秒恢复门禁、250ms观测或任何丢失门禁。仓库外工具 `tools/recovery-sender-boundaries-120s.mjs` / `tools/owned-sender-boundaries-host.ts` 仅转发原发送循环与HTTP会话每5000次的 `[DEBUG-rd]` 聚合墙钟计时，不记录正文/凭证、不增加管理或数据库观测请求。补丁为 `sender-boundaries-diagnostic.patch`、`http-boundaries-diagnostic.patch`，不进入产品。

- `queue10-sender-boundaries-120s-01`：exit1，120秒1033.523/s，503后3672，首次观察恢复接收3.756秒/待发7672；恢复期间最小待发1370、60秒末1692，没有进入后续观察。恢复的完整60000次窗口平均每1000条910.748ms：确认53.864、管理屏障0.335、循环开始身份127.182、两次控制140.001、队头79.128、发送前身份90.828、SendAsync416.589、其他2.820。实际补发余量约10%，不足以保证60秒内清除退避期间的新增债务；不是CPU百分比或单一根因。
- `queue10-http-boundaries-120s-01`：进一步仅拆开HTTP边界，实际exit0，但59.758秒才首次观察清空，仍接近门限。恢复的60000次HTTP窗口每1000条：凭证元数据67.776ms、准备/身份校验30.684ms、客户端信任核对0.693ms、SendAsync到响应头299.308ms、响应判定1.694ms。HTTP等待包含本机调度、接收器与网络，不能全部归为线缆/对方耗时。后续60秒999.796/s、阶段末19条瞬时待发。两轮overrun/策略与存储丢失/重复/坏报文均0，Host0。**带探针的一失败一临界成功不核销正式长测失败，也不作为候选吞吐通过。** 汇总在两目录的 `*-boundaries-summary.json`。

清理时先恢复备份源码再构建，实际逐文件核对发现增量构建产物不匹配，未假称恢复原2136文件。一次默认并行构建还出现MSB4166子节点退出，保留日志；后改为单节点、禁用构建服务器。显式更新恢复文件的时间戳并重编译后，HTTP适配器恢复原哈希，但Runtime/Host及其引用副本仍有14个DLL/PDB与原冻结02不同；源码核对只有拟新增测试块不同，产品源码没有 `[DEBUG-rd/tk/ob/rc]`，重建后Host加载的Runtime/Adapters程序集也不含这些探针标记。旧冻结02证据继续只对应旧产物，后续使用独立新冻结清单并重新回归，不能把源代码相同当作二进制逐字节相同。核对文件为 `queue10-reader-autotransaction-red-01/clean-baseline-verification*.json`、`clean-source-comparison.json`，构建日志逐次保留。

下一单变量实验只作用于已获批准的预编译队头读连接：单条SELECT自带SQLite隐式短读事务，读取器释放后结束快照，去掉额外的显式BEGIN/COMMIT；管理分页仍沿用原入口。早期未复用预编译查询时的同类试验没有可靠收益，已撤回，本次不能沿用该结论或假定优化有效，须重新按实际反馈环判定。

先增加原队头读取器的本机SQLite语句跟踪回归及解码失败释放回归：`queue10-reader-autotransaction-red-01`实际4项执行、3通过/1失败，失败明确观察到BEGIN/SELECT/COMMIT三条而非单条SELECT；已有未提交/提交可见性、WAL检查点、释放/重建仍通过。失败不是性能达标证据；后续实际回归与无探针负载结果如下，不宣称T3完成。


随后 `queue10-reader-autotransaction-green-01` **229/229集成通过**，0失败/跳过/未执行，4.3912分钟，TRX逐项核对；包含两条新增回归、已有未提交/提交可见性及WAL检查点，以及原发送失败、手动暂停/恢复、两种迁移和HTTP/MQTT进程中断子场景。这不是完整Integration或整个T3通过。修改文件格式验证通过，单节点Release全solution重建0warning/error，源码与Host加载的程序集均已检查无DEBUG探针。新冻结 `queue10-implicit-candidate-frozen-01.json` 共2136个文件。

`queue10-implicit-recovery-120s-01` 是**无探针短反馈环通过**：120秒1033.709/s（含热身积压排出）、稳态末121，3秒503后3087，持续输入下50.006秒首次观察清空；后续60秒998.786/s、阶段末104条瞬时待发。overrun/策略与存储丢失/重复/坏报文均0，Host0，2136个冻结文件全部不变。不能据单次短测归因全部改善或核销两次旧长测的不同结果；重复120秒、两次600秒和50/500原门禁及完整新候选回归尚待结束。外部串行执行器为 `tools/run-implicit-qualification.sh`，任一负载失败即保留结果并停止后续档，不重跑洗绿。


第二次无探针短测 `queue10-implicit-recovery-120s-02` exit0：120秒1036.505/s（含热身积压）、503后3738，56.432秒首次观察清空；后续60秒997.203/s、阶段末240、最终326。随后 `queue10-implicit-long-1000ps-01` **实际exit1/passed:false**：600秒1007.604671/s、待发5547→902，3秒503后3889；恢复60秒超时，断言末2176、最终2051，后续60秒观察未执行。两次来源overrun/策略与存储损失/重复/坏报文均0、Host0，冻结2136文件未变。长测Host RSS最大457.3125MiB、整段CPU均值约2.294核。串行执行器于首次失败停止；第二次600秒、50/500及最新候选完整Unit/Integration均未执行，不能沿用旧候选全量结果。**稳定千条和T3仍未完成。**

维护者随后明确选择“允许验证专用只读通道”：允许发送者独占config.db只读连接及预编译身份/暂停查询作为候选，每次仍读取当前已提交数据库状态，短读事务结束后才等待网络，不缓存身份/版本/暂停；常规配置访问、写事务及秘密解密继续SqlSugar。此项与此前批准的独立SqlSugarClient工厂、queue.db读连接不同；仅为验证授权，实测收益与安全回归通过前不记为正式实现或已验收。


## config.db 发送者专用只读候选（获准验证，尚未正式纳入）

候选实现仅位于整合工作树，不修改原工作树或人工服务：

- `ConfigDatabase.OpenSenderReadConnection` 显式以ReadOnly、非池化模式打开已有配置库，复用统一WAL/FULL/busy_timeout回读及原30秒命令超时，额外启用并回读query_only。缺文件或未初始化WAL时失败并释放，不创建/迁移库。
- 每个运行中的HTTP发送者持有独立身份读取器和暂停读取器（各一条专用只读物理连接，职责分离、不跨发送者共享），复用固定参数化预编译SELECT。身份投影不读recipient或protected_secret；每次仍执行真实数据库查询，不缓存值。秘密解析与解密始终转回原SqlSugar解析器；TLS信任读取和适配器内身份再次核对保持不变。其他协议可使用独占暂停读取器，身份默认沿用既有实现，未扩大为通用原生配置仓储。
- 仅在`RunAsync`实际开始时打开；目标定义与并发格式化仍用原仓储。循环开始、退避、发送前身份及其后的第二次暂停检查都保留。scope退出、查询故障或后续资源打开失败时释放；适配器共享租约仍由原发送单元/监督者最终释放，未增加DI实例或运行时。扩展默认返回null，冻结路由包装不透传scope，保留原路由核对。
- SELECT使用隐式短事务；reader在返回/异常时释放，不跨网络或轮询间隙保留快照。只读打开模式仍能在意外关闭query_only后阻止写入；管理查询、写事务、审计与凭证发布屏障没有变更。

红绿证据：`config-reader-red-01`原元数据路径2通过/2失败，新增专用scope明确为null；实现读取器后`config-reader-sender-red-01`4通过/3失败，原发送者未接入scope，打开/释放计数为0。它们是真实执行失败，不是编译失败，也不作为性能红例。

首次定向绿色序列`config-reader-green-01` **47通过/1失败**：损坏元数据测试夹具的越界kind被原CHECK拒绝，尚未到读取器。只在测试自己的物理连接临时关闭CHECK后注入损坏，再恢复检查；未改产品schema/默认设置。后续`config-reader-green-02`完整solution Release构建0warning/error，相关文件格式整理；Unit **5/5**、Integration **49/49**，均0失败/跳过/未执行，TRX计数与逐项结果核对。覆盖只读/连接独立、物理设置、缺文件/无WAL拒绝、身份空值/新建/换代/删除、暂停/恢复/删除、未提交/回滚/提交可见性、单条无秘密投影及读取间隙WAL TRUNCATE、解码异常后释放/修复、重复释放/重建、秘密逐次委托、适配器内部复核、默认扩展兼容及身份读取后提交暂停仍不得发送。49项还包括原输出失败、退避和暂停管理API，不是全部安全或Integration回归。

有限串行执行器`tools/run-config-reader-validation.sh`已启动：只读全solution格式校验后冻结源码/测试/Release产物，再依次完整Unit、原120秒反馈环、完整Integration、第二次120秒、两次600秒以及50/500原门禁。任一失败立即保存结果并停止后续阶段；不边测边改源代码/重建，不降低观测、改变退避/配额或重跑洗绿。启动不是通过；实际结果待完成后逐项登记。


上述序列已完成只读全solution格式校验、冻结`config-reader-candidate-frozen-01.json`共2139文件及完整Unit **2108/2108**（0失败/跳过/未执行，TRX逐项与退出码核对）。首轮无探针`config-reader-recovery-120s-01` **exit0**：120秒1004.686/s、热身待发610→0；3秒503后3000，持续输入下 **5.039秒首次观察清空**，后续60秒999.819/s、阶段末0、最终19条瞬时待发。来源overrun/策略与存储损失/重复/坏报文0、Host0，2139文件冻结不变。整段Host RSS最大464.871MiB、CPU平均约1.801核。前一隐式队头候选两次短测清空50.006/56.432秒，当前首轮改善明显，但热身/运行不同，不能用这一轮作严格配对因果或长期容量结论。完整Integration随后已启动，重复短测/两次600秒/50与500仍待执行；不提前将scope纳入正式约定或宣布稳定千条/T3完成。


`config-reader-validation-01`完整Integration已真实结束 **2850/2850**、13.6875分钟，0失败/跳过/未执行；原5分钟hang门禁保留，TRX计数、2850条逐项结果、exit0及2139文件冻结全部核对。包括真实HTTP/MQTT/Modbus软件对端、凭证/TLS/暂停/迁移及配置/队列进程中断回归；不是实物或历史MQTT偶发根因结论。

第二轮无探针`config-reader-recovery-120s-02` **exit0**：120秒1003.306/s、热身471→0；3秒503后3019，持续输入下 **5.893秒首次观察清空**，后续60秒999.842/s、阶段末0/最终19。损失/重复/坏报文/overrun0、Host0、2139文件冻结不变；RSS最大408.539MiB、整段CPU均值约1.997核。两轮反馈环均有明显恢复余量，但不能替代600秒独立长测。随后第一轮600秒已启动，第二轮600秒及50/500尚未执行。


第一轮无探针600秒`config-reader-long-1000ps-01` **exit0/passed:true**：平均1000.358/s、热身待发322→19；3秒503后3999，持续1000/s输入下 **15.299秒首次清空**；后续60秒999.794/s、阶段末52/最终0。overrun/策略与存储损失/重复/坏报文均0、Host0，2139文件冻结不变。RSS最大444.477MiB、整段约697秒CPU均值1.826核；不与不同持续时间样本直接作CPU降幅声明。第二轮600秒随后已启动，尚未记通过；50/500未执行，T3仍不关闭。


第二轮独立600秒`config-reader-long-1000ps-02`也已真实结束 **exit0/passed:true**：1000.649/s、热身待发818→394；3秒503后3379，持续输入下 **16.067秒首次清空**；后续60秒999.777/s、阶段末/最终0。overrun/策略与存储损失/重复/坏报文均0、Host0，2139文件冻结不变。RSS最大445.125MiB、整段CPU均值约1.851核。两次600秒在原恢复/250ms观测及损失门禁下重复通过，未改退避、配额或实时安全检查；旧失败原样保留，不能外推24小时、其他负载/平台/实物或生产SLA。50/500已进入后续序列，尚待实际结束；同一新候选前端/浏览器回归还未启动。


有限后端/性能序列已实际结束exit0，没有失败后重试替换结果。50/500原门禁也通过：`config-reader-standard-50ps-01`平均49.994/s、稳态待发31→31，15秒503后750、15.554秒首次清空，后续15秒49.968/s、阶段末/最终0；`config-reader-standard-500ps-01`平均500.880/s、稳态88→0，3秒503后1500、1.901秒首次清空，后续15秒499.690/s、阶段末/最终0。两轮overrun/策略与存储损失/重复/坏报文0、Host0，2139文件冻结逐次不变。50档恢复较长包含原退避，未为了改短数字改变策略；平均接收不足50的微小差异包含测量边界，不宣称更高峰值。

同一构建的6轮（两120秒、两600秒、50/500）汇总保存在`config-reader-performance-summary.json`，每轮原始performance.json、命令、环境、实际退出码、前后冻结证据均保留。运行数据根仍为独立ext4目录，不改原人工服务数据。**本软件场景已有重复千条吞吐及原故障恢复门禁通过证据，但不是长期生产/实物/SLA结论。**

随后独立有限前端执行器`tools/run-config-reader-web-validation.sh`已启动：只读typecheck/lint/format/api:check、完整Web单元、完整当前Chromium及两组关联浏览器，使用同一2139文件冻结构建，不重建、不安装依赖、不与后端网络/负载并行。实际结果尚未完成；不将此前836/188移植为新候选通过，T3剩余外部验收仍保持未完成。


在用户已明确授权“实测有收益且安全回归通过后才纳入”的条件下，现已完成本场景两次600秒/原恢复门禁及完整2850项集成回归，故同步总设计§2.3/§8.5/§22与后端实施规范§4的**受限元数据只读例外**，纳入本轮待验收实现；没有把验证授权写成T3/生产验收，不把本机匿名HTTP性能外推为HTTPS/认证/其他协议、多目标或全天容量。普通SqlSugar访问和写入、秘密/TLS处理、事务/实例边界不变。未重建或修改已冻结产品/测试源码；新候选完整Web **836/836**已通过，typecheck/lint/format/api:check通过，当前188项浏览器仍在执行中，尚未记全通过。


### 新候选浏览器全量实际失败，不能记整体绿色

`config-reader-web-validation-01`已结束exit4：typecheck/lint/format/api:check、Web **836/836**通过；完整Chromium156 **187通过/1失败/0跳过/0重试、38.7分钟**。唯一失败`output-modules.spec.ts:581`的MQTT扩展输出用例：真实预览/确认发送/正式应用/Viewer权限核对均已完成，停止自有模块Host时`host.stop()`返回null而非0，因此后续正常重启子场景未执行；不把它归为环境或已知MQTT消息计数问题。旧主分支曾有Modbus模块Host退出null（§M-2.311），但尚不能证明同一根因。完整JSON/log与失败trace复制到`browser-failure-artifacts`，没有被后续定向运行覆盖；失败后2139文件冻结仍一致。

有限前端执行器在首次完整浏览器失败停止，因此两组关联浏览器未执行，不能移植旧9/9为新结果。后端/6轮吞吐证据仍独立有效，但**新候选完整验收序列尚未全绿，T3不关闭**。原helper暴露exitSignal但当前用例断言未记录，原失败无法恢复其真实信号。

下一有界定向复现使用同一冻结产品/测试程序集，仓库外复制该用例和辅助启动器，仅记录自有Host PID、停止调用次数、退出码/信号和有界控制台尾部；不改产品/原测试源码或接受null为成功。排序假设为：①实际信号终止（预测退出signal非空）；②helper/worker重复停止（预测同一PID多次发送终止）；③关闭资源期间异常（预测失败信号/栈或安全日志）；④外部强杀（需同PID内核记录）。尚无结论，不以重复通过洗绿。


### 有界退出诊断未复现，保持原全量失败

仓库外诊断初次使用锚定grep导致**0用例/退出1**，记录`owned-exit-diagnostic-01/run.log`，不记为通过。去掉错误锚定并先`--list`核对1个真实用例，随后对复制的MQTT输出原场景执行5次（retries仍0），**5/5、约2.0分钟**；10个有真实业务/重启的自有模块Host全部exit0、signal=null，截至close均只有一次停止请求，worker退出补发信号0。然后独立模块/产品Host各15次、共30次快速启停也全部exit0；不运行原业务链的快速探针不是MQTT/队列回归用例。40个进程记录保存在`processes/`，汇总`summary.json`；无进程命中原人工服务，无产品/原测试源码修改。这些带观测、有限重复未复现，不证明原故障已修复、排除新通道或等同原188项完整通过。

因此当前判定仍是：完整Unit2108、Integration2850、Web836及6轮本机单HTTP负载通过；完整浏览器**187/188失败**，后续最低/系统关联两组**未执行**。原失败的真实信号/Host日志缺失，现有证据不足以定位根因，不猜测是环境、MQTT、SQLite或helper，也不改异常/退出断言。未形成可稳定变红的最小退出复现，故没有提交声称修复该问题的产品补丁。

前面的工程约定同步曾在完整浏览器结束前按后端/吞吐绿色写为“纳入待验收实现”；**现在据实际全量失败纠正为“维护者授权的候选验证，暂不正式纳入”**，已同步总设计/后端规范。保持同一2139文件候选不变，不回退或归因未证实的热路径修改，不提交/推送/部署，不宣布T3完成。已复制的原完整失败报告和全部绿色证据均保留，不能用5次或30次重跑洗绿。下一次原场景失败须使用仓库外诊断helper保留signal/停止记录，或在证据充分后补原辅助方法的失败诊断；此处停止无证据的修复试探。


随后再将退出诊断压缩到**同一已配置真实MQTT业务的连续50次停止/重启**：仓库外`owned-exit-mqtt-restart-01`保留原前半段两次精确发送/权限核对，循环仅用新收到的业务报文作为就绪信号（不把在途允许重复误判为退出故障），每次仍严格要求exit0，不改变产品/原测试代码、原完整失败或吞吐门禁。单个有界诊断用例exit0、约3.2分钟；原初始/恢复加50次循环，共52个自有模块Host全部exit0/signal=null，亦未复现。它不是增加52项产品测试或原188项完整通过；根因仍未知，结果不核销原全量失败。所有诊断执行结束，没有运行中的测试/工具会话。

### 异常退出诊断补齐及同序全量观测（2026-10-21 接续，进行中）

本轮修正的仅是测试辅助方法丢失退出诊断：`stop()`仍等close且严格要求exit0，非零/信号退出包含PID/code/signal及原16KiB控制台尾部，15秒停止门禁不变；四个Node边界模拟单测在原helper为1通过/3失败，修正后4/4。它们不是实际Host停止的红绿复现。系统内核记录在原失败时间窗未见dotnet崩溃，但这不是退出信号证据，不能推导“只是SIGTERM”。

新证据目录`owned-exit-followup-01`保留red/green日志和退出码；2140文件新冻结相对原冻结仅有helper修改和新增单测，产品及构建产物未变（`frozen-delta.json`）。完整Web840、静态检查通过；同序完整188项浏览器观测重放实际186通过/2失败/0跳过/0重试、38.7分钟、exit4。原MQTT停止异常本轮未复现，不是根因修复。两项新失败为Modbus非预期请求及正文大小跨记录断言，详见实施状态§2.325—§2.326；所有原始失败保留，不因此正式纳入只读候选或宣布T3完成。

### 两项新失败的独立诊断与正文断言修正（2026-10-22）

Modbus场景复制后加入有界原始帧观测，20次均通过、40次合法读取，约4.8分钟，未复现；`modbus-request-diagnostic-01`保留帧、报告和源哈希，不能改写原完整失败。本仓库夹具现在仅补错误原始12字节和解析头信息，不放宽校验、不忽略错误。

正文原完整trace明确是第23条列表大小（1048978 B）与第25条正文（1048980 B）被错误比较，各自API元数据正确。仓库外提高复现率的同场景保留完整1MiB上限及隐私/重启断言，原路径两次均红，绑定同一记录后三次均绿；证据`message-row-race-01`。项目用例修正为同DOM快照捕获身份/大小，明确让实际列表头前进，再点击原记录并严格核对标题中的编号/版本，原64KiB和精确大小断言不变。不是产品截断逻辑或吞吐修复。

新2140文件冻结相对原2139文件仅有3个浏览器用例/辅助方法修改及1个新增辅助单测文件，产品/Release/web/dist未变。`owned-exit-followup-02`的项目定向12/12、完整Web840/840及静态检查已通过；188项当前Chromium完整回归正在运行，全过才执行两组关联验证，未完成部分不计通过。Host停止异常与Modbus异常仍未定位，不纳入T3整体验收或生产性能承诺。

### 当前完整序列已结束，历史风险不由绿色核销（2026-10-22）

`owned-exit-followup-02`有限序列exit0：typecheck/lint/format/api:check及Web840通过，当前Chromium156完整188/188（2339.456秒）；归档Chromium111与实际Chrome152关联各9/9（190.202/182.011秒），全部0跳过/失败/flaky、重试0，报告验证器及最后2140文件核对通过。三组浏览器按原顺序执行，没有失败后继续/重试。产品及Release/web/dist仍是原性能候选；源差异仅为已记录的浏览器测试诊断和正文同记录绑定。

这不是历史停止或Modbus异常的根因修复，所有原失败与有限未复现诊断继续保留；当前软件回归通过与尚未定位风险分开登记，配置库只读仍为候选验证，不推送或部署。实际Chrome111/Edge111/Safari、真实Excel来源、实际CI和维护者验收仍不能由这些结果替代。

随后只增强两份独立实际浏览器冒烟脚本的退出码/信号诊断与Host严格exit0门禁，新增5项辅助单测；Web845与静态检查通过。新2142文件冻结相对上轮只含2脚本修改、共用退出观察辅助方法及5项测试，产品/Release/web/dist未变。`native-browser-followup-01`正在串行执行实际Firefox128 HTTP/HTTPS和Chrome155默认HTTP，当前只记进行中；工具原始归档已校验并在新证据目录解包，不变更系统浏览器或人工服务。

### 实际Firefox128双协议与Chrome155默认HTTP已复核（2026-10-22）

`native-browser-followup-01`有限序列exit0：当前产品实际Firefox128 HTTP/HTTPS各6/6，59670/61584 ms；实际Chrome155默认HTTP3/3，6652 ms。三个产品Host均exit0/signal=null；自有Chrome同样exit0，驱动由执行器明确SIGTERM停止并记录signal15。FirefoxHTTPS先未信任拒绝，再核对自有Host指纹、在独立profile登记信任并回读，始终acceptInsecureCerts=false；Chrome不改变HTTPS特性、证书检查或sandbox要求。各组报告、截图、日志、归档校验及2142文件冻结核对均保留，全部自有PID已退出，缓存helper恢复。完整Web845及静态检查通过；相对上一完整回归只变更独立冒烟脚本/辅助方法与测试，产品/Release/web/dist不变。详见实施状态§2.328。

当前软件回归与重复千条场景已有通过证据；不把当前绿色当作历史停止/Modbus/MQTT偶发的根因修复，不把本机匿名单HTTP外推成生产SLA。保留实际Chrome111/Edge111/Safari16.4、真实Excel、实际CI及维护者退出门槛；只读通道仍为候选验证。接续仅整理本地提交，不推送或改动人工服务。
