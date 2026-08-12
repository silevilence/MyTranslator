---
name: frontend-review
description: 'Frontend review standard for the MyTranslator project. Use when reviewing or auditing frontend work after implementation — checking a feature, branch, PR, or completed task against docs/front/审查标准.md and the design/component/interaction specs, verifying spec compliance, interaction correctness, accessibility, dual-theme quality, or accepting frontend deliverables. Assumes specs are already up to date: report violations with evidence (file:line), never modify specs during review.'
---

# Frontend Review (MyTranslator)

项目级前端审查技能。所有 MyTranslator 前端代码完成后的审查都必须遵守本技能流程。

## 1. 审查前必读（必须）

- `docs/front/审查标准.md` — 审查清单（五维度 + 严重度 + 输出格式），本次审查的主依据
- `docs/front/设计规范.md`、`docs/front/组件规范.md`、`docs/front/交互规范.md` — 被审查代码对应的规范条款
- `CONTEXT.md` — 术语表；审查输出使用其中词汇
- `docs/back/` 中本次改动涉及的接口契约（错误码、字段、状态流转）
- `ROADMAP.md` 对应验收项；`docs/adr/` 相关决策

## 2. 审查规则（必须）

- **默认规范已是最新**：审查时不得修改任何规范文档（`docs/front/`、`CONTEXT.md`、`docs/adr/`）。
- 代码与规范对不上 → **报问题**，格式：违反条款（文件+章节）+ 证据（`文件:行号`）+ 严重度。
- 按 `docs/front/审查标准.md` 五维度逐条过检：需求符合、规范符合、交互与状态、可访问性、工程质量。
- 亮/暗双主题都要检查（代码层面验证两套 palette 下语义 token 正确）。
- 阻断级问题存在 → 结论"不通过"，不得交付。

## 3. 规范缺陷处理（必须）

若审查中发现规范本身有错误、缺失或自相矛盾：

- **不得自行修改规范**，也不得按"推测的意图"审查。
- 作为独立问题报告：规范文件 + 条款 + 缺陷描述 + 建议修改方向。
- 报告交由用户决定是否走 `frontend-coding` 的逐点确认流程修改规范；规范修改完成前，按现有规范条款判定代码合规性。

## 4. 输出格式（必须）

按 `docs/front/审查标准.md` 附录模板：

```
## 审查结果
- 范围：<功能 / 提交>
- 结论：通过 / 不通过（阻断 N / 严重 N / 一般 N / 建议 N）

| 维度 | 条目 | 结果 | 证据 | 严重度 |
|---|---|---|---|---|
| … | … | 通过/不通过 | 文件:行号 | 阻断/严重/一般/建议 |
```

另附"规范缺陷"清单（如有）：规范文件 + 条款 + 缺陷 + 建议。

## 5. 禁止事项

- 禁止在审查中修改代码或规范——审查只报告，修复由编码流程执行。
- 禁止跳过可访问性维度（10 条必检）。
- 禁止以"用户口头要求"覆盖规范条款；口头要求与规范冲突时，报告冲突并按 §3 处理。
