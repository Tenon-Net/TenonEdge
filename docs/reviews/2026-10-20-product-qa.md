# 产品视角首轮测试：功能、端到端、压力与易用性

- 日期：2026-10-20。
- 基线：`cd12c889e0041ea2fcc92e99c6e78cc86d860d0e`，隔离分支 `t3-inline-reference-verification`。
- 工作树：`/home/shiny/.cache/tenonedge-inline-references/worktree`；未修改、合回原工作区，未推送或发布。
- 原始证据根（本机，不入库）：`/home/shiny/.cache/tenonedge-product-qa/evidence/`，下文路径均相对于此目录。
- 对应范围：T3-01/02/03/04/05/06/07/08/09/11 的既有软件子场景，以及相关 T0—T2 回归；不实施 T4/T5/G1，不核销整个阶段或跨阶段 AT/FE-AT。

## 1. 结论与交付边界

**不能将本轮写成“全部测试通过”或“产品已经验收”。** 功能链路已有广泛自动化覆盖，但全量后端与浏览器回归各有一次超时，单项连续三次通过尚不等于找到根因；单 HTTP 目标的高负载测试也出现持续积压。

本轮实际修复两处可复现的易用性问题：连接内部标识没有格式示例/中文校验说明，Modbus `UnitId` 没有解释为设备手册中的从站地址。保持现有协议规则、不可变 key、权限、草稿与确认应用机制，不以简化界面为由绕过安全确认。首页已有四步接入引导，保留而不重复添加。

这是一轮自动化回归、软件协议对端测试与代理执行的任务走查，**不是现场用户访谈、实物兼容认证或生产容量承诺**。

## 2. 环境与隔离

Linux x86_64，Intel Xeon Silver 4210R，20 个逻辑 CPU，约 61.4 GiB 内存；.NET SDK 10.0.401，Node 22.23.2，npm 11.21.0，Playwright 1.64.0 / Chromium 156.0.8078.4。见 `environment.log`。

此为共享开发机，开始时 8 GiB swap 几乎用满。全量回归期间同时运行后端及三个独立浏览器分片；后续性能分档串行执行，不与本轮重型回归并行，但不能保证其他会话完全空闲。每档记录系统 load average、精确匹配自有 Host 的 PID/startTicks、每秒 RSS/CPU ticks；CPU 百分比按单核计，不是全机占用。

`/tmp` 是 **tmpfs**，根目录为 `/dev/sda2` **ext4**。最初两档使用默认临时目录，仅作软件/内存文件系统对照；另将 `TENONEDGE_QA_DATA_ROOT` 指向本轮独立的 ext4 目录重新测试。SQLite 事务测试不等于断电持久性测试。

测试自行创建 Host、配置库、首次管理员、浏览器及软件对端，不使用人工账号或人工配置。保护原人工试用服务 `:18490`，不重启、不重置、不猜测密码；测试端口与人工服务分开。协议对端为真实 TCP/HTTP/MQTT 软件服务，不代表物理 PLC、电表、无线网关或远端平台已验证。

## 3. 全量回归与失败复查

| 范围 | 实际结果 | 证据与解释 |
|---|---|---|
| NuGet locked restore、格式、Release build | 通过；构建 0 warning / 0 error | `restore.log`、`dotnet-format.log`、`backend-build.log` |
| 后端 Unit | 2081/2081 通过 | `backend/*.trx` |
| 后端 Integration | **2801/2802 通过，1 失败** | `backend-tests.log` / `backend-report-verification.log`；32 路扇出场景超时 |
| Web 基线 | 59 文件，816/816 通过 | `web-unit.json`；当前修复后为 819 项，不能与基线相加 |
| 全量浏览器基线 | **176/177 通过，1 失败**，无跳过/重试 | `browser-baseline.json`、三个分片 JSON/log/artifacts；`browser-baseline-coverage.json` 确认 177 项各一次 |
| 32 路扇出独立复查 | 三次各 1/1 通过，约 23.3/23.6/24.1 秒含测试启动 | `fanout-{1,2,3}.log` 与各自 TRX；不覆盖全量失败 |
| 令牌删除创建者/保留令牌独立复查 | 连续 3/3 通过，共 23.2 秒 | `tokens-repro.json` / `.log`；不覆盖全量失败 |

后端失败为 `OutputPipelineTests.ThirtyTwoWayFanoutBackpressuresInputAtSharedBudgetAndDrainsAllAcceptedRecords`：250 条来源 × 32 个输出，先验证共享在途预算背压，释放格式化闸门后，`input.Written.Task.WaitAsync(90s)` 超时。TRX 实际用时 92.789 秒。单项复查保留原 90 秒限制及全部 8000 条持久记录/预算释放断言。尚不能区分共享负载放大、写者竞争或运行循环/预算释放问题，不直接归因环境，更未加大期限洗绿。已尝试的反馈环为原全量命令（红）及原单项命令的串行三次（绿）；尚无稳定/高概率的最小红色复现，因此不凭代码猜测提交运行时修复。

浏览器失败为 `tokens.spec.ts` 的 `delete creator requires explicit keep choice and preserves independent roles`，超过原 30 秒总期限。错误上下文仅显示管理员仍在令牌页，不能据此断言失败发生在哪个子步骤。令牌流程继续关闭 trace/video/秘密截图，不为诊断泄露 Token；单项复查保持原期限、角色与保留令牌可用性断言。根因未关闭。

实际运行后端命令（仓库根目录）：

```sh
dotnet restore TenonEdge.sln --locked-mode
dotnet format TenonEdge.sln --verify-no-changes --no-restore
dotnet build TenonEdge.sln -c Release --no-restore
E=/home/shiny/.cache/tenonedge-product-qa/evidence
dotnet test TenonEdge.sln -c Release --no-build \
  --blame-hang-timeout 5m --blame-hang-dump-type none \
  --results-directory "$E/backend" \
  --logger 'trx;LogFilePrefix=product-qa' --logger 'console;verbosity=normal'
python3 .github/scripts/verify-test-results.py "$E/backend" \
  --require TenonEdge.UnitTests --require TenonEdge.IntegrationTests
```

最后一条报告校验的结论也是“不通过”。全量浏览器执行器为外部证据目录同级 `run-baseline-regression.sh`：独立端口 18240/18241/18242，片内 `workers:1`、`retries:0`，`--shard=n/3`；blob 合并并对照 `--list`，没有只挑通过用例合并。

## 4. 从用户任务出发的走查

| 用户要完成的事 | 本轮证据/结论 |
|---|---|
| 首次进入后知道下一步 | 首页在状态加载完成后已有“建立连接→建立设备与来源→画数据路线→确认应用”；管理员/配置人员可见，只读无配置入口，读取失败或运行失败不冒充空网关 |
| 不懂 key 时仍知道怎么填 | 原来只有格式限制，新增例子 `plc_01`、允许字符、64 字符和创建后不可改说明，并通过 `aria-describedby` 关联；非法格式使用中文校验文案 |
| 输入错了能修正继续 | 检查非法 key 不发送 POST；修正后清除 customValidity，实际提交一次并从真实 API 读回草稿，不仅检查按钮或模拟成功 |
| 按设备手册填 Modbus 地址 | `UnitId` 旁解释“从站地址，多数设备为 1”，明确 TCP 为 0–255、RTU over TCP 为 1–247；不改变后端规则 |
| 不写 JSON 完成接入转发 | 既有真实网页闭环配置 Modbus→映射→HTTP，管理员与配置人员分别操作；核对 `9007199254740993` 和换算值、只读权限、浏览器退出后继续转发、Host 正常重启 |
| MQTT / HTTP 来源和故障操作 | 全量浏览器覆盖真实软件对端的采样、字段、预览、测试发送、凭证、导入、应用/恢复与失败区等；通过范围以逐项报告为准，不代表每个功能需求均已验收 |
| 手机/桌面能找到操作且不丢编辑 | 定向复验沿用已有页面家族、浅深色、抽屉焦点/Escape、键盘切换、点表/画布及离页保护用例；结果在最终复验节记录 |

可重复脚本 `web/scripts/product-usability.mjs` 检查 UX-01/02/03/04 与 JS-01，使用真实首次设置、DOM 可访问描述、表单及 API，不改产品 API、不新增依赖。原有效基线 `usability-baseline-ready/usability.json`：UX-01/04/JS-01 通过，UX-02/03 不通过。

**更正保留：** 最初探针在状态尚未加载时读首页，误报没有引导；修正为等待“刷新看板” enabled 后确认原引导存在，撤回重复引导代码。`usability-before` / `usability-after` / `usability-after-ready` 为早期或中间证据，不能作为最终版本。产品 `overview.tsx` / `dashboard-strings.ts` 没有因此增加第二套步骤。

仍需后续以新手任务实测改进，而非本轮直接声称解决：

1. 连接 key 仍须用户手填；设备已有名称建议机制。值得复用到连接，但不能自动改写既有 key 或静默解决并发冲突。
2. 设备页关于建议 key 的实现细节、连接高级时限、协议缩写仍偏工程语言；可考虑普通/高级渐进披露，保留必要安全与错误说明。
3. 首次配置横跨连接、设备、来源、路线和应用；自动化知道正确答案，不等于新用户知道应该填什么。需招募现场用户，记录首条有效数据耗时、求助点和失败恢复成功率。本轮不虚构 SUS 分数、满意度或“零学习成本”。
4. 首页最近异常可能仍显示已经完成设置之前的 `setup.windowOpen` 历史事件；它是历史日志，不应被误读为当前未设置状态。此为观感走查项，未改写事件留存逻辑。

## 5. 性能、资源与中断恢复

脚本 `web/scripts/product-performance.mjs` 使用原产品 Host、模拟输入、原 SQLite 发送缓存和本机真实 HTTP 接收端；每个样本含 energy、temperature 两个字段，HTTP 使用明文 loopback 接收端；不是 50 台实物或大点数 Modbus 采集容量测试。每条保留大整数数字原文与 Idempotency-Key，统计接收重复 ID/坏报文。负载从 Host 启动即开始，先执行 UI/API 测量，再记录稳态起点队列；稳态 60 秒→接收端 503 共 15 秒→恢复并等待 pending=0（最长 60 秒）→成功恢复后再观察 15 秒。**恢复时仍持续采集**，并非停输入后才排空。

这是短时阶梯探测，不是最大容量、24/72 小时稳定性、磁盘满、真实网络抖动或掉电测试。性能门槛未获指定，5/50 条每秒为本轮探测档位，不新创业务 SLA；50 条每秒失败仍必须公开。

| 文件系统 / 输入负载 | 稳态接收 | 60 秒稳态结束待发 | 503 结束待发 | 恢复结果 |
|---|---:|---:|---:|---|
| tmpfs / 5 × 1 Hz | 300 条 / 60.013 秒，约 5.00 条/秒 | 0 | 75 | 26.602 秒清空；再观察 15 秒保持无积压 |
| tmpfs / 50 × 1 Hz | 929 条 / 60.039 秒，约 15.47 条/秒 | 2524 | 3241 | **60 秒未清空**；断言最后 5711，最终快照 5727 |
| ext4 / 5 × 1 Hz | 300 条 / 60.013 秒，约 5.00 条/秒 | 0 | 75 | 6.268 秒清空；再观察 15 秒保持无积压 |
| ext4 / 50 × 1 Hz | 934 条 / 60.035 秒，约 15.56 条/秒 | 2487 | 3235 | **60 秒未清空**；断言与最终快照均为 5707 |

ext4 低档通过（exit 0）：共接收 538 条，重复 ID/坏报文均 0，RSS 抽样峰值 237.69 MiB，CPU 均值 22.34% 单核。恢复耗时包含退避与抖动，不能用两次短测的 6.268/26.602 秒差异证明 ext4 比 tmpfs 更快。

ext4 高档明确失败（exit 1）：共接收 1617 条、坏报文/重复 ID 均 0，末次待发 5707，拒绝/过期/失败/隔离计数均 0；RSS 抽样峰值 287.41 MiB，CPU 均值 57.12% 单核，Host 正常退出。原始数据为 `performance-ext4-{5,50}ps/performance.json`，两档均保留，不覆盖或相加。与 tmpfs 同样在约 15.5 条/秒积压，不能仅以临时目录或其他并行测试解释高档失败。

tmpfs 低档共接收 640 条、坏报文/重复接收 ID 均 0，末次队列 0；RSS 抽样峰值 242.19 MiB，CPU 均值 21.18% 单核。高档共接收 1597 条、坏报文/重复 ID 均 0，配额拒绝、过期、失败/隔离计数均 0，Host 正常退出；**积压不等同已经丢数据，但不能承诺该档可持续运行或恢复**。高档 RSS 抽样峰值 276.64 MiB、CPU 均值 58.04% 单核，不把两分钟抽样当作无内存泄漏证明。

代码检查发现可解释吞吐数量级的串行路径：`OutputSenderUnit.RunAsync` 收到 ACK 后，要等待 `DeleteAsync` 持久提交才能取下一条；`DeliveryWriterUnit` 对非满批请求等待默认 50ms `CommitInterval`。这会限制单目标连续发送频率；本轮未通过降低持久性、提前确认、并行发同一目标或跳过身份屏障来追求跑分，也不把代码阅读替代独立性能剖析。后续优化须保持持久 COMMIT、FIFO、暂停/迁移与重启恢复语义，并补同场景前后对照。

UI/API 的 ext4 低档测量（UI 3 次为描述性小样本，不是统计 SLA）：

- 1000 行软件粘贴中位 116ms；追加 9000 行中位 195ms；末行编辑中位 103ms。保留虚拟化行数、末行定位、CDP JS heap 原值。**这是既有点表原型路由，不是完整设备点表持久化，也不是真实 Excel 剪贴板测试。**
- 真实 UI 添加 50 个原型画布节点并修改名称约 3.16 秒；改名中位 117ms，不是完整业务图的编译吞吐。
- 连接/设备/路线/系统 SPA 可见页面的具体耗时见 JSON，只等待页面标识及两帧，**不含所有业务数据读取完成**。
- 登录态 `runtime/status` 并发 1/5/10，各 100 请求，均 HTTP 200；p95 分别 6.62 / 10.63 / 14.29ms；高档对应 10.13 / 17.99 / 25.51ms，也均为 200。不是登录限流、写事务或全部 API 的容量测试。

最早 `performance-smoke` 是验证脚本的缩短冒烟，且与重型回归并行：50 条/秒时约 14.31 条/秒，恢复超时并积压。保留其失败，不用作正式容量结论；改进脚本，使失败时也记录最终队列、接收/重复/坏报文、RSS、CPU和退出码。

复跑示例（先构建 Release Host 和前端，在 `web/` 执行）：

```sh
export PATH=/home/shiny/.npm/_npx/b0271877f536be0e/node_modules/.bin:$PATH
E=/home/shiny/.cache/tenonedge-product-qa/evidence
export TENONEDGE_TEST_WEBROOT=/home/shiny/.cache/tenonedge-product-qa/dist-final
export TENONEDGE_QA_DATA_ROOT=/home/shiny/.cache/tenonedge-product-qa/workloads
TENONEDGE_QA_INPUTS=5 TENONEDGE_QA_RESULTS="$E/performance-ext4-5ps" \
  node scripts/product-performance.mjs
TENONEDGE_QA_INPUTS=50 TENONEDGE_QA_RESULTS="$E/performance-ext4-50ps" \
  node scripts/product-performance.mjs
```

每次使用新建的自有随机数据子目录并在结束后清理，不能把人工生产目录作为测试数据。高档若仍不满足恢复断言，命令应非零，不得只用低档命令作为门禁。

## 6. 修复后最终复验

最终 lint、格式、类型、构建通过，保留两条既有 lint 警告与大 chunk 提示；Web **59 文件、819/819**，其中新增三项引导权限/失败边界测试。未重新安装前端依赖，不把既有锁文件环境冒充本轮 clean install 或远端 CI。

增强后的同一易用性脚本重新检查基线与最终产物：`usability-baseline-save/usability.json` 的 UX-02/03 仍失败，其余三项通过，且无执行异常、Host 退出 0；`usability-final-save/usability.json` **5/5**，非法标识不发送 POST，改正后恰好一次真实保存并读回，Host 退出 0、页面错误 0。它是实测先红后绿，不是用更改探针或跳过失败来制造通过。

修复后八个文件的浏览器定向回归 **21/21**，失败/跳过/flaky/运行错误均 0，341.274 秒：业务 key、连接列表与删除、设备建议 key/删除、Modbus 两角色闭环、HTTP/MQTT 两角色闭环、总览失败保护、响应式/主题与移动抽屉。`browser-verified.json` / `.log` / `browser-verified-artifacts/` 保存结果；`verify-verified-results.log` 验证非零用例、无失败或跳过。这不把最初全量 176/177 改写成全量通过，也不把重叠用例累计为新增覆盖。

实际命令（在 `web/`，顺序执行，性能完成后才启动此回归）：

```sh
export PATH=/home/shiny/.npm/_npx/b0271877f536be0e/node_modules/.bin:$PATH
E=/home/shiny/.cache/tenonedge-product-qa/evidence
npm run lint
npm run format:check
npm run typecheck
npm test -- --reporter=default --reporter=json --outputFile="$E/web-unit-verified.json"
npm run build -- --outDir /home/shiny/.cache/tenonedge-product-qa/dist-final
TENONEDGE_TEST_WEBROOT=/home/shiny/.cache/tenonedge-product-qa/dist-baseline \
TENONEDGE_QA_RESULTS="$E/usability-baseline-save" node scripts/product-usability.mjs
# 上一条预期非零，人工/执行器核对是 UX-02/03 红色且无运行异常后，再测最终产物。
export TENONEDGE_TEST_WEBROOT=/home/shiny/.cache/tenonedge-product-qa/dist-final
TENONEDGE_QA_RESULTS="$E/usability-final-save" node scripts/product-usability.mjs
TENONEDGE_TEST_URL=http://192.168.0.88:18244 \
PLAYWRIGHT_JSON_OUTPUT_NAME="$E/browser-verified.json" \
npm run test:e2e -- tests/business-keys.spec.ts tests/connection-list.spec.ts \
  tests/device-workflow.spec.ts tests/gateway-workflow.spec.ts \
  tests/json-protocol-workflow.spec.ts tests/app-design.spec.ts \
  tests/workspace.spec.ts tests/overview-dashboard.spec.ts \
  --output="$E/browser-verified-artifacts" --reporter=line,json
node scripts/verify-results.mjs "$E/web-unit-verified.json" "$E/browser-verified.json"
```

`source-verified-sha256.json` 冻结 343 个源码/测试/脚本与锁文件，`build-verified-sha256.json` 冻结产物目录 77 个文件；`frozen-verified.json` 确认复验期间未变，且 `performance-build-sha256.json` 与最终回归产物逐文件相等。外部 outDir 未清空，包含旧 hashed assets；77 是目录文件数，**不是干净发布包的新增文件数**。最终入口及其引用资源才决定当前页面，不使用早期中间入口作为交付。

试用服务于 2026-10-20T10:08:58.821984+00:00 更新至被测入口 `111bd6465390aad72819476270e9d730413207205c0f3a5cc6abca2dd1b592fd`。更新前后核对原 Host PID1060554/startTicks302364428、软件对端 PID1060538/startTicks302364412及完整命令；额外核对旧入口仍为上一轮预期哈希，若被其他会话改变就拒绝覆盖。保留旧 hashed assets、最后原子替换入口，不重启、不重置账号或读写人工配置。

入口/readyz 均 200，`preview-assets.json` 核对入口及 71 个 JS/CSS 目录资源（含保留的旧资源）；`preview-browser.json` 匿名确认登录页、390px 无溢出、无通用 HTTP 警告，页面错误/外部请求/写请求均 0。截图为 `login-desktop.png` / `login-mobile.png`。匿名确认不冒充维护者登录后的人工体验验收；这是开发试用静态更新，不是产品网页升级功能。

## 7. 尚未关闭的优先级与未验证项

| 编号 | 优先级 | 状态与关闭条件 |
|---|---|---|
| QA-PERF-01 | P1：容量/可靠性风险 | 50 条/秒单 HTTP 目标积压，接收端恢复后仍增长；须明确目标硬件/负载，定位并优化持久发送路径，原高档复验，不得降低持久性掩盖 |
| QA-REL-01 | P1：需排除背压停滞 | 全量 32 路扇出超时、独立三次通过；须取得高复现率及失败时队列/预算/运行单元证据，不能宣称已修复 |
| QA-E2E-01 | P2：凭证管理回归不稳定 | 删除创建者且明确保留令牌时超时；须在不记录秘密的前提下定位子阶段，原权限断言继续保留 |
| UX-02/03 | 本轮修复 | 中文内部标识说明与从站地址解释；真实浏览器证据与最终验证见上文 |
| UX 后续走查 | P2 | 名称建议、渐进披露、首次接入任务测量；先验证真实用户困难，不堆砌新配置项或第二套引导 |

最低实际 Chrome/Edge/Safari/Firefox、真实 Excel、真实设备与客户网络、不同工控硬件与慢盘、断电、长期 soak、远端 CI、T3 整体验收与维护者人工验收，**本轮均未新增通过证据**。已有证据仍按实施状态 §1.1 的具体边界使用。默认 HTTP 不新增通用警告；Token 的专门 HTTPS 安全边界维持原规则，不混为一谈。
