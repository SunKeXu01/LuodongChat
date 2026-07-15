using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ChatGPTConnector.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(true, @"Local\LuodongChat.WindowsClient", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("泺栋chat 已经在运行，请在任务栏托盘中打开主界面。", "泺栋chat",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            var smokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
            var window = new MainWindow(skipStartupChecks: smokeTest);
            window.Show();
            if (smokeTest)
            {
                window.PrepareForExit();
                window.Close();
                Shutdown(0);
            }
        }
        catch (Exception error)
        {
            ReportFatalError(error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception error) WriteCrashLog(error);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatalError(e.Exception);
        e.Handled = true;
        (Current.MainWindow as MainWindow)?.PrepareForExit();
        Current.Shutdown(1);
    }

    private static void ReportFatalError(Exception error)
    {
        var logPath = WriteCrashLog(error);
        MessageBox.Show(
            $"客户端启动失败。错误记录已保存到：\n{logPath}\n\n请联系客服 QQ：2554798585。",
            "泺栋chat",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteCrashLog(Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LuodongChat",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"泺栋chat {DateTimeOffset.Now:O}{Environment.NewLine}{error}", new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return "（错误日志写入失败）";
        }
    }
}
