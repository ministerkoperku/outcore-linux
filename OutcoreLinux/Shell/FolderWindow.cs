using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OutcoreLinux.Shell;

// A small File Explorer window. The game needs real, user-driven folder windows whose
// location and item rectangles it can query; explorer.exe under Wine cannot provide that
// (no ShellWindows COM, no UI Automation), so we draw our own with a stock ListView.
internal sealed class FolderWindow : ShellWindow
{
	private const int IdBack = 1;
	private const int IdUp = 2;
	private const int IdAddress = 3;
	private const int IdList = 4;
	private const int BarHeight = 30;

	private const uint LVM_FIRST = 0x1000;
	private const uint LVM_SETIMAGELIST = LVM_FIRST + 3;
	private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
	private const uint LVM_DELETEALLITEMS = LVM_FIRST + 9;
	private const uint LVM_GETNEXTITEM = LVM_FIRST + 12;
	private const uint LVM_GETITEMRECT = LVM_FIRST + 14;
	private const uint LVM_HITTEST = LVM_FIRST + 18;
	private const uint LVM_SETITEMSTATE = LVM_FIRST + 43;
	private const uint LVM_INSERTITEMW = LVM_FIRST + 77;
	private const uint LVM_EDITLABELW = LVM_FIRST + 118;
	private const uint LVS_AUTOARRANGE = 0x0100;
	private const uint LVS_SHOWSELALWAYS = 0x0008;
	private const uint LVS_EDITLABELS = 0x0200;
	private const uint LVS_SHAREIMAGELISTS = 0x0040;
	private const int NM_DBLCLK = -3;
	private const int NM_RETURN = -4;
	private const int LVN_BEGINDRAG = -109;
	private const int LVN_KEYDOWN = -155;
	private const int LVN_BEGINLABELEDITW = -175;
	private const int LVN_ENDLABELEDITW = -176;
	private const uint ES_AUTOHSCROLL = 0x0080;
	private const uint BS_PUSHBUTTON = 0;
	private static readonly IntPtr RefreshTimer = new IntPtr(7);

	private static IntPtr _systemImageList;

	private readonly Stack<string> _history = new Stack<string>();
	private List<ShellItem> _items = new List<ShellItem>();
	private IntPtr _list;
	private IntPtr _address;
	private string _signature;
	private int _dragIndex = -1;
	private Native.POINT _dragStart;
	private bool _dragging;

	// Original-case Windows path ("" = This PC). Written on the shell thread only.
	public volatile string CurrentPath = ShellPaths.ThisPc;

	public string Location => ShellPaths.Normalize(CurrentPath);

	public string LocationName => ShellPaths.DisplayNameForFolder(CurrentPath);

	public DateTime LastActivated { get; private set; } = DateTime.Now;

	// Asynchronous on purpose: activating the new window may send messages to the game window,
	// so the game thread must never block waiting for this.
	public static void Open(string path, int x, int y, int width, int height, bool bringToFront)
	{
		ShellThread.Post(() => new FolderWindow().CreateOnShellThread(path, x, y, width, height, bringToFront));
	}

	public static void OpenDefault(string path)
	{
		if (!ShellThread.IsShellThread)
		{
			ShellThread.Post(() => OpenDefault(path));
			return;
		}
		Native.RECT work = Screens.PrimaryWorkArea();
		int w = Math.Min(900, work.Width * 3 / 5);
		int h = Math.Min(560, work.Height * 3 / 5);
		int offset = 30 * (ShellThread.All<FolderWindow>().Count % 6);
		new FolderWindow().CreateOnShellThread(path, work.left + (work.Width - w) / 2 + offset - 75, work.top + (work.Height - h) / 3 + offset, w, h, bringToFront: true);
	}

	private void CreateOnShellThread(string path, int x, int y, int width, int height, bool bringToFront)
	{
		CurrentPath = ShellPaths.Normalize(path) == ShellPaths.ThisPc ? ShellPaths.ThisPc : ShellPaths.ToWindows(path);
		if (width < 200 || height < 150)
		{
			width = 700;
			height = 450;
		}
		ShellThread.Create(this, 0, ShellThread.FolderClass, LocationName, Native.WS_OVERLAPPEDWINDOW | Native.WS_CLIPCHILDREN, x, y, width, height, IntPtr.Zero);
		if (Hwnd == IntPtr.Zero)
		{
			Log.Error("Could not create folder window");
			return;
		}
		ShellThread.CreateControl("BUTTON", "\u2190", BS_PUSHBUTTON, 4, 3, 30, 24, Hwnd, IdBack);
		ShellThread.CreateControl("BUTTON", "\u2191", BS_PUSHBUTTON, 38, 3, 30, 24, Hwnd, IdUp);
		_address = ShellThread.CreateControl("EDIT", "", ES_AUTOHSCROLL | Native.WS_BORDER | Native.WS_TABSTOP, 74, 4, 300, 22, Hwnd, IdAddress);
		_list = ShellThread.CreateControl("SysListView32", "", LVS_AUTOARRANGE | LVS_SHOWSELALWAYS | LVS_EDITLABELS | LVS_SHAREIMAGELISTS | Native.WS_TABSTOP, 0, BarHeight, 300, 200, Hwnd, IdList, Native.WS_EX_CLIENTEDGE);
		Native.SendMessageW(_list, LVM_SETIMAGELIST, IntPtr.Zero, SystemImageList());
		ShellKeyboard.RegisterAddressBar(_address, this);
		Layout();
		Populate();
		Native.SetTimer(Hwnd, RefreshTimer, 1000, IntPtr.Zero);
		Native.ShowWindow(Hwnd, bringToFront ? Native.SW_SHOWNORMAL : Native.SW_SHOWNOACTIVATE);
		if (bringToFront)
		{
			Native.SetForegroundWindow(Hwnd);
			Native.SetFocus(_list);
		}
	}

	private static IntPtr SystemImageList()
	{
		if (_systemImageList == IntPtr.Zero)
		{
			Native.SHFILEINFO info = default;
			_systemImageList = Native.SHGetFileInfoW("C:\\", 0, ref info, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), Native.SHGFI_SYSICONINDEX | Native.SHGFI_LARGEICON);
		}
		return _systemImageList;
	}

	private static int IconIndex(ShellItem item)
	{
		Native.SHFILEINFO info = default;
		uint size = (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO));
		IntPtr ok = Native.SHGetFileInfoW(item.Path, 0, ref info, size, Native.SHGFI_SYSICONINDEX | Native.SHGFI_LARGEICON);
		if (ok == IntPtr.Zero)
		{
			uint attr = item.IsFolder ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
			Native.SHGetFileInfoW(item.Path, attr, ref info, size, Native.SHGFI_SYSICONINDEX | Native.SHGFI_LARGEICON | Native.SHGFI_USEFILEATTRIBUTES);
		}
		return info.iIcon;
	}

	public void Navigate(string path)
	{
		if (!ShellThread.IsShellThread)
		{
			ShellThread.Post(() => Navigate(path));
			return;
		}
		string target = ShellPaths.Normalize(path) == ShellPaths.ThisPc ? ShellPaths.ThisPc : ShellPaths.ToWindows(path);
		if (target != ShellPaths.ThisPc && !System.IO.Directory.Exists(target))
		{
			Log.Warn("Folder does not exist: " + target);
			return;
		}
		_history.Push(CurrentPath);
		CurrentPath = target;
		Native.SetWindowTextW(Hwnd, LocationName);
		Populate();
	}

	private void GoBack()
	{
		if (_history.Count > 0)
		{
			CurrentPath = _history.Pop();
			Native.SetWindowTextW(Hwnd, LocationName);
			Populate();
		}
	}

	private void GoUp()
	{
		string parent = ShellPaths.Parent(CurrentPath);
		if (parent != null)
		{
			Navigate(parent);
		}
	}

	internal void NavigateFromAddressBar()
	{
		string text = Native.GetWindowText(_address).Trim();
		if (text.Length == 0 || text.Equals(ShellPaths.ThisPcName, StringComparison.OrdinalIgnoreCase))
		{
			Navigate(ShellPaths.ThisPc);
			return;
		}
		Navigate(Environment.ExpandEnvironmentVariables(text));
	}

	public void Refresh()
	{
		if (!ShellThread.IsShellThread)
		{
			ShellThread.Post(Refresh);
			return;
		}
		if (IsAlive)
		{
			Populate();
		}
	}

	private void Populate()
	{
		Native.SetWindowTextW(_address, CurrentPath == ShellPaths.ThisPc ? ShellPaths.ThisPcName : CurrentPath);
		List<ShellItem> items = ShellPaths.List(CurrentPath);
		_signature = ShellPaths.Signature(CurrentPath);
		Native.SendMessageW(_list, LVM_DELETEALLITEMS, IntPtr.Zero, IntPtr.Zero);
		for (int i = 0; i < items.Count; i++)
		{
			InsertItem(i, items[i]);
		}
		lock (this)
		{
			_items = items;
		}
	}

	private void InsertItem(int index, ShellItem item)
	{
		IntPtr text = Marshal.StringToHGlobalUni(item.DisplayName);
		IntPtr buffer = Marshal.AllocHGlobal(88);
		try
		{
			for (int i = 0; i < 88; i += 4)
			{
				Marshal.WriteInt32(buffer, i, 0);
			}
			Marshal.WriteInt32(buffer, 0, 0x1 | 0x2 | 0x4); // LVIF_TEXT | LVIF_IMAGE | LVIF_PARAM
			Marshal.WriteInt32(buffer, 4, index);
			Marshal.WriteIntPtr(buffer, 24, text);
			Marshal.WriteInt32(buffer, 36, IconIndex(item));
			Marshal.WriteIntPtr(buffer, 40, new IntPtr(index));
			Native.SendMessageW(_list, LVM_INSERTITEMW, IntPtr.Zero, buffer);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
			Marshal.FreeHGlobal(text);
		}
	}

	private ShellItem ItemAt(int index)
	{
		lock (this)
		{
			return index >= 0 && index < _items.Count ? _items[index] : null;
		}
	}

	// Screen rectangles of every item, as UI Automation would report them for explorer.
	public List<KeyValuePair<string, Native.RECT>> GetItemRects()
	{
		List<KeyValuePair<string, Native.RECT>> result = new List<KeyValuePair<string, Native.RECT>>();
		List<ShellItem> items;
		lock (this)
		{
			items = new List<ShellItem>(_items);
		}
		IntPtr rectBuffer = Marshal.AllocHGlobal(16);
		try
		{
			for (int i = 0; i < items.Count; i++)
			{
				Marshal.WriteInt32(rectBuffer, 0, 0); // LVIR_BOUNDS
				if (Native.SendMessageW(_list, LVM_GETITEMRECT, new IntPtr(i), rectBuffer) == IntPtr.Zero)
				{
					continue;
				}
				Native.RECT rect = (Native.RECT)Marshal.PtrToStructure(rectBuffer, typeof(Native.RECT));
				Native.POINT tl = new Native.POINT(rect.left, rect.top);
				Native.POINT br = new Native.POINT(rect.right, rect.bottom);
				Native.ClientToScreen(_list, ref tl);
				Native.ClientToScreen(_list, ref br);
				result.Add(new KeyValuePair<string, Native.RECT>(items[i].DisplayName, new Native.RECT { left = tl.x, top = tl.y, right = br.x, bottom = br.y }));
			}
		}
		finally
		{
			Marshal.FreeHGlobal(rectBuffer);
		}
		return result;
	}

	// Folder under a screen point (used as a drop target), or null.
	public string FolderAt(Native.POINT screen)
	{
		int index = HitTest(screen);
		ShellItem item = ItemAt(index);
		return item != null && item.IsFolder ? item.Path : null;
	}

	private int HitTest(Native.POINT screen)
	{
		Native.POINT client = screen;
		Native.ScreenToClient(_list, ref client);
		IntPtr info = Marshal.AllocHGlobal(24);
		try
		{
			Marshal.WriteInt32(info, 0, client.x);
			Marshal.WriteInt32(info, 4, client.y);
			Marshal.WriteInt32(info, 8, 0);
			return (int)Native.SendMessageW(_list, LVM_HITTEST, IntPtr.Zero, info);
		}
		finally
		{
			Marshal.FreeHGlobal(info);
		}
	}

	private int SelectedIndex() => (int)Native.SendMessageW(_list, LVM_GETNEXTITEM, new IntPtr(-1), new IntPtr(2));

	private void OpenItem(int index)
	{
		ShellItem item = ItemAt(index);
		if (item == null)
		{
			return;
		}
		if (item.IsFolder)
		{
			Navigate(item.Path);
		}
		else
		{
			ShellOpen.Open(item.Path, Hwnd);
		}
	}

	private void Layout()
	{
		Native.GetClientRect(Hwnd, out Native.RECT client);
		Native.MoveWindow(_address, 74, 4, Math.Max(50, client.Width - 78), 22, true);
		Native.MoveWindow(_list, 0, BarHeight, client.Width, Math.Max(10, client.Height - BarHeight), true);
	}

	protected internal override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
	{
		switch (msg)
		{
		case Native.WM_SIZE:
			if (_list != IntPtr.Zero)
			{
				Layout();
			}
			return IntPtr.Zero;
		case 0x0006: // WM_ACTIVATE
			if (Native.LoWord(wParam) != 0)
			{
				LastActivated = DateTime.Now;
				if (_list != IntPtr.Zero)
				{
					Native.SetFocus(_list);
				}
			}
			break;
		case Native.WM_COMMAND:
			switch (Native.LoWord(wParam))
			{
			case IdBack:
				GoBack();
				return IntPtr.Zero;
			case IdUp:
				GoUp();
				return IntPtr.Zero;
			}
			break;
		case Native.WM_TIMER:
			if (wParam == RefreshTimer && !_dragging && ShellPaths.Signature(CurrentPath) != _signature)
			{
				Populate();
			}
			return IntPtr.Zero;
		case Native.WM_NOTIFY:
			return OnNotify(lParam);
		case Native.WM_MOUSEMOVE:
			if (_dragIndex >= 0)
			{
				Native.GetCursorPos(out Native.POINT pt);
				if (!_dragging && (Math.Abs(pt.x - _dragStart.x) > 3 || Math.Abs(pt.y - _dragStart.y) > 3))
				{
					_dragging = true;
				}
				if (_dragging)
				{
					Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, new IntPtr(DropResolver.CanDropAt(pt) ? Native.IDC_HAND : Native.IDC_NO)));
				}
				return IntPtr.Zero;
			}
			break;
		case Native.WM_LBUTTONUP:
			if (_dragIndex >= 0)
			{
				Native.GetCursorPos(out Native.POINT pt);
				ShellItem item = ItemAt(_dragIndex);
				bool wasDragging = _dragging;
				EndDrag();
				if (item != null && wasDragging)
				{
					DropResolver.Drop(new[] { item.Path }, pt, this);
				}
				return IntPtr.Zero;
			}
			break;
		case Native.WM_CAPTURECHANGED:
			if (_dragIndex >= 0 && lParam != Hwnd)
			{
				_dragIndex = -1;
				_dragging = false;
			}
			break;
		case Native.WM_DESTROY:
			Native.KillTimer(Hwnd, RefreshTimer);
			ShellKeyboard.UnregisterAddressBar(_address);
			return IntPtr.Zero;
		}
		return Default(msg, wParam, lParam);
	}

	private void EndDrag()
	{
		_dragIndex = -1;
		_dragging = false;
		Native.ReleaseCapture();
	}

	private IntPtr OnNotify(IntPtr lParam)
	{
		IntPtr from = Marshal.ReadIntPtr(lParam, 0);
		int code = Marshal.ReadInt32(lParam, 16);
		if (from != _list)
		{
			return IntPtr.Zero;
		}
		switch (code)
		{
		case NM_DBLCLK:
			OpenItem(Marshal.ReadInt32(lParam, 24));
			break;
		case NM_RETURN:
			OpenItem(SelectedIndex());
			break;
		case LVN_BEGINDRAG:
			_dragIndex = Marshal.ReadInt32(lParam, 24);
			Native.GetCursorPos(out _dragStart);
			_dragging = true;
			Native.SetCapture(Hwnd);
			break;
		case LVN_BEGINLABELEDITW:
		{
			ShellItem item = ItemAt(Marshal.ReadInt32(lParam, 28));
			return new IntPtr(item == null || CurrentPath == ShellPaths.ThisPc ? 1 : 0);
		}
		case LVN_ENDLABELEDITW:
		{
			ShellItem item = ItemAt(Marshal.ReadInt32(lParam, 28));
			IntPtr text = Marshal.ReadIntPtr(lParam, 48);
			if (item != null && text != IntPtr.Zero)
			{
				ShellPaths.Rename(item.Path, Marshal.PtrToStringUni(text));
				Native.PostMessageW(Hwnd, Native.WM_TIMER, RefreshTimer, IntPtr.Zero);
				_signature = null;
			}
			return IntPtr.Zero;
		}
		case LVN_KEYDOWN:
			OnKey(Marshal.ReadInt16(lParam, 24));
			break;
		}
		return IntPtr.Zero;
	}

	private void OnKey(int key)
	{
		switch (key)
		{
		case Native.VK_DELETE:
		{
			ShellItem item = ItemAt(SelectedIndex());
			if (item != null && CurrentPath != ShellPaths.ThisPc)
			{
				ShellPaths.Recycle(item.Path);
				Populate();
			}
			break;
		}
		case Native.VK_F2:
		{
			int index = SelectedIndex();
			if (index >= 0)
			{
				Native.SendMessageW(_list, LVM_EDITLABELW, new IntPtr(index), IntPtr.Zero);
			}
			break;
		}
		case Native.VK_F5:
			Populate();
			break;
		case Native.VK_BACK:
			GoUp();
			break;
		}
	}
}
