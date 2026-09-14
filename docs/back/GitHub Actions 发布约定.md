# GitHub Actions 发布约定

对应 ROADMAP「五、GitHub Actions 自动发布」。入口为 [release.yml](../../.github/workflows/release.yml)，发布脚本为 [release.py](../../.github/scripts/release.py)。仅实现 CD，不运行 push/PR CI 或测试流水线。

## 1. 触发与准备

仅推送 `V主版本.次版本.修订号` 格式的 Tag 触发发布，`V` / `v` 均可，例如 `V0.1.0`、`v12.34.567`。普通分支推送、PR、Tag 删除及其他格式（含预发布后缀）不执行发布。

发布前，提交代码、工作流和对应的 `changelog.md`，再在该提交上创建并推送版本 Tag。例如：

```powershell
git tag V0.1.0
git push origin V0.1.0
```

以上命令用于实际发布，不能只将 Tag 指向尚未包含工作流或该版本日志的旧提交。工作流检出触发事件的固定提交 SHA，镜像与日志均来自同一提交。

## 2. 更新日志格式

沿用根目录 [changelog.md](../../changelog.md) 的格式：

```markdown
# Change Log

## V0.1.0

### ✨ 新功能

- 本版本更新内容。
```

按二级版本标题精确匹配 Tag，忽略大小写，必须且只能找到一处。正文从版本标题之后开始，到下一二级标题之前或文件末尾结束；不含版本标题本身和其他版本，保留三级标题、列表、缩进与行末双空格等 Markdown 格式。代码围栏中的标题不会截断日志。支持 UTF-8 BOM 与 CRLF 换行。

缺少文件、版本标题缺失或重复、正文为空时，在登录 GHCR 和构建镜像之前失败，不生成空白 Release。

## 3. 镜像与 Release

按以下顺序执行，任何一步失败均阻止后续步骤：

1. 校验 Tag 并提取 Changelog。
2. 配置 Buildx，使用自动提供的 `GITHUB_TOKEN` 登录 GHCR。
3. 使用仓库根目录作为构建上下文，按原有后端 Dockerfile 构建并推送镜像。
4. 按原有前端 Dockerfile 构建 WASM 与 nginx 镜像并推送，保留 Release 裁剪。
5. 为原始 Tag 创建 GitHub Release；已存在时更新同一 Release 的标题和正文。仅查询返回 404 时创建，鉴权、限流与服务端故障直接报错。

镜像平台为 `linux/amd64`，名称取仓库完整名称转小写后加 `-api` / `-web`，本仓库对应：

| 镜像 | 版本标签示例 | 浮动标签 |
|---|---|---|
| `ghcr.io/silevilence/mytranslator-api` | `0.1.0` | `latest` |
| `ghcr.io/silevilence/mytranslator-web` | `0.1.0` | `latest` |

`V0.1.0` 和 `v0.1.0` 都使用镜像标签 `0.1.0`，Release 关联各自原始 Tag。部署变量使用 `IMAGE_TAG=0.1.0`，不带 `V/v` 前缀。同一版本选择一种 Tag 拼写即可。

发布全流程串行，正在运行的发布不会被新版本中断。`latest` 随实际镜像推送更新，不承诺始终为版本号最大的版本；重跑旧 Tag 也会更新它。两端镜像推送不是原子操作，后端成功而前端失败时，后端镜像可能已经可见，但不会创建 Release。修复外部故障后可在 Actions 重跑失败任务；代码或 Changelog 有误时，需要将修复提交纳入后续发布 Tag。

## 4. 权限与首次发布

工作流仅需要 `contents: write`（Release）和 `packages: write`（GHCR），使用 GitHub 自动注入的 `GITHUB_TOKEN`，无需额外添加发布密钥。仓库或组织策略须允许使用这些权限及工作流引用的 Actions。第三方 Action 固定到完整提交 SHA。

镜像带有 `org.opencontainers.image.source` 标签以关联源码仓库。若 GHCR 中已存在同名包，需要在包设置中授予本仓库 Actions 写入权限。工作流不修改包可见性；公开镜像可匿名拉取，私有镜像需按 [Docker 部署约定](Docker%20部署约定.md) 登录 GHCR。

## 5. 验证

发布辅助脚本仅使用 Python 3 标准库；本地回归命令如下，不加入 CD 流程：

```powershell
python -B -m unittest discover -s .github/scripts -p 'test_*.py' -v
```

工作流可使用 `actionlint .github/workflows/release.yml` 静态检查。已完成离线脚本测试与工作流静态验证；当前本地环境无 Docker，尚未实际执行 GitHub 镜像构建、GHCR 推送或 Release 发布。首次版本 Tag 发布后，需确认 Actions 成功、两端版本镜像可拉取，且 Release 正文与对应 Changelog 一致。

参考：[GitHub Actions 触发过滤语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#filter-pattern-cheat-sheet)、[GHCR 镜像发布](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images)、[GitHub Release API](https://docs.github.com/en/rest/releases/releases)。
