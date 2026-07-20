using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public partial class DiagnosticDialog : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly Uri Gateway = new("https://520skx.com");
    private readonly DiagnosticClient _client = new(Http);
    private readonly string _token;
    private readonly string _version;
    private readonly string _errorCode;
    private readonly ObservableCollection<HistoryRow> _history = [];
    private CancellationTokenSource? _uploadCancellation;

    public DiagnosticDialog(string token, string? version, string errorCode = "MANUAL_DIAGNOSTIC")
    {
        InitializeComponent(); _token=token; _version=version ?? "unknown"; _errorCode=errorCode;
        HistoryList.ItemsSource=_history; Loaded += async (_,_) => await RefreshHistoryAsync();
    }
    private DiagnosticRange SelectedRange => RangeInput.SelectedIndex switch { 1=>DiagnosticRange.ThirtyMinutes,2=>DiagnosticRange.TwentyFourHours,_=>DiagnosticRange.Related };
    private void Preview_OnClick(object sender,RoutedEventArgs e)
    {
        try { var package=DiagnosticPackageBuilder.Create(_errorCode,SelectedRange,_version); MessageBox.Show(this,$"诊断包大小：{package.Data.Length/1024d/1024d:0.00} MB\n文件数量：{package.FileCount}\n敏感信息扫描：已完成\n脱敏字段：{package.RedactedCount} 项\n\n不包含对话正文、文件正文或身份凭据。","诊断包预览",MessageBoxButton.OK,MessageBoxImage.Information); }
        catch(Exception error){StatusText.Text=error.Message;}
    }
    private async void Upload_OnClick(object sender,RoutedEventArgs e)
    {
        if(_uploadCancellation is not null){_uploadCancellation.Cancel();return;}
        try
        {
            UploadButton.Content="取消上传"; UploadProgress.Visibility=Visibility.Visible; UploadProgress.Value=0; StatusText.Text="正在整理并脱敏…";
            var package=await Task.Run(()=>DiagnosticPackageBuilder.Create(_errorCode,SelectedRange,_version));
            _uploadCancellation=new(); var progress=new Progress<double>(value=>{UploadProgress.Value=value*100;StatusText.Text=$"正在上传 {package.Data.Length*value/1024d/1024d:0.0} MB / {package.Data.Length/1024d/1024d:0.0} MB";});
            var item=await _client.UploadAsync(Gateway,_token,package,_version,progress,_uploadCancellation.Token);
            StatusText.Text=$"诊断日志已上传 · 编号 {item.Id} · 将于 {item.ExpiresAt.LocalDateTime:yyyy年M月d日} 删除";
            Clipboard.SetText(item.Id); await RefreshHistoryAsync();
        }
        catch(OperationCanceledException){StatusText.Text="上传已取消，可稍后重试。";}
        catch(Exception error){StatusText.Text=$"上传失败：{error.Message}";}
        finally{_uploadCancellation?.Dispose();_uploadCancellation=null;UploadButton.Content="同意并上传";UploadProgress.Visibility=Visibility.Collapsed;}
    }
    private async Task RefreshHistoryAsync(){try{var items=await _client.ListAsync(Gateway,_token);_history.Clear();foreach(var item in items)_history.Add(new(item.Id,$"{item.Status} · {item.PackageSize/1024d/1024d:0.00} MB · {item.ExpiresAt.LocalDateTime:M月d日}删除"));}catch{}}
    private void CopyId_OnClick(object sender,RoutedEventArgs e){if(sender is Button{Tag:string id}){Clipboard.SetText(id);StatusText.Text="诊断编号已复制。";}}
    private async void Delete_OnClick(object sender,RoutedEventArgs e){if(sender is not Button{Tag:string id})return;if(MessageBox.Show(this,$"立即删除服务器上的诊断日志 {id}？","删除诊断日志",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{await _client.DeleteAsync(Gateway,_token,id);await RefreshHistoryAsync();StatusText.Text="服务器诊断日志已删除。";}catch(Exception error){StatusText.Text=error.Message;}}
    private void OpenLogs_OnClick(object sender,RoutedEventArgs e){Directory.CreateDirectory(ApplicationDirectories.Logs);Process.Start(new ProcessStartInfo(ApplicationDirectories.Logs){UseShellExecute=true});}
    private void ClearLogs_OnClick(object sender,RoutedEventArgs e)
    {
        if(MessageBox.Show(this,"清理本机保存的诊断日志？这不会删除已上传到服务器的日志。","清理本地日志",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        DiagnosticLog.ClearLocalLogs(); StatusText.Text="本地诊断日志已清理。";
    }
    private sealed record HistoryRow(string Id,string Detail);
}
