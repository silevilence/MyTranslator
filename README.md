# MyTranslator

自用的 CAT（计算机辅助翻译）工具：参考 OmegaT 的「导入 → 翻译 → 编辑 → 导出」工作流，加入全自动 AI 翻译、AI 二审意见与不依赖 AI 的硬性规则检查，并保留一套供第三方系统对接的标准 REST API。

界面与文档为中文。

## 功能

- **任务与导入**：上传 txt、markdown、html、epub 文件，或输入网页 URL 创建翻译任务，自动拆分为可翻译分段；原文标记（markdown 标记、html 标签与实体等）与不可译内容（代码块、`pre/code`、脚本样式等）被提取为占位符或受保护块，导出时原位还原。txt 可选段落式或按行分段，未指定时按文本特征自动推荐。
- **重新提取**：html 类来源（html 文件 / URL / epub 章）可用 CSS 选择器调整可译范围，先预览提取结果再确认替换；已有译文会被丢弃时先行警告。
- **AI 全自动翻译**：对分段批量调用外部 LLM，占位符在翻译过程中受保护；异步运行、进度轮询、失败分段明细与重试；部分失败不回滚已成功的译文。
- **AI 审核意见**：对已有译文发起第二轮 LLM 审查（忠实度、术语对齐、语言自然度与风格、占位符保护），按分段产出严重度、问题描述与修改建议。人工编辑不会清除已有意见；新一轮审核成功审过的分段整体替换旧意见。
- **人工编辑与确认**：分段列表与详情编辑区；占位符以只读方式呈现，不可改写或删除；支持保存、确认与取消确认、批量确认（单批原子）、未保存脏标记、键盘快捷键与离开前确认。
- **硬性规则检查**：不依赖 AI 的确定性校验，内置占位符完整性（成对、顺序、自闭合）与漏翻检测，违规分段以颜色 + 图标 + 文字标示；规则可配置、可新增而不改动核心流程。
- **术语表**：术语增删改查与搜索（更新/删除带乐观并发版本），编辑译文时给出术语对齐提示，并可检查任务中未使用规定术语的分段。
- **翻译记忆库（TM）**：译文确认时自动沉淀为历史条目；编辑时查看相似历史译文、逐字差异对比，差异过大时给出告警（不视为规则违规）。
- **任务列表与进度看板**：任务状态、完成与确认进度百分比、状态筛选、创建入口。
- **配置与管理页**：AI 提供商与模型的两级管理（OpenAI 兼容接口、Ollama；密钥保存后仅掩码显示）、访问 Token 的生成/列表/撤销、浅色与深色主题切换。
- **统一开放 API**：网页端与第三方系统使用同一套 REST API，无内外部接口之分；全部 `/api` 路由默认要求 `Authorization: Bearer` 鉴权。

## 环境要求

| 项目 | 要求 |
|---|---|
| .NET SDK | 10.0（目标框架 `net10.0`；开发环境已验证 10.0.301） |
| 数据库 | 无需安装：SQLite 由 EF Core 在首次启动时自动迁移（默认 `data/mytranslator.db`，容器内 `/data/mytranslator.db`） |
| LLM 服务 | 可选：任何 OpenAI 兼容接口，或本地 Ollama；在界面中配置，不需要改配置文件 |
| Docker | 可选：Docker Engine + Compose v2（仅部署时需要） |

## 快速开始（本地开发）

```powershell
# 1. 构建
dotnet build MyTranslator.slnx

# 2. 启动后端（http://localhost:5199，Swagger 在 /swagger）
dotnet run --project src/MyTranslator.Api

# 3. 另开一个终端启动前端（http://localhost:5000）
dotnet run --project src/MyTranslator.Web
```

首次启动时后端自动执行 EF Core 迁移、启用 SQLite WAL 并写入初始 Token。

开发环境（`ASPNETCORE_ENVIRONMENT=Development`）未设置 `INITIAL_TOKEN` 时，会自动启用固定开发 Token `sk-dev-00000000000000000000000000000000` 并在日志中警告；生产环境必须显式提供 `INITIAL_TOKEN`，否则启动失败。

前端在发布态同源请求 `/api/...`（由 nginx 反向代理）；本地开发时前端运行在 5000、后端在 5199，需自建 `src/MyTranslator.Web/wwwroot/appsettings.Development.json`（该文件被 `.gitignore` 忽略）：

```json
{
  "Api": {
    "BaseUrl": "http://localhost:5199",
    "HealthPath": "/api/health"
  },
  "Auth": {
    "DevToken": "sk-dev-00000000000000000000000000000000"
  }
}
```

配置 `Auth:DevToken` 后，登录页会显示「一键填入开发 Token」按钮；后端在 Development 下允许任意来源的 CORS 请求。

VS Code 用户可直接使用 `.vscode/launch.json` 中的 **MyTranslator: Full Stack** 复合配置，同时启动前后端。

登录后进入「设置」页配置 AI：创建提供商（`openai` 或 `ollama`，填 BaseUrl 与密钥）、添加模型、设置默认对。未配置默认对时，翻译请求返回 `503 llm_not_configured`。

## 构建与测试

```powershell
dotnet build MyTranslator.slnx
dotnet test MyTranslator.slnx
```

当前共 359 个测试（`MyTranslator.Api.Tests` 163 个、`MyTranslator.Shared.Tests` 196 个）。后端测试通过 `WebApplicationFactory` 使用内存 SQLite 与替身 LLM 客户端，前端测试通过 `FakeJSRuntime` 等替身运行，均不需要外部服务或浏览器，可离线执行。

## 配置

后端配置（`src/MyTranslator.Api/appsettings.json` 与环境变量）：

| 键 | 默认值 | 说明 |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `Data Source=data/mytranslator.db` | 相对路径按内容根解析；容器内为 `/data/mytranslator.db` |
| `Translation:MaxConcurrentRuns` | `4` | 翻译运行并发上限 |
| `Review:MaxConcurrentRuns` | `4` | 审核运行并发上限 |
| `INITIAL_TOKEN` | 无（Development 回退固定开发 Token） | `sk-` 加 32 位十六进制字符；非 Development 缺失或格式错误时启动失败 |
| `Cors:AllowedOrigins` | 空 | 非 Development 的跨源白名单；生产部署走 nginx 同源代理，无需配置 |

前端配置（`src/MyTranslator.Web/wwwroot/appsettings.json`）：

| 键 | 默认值 | 说明 |
|---|---|---|
| `Api:BaseUrl` | 空 | 空表示同源请求 |
| `Api:HealthPath` | `/api/health` | 前端连通性检查路径 |
| `Auth:DevToken` | 空 | 仅本地开发用于登录页一键填入，生产留空 |

AI 提供商的 BaseUrl、密钥、模型与默认对不写入配置文件：经设置页或 `/api/providers` 写入 SQLite，密钥在响应中一律掩码显示（见 [ADR-0003](docs/adr/0003-AI提供商与模型二级配置.md)）。

## 部署（Docker Compose）

部署细节与变量说明见 [docs/back/Docker 部署约定.md](docs/back/Docker%20部署约定.md)。`compose.yaml` 从 GHCR 拉取已发布镜像（不含 `build`），前后端各一个容器，SQLite 使用命名卷持久化。

```powershell
Copy-Item .env.example .env
'sk-' + [guid]::NewGuid().ToString('N')   # 生成的 Token 填入 .env 的 INITIAL_TOKEN
docker compose pull
docker compose up -d
```

访问 `http://localhost:8080`（默认仅绑定 `127.0.0.1`，可用 `WEB_BIND_ADDRESS` / `WEB_PORT` 调整），接口文档在 `/swagger/index.html`（经 nginx 按原路径转发）。更新部署时修改 `.env` 的 `IMAGE_TAG` 后再次执行 `docker compose pull` 与 `docker compose up -d`。

仓库已配置 [GitHub Actions 自动发布](.github/workflows/release.yml)，仅推送 `V0.1.0` / `v0.1.0` 这样的版本 Tag 时运行，不设置 CI。工作流使用现有 Dockerfile 构建 `linux/amd64` 镜像，推送到 `ghcr.io/silevilence/mytranslator-api` 和 `ghcr.io/silevilence/mytranslator-web`，提供纯版本号（如 `0.1.0`）及 `latest` 标签；部署时可设置 `IMAGE_TAG=0.1.0`。

两个镜像均推送成功后，工作流从该 Tag 提交的 `changelog.md` 提取对应版本正文，创建或更新 GitHub Release。版本标题匹配不区分大小写，Release 关联原始 Tag；日志缺失、重复或为空会在构建前报错。发布步骤、权限和重跑说明见 [GitHub Actions 发布约定](docs/back/GitHub%20Actions%20发布约定.md)。

也可以从仓库根目录手动构建镜像：

```powershell
docker build -f src/MyTranslator.Api/Dockerfile -t <registry>/mytranslator-api:<tag> .
docker build -f src/MyTranslator.Web/Dockerfile -t <registry>/mytranslator-web:<tag> .
```

注意：`GET /api/health` 同样需要鉴权，无 Token 返回 `401`，不能作为匿名健康探针。

## 项目结构

```
src/MyTranslator.Api            后端 ASP.NET Core Minimal API：端点、服务、EF Core 迁移与内置规则
src/MyTranslator.Shared         Razor Class Library：Blazor 共享组件、模型、服务与 resx 文案（网页端与桌面端共用）
src/MyTranslator.Web            Blazor WebAssembly 网页端：页面、布局、静态资源与 nginx 配置
tests/MyTranslator.Api.Tests    后端接口与规则集成测试（内存 SQLite + 替身 LLM）
tests/MyTranslator.Shared.Tests 共享层纯单元测试（JS 互操作替身）
docs/back                       前后端接口约定：字段、状态、错误码（契约权威来源）
docs/front                      前端设计、组件、交互规范与审查标准（前端改动必须遵守）
docs/adr                        架构决策记录（ADR-0001 组件策略、ADR-0002 标记提取模型、ADR-0003 AI 二级配置）
docs/agents                     Agent 工作流文档：issue tracker、triage 标签、领域文档约定
CONTEXT.md                      领域术语表与用语红线
ROADMAP.md                      任务规划（计划中 / 开发中 / 已完成）
changelog.md                    版本变更记录
```

桌面端（.NET MAUI Blazor Hybrid）仍在 [ROADMAP.md](ROADMAP.md)「六、客户端开发」中，**尚未实现**；共享 RCL 与 `docs/front` 规范已按双端共用设计。

## 技术栈

| 层面 | 选择 |
|---|---|
| 后端 | ASP.NET Core Minimal API（.NET 10）、EF Core + SQLite、Swagger/OpenAPI |
| 前端 | Blazor WebAssembly + MudBlazor 9.8（通用组件）+ RCL 自研领域组件；无 npm/JS 生态依赖 |
| AI | Microsoft.Extensions.AI 抽象 + OpenAI 兼容接口 / OllamaSharp |
| 测试 | xUnit |
| 部署 | Docker 多阶段构建 + nginx 托管 WASM + Docker Compose |

## 文档

- [docs/back](docs/back) — 接口约定（鉴权与基础接口、文件导入拆解与导出、任务管理、翻译编辑器基础与增强、AI 翻译、AI 审核、AI 配置管理、术语、TM、规则检查、Docker 部署）
- [docs/front](docs/front/README.md) — 前端规范索引
- [docs/adr](docs/adr) — 架构决策记录
- [CONTEXT.md](CONTEXT.md) — 领域术语表
- [ROADMAP.md](ROADMAP.md) — 规划与进度
- [changelog.md](changelog.md) — 版本变更记录
