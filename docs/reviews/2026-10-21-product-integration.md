# 两开发线的本地产品集成

- 日期：2026-10-21；维护者批准先整合和回归，再继续千条性能与 T3 收口。不进入 T4/T5/G1，不推送、发布或部署。
- 独立工作树：`/home/shiny/.cache/tenonedge-product-integration/worktree`；分支 `t3-product-integration`。
- 来源：`t0-foundation@17c3657` 与 `t3-inline-reference-verification@b1157dc`，共同祖先 `1ac3e99`。两个原分支/工作区保留。
- 原始证据：`/home/shiny/.cache/tenonedge-product-integration/evidence/20261021/`。`start.json` 保存父线及人工服务 PID/startTicks，后续不触碰人工数据。
- 当前是整合实施记录，不是 T3 退出或稳定千条能力证明；动态状态统一在 `implementation-status.md` §2.323。

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
