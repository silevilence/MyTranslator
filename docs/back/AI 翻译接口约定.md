# AI 翻译接口约定

本文定义 AI 全自动翻译模块对 Web、桌面端和第三方系统公开的 REST 接口。接口覆盖触发分段批量翻译、查询运行进度、读取历史运行与分段级失败，并固化 LLM 配置、占位符保护、失败重试和部分失败后的状态语义。

鉴权、基础地址和通用 Problem Details 格式以[《鉴权与基础接口约定》](鉴权与基础接口约定.md)为准。本文所有接口均要求 Bearer Token。分段、提取修订、占位符和只读标记表的字段语义以[《文件导入拆解与导出接口约定》](文件导入拆解与导出接口约定.md)及 [ADR-0002](../adr/0002-标记提取模型.md)为准。

## 1. 范围与接口原则

- AI 翻译是任务上的异步运行资源。调用方创建翻译运行后通过 REST 轮询进度，不保持长连接，也不使用仅供内部前端调用的接口。
- 每次运行只选择 `targetText = null` 的未翻译分段。已有译文和已确认分段不得被 AI 覆盖。
- LLM 的 BaseUrl、Key 和模型由服务端配置；请求不得提交供应商密钥，响应也不得暴露密钥或供应商原始响应。
- 分段成功获得 AI 译文后，`targetText` 写入非空白译文，`confirmationStatus` 自动变为 `translated`，`version` 递增。
- 成功分段逐批持久化并可通过既有分段接口读取。部分失败不回滚已经成功的译文。
- 同一任务同一时刻只允许一个活动翻译运行，且翻译与重新提取、导出及其他会改变提取修订或译文的操作互斥；读取任务、运行和分段不受影响。
- 本文不定义人工译文保存、任务列表或 LLM 配置管理 API；相关接口由后续文档补充，且不得改变本文的运行、进度和写入语义。

## 2. 通用约定

### 2.1 标识、时间、枚举与语言标签

- `taskId`、`runId`、`segmentId` 均为 UUID 字符串。
- 时间使用 UTC ISO 8601，例如 `2026-08-14T08:30:00Z`。
- 枚举值使用本文列出的英文小写字符串；调用方必须容忍未来新增未知枚举值。
- JSON 字段使用 `camelCase`，请求和响应使用 UTF-8。
- `sourceLanguage` 和 `targetLanguage` 使用 BCP 47 语言标签，例如 `en`、`zh-CN`、`pt-BR`。服务端可规范化大小写后在响应中回显。
- `sourceLanguage = null` 表示由 LLM 自动识别源语言；`targetLanguage` 必填且不得为空。

### 2.2 任务公共状态

翻译运行对任务公共状态的影响如下：

| 值 | 含义 |
|---|---|
| `created` | 导入完成，尚未开始本次翻译 |
| `processing` | 翻译运行已创建且尚未进入终态 |
| `completed` | 当前提取修订内所有分段均已有非空白译文 |
| `failed` | 翻译运行全部或部分失败；已成功译文仍然保留 |

任务的人工确认状态不参与 AI 翻译运行是否完成的判断。`completed` 只表示每个分段已有译文，不表示每个分段均已人工确认。

### 2.3 翻译运行状态

`translation run.status` 使用独立于任务状态的枚举：

| 值 | 是否终态 | 含义 |
|---|---|---|
| `queued` | 否 | 已创建，等待后台执行 |
| `processing` | 否 | 正在调用 LLM、校验并保存译文 |
| `completed` | 是 | 本次选中的全部分段翻译成功 |
| `partial_failed` | 是 | 至少一个分段成功且至少一个分段失败 |
| `failed` | 是 | 本次选中的分段全部失败，或运行在处理前被中断 |

终态不可回退。对失败任务再次触发会创建新的 `runId`，不会复用或修改旧运行。

### 2.4 提取修订与互斥

- 创建运行时请求必须携带调用方读取到的 `extractionRevision`。
- 后端必须在选取分段和创建运行的同一事务内比对当前提取修订；不一致返回 `409 extraction_revision_changed`。
- 运行始终绑定创建时的提取修订，响应中的 `extractionRevision` 不会变化。
- 活动运行期间，另一次翻译、重新提取、导出或其他会改变分段/译文的操作返回 `409 task_busy`。
- 应用重新提取后，旧提取修订的翻译运行、分段级失败和分页游标失效并删除；按旧 `runId` 查询返回 `404 translation_run_not_found`。

### 2.5 分页

运行列表和分段级失败列表使用游标分页：

- `limit` 默认 `100`，范围 `1..200`。
- `cursor` 是不透明字符串，调用方不得解析或构造。
- `nextCursor = null` 表示末页。
- 游标只对产生它的任务、运行和提取修订有效；无法解析或不属于当前资源时返回 `400 invalid_cursor`。
- 重新提取后使用旧游标返回 `409 extraction_revision_changed`，调用方应从第一页重新加载。

## 3. 服务端 LLM 配置约定

部署方必须为 AI 翻译配置一个可插拔 LLM 提供程序，并至少提供：

| 配置项 | 必填 | 说明 |
|---|---|---|
| `Provider` | 是 | 服务端注册的提供程序适配器名称；不得由请求临时覆盖 |
| `BaseUrl` | 是 | LLM API 基础地址 |
| `ApiKey` | 是 | LLM API 密钥；只能从服务端配置或密钥存储读取 |
| `Model` | 是 | 翻译使用的模型名称 |
| `BatchSize` | 否 | 每批分段数，由服务端按模型限制配置 |
| `RequestTimeout` | 否 | 单次 LLM 请求超时 |
| `MaxAttempts` | 否 | 每批最大尝试次数，默认 `3`，包含首次请求 |

约束：

- 配置未完成或无法通过本地格式校验时，应用其他功能仍可使用；通过任务、修订和语言参数校验后的创建请求返回 `503 llm_not_configured`，不创建运行，并把任务标记为 `failed`。修复配置后可对该失败任务重新触发。
- `ApiKey` 不得写入日志、Problem Details、运行资源或分段级失败详情。
- 提供程序适配器负责把统一的批量翻译输入转换为供应商协议。新增供应商不得改变本文公开 REST 契约。
- 本任务不提供远程读取或修改 LLM 配置的 API；配置变更的加载方式由部署实现决定。

## 4. 创建翻译运行

### POST `/api/tasks/{taskId}/translation-runs`

请求字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `extractionRevision` | integer | 是 | 调用方当前看到的提取修订，必须大于等于 `1` |
| `sourceLanguage` | string/null | 否 | BCP 47；缺省或 `null` 表示自动识别 |
| `targetLanguage` | string | 是 | 目标语言 BCP 47 标签 |

请求体：

```json
{
  "extractionRevision": 1,
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN"
}
```

示例：

```bash
curl -X POST http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000" \
  -H "Content-Type: application/json" \
  -d '{"extractionRevision":1,"sourceLanguage":"en","targetLanguage":"zh-CN"}'
```

成功返回 `202 Accepted`，任务状态在响应提交前变为 `processing`：

```http
Location: /api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs/56ccae85-4fea-43d6-a736-644ac9ea2422
Retry-After: 1
```

```json
{
  "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
  "extractionRevision": 1,
  "status": "queued",
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "selection": {
    "totalSegments": 42,
    "selectedSegments": 40,
    "skippedExistingSegments": 2
  },
  "progress": {
    "processedSegments": 0,
    "succeededSegments": 0,
    "failedSegments": 0,
    "percent": 0.0
  },
  "failure": null,
  "createdAt": "2026-08-14T08:30:00Z",
  "startedAt": null,
  "finishedAt": null
}
```

运行资源字段约束：

| 字段 | 约束 |
|---|---|
| `selection.totalSegments` | 当前提取修订内全部可译分段数 |
| `selection.selectedSegments` | 创建时 `targetText = null` 的分段数；本次运行的固定工作量 |
| `selection.skippedExistingSegments` | 创建时已有非空白译文的分段数，包括 `translated` 和 `confirmed` |
| `progress.processedSegments` | 已进入成功或失败终态的本次选中分段数 |
| `progress.succeededSegments` | 已由本次运行成功保存译文的分段数 |
| `progress.failedSegments` | 已耗尽重试或不可重试的分段数 |
| `progress.percent` | `processedSegments / selectedSegments * 100`，保留一位小数；运行期间不得下降 |
| `failure` | 活动或成功运行时为 `null`；终态失败摘要见 §7.2 |

创建边界行为：

- 分段按 `order` 升序选取和批处理；服务端批大小不构成公开契约。
- `targetText = ""` 不是合法的“未翻译”表示；既有数据若出现空白译文，创建运行返回 `422 invalid_segment_state`，不得静默覆盖。
- 没有可选分段时返回 `409 no_segments_to_translate`。若任务所有分段已有译文，任务保持或修正为 `completed`。
- `sourceLanguage` 与 `targetLanguage` 规范化后相同，或语言组合被已配置提供程序明确拒绝时，返回 `422 unsupported_language_pair`。
- 提交按钮必须在请求进行中禁用；即使调用方重复提交，后端仍通过 `409 task_busy` 防止创建并行运行。
- 创建请求只表示后台运行已可靠入队，不表示任何分段已经翻译。

## 5. 查询翻译运行

### GET `/api/tasks/{taskId}/translation-runs/{runId}`

示例：

```bash
curl http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs/56ccae85-4fea-43d6-a736-644ac9ea2422 \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK` 和与创建响应相同形状的当前快照。活动运行同时返回 `Retry-After`：

```http
Retry-After: 1
```

```json
{
  "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
  "extractionRevision": 1,
  "status": "processing",
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "selection": {
    "totalSegments": 42,
    "selectedSegments": 40,
    "skippedExistingSegments": 2
  },
  "progress": {
    "processedSegments": 24,
    "succeededSegments": 23,
    "failedSegments": 1,
    "percent": 60.0
  },
  "failure": null,
  "createdAt": "2026-08-14T08:30:00Z",
  "startedAt": "2026-08-14T08:30:01Z",
  "finishedAt": null
}
```

查询约束：

- 调用方应遵守 `Retry-After`；未返回时活动运行轮询间隔不得短于 1 秒。
- 前端用 `progress.percent`、计数和 `status` 展示进度与阶段文案，不根据本地计时猜测进度。
- 进度更新后，调用方可通过 `GET /api/tasks/{taskId}/segments` 重新读取已成功分段；分段结果不嵌入运行响应。
- `runId` 存在但不属于路径中的 `taskId` 时返回 `404 translation_run_not_found`，不泄露跨任务资源是否存在。

## 6. 列出翻译运行

### GET `/api/tasks/{taskId}/translation-runs?limit={limit}&cursor={opaqueCursor}`

该接口用于页面刷新后恢复最新运行，以及第三方系统查询当前提取修订的运行历史。运行按 `createdAt` 降序返回。

示例：

```bash
curl "http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs?limit=20" \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK`：

```json
{
  "extractionRevision": 1,
  "items": [
    {
      "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
      "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
      "extractionRevision": 1,
      "status": "partial_failed",
      "sourceLanguage": "en",
      "targetLanguage": "zh-CN",
      "selection": {
        "totalSegments": 42,
        "selectedSegments": 40,
        "skippedExistingSegments": 2
      },
      "progress": {
        "processedSegments": 40,
        "succeededSegments": 39,
        "failedSegments": 1,
        "percent": 100.0
      },
      "failure": {
        "code": "segment_translation_failed",
        "retryable": true,
        "failedSegments": 1
      },
      "createdAt": "2026-08-14T08:30:00Z",
      "startedAt": "2026-08-14T08:30:01Z",
      "finishedAt": "2026-08-14T08:31:20Z"
    }
  ],
  "nextCursor": null
}
```

- 列表项与运行详情使用相同字段形状，调用方无需维护第二套状态模型。
- 只返回任务当前提取修订的运行；重新提取后的列表为空。
- 没有运行时返回 `200 OK`、空 `items` 和 `nextCursor = null`。

## 7. 查询分段级失败

### GET `/api/tasks/{taskId}/translation-runs/{runId}/failures?limit={limit}&cursor={opaqueCursor}`

该接口返回已经确定的分段级失败，按分段 `order` 升序排列。活动运行期间结果可能继续增加；调用方应在运行进入终态后读取完整列表。

示例：

```bash
curl "http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs/56ccae85-4fea-43d6-a736-644ac9ea2422/failures?limit=100" \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK`：

```json
{
  "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
  "items": [
    {
      "segmentId": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
      "segmentOrder": 17,
      "code": "llm_provider_unavailable",
      "retryable": true,
      "attempts": 3
    }
  ],
  "nextCursor": null
}
```

字段约束：

| 字段 | 约束 |
|---|---|
| `segmentId` | 当前提取修订的分段 ID |
| `segmentOrder` | 运行创建时的分段顺序，用于前端定位 |
| `code` | §10.2 定义的稳定机器可读失败码 |
| `retryable` | 再次创建运行时该分段是否值得重试；重试仍只选择 `targetText = null` 的分段 |
| `attempts` | 本次运行对所属批次实际发起的 LLM 请求次数 |

供应商响应正文、堆栈、BaseUrl、模型密钥及提示词不得出现在该资源中。前端按 `code` 映射明确提示，不能直接展示供应商原始消息。

## 8. 分段批量翻译与写入语义

### 8.1 输入与占位符保护

- 后端向 LLM 发送分段 ID、语言要求和 `sourceText`。`sourceText` 中的 `<xN>`、`</xN>`、`<xN/>` 作为不可翻译的透明引用随文本发送。
- `markupTable` 中的 `openingText`、`closingText`、`originalText` 等原始标记内容不发送给 LLM，避免模型改写标签或实体；只在后端用于校验。
- 提示词必须要求 LLM 按分段 ID 返回结构化结果，并逐字保留每个占位符引用，不得新增、删除、改写或改变引用顺序与嵌套关系。
- LLM 返回的分段集合必须与该批请求集合一致，不得缺少、重复或新增分段 ID。

### 8.2 后端校验

每个译文在写入前必须同时满足：

1. 返回分段 ID 与本批请求匹配且唯一；
2. 译文不是 `null`、空字符串或纯空白；
3. 引用只能来自该分段只读 `markupTable`；
4. `<xN>` / `</xN>` 正确配对和嵌套，`<xN/>` 种类正确；
5. 引用数量、编号、种类、顺序和成对关系与 `sourceText` 一致。

不合格译文不得写入 `targetText`，不得修改 `confirmationStatus` 或递增 `version`。占位符校验失败记录为 `placeholder_integrity_violation`，供后续硬性规则检查复用同一校验器，但不把未保存的 LLM 输出当作已持久化规则违规。

### 8.3 成功写入

LLM 返回的批次结构整体可解析且分段 ID 集合匹配后，后端按分段独立校验。校验通过的分段在事务中写入：

- `targetText` 设置为 LLM 返回的非空白译文；
- `confirmationStatus` 从 `pending` 变为 `translated`；
- `version` 在原值基础上递增一次；
- `sourceText`、`markupTable`、`chapter`、`order` 和 `extractionRevision` 不变。

同一批中某个分段校验失败不得阻止其他已通过分段提交；失败分段进入后续重试，已提交分段从重试输入中移除。提交后立即更新运行进度。调用方随后读取分段时必须能看到已提交译文，从而实现“分段逐条获得译文”的界面行为。

### 8.4 自动重试与部分失败

- 网络故障、超时、HTTP `429`、供应商 `5xx`、结构化响应整体无法解析和占位符校验失败可以按服务端策略重试。结构或分段 ID 集合整体不合法时重试整批；结构合法时只重试未通过校验的分段。
- 默认每批最多尝试 `3` 次，包含首次请求；重试使用指数退避并加入随机抖动。供应商明确拒绝的鉴权、模型或语言配置不得持续重试。
- 重试只重发尚未成功提交的批次；已经成功写入的分段不得重复翻译。
- 某批耗尽尝试后，其中每个未成功分段记录稳定失败码，后端继续处理其他批次，以获得可用的部分结果。
- `partial_failed` 或 `failed` 后再次调用创建接口会生成新运行，且只选择仍满足 `targetText = null` 的分段。
- 服务进程在活动运行中异常中断时，恢复阶段把未进入终态的运行标记为 `failed`、失败码 `translation_interrupted`，任务标记为 `failed`；已经提交的分段保持不变并可通过新运行重试。

## 9. 状态流转与结果判定

| 事件 | 运行状态 | 任务状态 | 分段结果 |
|---|---|---|---|
| 创建成功 | `queued` | `processing` | 尚未变化 |
| 后台开始 | `processing` | `processing` | 分批写入成功译文 |
| 所有选中分段成功 | `completed` | `completed` | 新译文均为 `translated` |
| 部分分段失败 | `partial_failed` | `failed` | 成功译文保留，失败分段仍为 `targetText = null` |
| 全部分段失败或运行中断 | `failed` | `failed` | 已提交结果保留；未成功分段不变 |
| 失败后重试成功且全部分段已有译文 | 新运行 `completed` | `completed` | 旧运行保留到重新提取前 |

运行进入终态时：

- `progress.processedSegments = selection.selectedSegments`；
- `progress.succeededSegments + progress.failedSegments = selection.selectedSegments`；
- `progress.percent = 100.0`，即使终态是失败；成功/失败结果必须结合计数和状态判断；
- `finishedAt` 非空；`queued` 时 `startedAt = null`，其余已开始运行的状态 `startedAt` 非空。

## 10. 错误码与异步失败码

### 10.1 请求错误

请求失败统一返回 `application/problem+json`。本文沿用基础文档并增加稳定的 `code`；字段级信息放在 `errors` 扩展中。前端按 `code` 映射文案，`title` 和 `detail` 仅用于诊断。

```json
{
  "type": "https://mytranslator.local/problems/extraction-revision-changed",
  "title": "Extraction revision changed",
  "status": 409,
  "code": "extraction_revision_changed",
  "instance": "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs",
  "errors": {
    "requestedRevision": 1,
    "currentRevision": 2
  }
}
```

| HTTP | `code` | 场景 |
|---|---|---|
| 400 | `invalid_language_tag` | `sourceLanguage` 或 `targetLanguage` 不是合法 BCP 47 标签 |
| 400 | `invalid_extraction_revision` | `extractionRevision` 缺失、类型错误或小于 `1` |
| 400 | `invalid_cursor` | 游标不可解析或不属于当前资源 |
| 400 | `invalid_pagination` | `limit` 不在 `1..200` 范围内 |
| 404 | `task_not_found` | 任务不存在 |
| 404 | `translation_run_not_found` | 运行不存在、不属于该任务或已因重新提取失效 |
| 409 | `task_busy` | 任务存在活动翻译或其他互斥操作 |
| 409 | `extraction_revision_changed` | 请求或游标绑定的提取修订不是当前修订 |
| 409 | `no_segments_to_translate` | 当前修订没有 `targetText = null` 的分段 |
| 422 | `invalid_segment_state` | 已有分段违反 `targetText = null` 表示未翻译等公共契约 |
| 422 | `unsupported_language_pair` | 源/目标语言相同，或已配置提供程序明确不支持该组合 |
| 503 | `llm_not_configured` | Provider、BaseUrl、ApiKey 或 Model 未配置/格式无效；不创建运行，任务标记为 `failed` |

Token 问题仍按基础文档返回 `401`；已认证无权时返回 `403`。服务端不得把供应商网络故障转换为创建请求的长时间同步等待：运行已成功创建后发生的故障必须记录在运行资源中。

### 10.2 运行与分段级失败

终态为 `partial_failed` 或 `failed` 时，运行的 `failure` 使用以下形状：

```json
{
  "code": "llm_provider_unavailable",
  "retryable": true,
  "failedSegments": 40
}
```

如果失败分段包含多个原因，运行摘要 `code` 使用 `segment_translation_failed`，具体原因通过 §7 查询。

| `code` | `retryable` 默认值 | 含义 |
|---|---|---|
| `segment_translation_failed` | `true` | 多个分段因不同原因失败，需读取失败列表 |
| `llm_provider_unavailable` | `true` | 供应商限流、网络故障或服务端暂时不可用 |
| `llm_provider_timeout` | `true` | LLM 请求耗尽配置的超时时间和重试次数 |
| `llm_authentication_failed` | `false` | 服务端配置的 LLM 密钥被供应商拒绝 |
| `llm_model_unavailable` | `false` | 配置模型不存在、无权访问或被供应商停用 |
| `llm_response_invalid` | `true` | 返回无法解析、分段 ID 缺失/重复/新增或译文为空 |
| `placeholder_integrity_violation` | `true` | LLM 返回结果新增、删除、改写或破坏占位符引用 |
| `unsupported_language_pair` | `false` | 供应商在运行期间明确拒绝该语言组合 |
| `translation_interrupted` | `true` | 服务进程中断导致运行未正常结束 |

`retryable` 表示按相同语言参数再次创建运行是否合理，不保证下一次一定成功。`llm_authentication_failed`、`llm_model_unavailable` 等配置类失败应先由部署方修复服务端配置。

## 11. 调用流程

### 11.1 Web/桌面端

1. 读取 `GET /api/tasks/{taskId}`，取得当前 `extractionRevision` 和任务状态。
2. 用户触发翻译后调用 `POST /api/tasks/{taskId}/translation-runs`；按钮进入 loading 并防重复提交。
3. 保存响应中的 `runId`，按 `Retry-After` 轮询运行详情。
4. 每次 `progress.succeededSegments` 增加后重新读取分段，逐步展示新译文；运行期间显示进度条、百分比和阶段文案。
5. `completed` 时停止轮询并刷新任务与分段；`partial_failed` / `failed` 时读取失败列表，按 `code` 显示明确提示和重试入口。
6. 页面刷新后调用运行列表并取第一项恢复最新运行；不得依赖仅存在于内存中的前端状态。

### 11.2 第三方系统

1. 按文件接口创建任务并读取 `extractionRevision`。
2. 创建翻译运行，保存 `Location` 或响应中的 `runId`。
3. 轮询运行资源至终态；终态失败时读取分段级失败。
4. 对可重试失败再次创建运行；新运行只处理仍未翻译的分段。
5. 运行成功后通过分段接口读取译文，或在满足文件接口导出条件时下载导出文件。

## 12. 与既有及后续契约的关系

| 相关文档/任务 | 本文约束 |
|---|---|
| 文件导入拆解与导出 | 复用 `taskId`、`extractionRevision`、分段字段、游标、占位符和只读 `markupTable`；不改变受保护块语义 |
| ADR-0002 | 原始标记留在标记表；LLM 只接触 `sourceText` 中的透明引用，导出仍按引用回填 |
| AI 翻译操作界面 | 以运行状态和进度计数展示长任务；以稳定失败码映射错误提示；通过既有分段接口逐步刷新译文 |
| 任务管理与开放 REST | 后续可在任务摘要增加最新翻译运行和进度字段，但必须与本文状态、计数和 `runId` 一致 |
| 翻译编辑器基础 | 后续人工保存接口必须尊重活动运行互斥、分段 `version` 与已确认不被 AI 覆盖的语义 |
| 术语表/TM | 本次接口不接收术语或 TM 参数；后续可在服务端构造 LLM 上下文，不得改变请求字段和运行资源语义 |
| 硬性规则检查 | 应复用本文的占位符校验器；未通过校验的 LLM 输出不得先保存再等待规则引擎发现 |

本文不定义 AI 二轮审查及其审核意见。后续审核、术语和 TM 文档只能扩展上下文或新增资源，不得改变本文既有字段语义、未翻译的 `null` 语义、已确认分段不可覆盖、成功分段逐批提交、占位符原样保留及部分失败不回滚的约定。
