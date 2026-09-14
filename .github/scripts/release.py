"""版本发布辅助：仅使用 Python 标准库，不安装第三方依赖。"""

import argparse
import json
import os
from pathlib import Path
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


def validate_tag(tag):
    if not re.fullmatch(r"[vV][0-9]+\.[0-9]+\.[0-9]+", tag):
        raise ValueError("版本 Tag 必须为 V主版本.次版本.修订号（V/v 均可）。")


def extract_notes(changelog, tag):
    validate_tag(tag)
    # 代码围栏里的二级标题属于示例，不作为版本边界。
    headings = []
    fence = None
    lines = changelog.splitlines(keepends=True)
    for index, line in enumerate(lines):
        if fence:
            if re.fullmatch(r" {0,3}" + re.escape(fence[0]) +
                            r"{" + str(len(fence)) + r",}[ \t]*", line.rstrip("\r\n")):
                fence = None
            continue
        opening = re.match(r" {0,3}(`{3,}|~{3,})", line)
        if opening:
            fence = opening[1]
            continue
        heading = re.match(r" {0,3}##[ \t]+(.+?)[ \t]*$", line.rstrip("\r\n"))
        if heading:
            title = re.sub(r"[ \t]+#+$", "", heading[1])
            headings.append((index, title))

    matches = [i for i, (_, title) in enumerate(headings) if title.casefold() == tag.casefold()]
    if len(matches) != 1:
        raise ValueError(f"changelog.md 必须包含且仅包含一个与 {tag} 对应的二级版本标题。")
    position = matches[0]
    start = headings[position][0] + 1
    end = headings[position + 1][0] if position + 1 < len(headings) else len(lines)
    # 仅去除边缘空行，保留正文缩进与 Markdown 行末双空格。
    body = "".join(lines[start:end]).strip("\r\n")
    if not body.strip():
        raise ValueError(f"changelog.md 中 {tag} 的更新日志为空。")
    return body + "\n"


def github_request(method, url, token, payload=None):
    request = Request(url, method=method, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "Content-Type": "application/json",
        "User-Agent": "MyTranslator-release",
    }, data=None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8"))
    with urlopen(request, timeout=60) as response:
        return json.load(response)


def publish_release(tag, body, repository, token, api_url):
    validate_tag(tag)
    if not body.strip():
        raise ValueError("Release 正文为空，拒绝发布。")
    base = f"{api_url.rstrip('/')}/repos/{repository}/releases"
    try:
        release = github_request("GET", f"{base}/tags/{quote(tag, safe='')}", token)
    except HTTPError as error:
        # 只有确定不存在时才创建，鉴权、限流或服务端故障直接失败。
        if error.code != 404:
            raise
        return github_request("POST", base, token, {
            "tag_name": tag, "name": tag, "body": body,
            "draft": False, "prerelease": False,
        })
    return github_request("PATCH", f"{base}/{release['id']}", token, {
        "name": tag, "body": body, "draft": False, "prerelease": False,
    })


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "publish"))
    parser.add_argument("--tag", required=True)
    parser.add_argument("--notes", type=Path, required=True)
    parser.add_argument("--changelog", type=Path, default=Path("changelog.md"))
    args = parser.parse_args()
    try:
        validate_tag(args.tag)
        if args.command == "prepare":
            body = extract_notes(args.changelog.read_text(encoding="utf-8-sig"), args.tag)
            args.notes.write_text(body, encoding="utf-8", newline="\n")
            print(f"已提取 {args.tag} 的更新日志。")
        else:
            release = publish_release(
                args.tag, args.notes.read_text(encoding="utf-8"),
                os.environ["GITHUB_REPOSITORY"], os.environ["GH_TOKEN"],
                os.environ.get("GITHUB_API_URL", "https://api.github.com"),
            )
            print(f"已发布：{release['html_url']}")
    except (ValueError, OSError, URLError, KeyError) as error:
        # 不输出请求头或响应正文，避免将鉴权信息写入日志。
        print(f"发布失败：{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
