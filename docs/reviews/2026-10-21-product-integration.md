# 两开发线的本地产品集成

- 日期：2026-10-21；维护者批准先整合和回归，再继续千条性能与 T3 收口。不进入 T4/T5/G1，不推送、发布或部署。
- 独立工作树：`/home/shiny/.cache/tenonedge-product-integration/worktree`；分支 `t3-product-integration`。
- 来源：`t0-foundation@17c3657` 与 `t3-inline-reference-verification@b1157dc`，共同祖先 `1ac3e99`。两个原分支/工作区保留。
- 原始证据：`/home/shiny/.cache/tenonedge-product-integration/evidence/20261021/`。`start.json` 保存父线及人工服务 PID/startTicks，后续不触碰人工数据。
- 本轮本地集成与完整软件回归已完成，千条档仍失败；这不是 T3 退出或稳定千条能力证明，动态状态统一在 `implementation-status.md` §2.323。

## 冲突决策

初始 `git merge --no-ff --no-commit t3-inline-reference-verification` 有 19 个冲突文件，日志 `merge.log`。逐项保留意图：

1. RuntimeHost 仍委托给抽取后的 RuntimePlanBuilder；将扩展输出的 queue-only、冻结传输、目标/输入不变及共享恢复分组校验移入 builder，不恢复大段重复实现或创建第二套运行时。原单事务配额和有界 ACK 窗口仍走唯一写者。
2. shadcn-admin 的产品外壳、仪表盘、各页分区及默认 HTTP 无通用警告保留；React.lazy、路由失败边界、按需文案和 bundle 门禁同时保留，不能因整合把全部功能重新塞入登录包。
3. 连接页保留主线的 queries/save seam；凭证仅绑定显式打开的连接快照、草稿过期禁写、保存期间禁关闭、删除确认只执行一次均保留。删除文案使用产品线的 prompt 键，避免与确认按钮键冲突。
4. 画布既保留节点实际观测，也保留读屏可定位的全局运行摘要；有效判断沿用完整的版本/依赖/本地编辑核对，不仅凭保存路线 effective 标记冒充当前一致。
5. OpenAPI 和 TypeScript 契约须从本轮 Release Host 重新生成，随后 `api:check` 验证；两份旧生成文件都不是整合证据。
6. 端到端保留更完整的扩展凭证/TLS/并发场景；容量测试使用环境和样本匹配公式，不恢复已被后续条件化模型替代的固定 k 假设。所有软件网络集成与浏览器套件串行运行，不借重跑掩盖旧失败。
7. 证据编号碰撞不覆盖历史。原隔离线 §2.288—§2.322 保留；原主线的不同证据导入为 §M-2.xxx，并显式标注来源。T/GW/AT/V/FS/FE 任务编号不变，旧绿色不外推至整合版。

## 验证约定

精确命令、日志、非零用例数和失败在执行后记录。locked restore、固定 npm ci、格式、Release build、API 生成/校验、Web typecheck/lint/unit/build、后端 unit/integration、当前及最低浏览器 E2E、50/500/1000 性能依次实施。性能中断时长与旧专项相同的档位才可对照；来源 overrun、策略/存储丢弃、重复与积压均保留，不放宽门禁。

当前未完成完整验证，不将未执行项目写为通过；千条失败、既有扇出超时与 Token E2E 不稳定仍需独立闭环。实物、工控硬件、掉电、长期 soak、真实平台和远端 CI 无新增通过证据。

## 首轮验证发现及保留的失败

- `restore.log`：`dotnet restore --locked-mode` 通过；`npm-ci.log`：npm 11.21.0 固定锁文件安装通过，未新增包版本。
- `build-first.log`：Release 构建通过，0 warning/error；`dotnet-format.log`：完整 .NET 格式校验通过。`api-generate.log` / `api-check.log`：整合 Host 真实生成后校验通过。
- `typecheck-first.log` 首次失败：Git 自动合并的连接列表浏览器测试保留了两次不同阶段的删除核对，但局部变量重名。只区分第二次观测变量，保留两组来源、队列及不可删除断言；`typecheck-second.log` 通过。
- `web-unit-first.json` **835/835**；这是后续按需加载修正之前的版本，不冒充最终回归。
- `web-build-first.log` 构建失败：初始静态 JS 561,723 B / gzip 178,463 B，超过原 500,000 / 165,000 门禁。保留失败，不提高预算。登录产品布局也改为惰性加载后构建通过，但真实浏览器冷登录仍未达原 580,000 B 门禁。
- `browser-loading-first.json` **3 通过/1 失败**：冷登录实际请求 685,900 B。将主题菜单改为显式操作后才加载；`browser-loading-second.json` **3 通过/1 失败**：冷登录降为 575,301 B，但恢复总览仍 694,219 B，超过原 650,000 B。
- 统一登录/工作区的轻量主题入口；仍使用原 shadcn-admin 菜单，保留键盘入口及局部失败保护，不删除主题能力。总览注册其真正使用的文案，通用诊断视图不再隐式带入整套失败区/迁移文案；每个直接使用的组件须注册自己的窄文案。`browser-loading-third.json` 的 4 项原按需加载和 2 项工作区回归 **6/6**，35.9 秒，冷登录 575,565 B、恢复总览 629,387 B；原两个浏览器预算均不变。
- 随后 `web-unit-final-static.json` **822 通过/14 失败/总 836**，全部是直接渲染容量组件时失去原通用视图的隐式文案注册。修正容量组件自己注册 outputs 文案，保留全部原 14 项精确文本/未知/经验系数断言，不修改测试期望。此轮失败保留，后续必须重新执行全量。
- 新增真实浏览器主题键盘按需加载、选中后保留登录材料、菜单资源失败不卸载表单/不提交/不自动重试的回归。它们不在上述 6 项中，须以新增后运行结果单列；后端完整回归、完整当前/最低浏览器及性能尚未完成。

以上均为本轮自己的测试 Host 和临时数据，没有操作人工试用服务。最终结果将在真实执行完毕后继续补充，不以定向补跑相加冒充全量。

## 当前整合版已完成的前端回归

`typecheck-static2.log` / `web-lint-static2.log` / `web-format-static2.log` / `web-build-static2.log` 均通过。`web-unit-static2.json` **836/836**、0 失败/跳过，包含未放宽的 14 项容量 SSR 与新增 AuthLayout 惰性门禁；`browser-loading-final-focused2.json` **8/8**、0 失败/跳过/重试，44.1 秒，包含新增主题键盘需求加载/网络失败保留表单，以及原布局、折叠、手机抽屉、焦点、主题和未保存编辑保护。冷登录 **575,565 B** / 恢复总览 **629,387 B**，原 580,000 / 650,000 浏览器门禁未变。

`integrated-frozen-01.json` 冻结 **2111** 个源码/测试/依赖、Release 运行产物、干净 Web dist 及 bundle 报告，在后续完整回归前建立；其本身不是 2111 项测试或发布文件数。完整后端 Unit/Integration 已启动，完成前不计通过；完整浏览器及性能尚未执行。


## 后端完整回归与本地合并边界

在上述 `integrated-frozen-01.json` 冻结版本上串行执行（没有并行浏览器/性能）：

```sh
dotnet test tests/UnitTests/TenonEdge.UnitTests.csproj -c Release --no-restore --no-build --results-directory "$E/backend-unit-01" --logger 'trx;LogFileName=unit.trx'
dotnet test tests/IntegrationTests/TenonEdge.IntegrationTests.csproj -c Release --no-restore --no-build --blame-hang --blame-hang-timeout 5m --results-directory "$E/backend-integration-01" --logger 'trx;LogFileName=integration.trx'
python3 .github/scripts/verify-test-results.py "$E/backend-unit-01" --require TenonEdge.UnitTests
python3 .github/scripts/verify-test-results.py "$E/backend-integration-01" --require TenonEdge.IntegrationTests
```

`E` 为本文证据根；SDK 10.0.401，完整环境见 `dotnet-environment.txt`。**2081/2081 单元、2829/2829 集成**通过，均 0 失败/跳过；集成耗时 13 分 35 秒。两份 TRX 运行级 outcome 均为 Completed，仓库核对脚本通过，日志为 `backend-unit-01.log` / `backend-integration-01.log` 及对应 `*-verified.log`。结束后重新计算冻结清单，2111 个文件均未变。人工试用 Host PID 1060554/startTicks 302364428、fixture PID 1060538/startTicks 302364412 均未变，两个原工作树保持干净。

本次完成本地合并提交后，继续串行执行整合版完整当前浏览器 E2E、最低 Chromium 补充验证与 50/500/1000 档性能。最低 Chromium 不冒充实际 Chrome/Edge111、Safari16.4；当前绿色结果不能关闭历史 MQTT 偶发风险、稳定千条目标或整个 T3 退出。


## 完整当前浏览器及最低版测试边界

本地合并为 `d96b781`（父提交 `17c3657` / `b1157dc`）。完整当前 Chromium **188/188**、39.2 分钟，版本 156.0.8078.4；1 worker、retries=0，0 skipped/flaky/unexpected，报告无运行级错误，逐条结果均为单次 Passed。`browser-current-full-01.json` / `*-verified.json`、同前缀日志/artifacts；环境见 `browser-current-environment.txt`。执行后 2111 个冻结文件无变化，产品从本工作树 `web/dist` 提供，没有误用旧外部 dist。

命令（web/，固定 npm 11.21.0）：

```sh
TENONEDGE_TEST_URL=http://127.0.0.1:18782 \
TENONEDGE_TEST_WEBROOT=/home/shiny/.cache/tenonedge-product-integration/worktree/web/dist \
PLAYWRIGHT_JSON_OUTPUT_NAME="$E/browser-current-full-01.json" \
npm run test:e2e -- --reporter=line,json --output="$E/browser-current-full-01-artifacts"
```

最低版预检检查了独立启动入口。新增显式版本一致断言后的初始探针 `browser-selection-red.json` 实际 **2/2 通过**：候选不一致没有重现，文件名不表示真实失败。随后重启用例及 TLS helper 显式传入 `TENONEDGE_TEST_EXECUTABLE`，避免依赖隐式 launchOptions 注入；增强后的当前版定向 `browser-selection-current.json` **2/2**、完整 Web `web-unit-selection.json` **836/836**、类型/lint 通过。此为测试证据加强，不是产品故障修复；未修改产品代码或放宽门禁。

测试提交 `c1680a5` 仅更改 `configuration-restart.spec.ts`、`tokens.spec.ts`、`support/trusted-browser.ts`。冻结清单 `integrated-frozen-02.json` / metadata 明确这三个文件与上一清单的差异，所有产品源码、后端二进制、Web dist/bundle 均未变。随后在相同产品构建上完成完整 Chromium 111.0.5563.0 补充回归（`TENONEDGE_TEST_EXECUTABLE=/tmp/tenonedge-t3/chromium111/chrome-linux/chrome`，其余命令同上，报告前缀 `browser-chromium111-full-01`）。**188/188**、42.2 分钟，0 skipped/flaky/unexpected，逐条单次 Passed，报告无运行级错误；`browser-chromium111-full-01-verified.json` 核对通过，2111 个冻结文件未变。实际 binary 版本和 SHA-256 另存 `browser-chromium111-executable.txt`。它不替代实际 Google Chrome / Microsoft Edge111、Safari16.4 和 Excel 来源的外部验收。


## 整合版关键性能复测

所有后端/浏览器重型测试结束后，串行运行 50、500、1000 档；当前产品源码、Release bins 与 `web/dist` 均来自本整合树，`integrated-frozen-02-final-verification.json` 核对 2111 个文件全程无变化。共享 Linux 开发机、20 逻辑 CPU、约 61 GiB RAM，swap 接近满；ext4 `/dev/sda2`，实际 queue.db 报 **SQLite 3.53.3 / WAL / synchronous=2（FULL）/ schema9**。环境保存在 `performance-integrated-environment.txt` 和每档 report，不能套用旧分支的 SQLite 版本或把共享机差异归因为合并。

```sh
export TENONEDGE_TEST_WEBROOT=/home/shiny/.cache/tenonedge-product-integration/worktree/web/dist
export TENONEDGE_QA_DATA_ROOT=/home/shiny/.cache/tenonedge-product-integration/workloads
TENONEDGE_QA_INPUTS=50 TENONEDGE_QA_OUTAGE_SECONDS=15 TENONEDGE_QA_RESULTS="$E/integrated-50ps-01" node web/scripts/product-performance.mjs
TENONEDGE_QA_INPUTS=500 TENONEDGE_QA_OUTAGE_SECONDS=3 TENONEDGE_QA_RESULTS="$E/integrated-500ps-01" node web/scripts/product-performance.mjs
TENONEDGE_QA_INPUTS=1000 TENONEDGE_QA_OUTAGE_SECONDS=3 TENONEDGE_QA_RESULTS="$E/integrated-1000ps-01" node web/scripts/product-performance.mjs
```

每档为原产品模拟来源 → 原持久队列 → 单目标真实 loopback HTTP，保留大整数与原编号；普通 HTTP 单请求在途，不是批量数组协议。60 秒稳态后先要求实际收到至少名义输入 95%，通过才进入 503 阶段，再于持续输入下最多等待 60 秒首次清空并观察 15 秒。脚本还要求 overrun、策略/存储丢弃、重复与坏报文均为 0。50 档 15 秒中断与高档 3 秒中断不能混成同一场景或恢复 SLA。

| 输入/s | 稳态收到/s | 稳态末待发 | 稳态 p95/p99 ms | 503 秒 | 首次清空秒 | overrun | 本次结果 |
|---|---:|---:|---|---|---|---:|---|
| 50 | 49.988 | 31 | 225/234 | 15 | 16.685 | 0 | 通过 |
| 500 | 500.865 | 0 | 339/376 | 3 | 15.718 | 0 | 通过 |
| 1000 | 770.583 | 5498 | 7081/7159 | 未执行 | 未执行 | 17157 | **失败，exit1** |

50/500 档重复接收、坏报文、队列策略丢弃、存储故障/拒绝均 0，Host 正常退出；最终待发分别 0/52。500 档恢复后观察末为 86，不能把一次观测清空或正常在途快照写成“始终无积压”。1000 档低于原 950/s 门禁即终止，没有执行 503/恢复；最终待发 5547，虽仍无队列丢弃/存储失败/重复，来源周期已经跳过 17157 次，不算可靠千条接管。

原始各档 `performance.json`、日志、`.exit-code` 全部保留；`integrated-performance-summary-01.json` 汇总阶段、计数和资源。Host 整段平均 CPU 按单核计为 28.67% / 151.93% / 264.62%，RSS 采样峰 241.27 / 367.52 / 451.86 MiB；不含 Node/浏览器 CPU，不是整机峰值、长期泄漏或工控硬件容量。50/500 仅为本次短软件负载通过；1000 稳定能力、恢复余量及 QA-PERF-01 仍开放。

`preview-protection-after-integration.json` 核对人工试用 Host/fixture PID/startTicks 未变；两个原工作树仍干净，未部署、推送、登录人工账号或修改人工配置。下一步收紧千条反馈环并剖析热路径，单变量验证后重新执行受影响的完整回归，不取消 FULL、放宽配额/门禁或用来源跳过换分数。T3 的实际浏览器、Excel、CI 和维护者验收仍按统一状态 §1.1；本轮不能自行宣布 T3 完成。
