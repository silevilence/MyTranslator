# Docker 部署约定

对应 ROADMAP「四、Docker 容器化」。本次交付构建配置与部署示例；本机无 Docker，未执行镜像构建、容器启动或容器内翻译验证。GitHub Actions 的构建、测试与 GHCR 发布留到「五、GitHub Actions 自动发布」。

## 1. 文件与部署拓扑

| 文件 | 用途 |
|---|---|
| [后端 Dockerfile](../../src/MyTranslator.Api/Dockerfile) | .NET 10 SDK 多阶段发布，ASP.NET Core 运行时托管 API |
| [前端 Dockerfile](../../src/MyTranslator.Web/Dockerfile) | 发布 Blazor WASM 与共享 RCL 静态资产，由 nginx 托管 |
| [nginx.conf](../../src/MyTranslator.Web/nginx.conf) | SPA 回退、静态资源 MIME、预压缩、同源 API 反向代理 |
| [compose.yaml](../../compose.yaml) | 前后端两个镜像服务与 SQLite 命名卷的完整示例 |
| [.env.example](../../.env.example) | 部署变量模板，不含可直接使用的 Token |
| [.dockerignore](../../.dockerignore) | 排除本地构建产物、数据库、开发配置与密钥文件 |

访问链路为：浏览器/第三方客户端 → `web:8080`（nginx）→ `api:8080` → `/data/mytranslator.db`。Compose 默认将 web 映射到宿主机 `127.0.0.1:8080`，API 不映射宿主机端口。两个 Dockerfile 的构建上下文均为**仓库根目录**。

前端发布时保留 Release 裁剪与 `.gz` 预压缩，使用 SDK 自带的 WASM 运行时，显式关闭原生重链接，无需安装 `wasm-tools` 或 npm 依赖。运行镜像只复制发布的 `wwwroot`。nginx 提供 `.wasm` 与 `.mjs` 的正确 MIME，优先使用预压缩 gzip 文件；未命中的页面路由回退 `index.html`，缺失的框架/共享库资源返回 404。

前端已有的同源配置直接适用：`Api.BaseUrl` 为空，浏览器请求当前站点的 `/api/...`。`/api` 与 `/swagger` 原路径及查询字符串转发到 API，Bearer 请求头和后端错误响应保留。不要将浏览器 API 地址设置成 Docker 内部名称 `http://api:8080`。nginx 经 Docker DNS 重新解析后端地址，以支持后端容器重建；API 地址变化后最多约 10 秒恢复解析。

静态资源使用 `Cache-Control: no-cache`，允许缓存但每次需重新验证，避免共享 RCL 的固定文件名在更新后继续使用旧版本。nginx 请求体上限为 12 MiB，容纳业务允许的 10 MiB 文件及 multipart 附加数据；业务文件大小上限保持不变。

## 2. 环境变量与首次启动

以下命令供**之后有 Docker Engine 与 Compose v2 的环境**使用，均从仓库根目录执行。本次不执行。

先复制配置模板：

```powershell
Copy-Item .env.example .env
'sk-' + [guid]::NewGuid().ToString('N')
```

将生成的 Token 填入 `.env` 的 `INITIAL_TOKEN=`。`.env` 不进入 Git 或 Docker 构建上下文。

| 变量 | 默认/要求 | 作用 |
|---|---|---|
| `INITIAL_TOKEN` | 必填，`sk-` + 32 位十六进制字符 | 仅注入 API 容器；空值在 Compose 解析时拒绝，格式错误由 API 启动校验拒绝 |
| `WEB_BIND_ADDRESS` | `127.0.0.1` | web 的宿主机绑定地址；局域网访问可改为 `0.0.0.0` |
| `WEB_PORT` | `8080` | web 的宿主机端口 |
| `ASPNETCORE_ENVIRONMENT` | Compose 固定 `Production` | 禁用开发 Token 回退 |
| `ASPNETCORE_HTTP_PORTS` | Compose 固定 `8080` | API 容器内部监听端口，与 nginx 上游保持一致 |
| `ConnectionStrings__DefaultConnection` | `Data Source=/data/mytranslator.db` | SQLite 数据库位于持久化卷 |

环境变量通过 Compose 显式映射到 API；宿主机同名环境变量优先于 `.env`。后端每次启动均要求有效的 `INITIAL_TOKEN`，包括已有数据库时。相同 Token 不重复插入；换成新 Token 会新增记录，不会撤销旧 Token；已撤销的同一 Token 不会因重启重新启用。撤销通过现有 Token 管理 API/界面完成。

准备好变量后：

```powershell
docker compose config --quiet
docker compose up --build -d
docker compose ps
docker compose logs --tail=100 api web
```

浏览器打开 `http://localhost:8080`，输入配置的 Token 登录。接口文档位于 `/swagger/index.html`。示例是 HTTP 部署；如需跨机器传输真实 Token/密钥，在入口反向代理配置 HTTPS。

`depends_on` 只保证后端容器先启动，不保证迁移已结束。API 在启动阶段自动执行 EF Core 迁移、启用 WAL 并初始化 Token；首次启动期间访问可能短暂返回 502，待 API 日志显示开始监听后重试。`GET /api/health` **需要鉴权**，无 Token 返回 401 是既有行为，不能作为匿名 200 健康探针。Compose 没有设置就绪健康检查。

## 3. 部署后配置 AI 并触发翻译

按 [ADR-0003](../adr/0003-AI提供商与模型二级配置.md) 的原则，提供商的 BaseUrl、ApiKey、模型和默认对均经 [AI 配置管理 API](AI%20配置管理接口约定.md) 写入 SQLite。不向容器注入 LLM 密钥，不提供额外首启种子机制。也可以登录后在设置页完成相同操作；未配置默认对时，缺省选择的翻译请求返回 `503 llm_not_configured`。

以下 PowerShell 7 示例使用同一个公开入口；实际执行会创建提供商、模型与翻译任务，并调用你配置的 LLM。先准备一个包含待译英文的 `sample.txt`。示例每次执行都会创建新资源，已有配置可通过 GET 查询并复用 ID。

```powershell
$baseUri = 'http://localhost:8080'
$token = Read-Host '部署时配置的 INITIAL_TOKEN' -MaskInput
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod "$baseUri/api/health" -Headers $headers

$providerBody = @{
    name = 'My provider'
    kind = 'openai'
    baseUrl = Read-Host 'OpenAI 兼容提供商的完整 API 基础地址'
    apiKey = Read-Host '提供商 API 密钥' -MaskInput
    enabled = $true
    isDefault = $true
} | ConvertTo-Json
$provider = Invoke-RestMethod "$baseUri/api/providers" -Method Post `
    -Headers $headers -ContentType 'application/json' -Body $providerBody
Remove-Variable providerBody

$modelBody = @{
    modelId = Read-Host '提供商实际支持的模型 ID'
    displayName = 'My model'
    isDefault = $true
} | ConvertTo-Json
$model = Invoke-RestMethod "$baseUri/api/providers/$($provider.id)/models" `
    -Method Post -Headers $headers -ContentType 'application/json' -Body $modelBody

Invoke-RestMethod "$baseUri/api/providers" -Headers $headers

$task = Invoke-RestMethod "$baseUri/api/tasks/imports/file" -Method Post `
    -Headers $headers -Form @{ file = Get-Item './sample.txt'; fileType = 'txt' }
$runBody = @{
    extractionRevision = $task.extractionRevision
    sourceLanguage = 'en'
    targetLanguage = 'zh-CN'
} | ConvertTo-Json
$run = Invoke-RestMethod "$baseUri/api/tasks/$($task.taskId)/translation-runs" `
    -Method Post -Headers $headers -ContentType 'application/json' -Body $runBody

# 翻译为异步执行；重复查询直至终态，再查看 source-units 中的译文。
Invoke-RestMethod "$baseUri/api/tasks/$($task.taskId)/translation-runs/$($run.runId)" `
    -Headers $headers
Invoke-RestMethod "$baseUri/api/tasks/$($task.taskId)/source-units" -Headers $headers
```

提供商响应只有 `apiKeyMasked`，后续修改请求不能将该响应字段回传。运行字段、终态与失败码见 [AI 翻译接口约定](AI%20翻译接口约定.md)。配置 Ollama 时改为 `kind=ollama`，使用后端容器能访问的服务地址；容器里的 `localhost` 指 API 容器自身。连接宿主机 Ollama 时使用宿主机可达地址；Linux Docker 如需 `host.docker.internal`，可在 api 服务增加 `extra_hosts: ["host.docker.internal:host-gateway"]`，同时确保 Ollama 监听可达接口。

## 4. 数据持久化、重建与备份

`sqlite-data` 命名卷挂载到 `/data`，默认 Compose 项目名为 `mytranslator`，卷名为 `mytranslator_sqlite-data`。数据库包含 Token 哈希、AI 配置（含明文密钥）、任务原文件 BLOB、分段及译文、术语与 TM，当前实现不需要另一个原文件卷。WAL/SHM 与数据库一起留在同一目录。API 以镜像内置 `app` 用户（UID/GID 1654）运行，镜像预先创建并赋权 `/data`，新命名卷沿用该目录权限。

- `docker compose restart`、容器重建以及 `docker compose down` 后再启动均保留命名卷。不要用 `down --volumes`，该命令会删除数据卷。
- 更换 Compose 项目名会使用另一份卷；更换部署目录时沿用本示例的项目名以复用现有数据。
- 默认使用命名卷。如改成宿主机目录绑定，需事先确保 UID/GID 1654 可以读写目录和数据库文件，命名卷的初始化权限机制不适用于绑定目录。
- 备份前停止 API 写入，再复制整个 `/data` 目录，避免只复制活动中的 `.db` 而遗漏 WAL。备份包含原文和 AI 密钥，应按原数据管理权限。

示例备份命令（在之后的 Docker 环境手动执行）：

```powershell
docker compose stop api
New-Item -ItemType Directory -Force './backups' | Out-Null
$backupPath = Join-Path './backups' (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory $backupPath | Out-Null
docker compose cp api:/data/. $backupPath
docker compose start api
```

恢复时先停止 API，将备份的整个数据目录恢复至卷并确保属主为 1654:1654，然后启动 API。恢复旧版本数据库前保留当前备份；不要在 API 运行期间覆盖数据库。

## 5. 后续云端验证清单

第五部分实施时使用以下独立镜像构建入口（本次不新增 workflow）：

```sh
docker build -f src/MyTranslator.Api/Dockerfile -t mytranslator-api:local .
docker build -f src/MyTranslator.Web/Dockerfile -t mytranslator-web:local .
docker compose run --rm --no-deps web nginx -t
```

镜像当前使用可跟随补丁更新的 .NET 10 noble 标签与 nginx stable-alpine 标签，构建需要拉取官方镜像和 NuGet 包；它们并非锁定 digest 的可复现构建。后续发布可在验证通过后固定 digest。

除构建通过外，云端运行验收还需核对：

1. 缺失/空 Token 被拒绝；合法 Token 可访问 `/api/health` 返回 200，无 Token 返回 401。
2. `/login`、`/settings` 和任务详情页直接访问/刷新正常；WASM/MJS MIME 正确，gzip 请求能获得压缩资源，缺失静态资源返回 404。
3. 经代理调用 API 时路由、查询参数、鉴权、文件上传下载及错误响应正确；10 MiB 合法文件不被 nginx 提前拦截；API 重建后代理可恢复访问。
4. 使用测试提供商或获授权的真实 LLM，经配置 API 创建默认对、导入文件、触发翻译并查询译文。仅构建成功不能证明真实提供商翻译链路可用。
5. 重建两个容器后，Token、提供商/模型、任务和译文仍存在；API 可在非 root 身份下写入 SQLite。

配置参考：[Microsoft Blazor WASM nginx 部署](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/nginx?view=aspnetcore-10.0)、[Docker Compose 环境变量插值](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/)、[nginx DNS resolver](https://nginx.org/en/docs/http/ngx_http_core_module.html#resolver)。
