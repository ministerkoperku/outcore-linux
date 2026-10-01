using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OutcoreLinux.Shell;

internal sealed class ScreenData
{
	public string Device;
	public Native.RECT Bounds;
	public Native.RECT Work;
	public bool Primary;
}

internal static class Screens
{
	private static List<ScreenData> _cache;
	private static long _cacheTicks;

	// Cached for half a second: called many times per frame by the desktop layout.
	public static List<ScreenData> All()
	{
		long now = System.DateTime.UtcNow.Ticks;
		lock (typeof(Screens))
		{
			if (_cache != null && now - _cacheTicks < 5000000)
			{
				return _cache;
			}
		}
		List<ScreenData> fresh = Query();
		lock (typeof(Screens))
		{
			_cache = fresh;
			_cacheTicks = now;
		}
		return fresh;
	}

	private static List<ScreenData> Query()
	{
		List<ScreenData> list = new List<ScreenData>();
		Native.EnumDisplayMonitors(System.IntPtr.Zero, System.IntPtr.Zero, (System.IntPtr monitor, System.IntPtr hdc, ref Native.RECT rect, System.IntPtr data) =>
		{
			Native.MONITORINFOEX info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
			if (Native.GetMonitorInfoW(monitor, ref info))
			{
				list.Add(new ScreenData
				{
					Device = info.szDevice,
					Bounds = info.rcMonitor,
					Work = info.rcWork,
					Primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0
				});
			}
			return true;
		}, System.IntPtr.Zero);
		if (list.Count == 0)
		{
			Native.RECT r = new Native.RECT { right = Native.GetSystemMetrics(0), bottom = Native.GetSystemMetrics(1) };
			list.Add(new ScreenData { Device = "\\\\.\\DISPLAY1", Bounds = r, Work = r, Primary = true });
		}
		if (!list.Exists(s => s.Primary))
		{
			list[0].Primary = true;
		}
		return list;
	}

	public static ScreenData Primary() => All().Find(s => s.Primary);

	public static Native.RECT PrimaryWorkArea()
	{
		ScreenData primary = Primary();
		// The game's desktop covers the whole screen; avoid the strip our taskbar uses.
		Native.RECT work = primary.Work;
		if (Plugin.Instance != null && Plugin.Instance.Desktop != null)
		{
			work.bottom -= Plugin.Instance.Desktop.TaskbarHeight;
		}
		return work;
	}
}
