using System;
using System.IO;
using System.Text;

namespace OutcoreLinux.Shell;

// Writes a minimal Shell Link (.lnk) pointing at a local path: header + LinkInfo
// (VolumeID + LocalBasePath) + working directory. Wine's shell resolves these fine, and it
// avoids COM (IShellLink) from inside Unity's Mono.
internal static class ShortcutWriter
{
	public static void Write(string lnkPath, string target)
	{
		string workingDir = Path.GetDirectoryName(target) ?? "";
		bool isDir = Directory.Exists(target);
		using MemoryStream ms = new MemoryStream();
		using BinaryWriter w = new BinaryWriter(ms);
		// ShellLinkHeader
		w.Write(0x4C);
		w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
		w.Write(0x2 | 0x10 | 0x80); // HasLinkInfo | HasWorkingDir | IsUnicode
		w.Write(isDir ? 0x10 : 0x20);
		w.Write(0L);
		w.Write(0L);
		w.Write(0L);
		w.Write(isDir ? 0 : (int)Math.Min(int.MaxValue, SafeLength(target)));
		w.Write(0); // icon index
		w.Write(1); // SW_SHOWNORMAL
		w.Write((ushort)0);
		w.Write((ushort)0);
		w.Write(0);
		w.Write(0);
		// LinkInfo
		byte[] basePath = Encoding.Default.GetBytes(target + "\0");
		byte[] volumeLabel = { 0 };
		int headerSize = 0x1C;
		int volumeIdSize = 0x10 + volumeLabel.Length;
		int volumeIdOffset = headerSize;
		int basePathOffset = volumeIdOffset + volumeIdSize;
		int suffixOffset = basePathOffset + basePath.Length;
		int linkInfoSize = suffixOffset + 1;
		w.Write(linkInfoSize);
		w.Write(headerSize);
		w.Write(1); // VolumeIDAndLocalBasePath
		w.Write(volumeIdOffset);
		w.Write(basePathOffset);
		w.Write(0);
		w.Write(suffixOffset);
		w.Write(volumeIdSize);
		w.Write(3); // DRIVE_FIXED
		w.Write(0);
		w.Write(0x10);
		w.Write(volumeLabel);
		w.Write(basePath);
		w.Write((byte)0);
		// StringData: WORKING_DIR
		w.Write((ushort)workingDir.Length);
		w.Write(Encoding.Unicode.GetBytes(workingDir));
		// Terminal block
		w.Write(0);
		w.Flush();
		File.WriteAllBytes(lnkPath, ms.ToArray());
	}

	private static long SafeLength(string path)
	{
		try
		{
			return new FileInfo(path).Length;
		}
		catch
		{
			return 0;
		}
	}
}
