# 青苹果下载器

青苹果下载器是一套“Google 表格框选 + Windows 本地下载”的视频与图片素材工具。

- 在 Google Sheets 中一次框选 10 个、20 个或更多带超链接的单元格。
- 表格脚本只把链接写入隐藏队列，不在 Apps Script 中搬运大文件。
- Windows 客户端使用当前 Google 账号通过 Drive API 下载，支持私有文件、失败重试和断点续传。
- 默认同时下载 2 个文件；这是并发数，不是框选数量限制。
- 每批自动创建独立文件夹，保留 Drive 原文件名。
- 手动粘贴页会读取剪贴板中的 HTML/RTF 富文本，复制文字超链接后按 Ctrl+V 即可提取真实 Drive 地址。
- ZIP 默认关闭；可在设置中选择“完成后额外生成 ZIP”。

## 目录

- `src/GreenAppleDownloader`：.NET 10 / WPF Windows 客户端源码
- `apps-script/Code.gs`：复制到目标 Google 表格的 Apps Script
- `docs/配置说明.html`：完整的首次配置向导
- `tests`：不依赖网络的解析与文件名测试

## 开发构建

```powershell
dotnet build .\src\GreenAppleDownloader\GreenAppleDownloader.csproj
```

程序不会内置、上传或共享 OAuth 凭据。OAuth JSON 路径保存在本机设置中，登录令牌通过 Windows DPAPI 加密并存储在当前 Windows 用户目录下。

## 如何发布新版本

本项目使用 GitHub Actions 自动构建和发布。每次发布新版本只需要创建一个 Git Tag 并推送即可。Release 产物由 GitHub Actions 自动上传，并附带可验证的 Attestation 构建来源证明；请勿在 GitHub 网页中手动替换或补传 Release 文件。

### 发布步骤

#### 1. 确保代码已提交并推送

```bash
# 查看当前状态
git status

# 添加改动并提交（请把说明替换成实际内容）
git add .
git commit -m "你的改动说明"

# 推送到 GitHub
git push origin main
```

#### 2. 创建版本 Tag

Git Tag 使用 `v主版本.次版本.修订版本` 格式，例如 `v1.0.0`、`v1.1.0` 或 `v2.0.0`。

```bash
git tag -a v1.0.1 -m "Release version 1.0.1"
```

#### 3. 推送 Tag 触发自动构建

```bash
git push origin v1.0.1
```

推送后，GitHub Actions 会构建 Windows x64 自包含版本、运行烟雾测试、生成 ZIP、签署构建来源证明，并创建 GitHub Release。

#### 4. 查看构建结果

- 构建进度：打开仓库的 **Actions** 页面。
- 发布结果：打开仓库的 **Releases** 页面。
- 来源验证：可使用 `gh attestation verify <ZIP文件> --repo secure-artifacts/xiazai`。

### 版本号说明

| 版本号格式 | 什么时候用 | 示例 |
|-----------|-----------|------|
| `vX.0.0` | 重大更新或不兼容改动 | `v2.0.0` |
| `vX.Y.0` | 新增功能 | `v1.1.0` |
| `vX.Y.Z` | 修复问题 | `v1.0.1` |

### 如果构建失败

1. 在仓库的 **Actions** 页面查看失败日志并修复代码或 workflow。
2. 删除失败的本地与远程 Tag。
3. 修复完成后，重新创建并推送相同版本的 Tag。

```bash
git tag -d v1.0.1
git push origin :refs/tags/v1.0.1
git tag -a v1.0.1 -m "Release version 1.0.1"
git push origin v1.0.1
```
