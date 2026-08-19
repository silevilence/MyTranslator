# AI 审核接口约定

本文定义 AI 二轮审查对 Web 端、桌面端和第三方系统公开的 REST 接口。接口覆盖创建独立审核运行、查询运行进度与历史、读取分段级失败，以及通过既有分段响应读取当前有效的结构化审核意见。

鉴权、基础地址和通用 Problem Details 格式以[《鉴权与基础接口约定》](鉴权与基础接口约定.md)为准。本文所有接口均要求 Bearer Token。任务、分段、提取修订、占位符和只读标记表的字段语义以[《文件导入拆解与导出接口约定》](文件导入拆解与导出接口约定.md)及 [ADR-0002](../adr/0002-标记提取模型.md)为准；提供商/模型的配置与三档解析以[《AI 配置管理接口约定》](AI%20配置管理接口约定.md)为准。

## 1. 范围与接口原则

- 审核运行（Review Run）是任务上的独立异步运行资源。调用方创建运行后通过 REST 轮询，不保持长连接，也不存在内部前端专用接口。
- 每次运行在创建时固定选择当前提取修订中全部具有合法非空白 `targetText` 的分段；`translated`、`confirmed` 和人工编辑过的译文一视同仁。
- 审核只产生 AI 审核意见，不修改 `sourceText`、`targetText`、确认状态、分段 `version` 或任务公共状态。
- 审核意见是只读的当前有效结果，不提供独立查询、编辑、一键应用或删除接口；调用方通过既有分段接口读取 `reviewComments`。
- 成功分段逐批提交意见。新运行成功审过的分段整体替换旧意见；失败或尚未处理的分段保留旧意见。
- 同一任务同一时刻只允许一个活动运行。审核与翻译、人工保存、导出、重新提取及另一审核互斥；读取任务、运行和分段不受影响。
- TM 审核上下文仅作预留。没有 TM 数据或 TM 功能尚未实现时上下文为空，审核仍须正常执行。

## 2. 通用约定

### 2.1 标识、时间、枚举与语言标签

- `taskId`、`runId`、`segmentId`、`providerId`、`modelId` 均为 UUID 字符串。
- 时间使用 UTC ISO 8601，例如 `2026-08-19T08:30:00Z`。
- 枚举值使用本文列出的英文小写字符串；调用方必须容忍未来新增未知枚举值。
- JSON 字段使用 `camelCase`，请求和响应使用 UTF-8。
- `sourceLanguage` 和 `targetLanguage` 使用 BCP 47 语言标签，例如 `en`、`zh-CN`、`pt-BR`；服务端可规范化大小写后回显。
- `sourceLanguage = null` 表示由 LLM 在审核时自动识别源语言；`targetLanguage` 必填且不得为空。

### 2.2 任务公共状态不变

审核运行不改变任务的 `created` / `processing` / `completed` / `failed` 公共状态。该规则覆盖：

- 创建校验失败或配置不可用；
- 运行排队、执行和逐批提交意见；
- 运行完成、部分失败、全部失败或服务中断。

任务公共状态中的 `processing` 仅表示翻译运行正在执行；审核状态必须通过本文的审核运行资源读取。任务处于 `created` 或 `failed` 时，只要当前提取修订至少有一个合法非空白译文且没有互斥操作，也允许创建审核运行。

### 2.3 审核运行状态

`review run.status` 使用独立状态枚举：

| 值 | 是否终态 | 含义 |
|---|---|---|
| `queued` | 否 | 已创建，等待后台执行 |
| `processing` | 否 | 正在调用 LLM、校验并提交意见 |
| `completed` | 是 | 本次选中的全部分段审核成功，包括返回 0 条意见的分段 |
| `partial_failed` | 是 | 至少一个分段审核成功且至少一个分段失败 |
| `failed` | 是 | 本次选中的分段全部失败，或运行因服务中断而终止 |

终态不可回退。失败后重试创建新的 `runId`，不复用或修改旧运行。

### 2.4 提取修订、选择快照与互斥

- 创建运行必须携带调用方读取到的 `extractionRevision`。
- 后端在同一事务内校验提取修订、取得任务互斥锁、固定选择分段并创建运行；修订不一致返回 `409 extraction_revision_changed`。
- 选择快照中的 `segmentId`、`sourceText` 和 `targetText` 在本次运行内固定。活动审核期间人工保存和翻译均被互斥锁阻止。
- 活动翻译、审核、导出、重新提取或人工保存占用任务写锁时，创建审核返回 `409 task_busy`。
- 审核活动期间，翻译、人工保存、导出、应用重新提取及另一审核同样返回 `409 task_busy`。
- 应用重新提取后，旧提取修订的审核运行、分段级失败、审核意见和分页游标一并删除；按旧 `runId` 查询返回 `404 review_run_not_found`。

### 2.5 游标分页

审核运行列表和分段级失败列表使用游标分页：

- `limit` 默认 `100`，范围 `1..200`。
- `cursor` 是不透明字符串，调用方不得解析或构造。
- `nextCursor = null` 表示末页。
- 游标只对产生它的任务、运行和提取修订有效；无法解析或不属于当前资源时返回 `400 invalid_cursor`。
- 重新提取后使用旧游标返回 `409 extraction_revision_changed`，调用方必须从第一页重新加载。

## 3. AI 配置与审核上下文

### 3.1 提供商/模型三档解析

创建审核运行的 `providerId` / `modelId` 按《AI 配置管理接口约定》的三档规则解析：

| 请求 | 解析结果 |
|---|---|
| 都缺省 | 默认对（默认提供商 + 其默认模型） |
| 只传 `providerId` | 该提供商的默认模型 |
| 都传 | 精确指定，`modelId` 必须属于该提供商 |

只传 `modelId` 不属于合法选择形状，返回 `400 invalid_model_selection`。解析成功后，运行资源回显实际使用且恒非空的 `providerId` / `modelId`；运行内重试沿用同一选择。

配置不可用的 HTTP 状态和错误码复用《AI 配置管理接口约定》：`provider_not_found`、`model_not_found`、`provider_disabled`、`llm_not_configured`。与翻译运行不同，任何配置失败均不得修改任务公共状态。

运行期间删除或停用所选提供商/模型时，后续批次记录 `llm_model_unavailable`，运行按部分失败规则结束。密钥、BaseUrl、提示词、供应商原始响应和内部异常不得出现在运行或失败资源中。

### 3.2 术语快照

- `sourceLanguage` 非 `null` 时，创建运行在同一数据库快照中读取与 `sourceLanguage` + `targetLanguage` 完全匹配的术语，并固定源术语、规定译法和大小写规则。
- 本次运行的全部批次使用同一术语快照。运行创建后新增、修改或删除术语只影响下一次审核运行。
- 每个分段按《术语接口约定》§9.2 的确定性匹配规则查找源文命中项；仅把命中术语及规定译法注入该分段的审核上下文。
- `sourceLanguage = null` 时术语快照为空，不执行术语注入；LLM 自动识别语言后继续完成其他审核维度。
- 术语不存在或没有命中项不是错误，审核继续执行。

### 3.3 TM 预留与审核维度

TM 匹配结果由服务端构造，不属于创建请求字段。TM 未实现、未配置或没有匹配时使用空上下文，不返回错误。

提示词至少要求检查以下维度：

1. 忠实度：误译、漏译、增译；
2. 术语对齐：是否使用注入术语的规定译法；
3. 语言自然度与风格；
4. 占位符保护：是否新增、删除、改写或错误移动 `<xN>` / `</xN>` / `<xN/>`。

## 4. 创建审核运行

### POST /api/tasks/{taskId}/review-runs

请求字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `extractionRevision` | integer | 是 | 当前提取修订，必须大于等于 `1` |
| `sourceLanguage` | string/null | 否 | BCP 47；缺省或 `null` 表示自动识别，且不注入术语 |
| `targetLanguage` | string | 是 | 目标语言 BCP 47 标签 |
| `providerId` | UUID/null | 否 | 指定 AI 提供商；缺省或 `null` 走默认对 |
| `modelId` | UUID/null | 否 | 指定 AI 模型；只传 `providerId` 时使用该提供商默认模型 |

请求体：

```json
{
  "extractionRevision": 1,
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "providerId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "modelId": "f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b"
}
```

示例：

```bash
curl -X POST http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000" \
  -H "Content-Type: application/json" \
  -d '{"extractionRevision":1,"sourceLanguage":"en","targetLanguage":"zh-CN"}'
```

成功返回 `202 Accepted`。任务公共状态保持原值：

```http
Location: /api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs/8060fd5f-4d8e-4149-b973-c828b77ff9d8
Retry-After: 1
```

```json
{
  "runId": "8060fd5f-4d8e-4149-b973-c828b77ff9d8",
  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
  "extractionRevision": 1,
  "status": "queued",
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "providerId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "modelId": "f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b",
  "selection": {
    "totalSegments": 42,
    "selectedSegments": 40,
    "skippedUntranslatedSegments": 2
  },
  "progress": {
    "processedSegments": 0,
    "succeededSegments": 0,
    "failedSegments": 0,
    "percent": 0.0
  },
  "failure": null,
  "createdAt": "2026-08-19T08:30:00Z",
  "startedAt": null,
  "finishedAt": null
}
```

运行资源字段约束：

| 字段 | 约束 |
|---|---|
| `selection.totalSegments` | 当前提取修订内全部可译分段数 |
| `selection.selectedSegments` | 创建时 `targetText` 非 `null` 且非空白的分段数；本次固定工作量 |
| `selection.skippedUntranslatedSegments` | 创建时 `targetText = null` 的分段数 |
| `progress.processedSegments` | 已进入成功或失败终态的选中分段数 |
| `progress.succeededSegments` | 已成功校验并提交本轮意见的分段数；返回 0 条意见也计为成功 |
| `progress.failedSegments` | 已耗尽重试或发生不可重试失败的分段数 |
| `progress.percent` | `processedSegments / selectedSegments * 100`，保留一位小数且不得下降 |
| `failure` | 活动或成功运行时为 `null`；失败摘要见 §11.2 |

创建边界行为：

- 分段按 `order` 升序选择和处理；服务端批大小不构成公开契约。
- 任务没有任何非空白译文时返回 `409 no_segments_to_review`，不创建运行，也不修改任务公共状态。
- `targetText = ""` 或纯空白违反公共分段契约；发现任一异常时返回 `422 invalid_segment_state`，不得把它当作未翻译跳过。
- `sourceLanguage` 与 `targetLanguage` 规范化后相同，或提供商明确拒绝该语言组合时，返回 `422 unsupported_language_pair`。
- 请求进行中前端必须禁用审核按钮；后端仍以 `409 task_busy` 防止重复提交。
- 创建成功只表示运行已可靠入队，不表示任何分段已经完成审核。

## 5. 查询审核运行

### GET /api/tasks/{taskId}/review-runs/{runId}

```bash
curl http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs/8060fd5f-4d8e-4149-b973-c828b77ff9d8 \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK` 和与创建响应相同形状的当前快照。活动运行同时返回 `Retry-After`；调用方必须遵守该值，未返回时轮询间隔不得短于 1 秒。

```json
{
  "runId": "8060fd5f-4d8e-4149-b973-c828b77ff9d8",
  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
  "extractionRevision": 1,
  "status": "processing",
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "providerId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "modelId": "f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b",
  "selection": {
    "totalSegments": 42,
    "selectedSegments": 40,
    "skippedUntranslatedSegments": 2
  },
  "progress": {
    "processedSegments": 24,
    "succeededSegments": 23,
    "failedSegments": 1,
    "percent": 60.0
  },
  "failure": null,
  "createdAt": "2026-08-19T08:30:00Z",
  "startedAt": "2026-08-19T08:30:01Z",
  "finishedAt": null
}
```

- `runId` 不存在、不属于路径任务或已因重新提取失效时返回 `404 review_run_not_found`。
- `progress.succeededSegments` 增加后，调用方可重新读取已加载的分段页，使逐批提交的新意见出现在界面中。
- 运行响应不内嵌分段或意见，避免维护第二套意见载体。

## 6. 列出审核运行

### GET /api/tasks/{taskId}/review-runs?limit={limit}&cursor={opaqueCursor}

运行按 `createdAt`、`runId` 降序返回。该接口用于页面刷新后恢复最新运行，以及第三方系统读取当前提取修订的审核历史。

```bash
curl "http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs?limit=20" \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK`：

```json
{
  "extractionRevision": 1,
  "items": [
    {
      "runId": "8060fd5f-4d8e-4149-b973-c828b77ff9d8",
      "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
      "extractionRevision": 1,
      "status": "partial_failed",
      "sourceLanguage": "en",
      "targetLanguage": "zh-CN",
      "providerId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
      "modelId": "f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b",
      "selection": {
        "totalSegments": 42,
        "selectedSegments": 40,
        "skippedUntranslatedSegments": 2
      },
      "progress": {
        "processedSegments": 40,
        "succeededSegments": 39,
        "failedSegments": 1,
        "percent": 100.0
      },
      "failure": {
        "code": "llm_response_invalid",
        "retryable": true,
        "failedSegments": 1
      },
      "createdAt": "2026-08-19T08:30:00Z",
      "startedAt": "2026-08-19T08:30:01Z",
      "finishedAt": "2026-08-19T08:31:20Z"
    }
  ],
  "nextCursor": null
}
```

- 列表项与详情使用相同字段形状。
- 只返回任务当前提取修订的运行；没有运行时返回空 `items` 和 `nextCursor = null`。
- 页面刷新后取第一页第一项作为最新运行；不得依赖仅存在于内存的状态。

## 7. 查询分段级失败

### GET /api/tasks/{taskId}/review-runs/{runId}/failures?limit={limit}&cursor={opaqueCursor}

结果按分段 `order` 升序排列。活动运行期间失败项可能继续增加；终态后可读取完整列表。

```bash
curl "http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs/8060fd5f-4d8e-4149-b973-c828b77ff9d8/failures?limit=100" \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK`：

```json
{
  "runId": "8060fd5f-4d8e-4149-b973-c828b77ff9d8",
  "items": [
    {
      "segmentId": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
      "segmentOrder": 17,
      "code": "llm_response_invalid",
      "retryable": true,
      "attempts": 3
    }
  ],
  "nextCursor": null
}
```

| 字段 | 约束 |
|---|---|
| `segmentId` | 运行创建时选择的当前提取修订分段 ID |
| `segmentOrder` | 运行创建时的分段顺序 |
| `code` | §11.2 的稳定异步失败码 |
| `retryable` | 重新创建完整审核运行前是否值得重试；不保证下一次成功 |
| `attempts` | 本次运行对所属批次实际发起的 LLM 请求次数 |

供应商响应正文、密钥、BaseUrl、提示词、源文、译文和内部堆栈不得出现在失败资源中。

## 8. 审核执行与结构化输出

### 8.1 LLM 输入与占位符

每批输入由服务端构造，包含语言要求和 `(segmentId, sourceText, targetText)`：

- `sourceText` 与 `targetText` 中的 `<xN>`、`</xN>`、`<xN/>` 作为透明引用随文本发送。
- `markupTable` 的 `openingText`、`closingText`、`originalText`、`meaning` 等原始内容不发送给 LLM。
- 提示词声明占位符不可翻译、改写、删除、新增或错误移动，并要求把发现的问题作为审核意见返回。
- 提示词必须显式声明每个分段最多返回 20 条意见；模型应优先保留严重度更高、内容不重复的意见。超过上限的响应仍按 §8.2 视为 `llm_response_invalid`，不得由后端静默截断。
- 审核意见只读，不直接写入 `targetText`；因此占位符问题是普通审核意见，不产生翻译专属的 `placeholder_integrity_violation` 失败码。

### 8.2 LLM 输出与意见校验

LLM 必须按分段 ID 返回结构化结果；请求批次中的每个分段恰好出现一次，不得缺少、重复或新增 ID。每个分段返回 `comments` 数组，空数组表示审核成功且没有意见。

单条意见的公开形状：

```json
{
  "severity": "high",
  "issue": "术语译法与规定译法不一致",
  "suggestion": "将“translation memory”统一译为“翻译记忆库”"
}
```

| 字段 | 类型 | 约束 |
|---|---|---|
| `severity` | string | `high` / `medium` / `low`；缺失、空白或未知值兜底为 `medium` |
| `issue` | string | 必填；去除首尾空白后为 `1..2000` 个 Unicode 标量值 |
| `suggestion` | string/null | 缺失、`null` 或纯空白统一为 `null`；非空时去除首尾空白，最多 4000 个 Unicode 标量值 |

- 每个分段最多返回 20 条意见。
- 意见超过数量上限、`issue` 无效、`suggestion` 超长或批次 ID 集合不匹配时，不得静默截断或提交；记录为 `llm_response_invalid` 并进入重试。
- 服务端按 `high` → `medium` → `low` 排序；相同严重度保持 LLM 返回顺序。该顺序即分段响应中的数组顺序。
- 公开意见不包含 `commentId`、`reviewRunId`、时间或内部排序字段；v1 没有单条意见寻址行为。

### 8.3 原子替换与逐批可见

- 创建新运行时不清除任何旧意见。
- 某分段本轮输出校验成功后，在一个事务中删除该分段全部当前意见并写入本轮数组；0 条意见表示只删除旧意见。
- 事务提交后立即更新运行进度。调用方重新读取分段时必须看到整组新意见，不得看到一半新、一半旧。
- 本轮失败或尚未处理的分段保留旧意见。部分失败运行结束后，不同分段的当前有效意见可能来自不同轮次；失败端点用于识别本轮未成功更新的分段。
- 审核意见不绑定分段 `version`。运行结束后的人工编辑、保存、确认或取消确认均不清除意见。

### 8.4 自动重试与部分失败

- 网络故障、超时、HTTP `429`、供应商 `5xx` 和结构化响应无效按提供商配置的 `maxAttempts` 重试；重试使用指数退避和随机抖动。
- 结构或分段 ID 集合整体无效时重试整批；结构可按分段独立校验时，只重试未成功提交的分段。
- 已成功替换意见的分段不得在同一运行中重复审核。
- 某批耗尽尝试后记录分段级失败，继续处理其他批次；部分失败不回滚已提交意见。
- 服务进程中断时，把未进入终态的运行标记为 `failed`、失败码 `review_interrupted`；已提交意见和旧意见保留。
- 用户重试时再次调用创建端点。新运行重新选择当时全部已有非空白译文的分段，不提供只重试失败分段的专用端点。

## 9. 分段响应中的审核意见

### GET /api/tasks/{taskId}/segments

《文件导入拆解与导出接口约定》§6 的每个正式分段响应新增 `reviewComments` 数组：

```json
{
  "extractionRevision": 1,
  "totalCount": 42,
  "items": [
    {
      "id": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
      "order": 17,
      "sourceText": "Use translation memory.",
      "targetText": "使用翻译缓存。",
      "confirmationStatus": "translated",
      "version": 4,
      "markupTable": [],
      "chapter": null,
      "reviewComments": [
        {
          "severity": "high",
          "issue": "术语译法与规定译法不一致",
          "suggestion": "将“翻译缓存”改为“翻译记忆库”"
        }
      ]
    }
  ],
  "nextCursor": null
}
```

- 正式分段响应始终包含 `reviewComments`；没有当前意见时返回 `[]`，不得返回 `null` 或省略字段。
- 调用方必须容忍尚未升级的兼容服务省略该新增字段，并按 `[]` 处理；服务端按本文实现后必须始终输出。
- 该字段只返回当前有效意见，不返回历史意见。数组顺序遵守 §8.2。
- 提取预览分段不是正式分段，`reviewComments` 固定为 `[]`；预览 ID 不继承正式意见。
- 不新增独立意见查询接口。分段游标、`version`、标记表和其他字段语义保持不变。

## 10. 状态流转、进度与意见生命周期

| 事件 | 运行状态 | 任务公共状态 | 审核意见 |
|---|---|---|---|
| 创建成功 | `queued` | 不变 | 旧意见保留 |
| 后台开始 | `processing` | 不变 | 成功分段逐批原子替换 |
| 全部成功 | `completed` | 不变 | 所有选中分段均为本轮结果 |
| 部分失败 | `partial_failed` | 不变 | 成功分段替换，失败分段保留旧意见 |
| 全部失败或中断 | `failed` | 不变 | 已成功提交结果保留，其余旧意见保留 |
| 人工编辑/确认 | 无运行变化 | 按人工保存契约处理 | 当前意见保留，不绑定新 `version` |
| 应用重新提取 | 旧运行删除 | 重置为 `created` | 旧分段意见级联删除 |

运行进入终态时：

- `progress.processedSegments = selection.selectedSegments`；
- `progress.succeededSegments + progress.failedSegments = selection.selectedSegments`；
- `progress.percent = 100.0`，即使状态为失败；
- `finishedAt` 非空；`queued` 时 `startedAt = null`，已开始的运行 `startedAt` 非空。

审核意见随分段级联删除。重新提取删除旧运行、失败记录和意见后，新分段使用新 ID 且 `reviewComments = []`。

## 11. 错误码与异步失败码

### 11.1 请求错误

请求失败统一返回 `application/problem+json`。本文沿用基础文档并增加稳定 `code`；字段级信息放在 `errors` 扩展中。前端按 `code` 映射文案，`title` 和 `detail` 只用于诊断。

```json
{
  "type": "https://mytranslator.local/problems/no-segments-to-review",
  "title": "No segments to review",
  "status": 409,
  "code": "no_segments_to_review",
  "instance": "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs"
}
```

| HTTP | `code` | 场景 |
|---|---|---|
| 400 | `invalid_language_tag` | 语言字段缺失或不是合法 BCP 47 标签 |
| 400 | `invalid_extraction_revision` | `extractionRevision` 缺失、类型错误或小于 `1` |
| 400 | `invalid_model_selection` | 只传 `modelId`，没有用于确定所属关系的 `providerId` |
| 400 | `invalid_cursor` | 游标不可解析或不属于当前资源 |
| 400 | `invalid_pagination` | `limit` 不在 `1..200` 范围内 |
| 404 | `task_not_found` | 任务不存在 |
| 404 | `review_run_not_found` | 运行不存在、不属于该任务或已因重新提取失效 |
| 404 | `provider_not_found` | 显式 `providerId` 不存在 |
| 409 | `task_busy` | 任务存在活动翻译、审核或其他互斥操作 |
| 409 | `extraction_revision_changed` | 请求或游标绑定的提取修订不是当前修订 |
| 409 | `no_segments_to_review` | 当前修订没有合法非空白译文 |
| 422 | `model_not_found` | 模型不存在、不属于提供商，或显式提供商没有默认模型 |
| 422 | `provider_disabled` | 显式选择的提供商已停用 |
| 422 | `invalid_segment_state` | 存在空字符串或纯空白 `targetText` 等非法分段状态 |
| 422 | `unsupported_language_pair` | 源/目标语言相同，或提供商明确不支持该组合 |
| 503 | `llm_not_configured` | 选中的提供商/模型未配置完整；不创建运行且不改变任务公共状态 |

Token 问题仍按基础文档返回 `401`；已认证无权时返回 `403`；未处理的服务端错误返回 `500`，且不得暴露内部细节。

### 11.2 运行与分段级失败

终态为 `partial_failed` 或 `failed` 时，`failure` 使用：

```json
{
  "code": "segment_review_failed",
  "retryable": true,
  "failedSegments": 3
}
```

失败分段包含多个原因时，摘要 `code` 使用 `segment_review_failed`，具体原因从 §7 读取。

| `code` | `retryable` 默认值 | 含义 |
|---|---|---|
| `segment_review_failed` | `true` | 多个分段因不同原因失败，需读取失败列表 |
| `llm_provider_unavailable` | `true` | 供应商限流、网络故障或暂时不可用 |
| `llm_provider_timeout` | `true` | LLM 请求耗尽超时和重试次数 |
| `llm_authentication_failed` | `false` | 配置密钥被供应商拒绝 |
| `llm_model_unavailable` | `false` | 模型不存在、无权访问、被停用或运行期间配置被删除 |
| `llm_response_invalid` | `true` | 响应无法解析、ID 集合不匹配或意见结构/数量/长度无效 |
| `unsupported_language_pair` | `false` | 供应商在运行期间明确拒绝该语言组合 |
| `review_interrupted` | `true` | 服务进程中断导致运行未正常结束 |

审核不使用 `segment_translation_failed`、`translation_interrupted` 或 `placeholder_integrity_violation`。占位符问题作为只读审核意见返回。

## 12. 前端与第三方调用流程

### 12.1 Web/桌面端

1. 读取任务摘要取得 `extractionRevision`；读取提供商列表，复用翻译操作的提供商/模型选择器及配置异常提示。
2. 用户触发审核时调用创建端点。审核按钮进入 loading，翻译按钮及其他互斥写操作禁用。
3. 保存 `runId`，按 `Retry-After` 轮询详情；以服务端 `progress.percent`、计数和 `status` 显示进度条、百分比和阶段文案。
4. `succeededSegments` 增加后刷新已加载分段，使逐批意见出现；不得根据本地计时推断进度。
5. `completed` 时刷新分段；`partial_failed` / `failed` 时读取失败列表，按 `code` 显示明确提示和重试入口。重试创建完整新运行；创建被 `llm_not_configured`、`provider_not_found`、`model_not_found` 或 `provider_disabled` 拒绝时显示配置异常，并提供“前往设置”入口。
6. 页面刷新后读取运行列表第一页并恢复最新运行；分段意见始终以 API 为准。
7. 有意见的分段显示 info 语义色的“颜色 + 图标 + N 条意见”三通道徽标，可与规则违规徽标共存；无意见分段不显示徽标。
8. `ReviewPanel` 按响应顺序只读展示严重度、问题描述和修改建议；`suggestion = null` 显示“无建议”，空数组显示“暂无审核意见”，不提供一键应用。

### 12.2 第三方系统

1. 读取任务与分段，取得当前 `extractionRevision` 并确认至少有一个译文。
2. 创建审核运行，保存响应 `runId` 或 `Location`。
3. 按 `Retry-After` 轮询运行到终态；失败时分页读取分段级失败。
4. 分页读取 `GET /api/tasks/{taskId}/segments`，从每段 `reviewComments` 取得当前有效意见。
5. 需要重试时重新创建完整审核运行；不得尝试修改或删除单条意见。

## 13. 与既有及后续契约的关系

| 相关文档/任务 | 本文约束 |
|---|---|
| 鉴权与基础接口约定 | 沿用 Bearer Token、`401` / `403` 语义和 Problem Details 格式 |
| 文件导入拆解与导出 | 复用任务、分段、提取修订、游标和占位符语义；仅扩展正式分段的 `reviewComments`；审核不改变任务公共状态 |
| AI 翻译接口约定 | 复用运行状态、进度、轮询、批处理重试和部分失败模式；不得改变翻译运行或任务状态语义 |
| AI 配置管理接口约定 | 复用 Provider/Model、默认对、三档解析、配置可用性和密钥保护语义 |
| 术语接口约定 | 复用语言对及确定性源术语匹配；本次运行使用创建时术语快照，不改变术语资源 |
| ADR-0002 | LLM 只接触文本中的透明占位符引用，不接触标记表原文；审核意见不得改变占位符或回填语义 |
| 硬性规则检查 | AI 审核意见不是规则违规；info 与 error 语义必须分离，后续规则结果可与意见同时显示 |
| TM 与历史对比 | TM 上下文由服务端后续补充；不得向创建请求增加由调用方提交的 TM 内容或改变本文运行字段 |

本文不改变既有分段字段、`targetText = null` 的未翻译语义、确认状态、乐观并发版本、占位符语法、标记表只读性、受保护块不可编辑性或重新提取替换分段 ID 的语义。后续扩展只能新增可选字段或新资源，不得改变本文已定义的运行状态、进度、意见替换和任务公共状态不变约定。
