using System;
using System.IO;

namespace OutcoreLinux.Shell;

internal static class ShellOpen
{
	private static bool IsImage(string path)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
	}

	// Double-click behaviour for files. Wine has no image viewer or Paint, so images go to ours.
	public static void Open(string path, IntPtr owner)
	{
		if (Directory.Exists(path))
		{
			FolderWindow.OpenDefault(path);
			return;
		}
		if (IsImage(path))
		{
			PaintWindow.Open(path);
			return;
		}
		string dir = Path.GetDirectoryName(path);
		long result = (long)Native.ShellExecuteW(owner, "open", path, null, dir, Native.SW_SHOWNORMAL);
		if (result <= 32)
		{
			Log.Warn("ShellExecute failed (" + result + ") for " + path);
			if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length < 512 * 1024)
			{
				Native.ShellExecuteW(owner, "open", "notepad.exe", "\"" + path + "\"", dir, Native.SW_SHOWNORMAL);
			}
		}
	}
}
