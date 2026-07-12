# Windows开发预览版

当前版本为 `0.1.0-preview.1`，目标平台为Windows 10/11 x64，采用.NET 10自包含发布。

## 限制

- 尚未在真实Windows 10/11环境完成端到端验证；
- 尚未进行Authenticode代码签名；
- 网关密钥需手动输入；
- 暂无安装程序、自动更新和卸载入口；
- 不应作为正式公开发行版使用。

## 验证顺序

1. 对照发布记录验证ZIP的SHA-256；
2. 在隔离的Windows测试账号启动；
3. 使用测试网关密钥执行开启连接；
4. 确认备份目录、`config.toml`和`auth.json`内容；
5. 重启Codex并执行普通、流式和工具调用；
6. 修改一个非受管字段后执行恢复，确认该字段被保留；
7. 修改一个受管字段后执行恢复，确认客户端报告冲突；
8. 验证重复点击、多进程、断网和只读文件错误。

## 从GitHub Actions下载

推送到`main`后，`Build and test`工作流会自动运行网关测试、核心库测试和Windows发布构建。成功后，在该次运行的Artifacts区域下载`ChatGPTConnector-0.1.0-preview.1-win-x64`。

下载的Artifact内包含发布ZIP和对应的`.sha256`文件。可在PowerShell中执行：

```powershell
$expected = (Get-Content .\ChatGPTConnector-0.1.0-preview.1-win-x64.sha256).Trim()
.\deploy\Test-PreviewBuild.ps1 `
  -ZipPath .\ChatGPTConnector-0.1.0-preview.1-win-x64.zip `
  -ExpectedSha256 $expected
```
