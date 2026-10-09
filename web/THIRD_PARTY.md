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

其余工程、Workspace 导航布局、认证、路由、API/i18n 和测试为本网关实现。
没有复制上游整仓、mock 用户/Token、Clerk、公开注册、社交登录、聊天/报表等演示页面。
不安装 Axios、React Router 或 ESLint；仅 TanStack Router（代码路由，不需要文件路由构建插件）及 openapi-fetch。
不用上游 package.json 的范围作为锁定版本；本项目 package.json 精确固定直接依赖，package-lock.json 固定完整图。

npm 生产依赖及其声明版本、许可证由锁文件与安装包核查；`npm run licenses` 收集实际生产依赖的 LICENSE/NOTICE 文本到
`public/licenses/npm-notices.txt`，缺失文本则构建失败；聚合时只规范 CRLF 与行末空白，不删改许可条款。该清单为包级保守超集，不声称每个导出都被最终 bundle 使用。
开发/测试依赖不进入运行服务，完整版本及许可声明仍可查锁文件；正式发行物完整许可审计属于 T5-07。

品牌直接复制自本仓库 `assets/brand/tenonedge/icon-128.png` 与 `favicon.ico`，未修改原图；来源见 `docs/brand-assets.md`。
