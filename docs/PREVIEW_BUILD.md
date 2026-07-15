# 泺栋chat 发布构建

当前版本为 `1.1`，Windows 目标平台为 Windows 10/11 x64，采用 .NET 10 自包含单文件发布；Android 目标为 Android 10 及以上。

## 限制

- 尚未在真实Windows 10/11环境完成端到端验证；
- 尚未进行Authenticode代码签名；
- Android APK 使用项目独立发布证书签名，证书和口令仅保存在本机离线备份与 GitHub Actions 加密变量中；
- 发布前应在真实 Windows 10/11 和 Android 设备上完成最终验收。

## 验证顺序

1. 对照发布记录验证ZIP的SHA-256；
2. 在隔离的Windows测试账号启动；
3. 验证无效邮箱不会触发邮件发送，并分别测试密码登录、验证码注册和验证码备用登录；
4. 验证登录后可新建对话、流式生成、停止生成和复制当前对话；
5. 确认客户端不会读取或修改用户的 Codex 配置；
6. 验证重复点击、断网、会话过期和上游故障切换；
7. 验证客户端发现新版本后可完成覆盖更新。

## 从 GitHub Release 下载

推送版本标签后，`Build and test` 工作流会自动运行网关测试、核心库测试、Windows 发布构建及可执行文件启动冒烟测试、Android 单元测试和签名校验。全部成功后，Windows EXE、ZIP 与 Android APK 会同步上传至对应 GitHub Release。

Release 内同时包含带版本号的安装 EXE、便携 ZIP、APK 及各自的 `.sha256` 文件。安装程序允许选择目录，并在该目录中创建 `Uninstall.exe`；便携版的所有运行数据保存在解压目录内。可在 PowerShell 中验证 Windows ZIP：

```powershell
$expected = (Get-Content .\LuodongChat-1.1-win-x64-portable.zip.sha256).Trim()
.\deploy\Test-PreviewBuild.ps1 `
  -ZipPath .\LuodongChat-1.1-win-x64-portable.zip `
  -ExpectedSha256 $expected
```
