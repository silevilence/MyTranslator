# 字幕人工验收样例

- `sample.srt`：UTF-8 无 BOM、CRLF、无末尾换行；序号 7/9/15，时间区间 1–4、2.5–5、7–9 秒，包含重叠、多行及粗体/斜体。
- `sample.vtt`：UTF-8 无 BOM、LF、无末尾换行；同一时间轴，带条目标识、定位、NOTE/STYLE/REGION、说话人、颜色及段内时间标记。
- `mixed.txt`：混合上传的文本成员。
- `invalid.srt`：结束早于开始，应以 `subtitle_parse_failed` 拒绝。

这些文件用于真实浏览器上传及导出播放。`.gitattributes` 禁止 Git 转换换行。自动化边界样例由 `SubtitleApiTests.cs` 中的固定文本生成，覆盖 BOM、UTF-16、CR、空正文、损坏编码和非法结构。

实际步骤与结果见 [字幕首版验收记录](../../../../docs/back/字幕首版验收记录.md)。
