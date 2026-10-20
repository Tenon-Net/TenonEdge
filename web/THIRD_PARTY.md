# 前端源码来源与裁剪记录

实施项 T3-01 / FE-D01。源仓库：https://github.com/satnaing/shadcn-admin 。
固定提交：`e16c87f213a5ba5e45964e9b67c792105ec74d26`。

源项目 MIT，Copyright (c) 2024 Sat Naing；完整文本保存于
`public/licenses/shadcn-admin-MIT.txt`，Vite 构建原样复制到产物。
不改变 TenonEdge 仓库许可证。

| 源文件（相对上游 src）     | 本地文件                       | 实际改动                                                |
| -------------------------- | ------------------------------ | ------------------------------------------------------- |
| components/ui/button.tsx   | src/components/ui/button.tsx   | 格式化，行为不变                                        |
| components/ui/input.tsx    | src/components/ui/input.tsx    | 格式化，行为不变                                        |
| components/ui/label.tsx    | src/components/ui/label.tsx    | 格式化，行为不变                                        |
| components/ui/card.tsx     | src/components/ui/card.tsx     | 格式化，行为不变                                        |
| components/layout/main.tsx | src/components/layout/main.tsx | 格式化，行为不变                                        |
| styles/theme.css           | src/styles/theme.css           | 移除未使用图表变量和外部字体名称，保留基础主题/侧栏变量 |

补正 FE-D01 的整体界面复用（2026-10-20）：以下文件同样来自上述固定提交，经裁剪接入产品；先前仅复用六个基础文件，不足以满足布局与导航基线。

| 源文件（相对上游 src）                     | 本地文件                              | 实际改动                                                                                                                                                                                                   |
| ------------------------------------------ | ------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| components/layout/authenticated-layout.tsx | src/components/layout/workspace.tsx   | 保留 SidebarProvider、SidebarInset 和内容容器组织；接真实会话、网关名称与草稿状态；移除搜索/布局配置 Provider 和侧栏 Cookie                                                                                |
| components/layout/app-sidebar.tsx          | src/components/layout/app-sidebar.tsx | 保留头部/内容/底部/折叠轨道组织；替换品牌和网关导航，按真实角色展示管理项；移除团队切换和模拟数据                                                                                                          |
| components/layout/nav-group.tsx            | src/components/layout/nav-group.tsx   | 保留菜单组、图标链接、激活状态、折叠提示和移动端关闭行为；裁掉无用嵌套菜单及徽标                                                                                                                           |
| components/layout/nav-user.tsx             | src/components/layout/nav-user.tsx    | 保留用户区域布局，接会话姓名/角色/上次登录和账号安全；裁掉头像外链、升级/计费/通知和示例退出，复用真实退出操作                                                                                             |
| components/layout/header.tsx               | src/components/layout/header.tsx      | 保留侧栏开关、分隔线、滚动阴影；支持小屏草稿摘要换行                                                                                                                                                       |
| components/ui/sidebar.tsx                  | src/components/ui/sidebar.tsx         | 保留桌面折叠、移动端模态抽屉及所需组合组件；删除未用子菜单/操作/骨架/输入框、全局快捷键和 Cookie 持久化；文案中文化，补触发器状态及关闭后的焦点恢复；Tooltip仅在桌面折叠时创建，避免隐藏提示拦截手机Escape |
| components/ui/sheet.tsx                    | src/components/ui/sheet.tsx           | 保留模态抽屉；关闭文案经 i18n，移除未用导出                                                                                                                                                                |
| components/ui/tooltip.tsx、separator.tsx   | src/components/ui/ 同名文件           | 格式化；移除 tooltip 的无用 use-client 指令                                                                                                                                                                |
| components/ui/dropdown-menu.tsx            | src/components/ui/dropdown-menu.tsx   | 保留主题选择需要的入口/内容/菜单项；裁掉未用多级菜单、单选/复选和快捷键组件                                                                                                                                |
| components/ui/alert.tsx                    | src/components/ui/alert.tsx           | 统一 API 错误提示，保留既有错误与字段内容                                                                                                                                                                  |
| components/ui/form.tsx                     | src/components/ui/form.tsx            | 接入真实登录/首次设置/重置/改密的 React Hook Form；不带上游账号、模拟令牌或社交登录逻辑                                                                                                                    |
| components/ui/table.tsx                    | src/components/ui/table.tsx           | 普通连接、账号、审计和诊断列表复用；单元格允许长地址/消息换行，保留数据、权限和操作；不替换独立点表/画布编辑模型                                                                                           |
| hooks/use-mobile.tsx                       | src/hooks/use-mobile.tsx              | 格式化，行为不变                                                                                                                                                                                           |
| components/skip-to-main.tsx                | src/components/skip-to-main.tsx       | 中文化；对应可聚焦的产品主内容                                                                                                                                                                             |
| context/theme-provider.tsx                 | src/context/theme-provider.tsx        | 保留浅色/深色/系统主题；将 Cookie 改为仅保存外观偏好的 localStorage，容忍禁用存储；系统变化同步上下文                                                                                                      |
| components/theme-switch.tsx                | src/components/theme-switch.tsx       | 菜单文案中文化；主题色跟随实际解析后的系统主题                                                                                                                                                             |
| features/auth/auth-layout.tsx              | src/features/auth/auth-layout.tsx     | 保留居中登录布局；替换本网关品牌、加入主题入口，适配较长首次设置表单                                                                                                                                       |

工程、业务认证、路由、API/i18n 和测试由本网关维护。没有新增 AI 提示区、演示仪表盘或模板虚构数据；原型入口不在产品主导航中。
没有复制上游整仓、mock 用户/Token、Clerk、公开注册、社交登录、聊天/报表等演示页面。
不安装 Axios、React Router 或 ESLint；仅 TanStack Router（代码路由，不需要文件路由构建插件）及 openapi-fetch。
不用上游 package.json 的范围作为锁定版本；本项目 package.json 精确固定直接依赖，package-lock.json 固定完整图。

npm 生产依赖及其声明版本、许可证由锁文件与安装包核查；`npm run licenses` 收集实际生产依赖的 LICENSE/NOTICE 文本到
`public/licenses/npm-notices.txt`，缺失文本则构建失败；聚合时只规范 CRLF 与行末空白，不删改许可条款。该清单为包级保守超集，不声称每个导出都被最终 bundle 使用。
开发/测试依赖不进入运行服务，完整版本及许可声明仍可查锁文件；正式发行物完整许可审计属于 T5-07。

品牌直接复制自本仓库 `assets/brand/tenonedge/icon-128.png` 与 `favicon.ico`，未修改原图；来源见 `docs/brand-assets.md`。

Radix 抽屉的传递依赖 `react-remove-scroll-bar@2.3.8` 的发布包声明 MIT，但漏带许可正文。
从维护者仓库固定提交 `8ca9ba5ea52de03308fe8ced94f7b159a44d28ff` 的
[LICENSE](https://github.com/theKashey/react-remove-scroll-bar/blob/8ca9ba5ea52de03308fe8ced94f7b159a44d28ff/LICENSE)
保存原文至 `public/licenses/react-remove-scroll-bar-2.3.8-MIT.txt`，Copyright (c) 2025 Anton Korzunov。
该上游提交包含补许可的合并，源码 package.json 为 2.3.7；npm 2.3.8 的 gitHead 无法从该仓读取，故不声称此提交就是 2.3.8 发布源码。
许可收集仅对当前精确包版本、MIT声明及该本地文本使用显式补充，其他缺失仍导致构建失败；构建不联网获取许可、不用其他库的MIT模板补写。
