using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ChatGPTConnector.App;

internal sealed class TrayIconService : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private const int LeftButtonDoubleClick = 0x0203;
    private const int RightButtonUp = 0x0205;
    private const uint AddIcon = 0;
    private const uint DeleteIcon = 2;
    private const uint MessageFlag = 0x01;
    private const uint IconFlag = 0x02;
    private const uint TipFlag = 0x04;
    private const uint MenuString = 0x0000;
    private const uint MenuSeparator = 0x0800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint NullMessage = 0x0000;
    private const uint ShowCommand = 1;
    private const uint RestartCommand = 2;
    private const uint ExitCommand = 3;

    private readonly HwndSource _source;
    private readonly Action _showWindow;
    private readonly Action _restartApplication;
    private readonly Action _exitApplication;
    private NotifyIconData _data;
    private IntPtr _iconHandle;
    private bool _disposed;

    public TrayIconService(string tooltip, Action showWindow, Action restartApplication, Action exitApplication)
    {
        _showWindow = showWindow;
        _restartApplication = restartApplication;
        _exitApplication = exitApplication;
        _source = new HwndSource(new HwndSourceParameters("ChatGPTConnector.TrayIcon")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000)
        });
        _source.AddHook(WindowMessageHook);

        _iconHandle = ExtractApplicationIcon();
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _source.Handle,
            Id = 1,
            Flags = MessageFlag | IconFlag | TipFlag,
            CallbackMessage = CallbackMessage,
            IconHandle = _iconHandle,
            Tip = tooltip
        };

        if (!ShellNotifyIcon(AddIcon, ref _data))
        {
            Dispose();
            throw new InvalidOperationException("无法创建系统托盘图标。");
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != CallbackMessage) return IntPtr.Zero;

        switch (lParam.ToInt32())
        {
            case LeftButtonDoubleClick:
                _showWindow();
                handled = true;
                break;
            case RightButtonUp:
                ShowContextMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MenuString, new UIntPtr(ShowCommand), "显示主界面");
            AppendMenu(menu, MenuString, new UIntPtr(RestartCommand), "重新启动");
            AppendMenu(menu, MenuSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MenuString, new UIntPtr(ExitCommand), "退出");

            if (!GetCursorPos(out var cursor)) return;
            SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenuEx(
                menu,
                TrackRightButton | TrackReturnCommand,
                cursor.X,
                cursor.Y,
                _source.Handle,
                IntPtr.Zero);
            PostMessage(_source.Handle, NullMessage, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case ShowCommand:
                    _showWindow();
                    break;
                case RestartCommand:
                    _restartApplication();
                    break;
                case ExitCommand:
                    _exitApplication();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr ExtractApplicationIcon()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return CopyIcon(LoadIcon(IntPtr.Zero, new IntPtr(32512)));

        ExtractIconEx(path, 0, out var large, out var small, 1);
        if (small != IntPtr.Zero)
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            return small;
        }
        return large != IntPtr.Zero ? large : CopyIcon(LoadIcon(IntPtr.Zero, new IntPtr(32512)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_data.WindowHandle != IntPtr.Zero) ShellNotifyIcon(DeleteIcon, ref _data);
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _source.RemoveHook(WindowMessageHook);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) => Shell_NotifyIcon(message, ref data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr largeIcon, out IntPtr smallIcon, uint iconCount);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
