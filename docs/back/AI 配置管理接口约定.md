# AI 配置管理接口约定

本文定义 AI 提供商（Provider）与 AI 模型（Model）两级配置的 REST 管理接口，覆盖提供商/模型的增删改查、默认对设置、密钥掩码回显、删除级联语义，以及与翻译运行创建的三档解析关系。调用方包括 Web 端配置界面、桌面端与第三方系统；本组接口是 AI 翻译配置的唯一写入途径。

鉴权、基础地址和通用 Problem Details 格式以《鉴权与基础接口约定》为准。本文所有接口均要求 Bearer Token。分段与占位符字段语义以《文件导入拆解与导出接口约定》及 ADR-0002 为准；配置与翻译运行创建的关系见《AI 翻译接口约定》§3，本文是其配置模型的完整定义。

## 1. 范围与接口原则

- 配置采用二级模型：AI 提供商（Provider）下挂多个 AI 模型（Model）。Provider 含连接器 kind、BaseUrl、密钥、启用状态与运行参数；Model 含厂商模型 ID、展示名与能力元数据。
- 配置的唯一写入途径是本组接口；appsettings 的 `Translation` 节退役。`MaxConcurrentRuns` 属基础设施并发旋钮，保留 appsettings，不属于本 API。
- ApiKey 明文存 SQLite（个人本地工具），但 API 响应一律只回显掩码，且密钥不得写入日志、Problem Details、运行资源或分段级失败详情。掩码以 `***` 为保留字（§2.3）。
- 配置允许不完整（草稿状态）：kind/baseUrl/密钥/默认标记可缺省或留空；完整可用性在创建翻译运行时校验（《AI 翻译接口约定》§3.2）。本组接口只做字段级格式校验。
- 列表接口不分页，返回完整对象裸数组（配置规模为个人级；与 Token 列表约定一致）。
- 删除立即生效：运行中引用已删/已停用提供商或模型的批次在按批构造客户端时失败，映射为分段失败码（§6.3）。

## 2. 通用约定

### 2.1 标识、时间、枚举与数值格式

- `providerId`、`modelId` 均为 UUID 字符串；`Model.modelId`（厂商模型 ID）是与提供商 API 对接的模型名称，为任意非空字符串，同一提供商内唯一。
- 时间使用 UTC ISO 8601，例如 `2026-08-14T08:30:00Z`。
- `kind` 为连接器类型，v1 支持 `openai` 与 `ollama`；调用方必须容忍未来新增未知枚举值。
- JSON 字段使用 `camelCase`，请求和响应使用 UTF-8。
- `requestTimeout` 使用 .NET TimeSpan 常量格式（`小时:分钟:秒`），例如 `00:01:00`。

### 2.2 默认对不变量

- `Provider.isDefault` 全局唯一：任一 Provider 置 `true` 时，其余 Provider 的 `isDefault` 在同一事务内置 `false`。
- `Model.isDefault` 提供商内唯一：某 Provider 下任一 Model 置 `true` 时，该 Provider 其余 Model 的 `isDefault` 在同一事务内置 `false`。
- 默认提供商 + 其默认模型合成**默认对**，翻译运行未显式指定时使用（《AI 翻译接口约定》§3.1）。
- 允许「有默认提供商但无默认模型」或「无默认提供商」的中间状态；此时未指定选择的翻译运行创建返回 `503 llm_not_configured`（§6.2）。默认对不完整只影响未指定选择的翻译运行，不影响配置管理本身。

### 2.3 密钥掩码与回写

- 响应中的密钥字段固定为 `apiKeyMasked`；请求**不得**携带该字段（携带返回 `400 invalid_provider_key_field`）。
- 掩码规则：明文长度不超过 `8` 时输出 `***`；否则保留前 `3` 个字符与后 `4` 个字符，中间以 `***` 连接。示例：`sk-0123456789abcdef` → `sk-***cdef`。
- `***` 为保留字：厂商密钥字符集（base64/hex）不含 `*`，任何真实密钥不会命中掩码格式。请求 `apiKey` 值为 `null`、缺省或命中掩码格式时一律视为「保持原密钥」；其他非空值替换为新的明文密钥。

## 3. 数据模型

### 3.1 Provider

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | UUID | 响应 | 提供商 ID |
| `name` | string | 是 | 展示名；允许重名；非空 |
| `kind` | string | 是 | 连接器类型：`openai` / `ollama` |
| `baseUrl` | string/null | 否 | LLM API 基础地址；提供时须为绝对 http(s) URL；为空表示未配置（运行时 503） |
| `apiKeyMasked` | string/null | 响应 | 掩码密钥；请求不得提交（§2.3） |
| `enabled` | boolean | 是 | 启用状态，创建默认 `true`；显式选择已停用提供商创建运行返回 `422 provider_disabled` |
| `isDefault` | boolean | 是 | 全局唯一默认提供商，创建默认 `false` |
| `batchSize` | integer | 否 | 每批分段数，默认 `20`，范围 `1..200` |
| `requestTimeout` | string | 否 | 单次 LLM 请求超时，默认 `00:01:00`，须大于 0 |
| `maxAttempts` | integer | 否 | 每批最大尝试次数，默认 `3`，范围 `1..10`，含首次请求 |
| `createdAt` | string | 响应 | 创建时间 |

运行参数（`batchSize`/`requestTimeout`/`maxAttempts`）描述「向该提供商调 API 的行为」，随提供商配置；`maxConcurrentRuns` 是全局并发旋钮，保留 appsettings。

### 3.2 Model

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | UUID | 响应 | 模型条目 ID |
| `providerId` | UUID | 是 | 所属提供商 |
| `modelId` | string | 是 | 厂商模型 ID（对接提供商 API 的模型名），同一提供商内唯一 |
| `displayName` | string | 是 | 展示名；非空 |
| `supportsThinking` | boolean | 否 | 能力声明：思考；默认 `false` |
| `supportsToolUse` | boolean | 否 | 能力声明：工具使用；默认 `false` |
| `supportsStreaming` | boolean | 否 | 能力声明：流式；默认 `false` |
| `isDefault` | boolean | 是 | 提供商内唯一默认模型，创建默认 `false` |
| `createdAt` | string | 响应 | 创建时间 |

能力元数据仅作声明与界面展示，**不进入翻译运行请求**（ADR-0003）。

### 3.3 内嵌模型

`GET /api/providers` 的每个 Provider 对象内嵌 `models` 数组（完整 Model 对象，含 `providerId`），供前端选择器一次加载；Provider 的创建/更新请求与单资源响应**不含** `models` 字段，模型经 §5 子资源操作。

## 4. Provider 接口

### POST /api/providers

请求体：

```json
{
  "name": "DeepSeek",
  "kind": "openai",
  "baseUrl": "https://api.deepseek.com",
  "apiKey": "sk-0123456789abcdef0123456789abcdef",
  "enabled": true,
  "isDefault": true,
  "batchSize": 20,
  "requestTimeout": "00:01:00",
  "maxAttempts": 3
}
```

字段校验：`name`/`kind` 必填；`baseUrl`/`apiKey` 可空；`enabled` 默认 `true`；`isDefault` 默认 `false`，置 `true` 时按 §2.2 清旧；运行参数缺省用文档默认值，越界返回 `400 invalid_runtime_settings`。

成功返回 `201 Created`，响应含 `apiKeyMasked`（提交了密钥时）：

```http
Location: /api/providers/7c9e6679-7425-40de-944b-e07fc1f90ae7
```

```json
{
  "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "name": "DeepSeek",
  "kind": "openai",
  "baseUrl": "https://api.deepseek.com",
  "apiKeyMasked": "sk-***cdef",
  "enabled": true,
  "isDefault": true,
  "batchSize": 20,
  "requestTimeout": "00:01:00",
  "maxAttempts": 3,
  "createdAt": "2026-08-14T08:30:00Z"
}
```

curl 示例：

```bash
curl -X POST http://localhost:5199/api/providers \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000" \
  -H "Content-Type: application/json" \
  -d '{"name":"DeepSeek","kind":"openai","baseUrl":"https://api.deepseek.com","apiKey":"sk-...","isDefault":true}'
```

### PUT /api/providers/{id}

- 全字段替换，`apiKey` 例外（§2.3）：`null`/缺省/掩码格式值 → 保持原密钥；其他非空值 → 替换。
- 字段校验与 POST 相同；`isDefault` 置 `true` 时按 §2.2 清旧；请求携带 `apiKeyMasked` 返回 `400 invalid_provider_key_field`。
- 成功返回 `200 OK` 与更新后 Provider（掩码）。
- 提供商不存在返回 `404 provider_not_found`。

### DELETE /api/providers/{id}

- 成功返回 `204 No Content`；级联删除其下全部 Model；若为默认提供商，`isDefault` 清除（回到无默认对状态，§6.2）。
- 提供商不存在返回 `404 provider_not_found`。

### GET /api/providers

- 返回 `200 OK` 裸数组，按创建时间升序，每个对象内嵌 `models`（§3.3，同样按创建时间升序）。
- 无提供商时返回空数组。

示例：

```bash
curl http://localhost:5199/api/providers \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

## 5. Model 接口

### POST /api/providers/{providerId}/models

请求体：

```json
{
  "modelId": "deepseek-chat",
  "displayName": "DeepSeek Chat",
  "supportsStreaming": true,
  "isDefault": true
}
```

- `modelId`/`displayName` 必填；能力布尔缺省 `false`；`isDefault` 默认 `false`，置 `true` 时清同提供商其他默认模型。
- 同一提供商内 `modelId` 重复返回 `409 model_id_conflict`（errors 附 `providerId` 与重复值）。
- 父提供商不存在返回 `404 provider_not_found`。
- 成功返回 `201 Created`，含 `Location: /api/providers/{providerId}/models/{modelId}` 与完整 Model 对象。

### PUT /api/providers/{providerId}/models/{modelId}

- 全字段替换（无密钥类例外）；`modelId` 变更后仍须提供商内唯一，冲突返回 `409 model_id_conflict`。
- 成功返回 `200 OK` 与更新后 Model。
- 父提供商不存在返回 `404 provider_not_found`；模型不存在或不属于该提供商返回 `404 model_not_found`。

### DELETE /api/providers/{providerId}/models/{modelId}

- 成功返回 `204 No Content`；若为默认模型，`isDefault` 清除。
- 404 语义同 PUT。

### GET /api/providers/{providerId}/models

- 返回 `200 OK` 裸数组，按创建时间升序。
- 父提供商不存在返回 `404 provider_not_found`。

## 6. 与翻译运行的关系

### 6.1 三档解析

创建翻译运行（《AI 翻译接口约定》§4）请求新增可选字段 `providerId`/`modelId`：

| 请求 | 解析结果 |
|---|---|
| 都缺省 | 默认对（默认提供商 + 其默认模型） |
| 只传 `providerId` | 该提供商默认模型 |
| 都传 | 精确指定（`modelId` 必须是该提供商的模型条目） |

只传 `modelId` 不属于合法选择形状，返回 `400 invalid_model_selection`。

解析在创建请求的事务内完成：解析成功才创建运行；运行资源顶层回显 `providerId`/`modelId`（恒为解析后的实际选择、非空，仅 ID 不回显名称）。运行内失败重试沿用同一选择。

### 6.2 解析失败映射

| 场景 | HTTP | code |
|---|---|---|
| 只传 `modelId`，未传 `providerId` | 400 | `invalid_model_selection` |
| 都缺省且无默认对（无默认提供商或默认提供商无默认模型） | 503 | `llm_not_configured` |
| 默认对中的提供商被停用 | 503 | `llm_not_configured` |
| 选中的提供商/模型未配置完整（如 `openai` kind 缺密钥、`baseUrl` 为空） | 503 | `llm_not_configured` |
| 显式 `providerId` 不存在 | 404 | `provider_not_found` |
| 显式 `modelId` 不存在、不属于该提供商，或只传 `providerId` 而该提供商无默认模型 | 422 | `model_not_found` |
| 显式选择已停用的提供商 | 422 | `provider_disabled` |

`503 llm_not_configured` 不创建运行，任务标记为 `failed`，修复配置后可重新触发；`400`/`404`/`422` 不创建运行、不改任务状态。解析失败详情（如缺失的配置项）放在 Problem Details 的 `errors` 扩展中。

### 6.3 运行期间的配置变更

- 运行创建后修改配置不影响该运行已解析的选择；批处理按批从当前 DB 状态构造客户端，不做缓存。
- 运行期间删除或停用所选提供商/模型：后续批构造客户端失败 → 分段失败码 `llm_model_unavailable`（retryable=false），运行按既有部分失败语义结束。
- 配置修改不触发运行回滚或重新解析。

## 7. 错误码

请求失败统一返回 `application/problem+json`（《鉴权与基础接口约定》），本文增加稳定 `code`；字段级信息放在 `errors` 扩展。Token 问题仍按基础文档返回 `401`。

| HTTP | `code` | 场景 |
|---|---|---|
| 400 | `invalid_model_selection` | 创建运行时只传 `modelId`，没有用于确定所属关系的 `providerId` |
| 400 | `invalid_provider_name` | `name` 缺失或纯空白 |
| 400 | `invalid_provider_kind` | `kind` 不是 `openai`/`ollama` |
| 400 | `invalid_base_url` | `baseUrl` 提供但不是绝对 http(s) URL |
| 400 | `invalid_runtime_settings` | `batchSize`/`requestTimeout`/`maxAttempts` 越界，errors 附字段与合法范围 |
| 400 | `invalid_provider_key_field` | 请求携带保留响应字段 `apiKeyMasked` |
| 404 | `provider_not_found` | 提供商不存在（路径资源或运行解析中的显式选择） |
| 404 | `model_not_found` | 模型条目不存在或不属于路径中的提供商（配置 CRUD 路径） |
| 409 | `model_id_conflict` | 同一提供商内 `modelId` 重复 |
| 422 | `model_not_found` | 运行解析：显式 `modelId` 不存在/不属于该提供商，或只传 `providerId` 而无默认模型（§6.2） |
| 422 | `provider_disabled` | 运行解析：显式选择已停用提供商（§6.2） |
| 503 | `llm_not_configured` | 运行解析：选中的提供商/模型未配置完整（§6.2；配置管理接口本身不产生此码） |

注：`model_not_found` 同时用于配置 CRUD 的 `404`（路径资源不存在）与运行解析的 `422`（选择不可解析），二者以 HTTP 状态与所属接口区分；前端错误映射需按接口上下文区分。

## 8. 与既有及后续契约的关系

| 相关文档/任务 | 本文约束 |
|---|---|
| 鉴权与基础接口约定 | 沿用 Token 鉴权与 Problem Details 格式 |
| AI 翻译接口约定 | 本文是其 §3 配置模型的完整定义；§3.1 三档解析、§10.1 错误码与本文 §6 一致；不改变运行/进度/写入语义 |
| ADR-0003 | 二级配置、三档解析、能力作元数据、M.E.AI 执行层迁移的落实；密钥掩码、删除级联与运行参数归属为本文补充 |
| 前端 AI 模型配置界面 | 设置页与翻译面板选择器经 `GET /api/providers` 一次加载两级配置；密钥编辑按 §2.3 回写语义（不碰即保持）；错误文案按 code 映射，未配置默认对时提示影响翻译 |

本文不定义 AI 审核意见、术语表、TM 等后续资源；后续文档可复用 Provider/Model 概念或新增资源，不得改变本文字段语义、掩码约定、默认对不变量与删除级联行为。
