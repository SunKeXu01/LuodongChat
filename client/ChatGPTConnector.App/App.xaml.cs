using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ChatGPTConnector.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ChatGPTConnector.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0].Equals("--restore-watchdog", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(e.Args[1], out var parentProcessId))
        {
            try { Process.GetProcessById(parentProcessId).WaitForExit(); } catch { }
            new ManagedCodexEnvironment().Restore();
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
            "ChatGPT 连接器",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteCrashLog(Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatGPTConnector",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"ChatGPT Connector {DateTimeOffset.Now:O}{Environment.NewLine}{error}", new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return "（错误日志写入失败）";
        }
    }
}
