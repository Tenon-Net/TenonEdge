# 输出流水线性能专项

- 日期：2026-10-21；基线：`0cee4bb`。
- 范围：QA-PERF-01，以及 T1-03/05/08、T3-08 对应的存储、故障与管理屏障回归。不进入 T4/T5/G1。
- 隔离工作树：`/home/shiny/.cache/tenonedge-inline-references/worktree`；不合回原工作区，不推送、发布或重启人工试用服务。
- 本机原始证据根：`/home/shiny/.cache/tenonedge-output-pipeline-perf/20261021/`。
- **本轮已修复原 50 条/秒积压缺陷并完成后端软件回归；最终 500 档通过，1000 档未通过。高性能目标尚未全部关闭，不构成 T3 整体验收或生产 SLA。**

## 1. 环境与负载

Linux x86_64，共享 Xeon Silver 4210R 开发机，20 个逻辑 CPU、约 61.4 GiB RAM，swap 接近满；不保证其他会话空闲。SDK 10.0.401、Node 22.23.2，原锁文件与依赖不变。数据目录使用 ext4，不以 tmpfs 替代磁盘结果。

使用原产品 Host、模拟来源、原 queue.db 和 loopback 真实 HTTP 接收端。每条含 energy/temperature 两个字段，保留大整数数字原文与 Idempotency-Key；不是实物采集容量，不把“报文/秒”写成“点位/秒”。普通 HTTP 仍只有一个网络请求在途，未把正文改为批量数组。前端使用上一轮冻结的外部 dist，没有修改或部署页面。

每档先运行网页/API 走查，再测稳态 60 秒、接收端 503、持续输入下最多等待 60 秒清空，成功后观察 15 秒。5/50/100 档中断 15 秒；500/1000 档拟用 3 秒，以免把默认单目标 10,000 条配额本身当作吞吐能力。高档不能与低档的故障持续时间混为相同场景。

脚本新增稳态接收至少达到名义输入 95% 的门禁、零策略/存储丢弃检查、来源周期跳过（overrun）检查和采集时间到接收端的 p95/p99。该门禁用于定位此次性能缺陷，不是已批准的生产 SLA。进程重启测试允许约定窗口的重复，普通性能场景则要求重复 ID 与坏报文均为零。源码和二进制冻结记录与每次脚本版本边界在最终结果中分别说明。

## 2. 反馈环与定位

### 2.1 逐条等待删除提交

新跑原 50 条/秒场景：稳态仅 15.5435 条/秒，稳态末待发 2491；503 后 3240，恢复断言失败，Host 正常退出。`baseline-50ps/performance.json` / `baseline-50ps.log` 保留 exit 1。

最小回归 `ConfirmedRecordsAdvanceInFifoOrderBeforeDeletionCommits` 使用真实 WAL 数据库：第一条返回 ACK 前取得测试侧写锁，要求后两条仍按 FIFO 发送，且三条在解除写锁前仍留在持久队列。原实现在两秒期限失败，见 `red-runtime/ack-pipeline.trx`；修复后通过。最初测试文件曾因 CA1861 编译失败，另存 `red.log`，不把编译失败冒充这个运行时红色证据。

### 2.2 积压时逐条配额扫描

仅解耦确认后的阶段性结果：50 条/秒通过，100 条/秒通过；500 档仅 196.727 条/秒，低于门禁，未运行中断与 1000 档。证据为 `pipeline-initial-50ps`、`final-{5,50,100,500}ps`；这些目录保留为中间版本，不能覆盖后续验证或改名冒充最终版本。

独立剖析仅附加到脚本新建 Node 的精确 Host 子进程，不匹配人工服务。使用仓库外安装的 Microsoft `dotnet-trace` 10.0.745401，15 秒 `dotnet-sampled-thread-time`，64 MiB 诊断缓冲；不采集业务正文或凭证。`500ps-cpu.nettrace` / `.speedscope.json` 和 `500ps-top-*.txt` 留存。采样含空闲/等待，**不是整机 CPU 百分比**：写者 Commit 的包含采样为 1.78%，Admit 为 1.70%，说明该写者的采样主要在准入路径；源码对应每条两次 COUNT/SUM 扫描。

仅增加事务内配额快照、仍使用 CopyNew 的单变量对照 `quota-only-500ps` 为 **176.101 条/秒，失败**，稳态末待发 9999，配额拒绝 3616；未进入中断阶段，存储故障计数为零但策略丢弃非零，不能算可靠接管通过。CPU 平均约 300.25% 单核、RSS 峰 432.18 MiB。该改动单独不足以解决 500 档，保留失败，不扩大配额掩盖。

同时观察到 `SqlSugarScope.CopyNew → GetContext → StackTrace` 的高频调用。维护者已确认允许等价的独立客户端工厂：Scope 与独立 SqlSugarClient 均从统一配置创建，每份独立配置和命令超时均显式设置，不再经 CopyNew 的上下文查找。总设计 §2.3 / §22、后端规范 §4 同步澄清；连接设置、释放及事务隔离由真实 SQLite 回归覆盖。

### 2.3 客户端工厂与混合提交

独立工厂后的同场景 `factory-500ps` 为 **442.263 条/秒，失败**，稳态末待发 6896，零策略/存储丢弃；p95 延迟 13.590 秒，仍未进入中断阶段。另一次受剖析负担影响的 `profile-factory-500ps` 也失败，不作为容量。其 `factory-500ps-cpu.nettrace` / `.speedscope.json` / `factory-500ps-top-500.txt` 已不见 CopyNew；写者 Commit 包含采样约 1.00%，其中准入约 0.75%（目标/全局扫描分别 0.39%/0.36%）。证据支持剩余写者热路径：交错的逐条删除令事务内占用快照不断失效，下一次插入重复扫描积压。改为仅对实际删除行做同事务增量扣减，其余管理变更仍保守失效，不缓存跨事务状态。该因果以相同负载继续复验，不把全部 CPU 或网络开销都归于扫描。

### 2.4 剩余查询构造开销的对照

事务内删除增量更新后的冻结版本 `verified-{5,50,100,500}ps` 中，5/50/100 档通过，但 500 档仍只有 **443.425 条/秒**，待发 7379，零策略/存储丢弃；1000 档未执行。1702 个源码、测试、依赖、二进制及冻结 Web 产物由 `verified-frozen.json` / `verified-frozen-verification.json` 核对全程未变。这些是继续定位的中间版本，不能当最终结果。该对照未支持“减少准入扫描即可解决 500 档”的假设；不能把容量瓶颈全部归咎于 SQLite 或把此改动单独宣称为吞吐提升。

既有采样中 HTTP 身份描述与暂停状态读取仍频繁经过 ORM 查询构造和实体映射。后续单变量组保持每次实时读取，仅将这两处固定主键查询改为 SqlSugar ADO 的参数化列投影，身份描述不读取 protected_secret；不增加状态或凭证缓存，不减少发送前的身份/暂停检查。新回归覆盖空记录、最大安全整数 ID、可空身份字段、账户/版本/证书变更、暂停/恢复、删除以及连接选择隔离，并继续执行既有凭证与审计屏障测试。

## 3. 实现及不变的可靠性边界

- ACK 编号与提交回执跨发送者重建保留；正常发送不逐条等待删除落盘。删除仍立即交给同一个唯一写者组提交，SQLite WAL / synchronous=FULL 不变。
- 每目标最多 128 个确认待持久删除编号/回执，不预取批量正文，不提前释放持久队列配额；满窗等待，已知删除失败先恢复提交，不重发已确认记录。
- 读取只越过已确认的 FIFO 前缀；成功毒消息探测可能在队头之后，不能用最大 ACK 编号跳过未确认队头。
- 失败、隔离、目标指纹变化，以及清空/迁移的原发送者屏障前排空确认回执。没有第二个写者、发送者或运行时。
- 配额统计只在写者的当前 SQLite 事务内复用：首次查询目标和全局占用，插入成功后增记；普通删除后按实际行增量更新，其他状态变更和保存点回滚使快照失效。事务结束即丢弃，不新增持久容量表或迁移版本，不跨事务缓存占用。高频删除通过 `DELETE … RETURNING connection_id, byte_size, state` 取得实际删除的行，按原状态增量扣减事务内快照；缺失/重复编号不扣减，失败区不占全局配额。只有其他状态变更或保存点回滚才使快照失效；删除的外部成功回执仍以持久 COMMIT 为界。
- 强杀窗口仍可能重放已确认但未持久删除的原报文；稳定编号、冻结正文和顺序保留。不宣称 exactly-once 或硬件断电保证。新增真实进程用例固定五条持久记录，软件 MQTT 对端发送五个 PUBACK 后强杀；后续 PUBLISH 证明前四个 ACK 已消费，最后一个 ACK 可能仍在途。重启后按原字节 FIFO 重放这五条，再发送新采样，不能声称强杀前五个确认均已确定被产品消费。

## 4. 回归与最终性能结果

配额快照后的存储/策略回归 `quota-regression/quota.trx` 为 **49/49**，0 失败/跳过，8 分 54 秒，包含 ext4 容量校准，不把等待期间记作通过。客户端工厂及格式修正后的 `factory-focused/focused.trx` 为 **35/35**，0 失败/跳过，39 秒：覆盖五项确认流水线、配额回滚/同事务混合变更、迟到 ACK 所有权、真实强杀与正常退出，以及 8 项配置库连接/事务/释放用例（其中含两分支 WAL 拒绝）。

删除增量更新后的 `incremental-focused/focused.trx` 为 **38/38**，0 失败/跳过，38 秒，新增 pending/quarantined/failed 三分支，验证重复/缺失删除不会多释放配额。固定元数据投影后的 `projection-focused/focused.trx` 为 **80/80**，0 失败/跳过，28 秒，覆盖凭证写入与作废、客户端证书身份、发布屏障、权限/CSRF/审计、暂停与重启以及新增的实时元数据读取。

最终版本的性能与全量回归见 §4.2；各轮不相加，也不覆盖失败。早期定向回归 `green-expanded/ack-pipeline.trx` 为 38/38，包含五项确认流水线、既有失败/毒消息、迟到 ACK 所有权及真实子进程退出；它不是配额优化后的完整回归。

### 4.0 全量回归发现的统一事件回归

`full-unit/unit.trx` 为 **2081/2081**，但随后首轮 `full-integration.log` 明确失败：迁移意图 SQL 投影断言收不到任何 SQL，配置真实持久 COMMIT 探针也无法到达原中断门。原因是初版工厂每次另建 AopEvents，失去了原根配置统一事件的观察行为；不是删除断言、增加等待或把中断场景跳过即可解决。确认后仅向本轮自有 dotnet test 控制进程发送 SIGINT，保存 `full-integration-stop.json`；该轮至少有上述两项失败，取消后没有完整 TRX，不能记作完整回归、不能估算通过数。

修复为每个 ConfigDatabase 统一持有连接检查/观测事件，由 Scope 和各独立连接配置引用；配置对象、客户端、超时、连接和事务仍独立，事件不持有请求上下文或活跃事务。新增跨 Scope/独立客户端事件覆盖及跨配置库实例隔离回归，原投影与真实持久提交断言保持不变。修正后旧投影用例及连接/元数据定向 `shared-events-focused/focused.trx` **11/11**，4 秒；此批尚不含随后新增的事件测试。`shared-events-regression/regression.trx` **12/12**，0 失败/跳过，12 秒，包含新增事件隔离、原 SQL 投影和真实持久 COMMIT 的正常释放/强杀两分支。没有改中断门、期限或旧断言。

因此下面 `projection-*` 性能仍属于事件修复前产品版本；最终版本须重新执行后端全量回归与关键性能档，不用旧二进制的成绩代替。

### 4.0.1 ACK 与持久删除的旧测试假设

统一事件修正后的完整 `final-integration/integration.trx` 为 **2821 通过 / 1 失败 / 总 2822 / 0 跳过**，14 分 36 秒；Unit **2081/2081**。失败是 `KillAfterConfigurationCommitKeepsCommittedVersionAndIgnoresLaterDraft(beforeInputs: True)`：已按原字节重放 r1/42 并确认，收到正确 r2/84 后立刻断言数据库只剩 r2，但旧 ACK 的删除仍等组提交。独立两分支 `ack-boundary-red/red.trx` 以 **1 通过 / 1 失败** 稳定重现原窗口，不能把这一轮写成全绿。

流水线正是允许“后续发送先于之前 ACK 的持久删除”；因此测试改为使用已有 15 秒存储等待工具，观察真实删除完成，再执行原版本断言，并额外要求所有已重放/确认的冻结编号均消失。只固定 r2 未确认报文的另一分支也先观察旧 r1 删除。未改产品行为、总会话/原等待工具期限、重放原字节/FIFO、有效/草稿指针或原 r2 断言，不用固定 sleep 或增大超时洗绿。新写锁测试仍专门证明“已发/已确认但尚未删除”的真实窗口。`ack-boundary-green/green.trx` 的两分支、五项 ACK 流水线与真实退出用例 **12/12**，38 秒。随后补齐另一分支的旧 r1 删除观察，`ack-boundary-final/final.trx` 两分支 **2/2**，13 秒。再一轮完整回归及最终关键性能将分别留存，不覆盖失败证据。

### 4.1 冻结产品版本的首轮分档结果

`projection-{5,50,100,500,1000}ps` 使用相同产品源码、二进制与外部 Web。1703 个文件在全部性能分档期间未变，见 `projection-frozen.json` / `projection-performance-frozen-verification.json`。500、1000 档串行先执行，随后 5、50、100；没有与本轮重型回归并行。原始值另存 `projection-summary.json`。

| 输入（条/秒） | 稳态收到（条/秒） | 稳态末待发 | 稳态延迟 p95（ms） | 中断（s） | 持续输入下清空（s） | 来源 overrun | 结果 |
|---|---|---|---|---|---|---|---|
| 5 | 4.999 | 0 | 152 | 15 | 1.040 | 0 | 通过 |
| 50 | 49.988 | 0 | 184 | 15 | 16.670 | 0 | 通过 |
| 100 | 99.981 | 0 | 234 | 15 | 17.704 | 0 | 通过 |
| 500 | 501.624 | 185 | 282 | 3 | 10.502 | 0 | 通过，恢复后观察末待发 0 |
| 1000 | 1003.323 | 1213 | 7636 | 3 | 超过 60 秒仍未清空 | 17499 | **失败** |

各档普通接收重复 ID / 坏报文均 0，队列策略丢弃与存储故障/数据拒绝计数均 0，Host 正常退出。大于名义速率包含此前小幅积压的排空，不是比输入更多生成消息。1000 档轮询超时最后观察到待发 3022；最终快照为 3041，时间点不同，不强行改成同一个值。其来源累计 17499 次跳过（含尚未持久的增量），不是零损失通过，既无恢复余量也有来源调度/背压问题。500 档来源完成 51500 次、来源 overrun 为 0、最终队列为 0。上述来源计数复核在 `projection-overrun-audit.json`；不能只凭接收近 1000 就声称支持千条档。

CPU 平均按单核计：5/50/100/500/1000 档约 18.38% / 25.94% / 35.36% / 140.04% / 231.74%；RSS 峰约 249.79 / 248.00 / 274.77 / 367.69 / 546.73 MiB，均为各档整个短测期间，不等同于稳态 CPU 或长期泄漏验证。

补查发现旧脚本虽已保存来源计数，但尚未将 overrun 计入自动 passed。四个通过档经原始计数复核均为 0，不存在用来源跳过洗绿；1000 档本来就失败。随后只补强 QA 脚本，把持久与未持久来源 overrun 合并为明确字段，并要求为 0；产品源码和二进制不变。随后完成后端全量回归，再执行关键 50/500/1000 档，最终结果单列于 §4.2，不改写以上原报告。

### 4.2 最终版本的软件回归与关键档复测

统一事件修正及两处显式持久删除观察之后，重新执行完整测试，不把定向重跑补算成全量通过：

- locked restore 通过，依赖与锁文件不变；最终 `closure-format.log` / `closure-build.log` 为格式校验通过、Release 构建 **0 warning / 0 error**。
- `post-boundary-unit/unit.trx` **2081/2081**，4 秒；`post-boundary-integration/integration.trx` **2822/2822**，13 分 31 秒。失败、跳过、未执行、取消、超时均 0；`post-boundary-trx-verification.json` 对计数、非零结果和每条 outcome 进行核对。
- 完整 Integration 包括原配置中断/持久提交/回滚、凭证/权限/审计、所有权与清空/迁移/恢复、失败/毒消息/存储保护，以及新增写锁/删除流水线和真实强杀子场景。是软件与真实子进程验证，不是实物或掉电验证。
- `post-boundary-frozen.json` / `post-boundary-frozen-verification.json` 核对最终 **1706** 个源码/测试/依赖、Host 与测试二进制及外部 Web 文件，在完整回归和关键性能期间均未变。不是 1706 个发布文件；未重新构建或部署网页。

下列三档使用已补强来源 overrun 门禁的同一脚本，依次 50、500、1000，全部在本轮完整后端回归结束后串行执行。证据为 `post-boundary-{50,500,1000}ps/performance.json` / 对应日志；汇总为 `post-boundary-summary.json`。

| 输入（条/秒） | 稳态收到（条/秒） | 稳态末待发 | 稳态 p95 / p99（ms） | 中断（s） | 恢复清空（s） | 来源 overrun | 结果 |
|---|---|---|---|---|---|---|---|
| 50 | 49.994 | 0 | 196 / 243 | 15 | 1.369 | 0 | 通过 |
| 500 | 508.336 | 126 | 411 / 1215 | 3 | 17.731 | 0 | 通过，恢复后观察阶段末待发 0 |
| 1000 | 812.790 | 4473 | 5825 / 5900 | **未执行** | **未执行** | 13443 | **失败，稳态未达 ≥950 门槛** |

50/500 档均无重复接收 ID、坏报文、来源 overrun、队列策略丢弃或存储故障/数据拒绝；Host 正常退出。500 档恢复后观察阶段末为 0，随后持续输入的最终快照为 19，不能把不同时间点写成始终空队列。50 档最终快照为 0。500 的稳态速率包含此前积压排空，不把 508.336 写成精确生成速率或通用容量上限。

最终 1000 档未通过稳态门禁，**没有进入 503/恢复阶段**；不能把前一版“稳态近千条、恢复失败”的结果移植到此轮。最终快照待发 4557，来源 overrun 13443；虽仍是重复/坏报文和队列丢弃/存储故障计数 0，仍不是零损失容量通过。高档结果对共享负载敏感，尚未证明稳定千条能力，保留全部红色证据；不把本轮低档通过核销整个 QA-PERF-01。

最终三档 Host 整段 CPU 均值约 **30.25% / 156.80% / 271.74% 单核**，RSS 峰约 **240.09 / 372.87 / 454.39 MiB**，不含 Node 接收端与浏览器 CPU，不是长期资源上限。中断清空时间含重试退避，单次观测不构成恢复 SLA。5/100 档仅保留 §4.1 的中间版本证据，不冒称在最终事件修正版本重新测过。

`preview-protection-after.json` 核对人工 Host PID1060554/startTicks302364428、软件对端 PID1060538/startTicks302364412 与开工记录一致；只做匿名 healthz/readyz GET，均 200，没有重启、部署、登录人工账号或修改人工配置。原工作区仅只读核对，存在其他进行中改动，未覆盖或合并。

本轮关闭原 50 档可复现的串行持久删除瓶颈和客户端上下文/固定元数据构造热点；**千条档及生产高性能目标仍未完成**。后续需以失败档继续剖析来源调度、持久接管和补发余量，明确目标硬件/报文/网络与验收指标；普通 HTTP 仍单请求在途，真实对端时延容量未由 loopback 成绩证明。不取消 FULL、不扩大配额、不用来源跳过或放宽断言洗绿。

## 5. 复跑命令

```sh
# 仓库根目录，原始日志与 TRX 使用每轮独立目录，不覆盖失败。
dotnet restore --locked-mode
dotnet format --no-restore --verify-no-changes
dotnet build -c Release --no-restore
E=/home/shiny/.cache/tenonedge-output-pipeline-perf/20261021
dotnet test tests/UnitTests/TenonEdge.UnitTests.csproj -c Release --no-restore --no-build \
  --results-directory "$E/post-boundary-unit" --logger 'trx;LogFileName=unit.trx'
dotnet test tests/IntegrationTests/TenonEdge.IntegrationTests.csproj -c Release --no-restore --no-build \
  --blame-hang --blame-hang-timeout 5m --results-directory "$E/post-boundary-integration" \
  --logger 'trx;LogFileName=integration.trx'

# Web 工具直接使用既有锁定安装，未安装新依赖。
(
  cd web
  node_modules/.bin/oxlint scripts/product-performance.mjs
  node_modules/.bin/prettier --check scripts/product-performance.mjs
  node --check scripts/product-performance.mjs
)
export PATH=/home/shiny/.npm/_npx/b0271877f536be0e/node_modules/.bin:$PATH
export TENONEDGE_TEST_WEBROOT=/home/shiny/.cache/tenonedge-product-qa/dist-final
export TENONEDGE_QA_DATA_ROOT=/home/shiny/.cache/tenonedge-product-qa/workloads
# 实际顺序 50、500、1000；此处 NEW 须替换为新目录，不能覆盖 post-boundary 证据。
TENONEDGE_QA_INPUTS=50 TENONEDGE_QA_OUTAGE_SECONDS=15 \
  TENONEDGE_QA_RESULTS="$E/NEW-50ps" node web/scripts/product-performance.mjs
TENONEDGE_QA_INPUTS=500 TENONEDGE_QA_OUTAGE_SECONDS=3 \
  TENONEDGE_QA_RESULTS="$E/NEW-500ps" node web/scripts/product-performance.mjs
TENONEDGE_QA_INPUTS=1000 TENONEDGE_QA_OUTAGE_SECONDS=3 \
  TENONEDGE_QA_RESULTS="$E/NEW-1000ps" node web/scripts/product-performance.mjs
# 1000 本轮保持 exit 1，不忽略错误，也不把未执行的中断阶段写成通过。
```

尚未新增实物、最低工控硬件、慢盘、掉电、24/72 小时 soak、真实平台或远端 CI 的通过证据。MQTT 网络流水线窗口与独立容量、HTTP 对端批量契约未在本轮实现或验收。上一轮扇出超时与令牌 E2E 不稳定的根因也不能仅凭本次重跑核销。
