# SQLite 依赖安全更新记录

日期：2026-10-27；发布版本：V0.2.1。

## 问题与处理

V0.2.0 镜像构建产生 NU1903：`Microsoft.EntityFrameworkCore.Sqlite 10.0.9` 经 `SQLitePCLRaw.bundle_e_sqlite3 2.1.11` 引入 `SQLitePCLRaw.lib.e_sqlite3 2.1.11`，涉及 [CVE-2025-6965 / GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)。该问题的原生 SQLite 修复基线为 3.50.2，参见 [SQLite 3.50.2 发布说明](https://sqlite.org/releaselog/3_50_2.html)。

后端显式引用 `SQLitePCLRaw.bundle_e_sqlite3 2.1.14`，使 bundle、provider、core 和原生库采用同一维护版本。保持 EF Core 10.0.9 及数据库模型、迁移和连接方式不变；不抑制 NU1903，不关闭 NuGetAudit，不通过排除原生库隐藏漏洞。

来源评估：[NuGet 官方包](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/2.1.14) 列出的维护者为 Eric Sink，仓库指向 [ericsink/SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw)，Apache-2.0；2.1.14 于 2026-10-13 发布，属于仍在更新的 2.x 维护线。下载的原生库包通过 NuGet.org 仓库签名验证。

## 本地验证

- 强制重新还原整个解决方案，启用全部依赖审计，未使用 HTTP 缓存：无漏洞告警。
- `dotnet list MyTranslator.slnx package --include-transitive --vulnerable --format json`：五个项目均未报告已知漏洞（以本次 NuGet 数据为准）。
- `dotnet nuget why`：原生库实际解析为 2.1.14，不再引入 2.1.11。
- 新增 `SqliteRuntimeTests`，通过数据库连接查询实际加载的 `sqlite_version()`，断言不低于修复基线；Windows x64 实测为 **3.54.0**。
- Release 全量测试 **489 项通过**（API 275、Shared 214）；包括数据库初始化、迁移及翻译流程回归。
- Release 解决方案构建：**0 警告、0 错误**。

本次无前端功能改动；API、Web、Shared 产品版本统一为 0.2.1。Linux 镜像构建与推送结果以 V0.2.1 对应 GitHub Actions 发布记录为准。
