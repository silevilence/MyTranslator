"""发布逻辑的离线回归测试；不由 CD 工作流运行。"""

import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

import release


class ReleaseNotesTests(unittest.TestCase):
    def test_matches_tag_case_and_preserves_markdown(self):
        body = "### ✨ 新功能\n\n- 中文说明  \n  下一行\n\n```markdown\n## 示例\n```\n"
        changelog = f"# Change Log\n\n## V0.2.0\n\n- 新版\n\n## V0.1.0\n\n{body}\n## V0.0.1\n\n- 旧版\n"
        for tag in ("V0.1.0", "v0.1.0"):
            with self.subTest(tag=tag):
                self.assertEqual(body, release.extract_notes(changelog, tag))

    def test_last_version_and_lowercase_heading(self):
        self.assertEqual("- 最初版本\n", release.extract_notes("## v0.1.0\n\n- 最初版本", "V0.1.0"))

    def test_next_nonversion_heading_ends_notes(self):
        self.assertEqual("- 内容\n", release.extract_notes("## V0.1.0\n- 内容\n## 其他\n参考", "v0.1.0"))

    def test_accepts_multidigit_versions(self):
        release.validate_tag("v12.34.567")

    def test_rejects_invalid_tags(self):
        for tag in ("main", "0.1.0", "v1.0", "V1.2.3.4", "v1.2.3-beta", "v1.2.3/extra", "v１.2.3", "V1.2.3\n"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.validate_tag(tag)

    def test_missing_empty_or_duplicate_version_fails(self):
        for changelog in ("# Change Log", "## V0.1.00\n- 其他", "## V0.1.0\n \n## V0.0.1\n- 旧版", "## V0.1.0\n", "## V0.1.0\n- 一\n## v0.1.0\n- 二"):
            with self.subTest(changelog=changelog), self.assertRaises(ValueError):
                release.extract_notes(changelog, "V0.1.0")

    def test_cli_reads_bom_crlf_and_writes_utf8(self):
        with tempfile.TemporaryDirectory() as directory:
            changelog = Path(directory) / "changelog.md"
            notes = Path(directory) / "notes.md"
            changelog.write_bytes("\ufeff# Change Log\r\n\r\n## V0.1.0\r\n\r\n### ✨ 新功能\r\n\r\n- 中文\r\n".encode("utf-8"))
            result = self.run_prepare(changelog, notes)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("### ✨ 新功能\n\n- 中文\n".encode("utf-8"), notes.read_bytes())

    def test_cli_missing_file_or_version_fails_without_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            changelog = Path(directory) / "changelog.md"
            notes = Path(directory) / "notes.md"
            for content in (None, "## V0.2.0\n- 其他版本"):
                if content:
                    changelog.write_text(content, encoding="utf-8")
                result = self.run_prepare(changelog, notes)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(notes.exists())

    @staticmethod
    def run_prepare(changelog, notes):
        return subprocess.run([
            sys.executable, str(Path(release.__file__)), "prepare", "--tag", "v0.1.0",
            "--changelog", str(changelog), "--notes", str(notes),
        ], capture_output=True)


class PublishReleaseTests(unittest.TestCase):
    def publish(self, tag="V0.1.0", body="### ✨ 新功能\n\n- 内容\n"):
        return release.publish_release(tag, body, "owner/repo", "test-token", "https://api.github.com")

    @patch("release.github_request")
    def test_creates_release_for_original_tag_on_404(self, request):
        request.side_effect = [HTTPError("url", 404, "Not Found", {}, None), {"id": 1}]
        self.assertEqual({"id": 1}, self.publish())
        self.assertEqual("https://api.github.com/repos/owner/repo/releases/tags/V0.1.0", request.call_args_list[0].args[1])
        method, url, _, payload = request.call_args.args
        self.assertEqual("POST", method)
        self.assertEqual("https://api.github.com/repos/owner/repo/releases", url)
        self.assertEqual("V0.1.0", payload["tag_name"])
        self.assertEqual("### ✨ 新功能\n\n- 内容\n", payload["body"])
        self.assertFalse(payload["draft"])

    @patch("release.github_request")
    def test_rerun_updates_existing_release(self, request):
        request.side_effect = [{"id": 42}, {"id": 42}]
        self.publish("v0.1.0")
        method, url, _, payload = request.call_args.args
        self.assertEqual("PATCH", method)
        self.assertEqual("https://api.github.com/repos/owner/repo/releases/42", url)
        self.assertEqual("v0.1.0", payload["name"])
        self.assertNotIn("tag_name", payload)

    @patch("release.github_request")
    def test_auth_rate_limit_and_server_errors_do_not_create(self, request):
        for status in (401, 403, 429, 500):
            request.reset_mock()
            request.side_effect = HTTPError("url", status, "失败", {}, None)
            with self.subTest(status=status), self.assertRaises(HTTPError):
                self.publish()
            self.assertEqual(1, request.call_count)

    @patch("release.github_request")
    def test_invalid_tag_or_empty_body_never_accesses_network(self, request):
        for tag, body in (("main", "内容"), ("V0.1.0", " \n")):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                self.publish(tag, body)
        request.assert_not_called()

    @patch("release.urlopen")
    def test_http_request_preserves_body_and_authenticates(self, urlopen):
        urlopen.return_value = io.BytesIO(b'{"id":42}')
        body = '### 更新\n\n- "引号"、`代码`、$() 和 \\ 路径\n'
        self.assertEqual({"id": 42}, release.github_request("POST", "https://api.github.com/test", "test-token", {"body": body}))
        request = urlopen.call_args.args[0]
        self.assertEqual("POST", request.method)
        self.assertEqual("Bearer test-token", request.get_header("Authorization"))
        self.assertEqual(body, json.loads(request.data)["body"])
        self.assertEqual(60, urlopen.call_args.kwargs["timeout"])


if __name__ == "__main__":
    unittest.main()
