using System;
using System.Collections.Generic;
using System.IO;

namespace OutcoreLinux.Shell;

internal sealed class ShellItem
{
	public string Path;
	public string DisplayName;
	public bool IsFolder;
	public bool IsVirtual;
}

// Path conventions shared with the game. The game talks in explorer "LocationURL" terms:
// lower case, forward slashes, no "file:///". The empty string means This PC (that is what
// Environment.SpecialFolder.MyComputer resolves to in .NET).
internal static class ShellPaths
{
	public const string ThisPc = "";
	public const string ThisPcName = "This PC";

	private static readonly HashSet<string> HiddenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".txt", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".lnk", ".url", ".exe", ".wav", ".mp3", ".mp4",
		".html", ".htm", ".pdf", ".ini", ".log", ".json", ".xml", ".zip", ".doc", ".docx"
	};

	public static string Normalize(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return ThisPc;
		}
		string p = Uri.UnescapeDataString(path.Trim().Trim('"'));
		if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
		{
			p = p.Substring(8);
		}
		p = p.Replace('\\', '/');
		if (p.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase))
		{
			p = ResolveLibrary(p);
		}
		while (p.Length > 3 && p.EndsWith("/"))
		{
			p = p.Substring(0, p.Length - 1);
		}
		if (p.Length == 2 && p[1] == ':')
		{
			p += "/";
		}
		return p.ToLowerInvariant();
	}

	public static bool Same(string a, string b) => Normalize(a) == Normalize(b);

	public static string ToWindows(string path) => string.IsNullOrEmpty(path) ? ThisPc : path.Replace('/', '\\');

	public static string ResolveLibrary(string libraryPath)
	{
		string name = System.IO.Path.GetFileNameWithoutExtension(libraryPath.Replace('/', '\\')).ToLowerInvariant();
		Environment.SpecialFolder folder = name switch
		{
			"documents" => Environment.SpecialFolder.MyDocuments,
			"pictures" => Environment.SpecialFolder.MyPictures,
			"music" => Environment.SpecialFolder.MyMusic,
			"videos" => Environment.SpecialFolder.MyVideos,
			_ => Environment.SpecialFolder.MyDocuments
		};
		return Environment.GetFolderPath(folder).Replace('\\', '/');
	}

	public static string DisplayNameForFolder(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return ThisPcName;
		}
		string win = ToWindows(path).TrimEnd('\\');
		if (win.Length <= 2)
		{
			return "Local Disk (" + win + ")";
		}
		foreach (KeyValuePair<string, string> known in KnownFolderNames())
		{
			if (Same(known.Key, path))
			{
				return known.Value;
			}
		}
		return System.IO.Path.GetFileName(win);
	}

	public static string DisplayNameForFile(string path)
	{
		string name = System.IO.Path.GetFileName(path);
		string ext = System.IO.Path.GetExtension(name);
		if (!string.IsNullOrEmpty(ext) && HiddenExtensions.Contains(ext))
		{
			return name.Substring(0, name.Length - ext.Length);
		}
		return name;
	}

	public static IEnumerable<KeyValuePair<string, string>> KnownFolderNames()
	{
		yield return new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop");
		yield return new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents");
		yield return new KeyValuePair<string, string>(DownloadsPath(), "Downloads");
		yield return new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music");
		yield return new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures");
		yield return new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos");
	}

	public static string DownloadsPath()
	{
		try
		{
			return KnownFolders.GetPath(KnownFolder.Downloads);
		}
		catch
		{
			return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		}
	}

	public static List<ShellItem> List(string path)
	{
		List<ShellItem> items = new List<ShellItem>();
		if (string.IsNullOrEmpty(path))
		{
			foreach (KeyValuePair<string, string> known in KnownFolderNames())
			{
				if (!string.IsNullOrEmpty(known.Key) && Directory.Exists(known.Key))
				{
					items.Add(new ShellItem { Path = known.Key, DisplayName = known.Value, IsFolder = true });
				}
			}
			foreach (string drive in Directory.GetLogicalDrives())
			{
				string letter = drive.TrimEnd('\\');
				items.Add(new ShellItem { Path = drive, DisplayName = "Local Disk (" + letter + ")", IsFolder = true });
			}
			return items;
		}
		string dir = ToWindows(path);
		try
		{
			foreach (string sub in Directory.GetDirectories(dir))
			{
				if (!IsHidden(sub))
				{
					items.Add(new ShellItem { Path = sub, DisplayName = System.IO.Path.GetFileName(sub), IsFolder = true });
				}
			}
			foreach (string file in Directory.GetFiles(dir))
			{
				if (!IsHidden(file))
				{
					items.Add(new ShellItem { Path = file, DisplayName = DisplayNameForFile(file), IsFolder = false });
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("Cannot list " + dir + ": " + ex.Message);
		}
		return items;
	}

	public static string Signature(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return "thispc";
		}
		try
		{
			string dir = ToWindows(path);
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			foreach (string entry in Directory.GetFileSystemEntries(dir))
			{
				sb.Append(entry).Append('|');
			}
			return sb.ToString();
		}
		catch
		{
			return "missing";
		}
	}

	public static string Parent(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}
		string win = ToWindows(path).TrimEnd('\\');
		if (win.Length <= 2)
		{
			return ThisPc;
		}
		string parent = System.IO.Path.GetDirectoryName(win + "\\");
		parent = System.IO.Path.GetDirectoryName(parent);
		return string.IsNullOrEmpty(parent) ? ThisPc : parent;
	}

	private static bool IsHidden(string path)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(path);
			if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
			{
				return true;
			}
		}
		catch
		{
			return true;
		}
		string name = System.IO.Path.GetFileName(path);
		return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".");
	}

	// Moves a file or folder into a directory; returns the new path or null.
	public static string MoveInto(string source, string targetDir)
	{
		try
		{
			string winTarget = ToWindows(targetDir);
			if (string.IsNullOrEmpty(winTarget) || !Directory.Exists(winTarget))
			{
				return null;
			}
			string name = System.IO.Path.GetFileName(source.TrimEnd('\\'));
			string destination = System.IO.Path.Combine(winTarget, name);
			if (Same(destination, source) || Same(System.IO.Path.GetDirectoryName(source), winTarget))
			{
				return null;
			}
			if (Normalize(winTarget).StartsWith(Normalize(source) + "/"))
			{
				return null;
			}
			if (File.Exists(destination) || Directory.Exists(destination))
			{
				Log.Warn("Target already exists: " + destination);
				return null;
			}
			if (Directory.Exists(source))
			{
				Directory.Move(source, destination);
			}
			else
			{
				File.Move(source, destination);
			}
			return destination;
		}
		catch (Exception ex)
		{
			Log.Warn("Move failed: " + ex.Message);
			return null;
		}
	}

	public static void Recycle(string path)
	{
		Native.SHFILEOPSTRUCT op = new Native.SHFILEOPSTRUCT
		{
			wFunc = Native.FO_DELETE,
			pFrom = path + "\0\0",
			fFlags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI
		};
		int result = Native.SHFileOperationW(ref op);
		if (result != 0 && (File.Exists(path) || Directory.Exists(path)))
		{
			Log.Warn("Recycle failed (" + result + "), deleting " + path);
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}
				else
				{
					File.Delete(path);
				}
			}
			catch (Exception ex)
			{
				Log.Warn("Delete failed: " + ex.Message);
			}
		}
	}

	public static string Rename(string path, string newName)
	{
		try
		{
			if (string.IsNullOrEmpty(newName) || newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
			{
				return null;
			}
			string dir = System.IO.Path.GetDirectoryName(path);
			bool isDir = Directory.Exists(path);
			string ext = System.IO.Path.GetExtension(path);
			// Extensions are hidden in the UI, so keep the old one unless the user typed one.
			if (!isDir && !string.IsNullOrEmpty(ext) && HiddenExtensions.Contains(ext) && !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
			{
				newName += ext;
			}
			string destination = System.IO.Path.Combine(dir, newName);
			if (Same(destination, path))
			{
				return path;
			}
			if (isDir)
			{
				Directory.Move(path, destination);
			}
			else
			{
				File.Move(path, destination);
			}
			return destination;
		}
		catch (Exception ex)
		{
			Log.Warn("Rename failed: " + ex.Message);
			return null;
		}
	}
}
