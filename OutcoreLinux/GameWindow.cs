using System;

namespace OutcoreLinux;

// The Unity player window of this process.
internal static class GameWindow
{
	private static IntPtr _handle;

	public static IntPtr Handle
	{
		get
		{
			if (_handle == IntPtr.Zero || !Native.IsWindow(_handle))
			{
				_handle = Find();
			}
			return _handle;
		}
	}

	private static IntPtr Find()
	{
		uint me = Native.GetCurrentProcessId();
		IntPtr found = IntPtr.Zero;
		Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
		{
			Native.GetWindowThreadProcessId(hwnd, out uint pid);
			if (pid == me && Native.GetClassName(hwnd) == "UnityWndClass")
			{
				found = hwnd;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		return found;
	}
}
