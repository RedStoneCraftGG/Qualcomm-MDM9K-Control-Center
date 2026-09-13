using System;
using System.Runtime.InteropServices;

namespace ModemController
{
    // Native Windows notification-area integration.
    // WinUI 3 does not support System.Windows.Forms/NotifyIcon directly, so this
    // service uses Shell_NotifyIcon and a small hidden Win32 message window.
    public sealed class TrayService : IDisposable
    {
        private const string WindowClassName = "QualcommMdm9kControlCenterTrayWindow";
        private const uint WM_APP = 0x8000;
        private const uint WM_TRAYICON = WM_APP + 1;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_COMMAND = 0x0111;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint MF_STRING = 0x00000000;

        private const uint ID_TRAY_OPEN = 1001;
        private const uint ID_TRAY_EXIT = 1002;
        private const int IDI_APPLICATION = 32512;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;
        private const int SM_CXSMICON = 49;

        private readonly Action _showAction;
        private readonly Action _exitAction;
        private readonly WndProc _wndProc;
        private readonly string _className;
        private IntPtr _messageWindow;
        private IntPtr _hIcon;
        private bool _ownsIcon;
        private bool _iconVisible;
        private bool _disposed;
        private bool _classRegistered;

        public TrayService(Action showAction, Action exitAction)
        {
            _showAction = showAction ?? throw new ArgumentNullException(nameof(showAction));
            _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));
            _wndProc = WindowProc;
            _className = WindowClassName + "_" + Environment.ProcessId;
            _hIcon = LoadAppIcon();

            CreateMessageWindow();
            AddTrayIcon();
        }

        public void Show()
        {
            if (_disposed || _iconVisible)
                return;

            AddTrayIcon();
        }

        public void Hide()
        {
            if (_disposed || !_iconVisible)
                return;

            RemoveTrayIcon();
        }

        private void CreateMessageWindow()
        {
            IntPtr hInstance = GetModuleHandle(null);

            var windowClass = new WNDCLASS
            {
                style = 0,
                lpfnWndProc = _wndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = _hIcon,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = _className
            };

            ushort atom = RegisterClass(ref windowClass);
            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                // ERROR_CLASS_ALREADY_EXISTS is harmless for our process-specific class.
                if (error != 1410)
                    throw new InvalidOperationException($"Unable to register tray window class. Win32 error: {error}");
            }
            else
            {
                _classRegistered = true;
            }

            _messageWindow = CreateWindowEx(
                0,
                _className,
                "Qualcomm MDM9K 4G Control Center Tray",
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_messageWindow == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Unable to create tray message window. Win32 error: {error}");
            }
        }

        private void AddTrayIcon()
        {
            if (_messageWindow == IntPtr.Zero)
                return;

            var data = CreateNotifyIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Unable to add notification-area icon. Win32 error: {error}");
            }

            _iconVisible = true;
        }

        private void RemoveTrayIcon()
        {
            if (_messageWindow == IntPtr.Zero)
                return;

            var data = CreateNotifyIconData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconVisible = false;
        }

        private NOTIFYICONDATA CreateNotifyIconData(uint flags)
        {
            return new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _messageWindow,
                uID = 1,
                uFlags = flags,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = "Qualcomm MDM9K 4G Control Center"
            };
        }

        private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WM_TRAYICON)
            {
                uint eventCode = unchecked((uint)lParam.ToInt64());

                if (eventCode == WM_LBUTTONDBLCLK)
                {
                    _showAction();
                    return IntPtr.Zero;
                }

                if (eventCode == WM_RBUTTONUP || eventCode == WM_CONTEXTMENU)
                {
                    ShowContextMenu();
                    return IntPtr.Zero;
                }
            }
            else if (message == WM_COMMAND)
            {
                uint command = unchecked((uint)wParam.ToInt64()) & 0xFFFF;

                if (command == ID_TRAY_OPEN)
                {
                    _showAction();
                    return IntPtr.Zero;
                }

                if (command == ID_TRAY_EXIT)
                {
                    _exitAction();
                    return IntPtr.Zero;
                }
            }

            return DefWindowProc(hWnd, message, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            try
            {
                AppendMenu(menu, MF_STRING, ID_TRAY_OPEN, "Open Control Center");
                AppendMenu(menu, MF_STRING, ID_TRAY_EXIT, "Exit");

                if (!GetCursorPos(out POINT point))
                    return;

                SetForegroundWindow(_messageWindow);
                TrackPopupMenu(
                    menu,
                    TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON,
                    point.X,
                    point.Y,
                    0,
                    _messageWindow,
                    IntPtr.Zero);

                // Required so the menu closes cleanly when invoked from the tray.
                PostMessage(_messageWindow, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            RemoveTrayIcon();
            DestroyLoadedIcon();

            if (_messageWindow != IntPtr.Zero)
            {
                DestroyWindow(_messageWindow);
                _messageWindow = IntPtr.Zero;
            }

            if (_classRegistered)
            {
                UnregisterClass(_className, GetModuleHandle(null));
                _classRegistered = false;
            }
        }

        private IntPtr LoadAppIcon()
        {
            if (AppIcon.TryGetIcoPath(out string iconPath))
            {
                int size = GetSystemMetrics(SM_CXSMICON);
                if (size <= 0)
                    size = 16;

                IntPtr loaded = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);
                if (loaded == IntPtr.Zero)
                    loaded = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);

                if (loaded != IntPtr.Zero)
                {
                    _ownsIcon = true;
                    return loaded;
                }
            }

            _ownsIcon = false;
            return LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        }

        private void DestroyLoadedIcon()
        {
            if (_ownsIcon && _hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
                _ownsIcon = false;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool TrackPopupMenu(
            IntPtr hMenu,
            uint uFlags,
            int x,
            int y,
            int nReserved,
            IntPtr hWnd,
            IntPtr prcRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
