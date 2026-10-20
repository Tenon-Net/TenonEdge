# Design — TenonEdge

2026-10-20：维护者确认全应用页面重设计。本文仅锁定视觉和交互层，
从属于 `docs/frontend-design.md`、功能需求和总设计；不覆盖授权、安全、
路由、API、运行语义或阶段范围。不更换真实固定来源的 shadcn-admin。

## Genre
Modern-minimal：紧凑、安静、面向现场任务的网关工作台，不是营销站或商业分析后台。

## Macrostructure family
- 总览：Operations dashboard，沿用已完成的真实看板，不重复开发。
- 资源：紧凑工具栏 → 筛选/列表 → 按需出现的编辑区；桌面并排，小屏自然纵排。
- 点表/路线：Workbench；资源选择收窄，表格/画布优先，属性和预览各归其位。
- 运行诊断：当前状态 → 可筛选结果 → 选中对象详情；不将所有计数都做成卡片。
- 配置/迁移：核对变更与新旧身份 → 明确确认 → 真实进度/结果。危险声明不能折叠隐藏。
- 设置：分类面板；切换保留已挂载表单和未保存内容，不触发写入。
- 登录/账号：单一聚焦表单；文档：可搜索操作索引和按需展开的契约。
- 无营销页面。保留原型深链，不加入产品主导航。

## Theme
沿用 `web/src/styles/theme.css` 的 shadcn-admin slate 浅/深主题。
背景、文字、边线、控件均引用该文件的语义变量，不新增第二套品牌配色。
状态色只表达真实状态；未知/失败不能冒充正常。无装饰渐变、外部图片或字体。

## Typography
系统字体用于标题和正文；等宽字体只用于 key、精确数值和协议正文。
标题正体 600，页面标题 18px，分区标题 16px，正文 14px，辅助信息 12px。
一级页面不再显示与侧栏重复的菜单标题，仅保留读屏可访问的 h1，且不保留空标题行。
嵌入编辑、详情与具体对象标题继续显示；嵌入点表/画布用 h2，不制造多个页面 h1。

## Spacing
4px 基准，`web/tokens.css` 中 `--space-*` 为共享间距；既有 Tailwind 4 工具类沿用其同一比例。
桌面外壳使用浅灰底、独立侧栏与内容面板，12px 间距、细边框与小圆角；
深色模式使用既有 background/card 分层。手机保留抽屉与全宽内容。
内容外边距 16/24px。宽表格和画布使用可用宽度，阅读表单不无限拉伸。
表格只在自身容器内横向滚动；不使用全局 overflow 裁剪掩盖溢出。

## Motion
不增加动效依赖，不滚动揭示、不数字翻牌。保留原组件有意义的反馈。
焦点立即可见；尊重 prefers-reduced-motion。无 hover-only 操作。

## Microinteractions stance
保留原有加载、禁用、失败、成功和不确定结果状态；写入不自动重试。
分类切换使用标准 tablist、方向键/Home/End 和可见焦点，面板不卸载表单。
一般说明可展开；错误、未保存提醒和危险操作确认始终在对应任务区域。
不通过加超时、强制点击或隐藏失败测试修补布局。

## CTA voice
动作＋对象；一个任务一个主操作，刷新/取消用次级样式，破坏性操作保留明确确认。
沿用 shadcn-admin 按钮、输入和边角，不另造 UI 框架。

## What pages MUST share
品牌、侧栏/页头、浅深主题、系统字体、紧凑标题、按钮、筛选条、表格与空态层级。
默认内网 HTTP，不显示全站通用 HTTP 警告；专门的凭证/TLS安全边界不因此取消。
无 AI 提示、开发任务说明、虚构统计或演示内容。

## What pages MAY differ on
列表/编辑区的比例、画布布局、设置分类、诊断详情的位置按任务决定。
不要求每页四张指标卡，不随机更换主题，不删除生产路由或功能。
常规任务操作保持单行；垂直模块目录中的长技术名称允许换行，以保留完整身份，
目录在桌面独立滚动，不因此挤压画布。协议路径、正文和宽表格按需换行或局部滚动。

## Page inventory
| 页面文件（web/src/features/） | 职责 |
|---|---|
| connections/page.tsx | 连接列表、参数、凭证与测试 |
| devices/page.tsx | 设备草稿与引用核对 |
| inputs/page.tsx | 设备来源、点表、读取和样本 |
| routes/page.tsx | 路线选择、画布、映射与预览 |
| configuration/page.tsx | 应用、版本、回滚与操作 |
| configuration/migration.tsx | 换址变更核对与应用 |
| configuration/migration-intents.tsx | 持久意图与恢复核对 |
| diagnostics/page.tsx | 运行单元、趋势、计数与事件 |
| diagnostics/current-values.tsx | 设备与来源当前值 |
| diagnostics/outputs.tsx | 输出状态、容量与控制 |
| diagnostics/messages.tsx | 近期报文诊断 |
| deliveries/page.tsx | 失败/隔离记录与批量操作 |
| deliveries/migration.tsx | 投递迁移核对与结果 |
| system/page.tsx | 系统资料与分类设置 |
| users/page.tsx | 本地账号管理 |
| tokens/page.tsx | API 令牌与撤销 |
| audit/page.tsx | 操作审计查询与导出 |
| documentation/page.tsx | API 文档 |
| auth/session-gate.tsx、auth/auth-layout.tsx | 首次设置/登录/临时改密 |
| ../router.tsx 中 AccountPage、NotFound | 账号安全、未找到页面 |
| points/editor.tsx、canvas/editor.tsx | 共享编辑器及保留的原型深链 |

## Exports
现有变量是已解析的 `oklch(...)` 颜色，不将其错误替换成裸三元组。

### tokens.css
```css
/* Hallmark · macrostructure: Operations dashboard · tone: modern-minimal · anchor hue: existing shadcn slate
 * Hallmark · pre-emit critique: P5 H4 E4 S5 R4 V4; existing app theme preserved.
 * Dashboard-only semantic aliases preserve the existing shadcn-admin theme and system fonts. */
:root {
    --dashboard-received: oklch(0.5 0.18 255);
    --dashboard-sent: oklch(0.46 0.1 170);
    --dashboard-attention: oklch(0.5 0.14 65);
    --dashboard-normal: var(--dashboard-sent);
    --dashboard-push: var(--dashboard-received);
    --dashboard-disabled: var(--muted-foreground);
    --dashboard-other: var(--border);
}
.dark {
    --dashboard-received: oklch(0.74 0.13 255);
    --dashboard-sent: oklch(0.77 0.12 170);
    --dashboard-attention: oklch(0.78 0.13 75);
}

/* Shared product workspaces. Existing shadcn-admin colors remain authoritative. */
:root {
    --font-display: system-ui, -apple-system, 'Segoe UI', sans-serif;
    --font-body: var(--font-display);
    --font-outlier: ui-monospace, SFMono-Regular, Consolas, monospace;
    --workspace-paper: var(--card);
    --workspace-subtle: var(--muted);
    --workspace-ink: var(--foreground);
    --workspace-secondary: var(--muted-foreground);
    --workspace-rule: var(--border);
    --workspace-focus: var(--primary);
    --space-3xs: 0.25rem;
    --space-2xs: 0.5rem;
    --space-xs: 0.75rem;
    --space-sm: 1rem;
    --space-md: 1.5rem;
    --space-lg: 2rem;
    --workspace-title: 1.125rem;
    --workspace-rail: 16rem;
    --workspace-editor: 28rem;
    --workspace-form: 64rem;
    --workspace-radius: var(--radius);
    --workspace-rule-width: 1px;
    --ease-out: cubic-bezier(0.16, 1, 0.3, 1);
    --dur-micro: 120ms;
}
```

### Tailwind v4 @theme
现有全色表与映射位于 `web/src/styles/theme.css`。增量映射：
```css
@theme inline {
  --color-paper: var(--card);
  --color-ink: var(--foreground);
  --color-rule: var(--border);
  --font-display: system-ui, -apple-system, 'Segoe UI', sans-serif;
  --font-body: var(--font-display);
  --spacing-sm: 1rem;
  --spacing-md: 1.5rem;
}
```

### DTCG tokens.json
```json
{
  "color": {
    "paper": {"$type":"color","$value":"oklch(1 0 0)"},
    "ink": {"$type":"color","$value":"oklch(0.129 0.042 264.695)"},
    "rule": {"$type":"color","$value":"oklch(0.929 0.013 255.508)"},
    "dark-paper": {"$type":"color","$value":"oklch(0.14 0.04 259.21)"},
    "dark-ink": {"$type":"color","$value":"oklch(0.984 0.003 247.858)"}
  },
  "font": {"body":{"$type":"fontFamily","$value":["system-ui","-apple-system","Segoe UI","sans-serif"]}},
  "space": {"sm":{"$type":"dimension","$value":{"value":1,"unit":"rem"}},"md":{"$type":"dimension","$value":{"value":1.5,"unit":"rem"}}}
}
```

### shadcn/ui CSS variables
本工程使用完整颜色值，兼容当前 Tailwind 4 变量引用方式：
```css
:root {
  --background: oklch(1 0 0);
  --foreground: oklch(0.129 0.042 264.695);
  --card: oklch(1 0 0);
  --primary: oklch(0.208 0.042 265.755);
  --primary-foreground: oklch(0.984 0.003 247.858);
  --border: oklch(0.929 0.013 255.508);
  --radius: 0.625rem;
}
.dark {
  --background: oklch(0.129 0.042 264.695);
  --foreground: oklch(0.984 0.003 247.858);
  --card: oklch(0.14 0.04 259.21);
  --primary: oklch(0.929 0.013 255.508);
  --primary-foreground: oklch(0.208 0.042 265.755);
  --border: oklch(1 0 0 / 10%);
}
```
完整主题以 `theme.css` 为准，导出不作为第二份动态状态或业务规则。
