using System.Collections.Generic;

namespace OutcoreLinux.Shell;

// What happens when the user lets go of a dragged file. Mirrors Explorer closely enough for
// the game: onto a folder window = move there, onto the game = the game's file-drop event,
// onto the desktop = move to the Desktop folder.
internal static class DropResolver
{
	public static IntPtrRoot RootAt(Native.POINT screen)
	{
		System.IntPtr hwnd = Native.WindowFromPoint(screen);
		System.IntPtr root = hwnd == System.IntPtr.Zero ? System.IntPtr.Zero : Native.GetAncestor(hwnd, Native.GA_ROOT);
		return new IntPtrRoot(root);
	}

	public static bool CanDropAt(Native.POINT screen)
	{
		IntPtrRoot root = RootAt(screen);
		if (root.Handle == GameWindow.Handle && GameWindow.Handle != System.IntPtr.Zero)
		{
			return true;
		}
		if (ShellThread.Find(root.Handle) is FolderWindow folder)
		{
			return folder.CurrentPath != ShellPaths.ThisPc || folder.FolderAt(screen) != null;
		}
		return false;
	}

	public static void Drop(string[] paths, Native.POINT screen, object source)
	{
		IntPtrRoot root = RootAt(screen);
		if (ShellThread.Find(root.Handle) is FolderWindow folder)
		{
			string target = folder.FolderAt(screen) ?? folder.CurrentPath;
			if (target == ShellPaths.ThisPc)
			{
				return;
			}
			bool moved = false;
			foreach (string path in paths)
			{
				if (ShellPaths.Same(path, target))
				{
					continue;
				}
				moved |= ShellPaths.MoveInto(path, target) != null;
			}
			if (moved)
			{
				folder.Refresh();
				(source as FolderWindow)?.Refresh();
			}
			return;
		}
		if (root.Handle == GameWindow.Handle && GameWindow.Handle != System.IntPtr.Zero)
		{
			List<string> list = new List<string>(paths);
			MainThread.Post(() => Plugin.Instance?.Desktop?.DropFromShell(list.ToArray(), screen, source));
		}
	}

	internal readonly struct IntPtrRoot
	{
		public readonly System.IntPtr Handle;

		public IntPtrRoot(System.IntPtr handle)
		{
			Handle = handle;
		}
	}
}
