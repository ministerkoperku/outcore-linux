using System;
using System.Collections.Generic;

namespace OutcoreLinux.Shell;

// Keys that standard controls swallow before our windows see them.
internal static class ShellKeyboard
{
	private static readonly Dictionary<IntPtr, FolderWindow> AddressBars = new Dictionary<IntPtr, FolderWindow>();

	public static void RegisterAddressBar(IntPtr edit, FolderWindow owner) => AddressBars[edit] = owner;

	public static void UnregisterAddressBar(IntPtr edit) => AddressBars.Remove(edit);

	// Runs on the shell thread for every WM_KEYDOWN; true = handled.
	public static bool PreTranslate(ref Native.MSG msg)
	{
		int key = (int)msg.wParam;
		if (key == Native.VK_RETURN && AddressBars.TryGetValue(msg.hwnd, out FolderWindow folder))
		{
			folder.NavigateFromAddressBar();
			return true;
		}
		if (key == 'S' && (Native.GetKeyState(Native.VK_CONTROL) & 0x8000) != 0)
		{
			IntPtr root = Native.GetAncestor(msg.hwnd, Native.GA_ROOT);
			if (ShellThread.Find(root) is PaintWindow paint)
			{
				paint.Save();
				return true;
			}
		}
		return false;
	}
}
