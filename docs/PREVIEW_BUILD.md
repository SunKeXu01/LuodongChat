# Windows开发预览版

当前版本为 `0.1.0-preview.17`，Windows 目标平台为 Windows 10/11 x64，采用 .NET 10 自包含单文件发布；Android 预览版目标为 Android 10 及以上。

## 限制

- 尚未在真实Windows 10/11环境完成端到端验证；
- 尚未进行Authenticode代码签名；
- Android APK 使用项目独立发布证书签名，证书和口令仅保存在本机离线备份与 GitHub Actions 加密变量中；
- 不应作为正式公开发行版使用。

## 验证顺序

1. 对照发布记录验证ZIP的SHA-256；
2. 在隔离的Windows测试账号启动；
3. 验证无效邮箱不会触发邮件发送，并分别测试密码登录、验证码注册和验证码备用登录；
4. 确认 `.codex` 目录联接、受管 `config.toml` 和 `auth.json` 内容；
5. 重启Codex并执行普通、流式和工具调用；
6. 修改一个非受管字段后执行恢复，确认该字段被保留；
7. 修改一个受管字段后执行恢复，确认客户端报告冲突；
8. 验证重复点击、多进程、断网和只读文件错误。

## 从 GitHub Release 下载

推送版本标签后，`Build and test` 工作流会自动运行网关测试、核心库测试、Windows 发布构建及可执行文件启动冒烟测试、Android 单元测试和签名校验。全部成功后，Windows EXE、ZIP 与 Android APK 会同步上传至对应 GitHub Release。

Release 内同时包含带版本号的 EXE、ZIP、APK 及各自的 `.sha256` 文件。可在 PowerShell 中验证 Windows ZIP：

```powershell
$expected = (Get-Content .\ChatGPTConnector-0.1.0-preview.17-win-x64.zip.sha256).Trim()
.\deploy\Test-PreviewBuild.ps1 `
  -ZipPath .\ChatGPTConnector-0.1.0-preview.17-win-x64.zip `
  -ExpectedSha256 $expected
```
