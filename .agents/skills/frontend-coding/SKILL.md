---
name: frontend-coding
description: 'Frontend design and coding standard enforcement for the MyTranslator project (Blazor WASM web, .NET MAUI Blazor Hybrid desktop, shared Razor Class Library). Use when designing, implementing, or modifying any frontend code in this repo — .razor / .razor.css / Blazor components / RCL shared components / frontend service and page classes — or when a user request touches frontend UI behavior. Enforces docs/front specs strictly; when a user requirement conflicts with a MUST-level spec, requires per-item user confirmation before modifying the spec, one item at a time.'
---

# Frontend Coding (MyTranslator)

项目级前端设计编码规范技能。所有 MyTranslator 前端设计、编码、修改都必须遵守本技能流程与 `docs/front/` 规范。

## 1. 编码前必读（必须）

设计或编码任何前端代码前，先读取以下文件（除非本会话已读过且未被修改）：

- `docs/front/README.md` — 规范索引、强制级别（必须/推荐）
- `docs/front/设计规范.md` — 设计原则、视觉 token、语义色映射、字体栈、断点
- `docs/front/组件规范.md` — 组件分层、MudBlazor 约定、领域组件清单、resx 约定
- `docs/front/交互规范.md` — 页面骨架、编辑器交互、反馈分级、快捷键
- `CONTEXT.md` — 领域术语表；输出与代码命名必须使用其中词汇
- `docs/back/` 中与本次改动相关的接口契约（错误码、字段、状态）
- `docs/adr/` — 相关 ADR（尤其 0001 组件策略）

## 2. 编码规则（必须）

- 严格遵守 `docs/front/` 全部"必须（MUST）"级条款，逐条落实到代码：
  - 视觉：token 化、语义色映射、字体双栈、双主题（亮/暗）
  - 组件：通用能力用 MudBlazor，领域组件进 RCL，页面只组装
  - 交互：编辑器切换即存、脏标记、反馈分级、危险操作确认、快捷键表
  - 文案：resx 集中，禁止硬编码用户可见字符串
- "推荐（SHOULD）"级条款默认遵守；偏离时记录理由，供审查阶段说明。
- 前端代码不得自行解释错误码；错误文案由 `docs/back` 错误码映射产生。
- 术语使用 `CONTEXT.md` 词汇（确认≠完成、违规≠错误）。

## 3. 规范冲突处理（必须，逐点确认）

当用户新要求与规范冲突时：

1. **不得**直接修改规范文档，也**不得**静默按新要求编码绕过规范。
2. 将冲突内容完整列出：规范原文（文件+条款）vs 用户要求，呈现给用户确认。
3. **每次每点修改都要单独确认**：用户确认第 1 点后，才能修改第 1 点；第 2 点未经确认不得修改。禁止"用户确认了 A 点，顺手把 B 点也改了"。
4. 确认后先修改规范文档（保持 `docs/front/` 与 `CONTEXT.md`/ADR 一致），再按新规范编码。
5. 冲突只针对"必须（MUST）"级条款；"推荐（SHOULD）"级偏离走 §2 记录理由即可。

## 4. 交付前自查（必须）

编码完成后，按 `docs/front/审查标准.md` 五维度自查一遍；发现不通过项先修复再交付。自查不代替正式审查：交付后调用 `frontend-review` skill 进行正式审查。

## 5. 禁止事项

- 禁止新增与规范并行的"第二套约定"（如新颜色、新组件风格）。
- 禁止为了绕过规范而把页面逻辑内联进组件或反之。
- 禁止未读规范直接编码。
