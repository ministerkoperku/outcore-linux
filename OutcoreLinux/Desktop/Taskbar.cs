using System;
using System.Collections.Generic;
using OutcoreLinux.Shell;

namespace OutcoreLinux.Desktop;

// Minimal taskbar for setups with none (GameNative). Lists open windows; click restores.
// It is a tool window, so the game ignores it when it collects desktop windows.
internal sealed class Taskbar : ShellWindow
{
	private static readonly IntPtr Timer = new IntPtr(11);
	private readonly List<KeyValuePair<IntPtr, string>> _windows = new List<KeyValuePair<IntPtr, string>>();
	private string _signature = "";

	public int Height { get; private set; } = 34;

	public bool Visible { get; private set; }

	public static Taskbar Create()
	{
		Taskbar bar = new Taskbar();
		ShellThread.Post(() =>
		{
			ShellThread.Create(bar, Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST, ShellThread.PanelClass, "", 0x80000000u /* WS_POPUP */, 0, 0, 10, 10, IntPtr.Zero);
			Native.SetTimer(bar.Hwnd, Timer, 400, IntPtr.Zero);
		});
		return bar;
	}

	public void SetVisible(bool visible)
	{
		ShellThread.Post(() =>
		{
			Visible = visible;
			if (visible)
			{
				Place();
				Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
			}
			else
			{
				Native.ShowWindow(Hwnd, Native.SW_HIDE);
			}
		});
	}

	private void Place()
	{
		ScreenData primary = Screens.Primary();
		Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, primary.Work.left, primary.Work.bottom - Height, primary.Work.Width, Height, Native.SWP_NOACTIVATE);
	}

	private void Collect()
	{
		_windows.Clear();
		IntPtr game = GameWindow.Handle;
		Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
		{
			if (hwnd == Hwnd || hwnd == game || !Native.IsWindowVisible(hwnd))
			{
				return true;
			}
			long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
			if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || Native.GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero)
			{
				return true;
			}
			string title = Native.GetWindowText(hwnd);
			if (title.Length > 0)
			{
				_windows.Add(new KeyValuePair<IntPtr, string>(hwnd, title));
			}
			return true;
		}, IntPtr.Zero);
		_windows.Sort((a, b) => a.Key.ToInt64().CompareTo(b.Key.ToInt64()));
	}

	private Native.RECT ButtonRect(int index)
	{
		const int width = 170;
		int left = 4 + index * (width + 4);
		return new Native.RECT { left = left, top = 3, right = left + width, bottom = Height - 3 };
	}

	protected internal override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
	{
		switch (msg)
		{
		case Native.WM_TIMER:
			if (Visible)
			{
				Collect();
				string signature = string.Join("|", _windows.ConvertAll(w => w.Key + w.Value + Native.IsIconic(w.Key)).ToArray()) + Native.GetForegroundWindow();
				if (signature != _signature)
				{
					_signature = signature;
					Native.InvalidateRect(Hwnd, IntPtr.Zero, true);
				}
				Place();
			}
			return IntPtr.Zero;
		case Native.WM_LBUTTONDOWN:
		{
			int x = Native.LoWord(lParam);
			int y = Native.HiWord(lParam);
			for (int i = 0; i < _windows.Count; i++)
			{
				if (ButtonRect(i).Contains(x, y))
				{
					IntPtr target = _windows[i].Key;
					if (Native.IsIconic(target))
					{
						Native.ShowWindow(target, Native.SW_RESTORE);
					}
					else if (Native.GetForegroundWindow() == target)
					{
						Native.ShowWindow(target, Native.SW_MINIMIZE);
						break;
					}
					Native.SetForegroundWindow(target);
					break;
				}
			}
			return IntPtr.Zero;
		}
		case 0x0021: // WM_MOUSEACTIVATE: never steal focus
			return new IntPtr(3); // MA_NOACTIVATE
		case Native.WM_PAINT:
		{
			IntPtr hdc = Native.BeginPaint(Hwnd, out Native.PAINTSTRUCT ps);
			Native.GetClientRect(Hwnd, out Native.RECT client);
			IntPtr bg = Native.CreateSolidBrush(Native.Rgb(24, 30, 42));
			Native.FillRect(hdc, ref client, bg);
			Native.DeleteObject(bg);
			Native.SelectObject(hdc, ShellThread.DefaultFont);
			Native.SetBkMode(hdc, Native.TRANSPARENT);
			IntPtr foreground = Native.GetForegroundWindow();
			for (int i = 0; i < _windows.Count; i++)
			{
				Native.RECT r = ButtonRect(i);
				bool active = _windows[i].Key == foreground;
				bool minimized = Native.IsIconic(_windows[i].Key);
				IntPtr brush = Native.CreateSolidBrush(active ? Native.Rgb(70, 90, 125) : Native.Rgb(44, 52, 68));
				Native.FillRect(hdc, ref r, brush);
				Native.DeleteObject(brush);
				Native.SetTextColor(hdc, minimized ? Native.Rgb(160, 170, 185) : Native.Rgb(240, 244, 250));
				Native.RECT text = new Native.RECT { left = r.left + 8, top = r.top, right = r.right - 6, bottom = r.bottom };
				Native.DrawTextW(hdc, _windows[i].Value, -1, ref text, Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
			}
			Native.EndPaint(Hwnd, ref ps);
			return IntPtr.Zero;
		}
		}
		return Default(msg, wParam, lParam);
	}
}
