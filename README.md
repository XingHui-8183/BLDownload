# BLDownload

Windows B 站（bilibili）视频 / 音频下载器，C#/.NET 9 WPF 实现。

## 功能

- **链接解析**：支持 BV 号 / av 号 / 番剧 ep/ss / b23.tv 短链 / 含中文的整段分享文本
- **清晰度**：免登录可用基础档位；扫码登录（大会员）解锁 1080P / 1080P60 / 4K
- **下载引擎**：B 站官方 API（WBI 签名 + DASH 流 + Referer 校验）为主，Range 多连接分块并行，实时进度与速度显示
- **音频提取**：可选输出 MP3 / FLAC / OPUS / WAV，自动嵌入视频封面与标题/UP 主标签（wav 除外）
- **自动兜底**：API 任一环节失败自动降级 yt-dlp（可自动从 GitHub 拉取 yt-dlp.exe）
- **多 P / 番剧批量**：分 P 列表勾选批量下载
- **界面**：Fluent 风格深色 UI

## 使用

1. 从 [Releases](../../releases) 下载**单文件** `BLDownload.exe` 双击即用（自包含 .NET 9，无需安装运行时）
2. 粘贴分享链接 → 解析 → 选择输出格式与清晰度 → 下载
3. 需要 1080P/4K 时点击右上角"登录"扫码

## 依赖

- **ffmpeg**：合并 DASH 音视频 / 音频转码需要，需在 PATH 中（缺失时视频合并会失败并走 yt-dlp 兜底）
- yt-dlp：兜底通道，缺失时自动下载

## 构建

```
dotnet build BLDownload.csproj -c Release
```

## 作者

[@XingHui-8183](https://github.com/XingHui-8183)

## 免责声明

本项目仅供个人学习备份使用，请遵守 bilibili 用户协议与相关法律法规，勿用于商业分发。
