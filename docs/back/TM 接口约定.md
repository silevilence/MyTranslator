# TM 接口约定

本文定义翻译记忆库（TM）存储、相似历史译文匹配、译文差异对比与差异告警对 Web 端、桌面端和第三方系统公开的 REST 接口。接口覆盖已确认分段自动沉淀、显式批量录入、TM 条目查询与删除、任务分段对比，以及不依赖任务的通用文本对比。

鉴权、基础地址和通用 Problem Details 格式以[《鉴权与基础接口约定》](鉴权与基础接口约定.md)为准。本文所有接口均要求 Bearer Token。任务、分段、提取修订、占位符和只读标记表的字段语义以[《文件导入拆解与导出接口约定》](文件导入拆解与导出接口约定.md)及 [ADR-0002](../adr/0002-标记提取模型.md)为准。

## 1. 范围与接口原则

- TM 条目是一个源语言、目标语言、历史源文、历史译文及其标记表的不可变快照；Web、桌面端和第三方系统使用完全相同的接口。
- 分段从非 `confirmed` 变为 `confirmed` 时，后端在确认事务中自动写入 TM；同时提供显式录入接口，供第三方导入既有历史译文。取消确认不删除已经形成的历史条目。
- 相似匹配只在完全相同的 `sourceLanguage` + `targetLanguage` 语言对内执行。不存在适用条目不是错误，返回空匹配和“无告警”。
- 后端是匹配分数、差异分数和告警结论的唯一计算方。调用方可渲染逐字差异，但不得自行改变匹配门槛或重新判定告警。
- 任务分段对比读取后端已保存的 `sourceText`、`targetText` 和只读 `markupTable`，并绑定 `extractionRevision` 与分段 `version`；脏标记尚未保存的本地译文不属于该结果。
- 通用文本对比供不具有任务/分段资源的第三方系统使用。它与任务分段对比复用同一算法和阈值。
- 本文不定义人工译文保存接口。后续任务接口必须落实 §4.3 的确认时自动沉淀要求，且不得改变本文的 TM 条目、匹配、差异和告警语义。

## 2. 通用约定

### 2.1 标识、时间、枚举与 JSON

- `entryId`、`taskId`、`segmentId` 均为 UUID 字符串。
- 时间使用 UTC ISO 8601，例如 `2026-08-31T08:30:00Z`。
- JSON 字段使用 `camelCase`，请求和响应使用 UTF-8。
- 枚举值使用本文列出的英文小写字符串；调用方必须容忍未来增加未知枚举值。
- `sourceLanguage` 和 `targetLanguage` 使用 BCP 47 语言标签，例如 `en`、`zh-CN`、`pt-BR`；服务端规范化大小写后保存和回显。两者均必填，且规范化后不得相同。

### 2.2 文本、占位符与标记表

- `sourceText`、`targetText` 去除首尾空白后均须包含至少一个非空白字符，最大分别为 `20,000` 个 Unicode 标量值；服务端以 Unicode NFC 保存原文本，不折叠保存值的内部空白。
- 文本可含 `<xN>`、`</xN>`、`<xN/>`。只有与同一条目 `markupTable` 中同 ID、同种类记录对应的完整引用才是占位符；字面 `<xN>` 仍按普通可译文本处理。
- `markupTable` 使用《文件导入拆解与导出接口约定》§7 的只读形状。录入时服务端必须校验源文和译文中的引用编号、数量、种类、顺序与成对关系一致。
- 没有标记时 `markupTable` 必须为 `[]`，不得为 `null`。显式录入缺省该字段时按 `[]` 处理。
- TM 只保存合法历史译文。占位符不一致的显式录入或确认操作不得产生 TM 条目。

### 2.3 游标分页

TM 条目列表使用游标分页：

```http
GET /api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&query=translation&limit=100&cursor={opaqueCursor}
```

- `limit` 默认 `100`，范围 `1..200`。
- `cursor` 是不透明字符串，调用方不得解析或构造；`nextCursor = null` 表示末页。
- 游标绑定产生它的 `sourceLanguage`、`targetLanguage` 和 `query`；改变任一条件后必须丢弃旧游标。
- 游标不可解析、不属于 TM 条目列表或与当前条件不一致时返回 `400 invalid_cursor`。
- 列表不提供跨请求快照；录入或删除后要求最新视图的调用方应从第一页重新加载并按 `id` 去重。

### 2.4 v1 匹配与告警阈值

v1 阈值由服务端固定，所有调用方一致使用，并在每次对比响应的 `thresholds` 中回显：

| 字段 | v1 值 | 含义 |
|---|---:|---|
| `minimumSourceMatchScore` | `0.7000` | 返回为相似历史译文的最低源文相似度 |
| `warningSourceMatchScore` | `0.8500` | 允许成为差异告警参考项的最低源文相似度 |
| `warningTargetDifference` | `0.3000` | 当前译文与参考历史译文的差异分数大于该值时告警 |

阈值不接受请求覆盖，也不提供 v1 配置接口。后续若需可配置，只能新增配置资源并继续在响应中回显实际值；不得让同一时刻不同调用方对相同输入产生不同结论。

## 3. TM 条目数据模型

TM 条目形状：

```json
{
  "id": "b9fa8ec4-b721-48b9-90e4-09ab14aa9403",
  "sourceText": "Use <x1>translation memory</x1>.",
  "targetText": "使用<x1>翻译记忆库</x1>。",
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "markupTable": [
    {
      "id": 1,
      "kind": "paired",
      "openingText": "<strong>",
      "closingText": "</strong>",
      "originalText": null,
      "meaning": "加粗强调"
    }
  ],
  "origin": "confirmed_segment",
  "originTaskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
  "originSegmentId": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
  "originExtractionRevision": 1,
  "createdAt": "2026-08-31T08:30:00Z"
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | UUID | 响应 | TM 条目 ID |
| `sourceText` | string | 是 | 历史源文，约束见 §2.2 |
| `targetText` | string | 是 | 历史译文，约束见 §2.2 |
| `sourceLanguage` | string | 是 | 源语言 BCP 47 标签 |
| `targetLanguage` | string | 是 | 目标语言 BCP 47 标签 |
| `markupTable` | array | 否 | 条目只读标记表；缺省按 `[]` |
| `origin` | string | 响应 | `confirmed_segment` / `external` |
| `originTaskId` | UUID/null | 响应 | 自动沉淀时为来源任务 ID；显式录入为 `null` |
| `originSegmentId` | UUID/null | 响应 | 自动沉淀时为来源分段 ID；显式录入为 `null` |
| `originExtractionRevision` | integer/null | 响应 | 自动沉淀时的提取修订；显式录入为 `null` |
| `createdAt` | string | 响应 | 首次写入时间 |

TM 条目不可更新。历史译法修正通过录入新条目并按需删除旧条目完成，避免静默改写已经参与过对比的历史事实。

同一语言对内，按 §7.1 规范化后的 `sourceText`、`targetText` 以及规范化标记表完全相同，视为同一内容条目。重复自动沉淀或显式录入不创建第二条记录，返回已有条目；来源字段保持首次写入值。

## 4. 录入 TM

### 4.1 POST /api/tm/entries

显式接口以 `items` 数组支持单条或批量录入，每批 `1..500` 条。整个请求先完成校验；任一项无效时整批拒绝，不产生部分写入。

请求体：

```json
{
  "items": [
    {
      "sourceText": "Use translation memory.",
      "targetText": "使用翻译记忆库。",
      "sourceLanguage": "en",
      "targetLanguage": "zh-CN",
      "markupTable": []
    }
  ]
}
```

示例：

```bash
curl -X POST http://localhost:5199/api/tm/entries \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000" \
  -H "Content-Type: application/json" \
  -d '{"items":[{"sourceText":"Use translation memory.","targetText":"使用翻译记忆库。","sourceLanguage":"en","targetLanguage":"zh-CN","markupTable":[]}]}'
```

至少创建一条新记录时返回 `201 Created`；全部为重复内容时返回 `200 OK`。两种成功响应形状相同：

```json
{
  "summary": {
    "submitted": 1,
    "created": 1,
    "duplicates": 0
  },
  "items": [
    {
      "index": 0,
      "disposition": "created",
      "entry": {
        "id": "b9fa8ec4-b721-48b9-90e4-09ab14aa9403",
        "sourceText": "Use translation memory.",
        "targetText": "使用翻译记忆库。",
        "sourceLanguage": "en",
        "targetLanguage": "zh-CN",
        "markupTable": [],
        "origin": "external",
        "originTaskId": null,
        "originSegmentId": null,
        "originExtractionRevision": null,
        "createdAt": "2026-08-31T08:30:00Z"
      }
    }
  ]
}
```

- `index` 是请求数组中的零基位置；响应按请求顺序返回。
- `disposition` 为 `created` / `duplicate`。`duplicate` 时 `entry` 是数据库中的已有条目。
- 同一请求内的重复项同样计入 `duplicates`，第一个合法出现位置负责创建。
- 调用方可安全重试相同批次；内容去重保证不会因网络重试生成重复条目。

### 4.2 已确认分段自动沉淀

后续人工译文保存/确认接口必须在分段从非 `confirmed` 变为 `confirmed` 的同一数据库事务中执行以下动作：

1. 校验 `extractionRevision`、分段 `version`、非空白 `targetText` 与占位符完整性；
2. 取得该次确认明确提交并由服务端规范化后的 `sourceLanguage`、`targetLanguage`；
3. 以分段的 `sourceText`、保存后的 `targetText`、只读 `markupTable` 和语言对写入或命中已有 TM 条目；
4. 提交确认状态、分段新版本和 TM 去重结果。

语言对是确认操作的必要上下文。任务接口可把语言对持久化为任务上下文并由确认请求引用，或直接在确认请求中提交，但必须保证调用方能明确选择；不得根据文本猜测。缺少语言对时确认请求返回 `422 tm_language_pair_required`，确认状态与 TM 均不修改。

边界行为：

- 已经是 `confirmed` 且译文未变化的幂等保存不重复入库。
- 取消确认只改变分段确认状态，不删除 TM；历史记录仍然成立。
- 已确认分段修改译文后再次确认，若内容不同则形成新条目；旧条目保留，除非调用方显式删除。
- 应用重新提取会删除旧分段，但不删除已经沉淀的 TM 条目；条目来源字段仅用于追溯，不表示实时外键。
- TM 写入失败时整个确认事务失败；不得出现“分段已确认但应产生的 TM 条目缺失”的部分提交。

## 5. 查询与删除 TM 条目

### 5.1 GET /api/tm/entries

`sourceLanguage` 与 `targetLanguage` 必填；`query` 可选。无 `query` 时按 `createdAt` 降序、`id` 升序返回。有 `query` 时只对 `sourceText` 执行 §7.1 的相似度计算，低于 `minimumSourceMatchScore` 的条目不返回，并按 `sourceMatchScore` 降序、`createdAt` 降序、`id` 升序返回。

查询参数：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `sourceLanguage` | string | 是 | 源语言 BCP 47 标签 |
| `targetLanguage` | string | 是 | 目标语言 BCP 47 标签 |
| `query` | string | 否 | 源文模糊查询；纯空白按未提供处理，最大 20,000 个 Unicode 标量值 |
| `limit` | integer | 否 | 默认 `100`，范围 `1..200` |
| `cursor` | string | 否 | 上一页返回的不透明游标 |

示例：

```bash
curl "http://localhost:5199/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&query=Use%20translation%20memory.&limit=20" \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK`：

```json
{
  "items": [
    {
      "id": "b9fa8ec4-b721-48b9-90e4-09ab14aa9403",
      "sourceText": "Use translation memory.",
      "targetText": "使用翻译记忆库。",
      "sourceLanguage": "en",
      "targetLanguage": "zh-CN",
      "markupTable": [],
      "origin": "external",
      "originTaskId": null,
      "originSegmentId": null,
      "originExtractionRevision": null,
      "createdAt": "2026-08-31T08:30:00Z",
      "sourceMatchScore": 1.0000
    }
  ],
  "nextCursor": null
}
```

`sourceMatchScore` 仅在存在非空 `query` 时为 `0..1` 数值；无查询时为 `null`。没有条目时返回空 `items` 和 `nextCursor = null`。

### 5.2 GET /api/tm/entries/{entryId}

示例：

```bash
curl http://localhost:5199/api/tm/entries/b9fa8ec4-b721-48b9-90e4-09ab14aa9403 \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK` 和 §3 的完整条目形状，不包含列表查询专用的 `sourceMatchScore`。条目不存在时返回 `404 tm_entry_not_found`。

### 5.3 DELETE /api/tm/entries/{entryId}

示例：

```bash
curl -X DELETE http://localhost:5199/api/tm/entries/b9fa8ec4-b721-48b9-90e4-09ab14aa9403 \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

删除成功返回 `204 No Content`；条目不存在返回 `404 tm_entry_not_found`。删除只影响之后的匹配与告警，不修改任务分段、译文、确认状态、既有审核意见或已经返回给调用方的历史响应。

## 6. 历史翻译对比

### 6.1 GET /api/tasks/{taskId}/segments/{segmentId}/tm-comparison

任务分段对比供编辑器使用。后端读取指定分段当前已保存的源文、译文和标记表；调用方必须提交自己看到的提取修订和分段版本。

查询参数：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `extractionRevision` | integer | 是 | 当前提取修订，必须大于等于 `1` |
| `segmentVersion` | integer | 是 | 调用方最后读取的分段版本，必须大于等于 `1` |
| `sourceLanguage` | string | 是 | 源语言 BCP 47 标签 |
| `targetLanguage` | string | 是 | 目标语言 BCP 47 标签 |
| `limit` | integer | 否 | 返回匹配数，默认 `5`，范围 `1..20` |

示例：

```bash
curl "http://localhost:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/segments/7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b/tm-comparison?extractionRevision=1&segmentVersion=4&sourceLanguage=en&targetLanguage=zh-CN&limit=5" \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000"
```

成功返回 `200 OK`，响应形状见 §6.3。任务或分段不存在时分别返回 `404 task_not_found`、`404 segment_not_found`；分段不属于路径任务也返回 `404 segment_not_found`。

- 请求修订不是当前修订时返回 `409 extraction_revision_changed`。
- 请求版本不是分段当前版本时返回 `409 segment_version_conflict`，`errors` 含 `requestedVersion` 和 `currentVersion`。
- `targetText = null` 时仍返回源文匹配，所有目标差异字段为 `null`，且不产生差异告警。
- 本接口只比较已保存译文。前端存在脏标记时应先保存；不得把本地未保存文本与服务端结论混合展示为当前告警。

### 6.2 POST /api/tm/comparisons

通用对比不要求任务资源，适用于第三方独立文本。`markupTable` 与文本的关系仍须符合 §2.2。

请求体：

```json
{
  "sourceText": "Use translation memory.",
  "targetText": "使用翻译缓存。",
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "markupTable": [],
  "limit": 5
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `sourceText` | string | 是 | 待匹配源文 |
| `targetText` | string/null | 否 | 待比较译文；缺省或 `null` 时只返回源文匹配，不判定告警 |
| `sourceLanguage` | string | 是 | 源语言 BCP 47 标签 |
| `targetLanguage` | string | 是 | 目标语言 BCP 47 标签 |
| `markupTable` | array | 否 | 与文本对应的标记表；缺省按 `[]` |
| `limit` | integer | 否 | 默认 `5`，范围 `1..20` |

示例：

```bash
curl -X POST http://localhost:5199/api/tm/comparisons \
  -H "Authorization: Bearer sk-dev-00000000000000000000000000000000" \
  -H "Content-Type: application/json" \
  -d '{"sourceText":"Use translation memory.","targetText":"使用翻译缓存。","sourceLanguage":"en","targetLanguage":"zh-CN","markupTable":[],"limit":5}'
```

成功返回 `200 OK` 和 §6.3 的形状；通用响应的 `taskId`、`segmentId`、`extractionRevision`、`segmentVersion` 均为 `null`。

### 6.3 对比响应

```json
{
  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
  "segmentId": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
  "extractionRevision": 1,
  "segmentVersion": 4,
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "thresholds": {
    "minimumSourceMatchScore": 0.7000,
    "warningSourceMatchScore": 0.8500,
    "warningTargetDifference": 0.3000
  },
  "items": [
    {
      "entryId": "b9fa8ec4-b721-48b9-90e4-09ab14aa9403",
      "sourceText": "Use translation memory.",
      "targetText": "使用翻译记忆库。",
      "markupTable": [],
      "sourceMatchScore": 1.0000,
      "targetSimilarity": 0.6250,
      "targetDifference": 0.3750,
      "createdAt": "2026-08-31T08:30:00Z"
    }
  ],
  "differenceWarning": {
    "hasWarning": true,
    "referenceEntryId": "b9fa8ec4-b721-48b9-90e4-09ab14aa9403",
    "sourceMatchScore": 1.0000,
    "targetDifference": 0.3750,
    "reason": "target_difference_exceeded"
  },
  "comparedAt": "2026-08-31T08:35:00Z"
}
```

字段约束：

| 字段 | 约束 |
|---|---|
| `items` | 只含 `sourceMatchScore >= minimumSourceMatchScore` 的同语言对条目，按源文分数降序、`createdAt` 降序、`entryId` 升序，最多为请求 `limit` |
| `sourceMatchScore` | 当前源文与历史源文的 `0..1` 相似度，四位小数 |
| `targetSimilarity` | 当前译文与历史译文的 `0..1` 相似度；当前译文为 `null` 时为 `null` |
| `targetDifference` | `1 - targetSimilarity`，四位小数；当前译文为 `null` 时为 `null` |
| `differenceWarning.referenceEntryId` | 第一条满足 `sourceMatchScore >= warningSourceMatchScore` 的匹配；没有合格参考项或当前译文为 `null` 时为 `null` |
| `differenceWarning.hasWarning` | 仅当参考项存在且其 `targetDifference > warningTargetDifference` 时为 `true` |
| `differenceWarning.reason` | 告警时为 `target_difference_exceeded`；无告警时为 `null` |
| `comparedAt` | 后端完成本次快照计算的时间，不代表 TM 条目更新时间 |

没有匹配时仍返回 `200 OK`：`items = []`，`hasWarning = false`，其余告警字段及 `reason` 为 `null`。多个同分条目只用排序后的第一条高置信匹配作为告警参考，避免不同历史译法同时产生相互冲突的告警；其他匹配仍在 `items` 中供人工参考。

## 7. 匹配、差异与一致性规则

### 7.1 文本规范化与相似度

源文和译文分别按以下确定性流程计算，保存的原文本不受影响：

1. 依据各自 `markupTable` 识别真实 `<xN>`、`</xN>`、`<xN/>` 引用，并把每个引用替换为一个空格；不得用正则删除所有形似占位符的字面文本。
2. 执行 Unicode NFC、首尾去空白、连续空白折叠和 Unicode 不区分大小写规范化。
3. 对规范化后的 Unicode 标量序列计算 Levenshtein 距离。
4. 相似度为 `1 - distance / max(currentLength, historyLength)`；两者完全相同为 `1.0000`，结果限制在 `0..1` 并四舍五入保留四位小数。

源文使用该结果作为 `sourceMatchScore`；译文使用该结果作为 `targetSimilarity`。调用方可基于服务端返回的两段原文本做视觉差异高亮，但不得用前端差异算法覆盖数值或告警结论。

### 7.2 告警判定

1. 先按 §6.3 排序匹配项；
2. 取第一条 `sourceMatchScore >= 0.8500` 的条目作为唯一告警参考；
3. 当前译文非 `null` 且参考项 `targetDifference > 0.3000` 时，返回 TM 差异告警；等于阈值不告警；
4. 无高置信参考项、当前译文为 `null` 或差异未超过阈值时不告警。

TM 差异告警是参考性告警，不是规则违规、系统错误或 AI 审核意见；它不阻止保存、确认、翻译运行、审核运行或导出，也不改变任务公共状态和分段确认状态。

### 7.3 快照与失效

- 每次对比在一个只读数据库快照中读取目标分段和适用 TM 条目；响应描述该次快照，不持久化告警资源。
- 录入或删除 TM 后，既有响应不会被服务端主动推送更新；调用方重新发起对比取得新结论。
- 任务分段对比返回的 `segmentVersion` 必须与当前分段一致。保存译文后前端应丢弃旧结果并重新请求。
- 重新提取后旧分段 ID 不再有效；收到 `409 extraction_revision_changed` 后必须丢弃结果与旧缓存，从任务及分段第一页重新加载。
- 通用对比没有任务锁或乐观并发语义，只对请求正文和读取到的 TM 快照负责。

## 8. 错误码

请求失败统一返回 `application/problem+json`。本文沿用基础文档并增加稳定的 `code`；字段级信息放在 `errors` 扩展中。前端按 `code` 映射文案，`title` 和 `detail` 仅用于诊断。

版本冲突示例：

```json
{
  "type": "urn:mytranslator:problem:segment-version-conflict",
  "title": "The segment has changed since it was read.",
  "status": 409,
  "code": "segment_version_conflict",
  "instance": "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/segments/7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b/tm-comparison",
  "errors": {
    "requestedVersion": 3,
    "currentVersion": 4
  }
}
```

| HTTP | `code` | 场景 |
|---|---|---|
| 400 | `invalid_tm_batch` | `items` 缺失、不是数组或数量不在 `1..500` |
| 400 | `invalid_tm_source_text` | `sourceText` 缺失、纯空白或超过 20,000 个 Unicode 标量值 |
| 400 | `invalid_tm_target_text` | 录入的 `targetText` 缺失/纯空白/过长，或通用对比提供了空白非 `null` 译文 |
| 400 | `invalid_tm_markup_table` | 标记表形状、ID、种类或文本字段不合法 |
| 400 | `invalid_language_tag` | 语言字段缺失、不是合法 BCP 47 标签或源/目标语言相同 |
| 400 | `invalid_extraction_revision` | `extractionRevision` 缺失、类型错误或小于 `1` |
| 400 | `invalid_segment_version` | `segmentVersion` 缺失、类型错误或小于 `1` |
| 400 | `invalid_match_limit` | 对比 `limit` 不在 `1..20` |
| 400 | `invalid_cursor` | 游标不可解析、不属于 TM 列表或与查询条件不一致 |
| 400 | `invalid_pagination` | 条目列表 `limit` 不在 `1..200` |
| 404 | `tm_entry_not_found` | TM 条目不存在 |
| 404 | `task_not_found` | 任务不存在 |
| 404 | `segment_not_found` | 分段不存在、不属于任务或已因重新提取失效 |
| 409 | `extraction_revision_changed` | 请求修订不是任务当前修订 |
| 409 | `segment_version_conflict` | 请求版本不是分段当前版本 |
| 422 | `tm_placeholder_integrity_violation` | 源文与译文的占位符编号、数量、种类、顺序或成对关系不一致 |
| 422 | `tm_language_pair_required` | 分段确认缺少自动沉淀所需的明确语言对；确认与 TM 均不修改 |

Token 问题仍按《鉴权与基础接口约定》返回 `401`；已认证无权时返回 `403`；未处理的服务端错误返回 `500`，且不得暴露数据库语句、主机路径或内部堆栈。

TM 未配置、条目为空、当前语言对无条目或没有达到匹配门槛均不是错误，不得返回 `404`、`409` 或 `422`。

## 9. 前端与第三方调用流程

### 9.1 Web/桌面端历史对比界面

1. 进入任务编辑器后读取任务与分段，取得当前 `extractionRevision`、分段 `version` 及明确的源/目标语言。
2. 选中分段且本地无脏标记时调用 §6.1；区块加载显示骨架屏，页级失败显示带重试动作的 ErrorBanner。
3. 有匹配时按服务端顺序展示相似历史源文、历史译文和 `sourceMatchScore`；`DiffViewer` 对当前译文与所选历史译文做并排/逐字高亮。视觉差异只用于呈现，不重新计算 `targetDifference`。
4. `differenceWarning.hasWarning = true` 时使用 warning 语义的颜色 + 图标 +“TM 差异过大”文字三通道标记；不得使用 error 语义或称为规则违规。无告警时不显示告警标记。
5. 当前译文为 `null` 时仍可展示相似历史译文作为参考，但不显示差异告警。没有匹配时显示“暂无相似历史译文”的区块空态，不把它当作页级错误。
6. 保存当前分段后丢弃旧对比，以响应中的新 `version` 重新请求；存在脏标记时保留脏标记，不把旧服务端告警显示为当前结论。
7. `segment_version_conflict` 时重新读取当前分段；`extraction_revision_changed` 时丢弃分段、游标和对比缓存，从任务与分段第一页重新加载。
8. 所有接口失败文案由稳定 `code` 映射生成；任意 `401` 执行全局清理 Token 并跳转登录的既有流程。

### 9.2 第三方系统

1. 通过 `POST /api/tm/entries` 单条或批量导入历史译文；网络失败后可安全重试相同内容。
2. 有任务分段时优先调用任务分段对比，使修订与版本冲突可被检测；独立文本调用通用对比。
3. 使用 `items` 展示或消费相似历史译文，使用 `differenceWarning` 作为统一告警结论，不自行调整阈值。
4. 删除错误历史条目后重新发起对比；既有任务译文和确认状态不会被删除操作改变。

## 10. 与既有及后续契约的关系

| 相关文档/任务 | 本文约束 |
|---|---|
| 鉴权与基础接口约定 | 沿用 Bearer Token、`401` / `403` 语义和 Problem Details 格式 |
| 文件导入拆解与导出 | 复用 `taskId`、`segmentId`、`extractionRevision`、分段 `version`、`targetText = null`、占位符与只读 `markupTable`；不改变分段或受保护块语义 |
| ADR-0002 | 必须根据标记表识别真实占位符；字面 `<xN>` 仍参与相似度与视觉差异 |
| AI 翻译 | 后端可把 §6 的相似匹配注入 LLM 上下文；TM 为空时使用空上下文，不改变翻译运行请求、进度、写入与失败语义 |
| AI 审核 | 后端可把相似历史译文注入审核上下文；TM 为空不是错误，不改变审核运行字段、任务公共状态或审核意见语义 |
| 术语接口约定 | 复用 BCP 47 与占位符忽略方式；TM `sourceMatchScore` 不得当作术语对齐结论，术语 `matchScore` 也不得当作 TM 告警结论 |
| 任务管理与翻译编辑器 | 后续人工确认契约必须携带或引用明确语言对，并以同一事务自动沉淀；保存成功后的新分段 `version` 用于重新请求对比 |
| 硬性规则检查 | TM 差异告警不是规则违规，不得进入规则违规计数；两者可在同一分段同时显示且使用不同语义 |

本文不改变既有任务公共状态、分段字段、确认状态、乐观并发版本、占位符语法、标记表只读性、受保护块不可编辑性或重新提取替换分段 ID 的语义。后续文档只能新增资源或可选字段，不得改变本文的条目去重、固定阈值、匹配算法、告警判定及无匹配时成功返回空结果的约定。
