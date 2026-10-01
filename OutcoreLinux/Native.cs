using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OutcoreLinux;

// Win32 surface used by the shell replacement. Everything here is implemented by Wine's
// user32/gdi32/comctl32/shell32, which is the whole point: we stop relying on the parts of
// Windows that Wine does not have (DWM composition, explorer.exe COM, UI Automation).
internal static class Native
{
	public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
	public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
	public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT
	{
		public int left, top, right, bottom;
		public int Width => right - left;
		public int Height => bottom - top;
		public bool Contains(int x, int y) => x >= left && x < right && y >= top && y < bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct POINT
	{
		public int x, y;
		public POINT(int x, int y) { this.x = x; this.y = y; }
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MSG
	{
		public IntPtr hwnd;
		public uint message;
		public IntPtr wParam;
		public IntPtr lParam;
		public uint time;
		public POINT pt;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct WNDCLASSEX
	{
		public uint cbSize;
		public uint style;
		public IntPtr lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public IntPtr hInstance;
		public IntPtr hIcon;
		public IntPtr hCursor;
		public IntPtr hbrBackground;
		public string lpszMenuName;
		public string lpszClassName;
		public IntPtr hIconSm;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct PAINTSTRUCT
	{
		public IntPtr hdc;
		public int fErase;
		public RECT rcPaint;
		public int fRestore;
		public int fIncUpdate;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
		public byte[] rgbReserved;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct MONITORINFOEX
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string szDevice;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct SHFILEINFO
	{
		public IntPtr hIcon;
		public int iIcon;
		public uint dwAttributes;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szDisplayName;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
		public string szTypeName;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct SHFILEOPSTRUCT
	{
		public IntPtr hwnd;
		public uint wFunc;
		public string pFrom;
		public string pTo;
		public ushort fFlags;
		public int fAnyOperationsAborted;
		public IntPtr hNameMappings;
		public string lpszProgressTitle;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct BITMAPINFOHEADER
	{
		public uint biSize;
		public int biWidth;
		public int biHeight;
		public ushort biPlanes;
		public ushort biBitCount;
		public uint biCompression;
		public uint biSizeImage;
		public int biXPelsPerMeter;
		public int biYPelsPerMeter;
		public uint biClrUsed;
		public uint biClrImportant;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct ICONINFO
	{
		public int fIcon;
		public int xHotspot;
		public int yHotspot;
		public IntPtr hbmMask;
		public IntPtr hbmColor;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct INITCOMMONCONTROLSEX
	{
		public int dwSize;
		public uint dwICC;
	}

	public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
	public const uint WS_CHILD = 0x40000000;
	public const uint WS_VISIBLE = 0x10000000;
	public const uint WS_CLIPCHILDREN = 0x02000000;
	public const uint WS_CLIPSIBLINGS = 0x04000000;
	public const uint WS_TABSTOP = 0x00010000;
	public const uint WS_BORDER = 0x00800000;
	public const uint WS_EX_CLIENTEDGE = 0x00000200;
	public const uint WS_EX_TOOLWINDOW = 0x00000080;
	public const uint WS_EX_LAYERED = 0x00080000;
	public const uint WS_EX_TRANSPARENT = 0x00000020;
	public const uint WS_EX_TOPMOST = 0x00000008;

	public const int GWL_STYLE = -16;
	public const int GWL_EXSTYLE = -20;

	public const uint WM_DESTROY = 0x0002;
	public const uint WM_SIZE = 0x0005;
	public const uint WM_PAINT = 0x000F;
	public const uint WM_CLOSE = 0x0010;
	public const uint WM_ERASEBKGND = 0x0014;
	public const uint WM_SETFONT = 0x0030;
	public const uint WM_NOTIFY = 0x004E;
	public const uint WM_KEYDOWN = 0x0100;
	public const uint WM_COMMAND = 0x0111;
	public const uint WM_TIMER = 0x0113;
	public const uint WM_MOUSEMOVE = 0x0200;
	public const uint WM_LBUTTONDOWN = 0x0201;
	public const uint WM_LBUTTONUP = 0x0202;
	public const uint WM_RBUTTONDOWN = 0x0204;
	public const uint WM_RBUTTONUP = 0x0205;
	public const uint WM_MOUSEWHEEL = 0x020A;
	public const uint WM_CAPTURECHANGED = 0x0215;
	public const uint WM_APP = 0x8000;

	public const int SW_HIDE = 0;
	public const int SW_SHOWNORMAL = 1;
	public const int SW_SHOWMINIMIZED = 2;
	public const int SW_MINIMIZE = 6;
	public const int SW_SHOWNOACTIVATE = 4;
	public const int SW_SHOW = 5;
	public const int SW_RESTORE = 9;

	public const uint SWP_NOSIZE = 0x0001;
	public const uint SWP_NOMOVE = 0x0002;
	public const uint SWP_NOZORDER = 0x0004;
	public const uint SWP_NOACTIVATE = 0x0010;
	public const uint SWP_SHOWWINDOW = 0x0040;
	public static readonly IntPtr HWND_TOP = IntPtr.Zero;
	public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
	public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
	public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
	public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

	public const uint GW_HWNDNEXT = 2;
	public const uint GA_ROOT = 2;

	public const uint MONITORINFOF_PRIMARY = 1;

	public const int VK_LBUTTON = 0x01;
	public const int VK_BACK = 0x08;
	public const int VK_RETURN = 0x0D;
	public const int VK_CONTROL = 0x11;
	public const int VK_DELETE = 0x2E;
	public const int VK_F2 = 0x71;
	public const int VK_F5 = 0x74;

	public const int IDC_ARROW = 32512;
	public const int IDC_CROSS = 32515;
	public const int IDC_HAND = 32649;
	public const int IDC_NO = 32648;

	public const int COLOR_WINDOW = 5;
	public const int COLOR_BTNFACE = 15;

	public const uint SHGFI_ICON = 0x000000100;
	public const uint SHGFI_DISPLAYNAME = 0x000000200;
	public const uint SHGFI_SYSICONINDEX = 0x000004000;
	public const uint SHGFI_LARGEICON = 0x000000000;
	public const uint SHGFI_SMALLICON = 0x000000001;
	public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
	public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
	public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

	public const uint FO_DELETE = 3;
	public const ushort FOF_SILENT = 0x0004;
	public const ushort FOF_NOCONFIRMATION = 0x0010;
	public const ushort FOF_ALLOWUNDO = 0x0040;
	public const ushort FOF_NOERRORUI = 0x0400;

	public const uint DI_NORMAL = 3;
	public const int DT_CENTER = 0x1;
	public const int DT_VCENTER = 0x4;
	public const int DT_SINGLELINE = 0x20;
	public const int DT_END_ELLIPSIS = 0x8000;
	public const int TRANSPARENT = 1;
	public const uint SRCCOPY = 0x00CC0020;
	public const int HALFTONE = 4;
	public const int DEFAULT_GUI_FONT = 17;

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

	[DllImport("user32.dll")]
	public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

	[DllImport("user32.dll")]
	public static extern bool TranslateMessage(ref MSG msg);

	[DllImport("user32.dll")]
	public static extern IntPtr DispatchMessageW(ref MSG msg);

	[DllImport("user32.dll")]
	public static extern bool IsDialogMessageW(IntPtr hDlg, ref MSG msg);

	[DllImport("user32.dll")]
	public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

	[DllImport("user32.dll")]
	public static extern bool DestroyWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool ShowWindow(IntPtr hWnd, int cmd);

	[DllImport("user32.dll")]
	public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

	[DllImport("user32.dll")]
	public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

	[DllImport("user32.dll")]
	public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

	[DllImport("user32.dll")]
	public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

	[DllImport("user32.dll")]
	public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern bool SetWindowTextW(IntPtr hWnd, string text);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

	[DllImport("user32.dll")]
	public static extern int GetWindowTextLengthW(IntPtr hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int max);

	[DllImport("user32.dll")]
	public static extern bool IsWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern IntPtr SetFocus(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

	[DllImport("user32.dll")]
	public static extern IntPtr GetTopWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

	[DllImport("user32.dll")]
	public static extern IntPtr WindowFromPoint(POINT pt);

	[DllImport("user32.dll")]
	public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

	[DllImport("user32.dll")]
	public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

	[DllImport("user32.dll")]
	public static extern bool GetCursorPos(out POINT pt);

	[DllImport("user32.dll")]
	public static extern short GetAsyncKeyState(int vk);

	[DllImport("user32.dll")]
	public static extern short GetKeyState(int vk);

	[DllImport("user32.dll")]
	public static extern IntPtr SetCapture(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool ReleaseCapture();

	[DllImport("user32.dll")]
	public static extern IntPtr GetCapture();

	[DllImport("user32.dll")]
	public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

	[DllImport("user32.dll")]
	public static extern IntPtr SetCursor(IntPtr cursor);

	[DllImport("user32.dll")]
	public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr proc);

	[DllImport("user32.dll")]
	public static extern bool KillTimer(IntPtr hWnd, IntPtr id);

	[DllImport("user32.dll")]
	public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

	[DllImport("user32.dll")]
	public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

	[DllImport("user32.dll")]
	public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

	[DllImport("user32.dll")]
	public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, int format);

	[DllImport("user32.dll")]
	public static extern IntPtr GetSysColorBrush(int index);

	[DllImport("user32.dll")]
	public static extern IntPtr GetDC(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

	[DllImport("user32.dll")]
	public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);

	[DllImport("user32.dll")]
	public static extern bool DestroyIcon(IntPtr icon);

	[DllImport("user32.dll")]
	public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

	[DllImport("user32.dll")]
	public static extern int GetSystemMetrics(int index);

	[DllImport("user32.dll")]
	public static extern uint GetDoubleClickTime();

	[DllImport("user32.dll")]
	public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

	[DllImport("user32.dll")]
	public static extern bool OpenIcon(IntPtr hWnd);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

	[DllImport("gdi32.dll")]
	public static extern bool DeleteDC(IntPtr hdc);

	[DllImport("gdi32.dll")]
	public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

	[DllImport("gdi32.dll")]
	public static extern bool DeleteObject(IntPtr obj);

	[DllImport("gdi32.dll")]
	public static extern IntPtr GetStockObject(int index);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreateSolidBrush(uint color);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreatePen(int style, int width, uint color);

	[DllImport("gdi32.dll")]
	public static extern int SetBkMode(IntPtr hdc, int mode);

	[DllImport("gdi32.dll")]
	public static extern uint SetTextColor(IntPtr hdc, uint color);

	[DllImport("gdi32.dll")]
	public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

	[DllImport("gdi32.dll")]
	public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);

	[DllImport("gdi32.dll")]
	public static extern int SetStretchBltMode(IntPtr hdc, int mode);

	[DllImport("gdi32.dll")]
	public static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

	[DllImport("gdi32.dll")]
	public static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

	[DllImport("gdi32.dll")]
	public static extern bool GdiFlush();

	[DllImport("comctl32.dll")]
	public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr ShellExecuteW(IntPtr hWnd, string verb, string file, string parameters, string dir, int show);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	public static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr GetModuleHandleW(string name);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
	public static extern IntPtr GetProcAddress(IntPtr module, string name);

	[DllImport("kernel32.dll")]
	public static extern uint GetCurrentThreadId();

	[DllImport("kernel32.dll")]
	public static extern uint GetCurrentProcessId();

	public static IntPtr MakeLParam(int lo, int hi) => new IntPtr((hi << 16) | (lo & 0xFFFF));

	public static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);

	public static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);

	public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

	public static string GetWindowText(IntPtr hWnd)
	{
		int len = GetWindowTextLengthW(hWnd);
		if (len <= 0)
		{
			return "";
		}
		StringBuilder sb = new StringBuilder(len + 1);
		GetWindowTextW(hWnd, sb, sb.Capacity);
		return sb.ToString();
	}

	public static string GetClassName(IntPtr hWnd)
	{
		StringBuilder sb = new StringBuilder(256);
		GetClassNameW(hWnd, sb, sb.Capacity);
		return sb.ToString();
	}

	public static bool IsRunningUnderWine()
	{
		IntPtr ntdll = GetModuleHandleW("ntdll.dll");
		return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
	}
}
