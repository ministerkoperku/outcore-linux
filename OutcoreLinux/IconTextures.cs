using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OutcoreLinux;

// Shell icons (HICON) to Unity textures, for the desktop drawn inside the game.
internal static class IconTextures
{
	public static Texture2D ForPath(string path, bool isFolder)
	{
		Native.SHFILEINFO info = default;
		uint size = (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO));
		Native.SHGetFileInfoW(path, 0, ref info, size, Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
		if (info.hIcon == IntPtr.Zero)
		{
			uint attr = isFolder ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
			Native.SHGetFileInfoW(path, attr, ref info, size, Native.SHGFI_ICON | Native.SHGFI_LARGEICON | Native.SHGFI_USEFILEATTRIBUTES);
		}
		if (info.hIcon == IntPtr.Zero)
		{
			return Fallback(isFolder);
		}
		try
		{
			return FromIcon(info.hIcon) ?? Fallback(isFolder);
		}
		finally
		{
			Native.DestroyIcon(info.hIcon);
		}
	}

	private static Texture2D FromIcon(IntPtr icon)
	{
		if (!Native.GetIconInfo(icon, out Native.ICONINFO ii))
		{
			return null;
		}
		try
		{
			int w = 0;
			int h = 0;
			byte[] color = ii.hbmColor != IntPtr.Zero ? ReadBitmap(ii.hbmColor, out w, out h) : null;
			byte[] mask = ReadBitmap(ii.hbmMask, out int mw, out int mh);
			if (color == null)
			{
				// Monochrome icon: the mask holds AND (top half) and XOR (bottom half).
				w = mw;
				h = mh / 2;
				color = new byte[w * h * 4];
				for (int i = 0; i < w * h; i++)
				{
					byte v = mask[(w * h + i) * 4];
					color[i * 4] = color[i * 4 + 1] = color[i * 4 + 2] = v;
				}
			}
			bool hasAlpha = false;
			for (int i = 3; i < color.Length; i += 4)
			{
				if (color[i] != 0)
				{
					hasAlpha = true;
					break;
				}
			}
			Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
			{
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp
			};
			Color32[] pixels = new Color32[w * h];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int src = (y * w + x) * 4; // top-down BGRA
					byte a = hasAlpha ? color[src + 3] : (byte)(mask[(y * mw + x) * 4] == 0 ? 255 : 0);
					pixels[(h - 1 - y) * w + x] = new Color32(color[src + 2], color[src + 1], color[src], a);
				}
			}
			tex.SetPixels32(pixels);
			tex.Apply();
			return tex;
		}
		finally
		{
			if (ii.hbmColor != IntPtr.Zero)
			{
				Native.DeleteObject(ii.hbmColor);
			}
			if (ii.hbmMask != IntPtr.Zero)
			{
				Native.DeleteObject(ii.hbmMask);
			}
		}
	}

	private static byte[] ReadBitmap(IntPtr bitmap, out int width, out int height)
	{
		Native.BITMAPINFOHEADER bmi = new Native.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)) };
		IntPtr dc = Native.GetDC(IntPtr.Zero);
		try
		{
			Native.GetDIBits(dc, bitmap, 0, 0, null, ref bmi, 0);
			width = bmi.biWidth;
			height = Math.Abs(bmi.biHeight);
			bmi.biHeight = -height;
			bmi.biBitCount = 32;
			bmi.biCompression = 0;
			bmi.biSizeImage = 0;
			byte[] bits = new byte[width * height * 4];
			Native.GetDIBits(dc, bitmap, 0, (uint)height, bits, ref bmi, 0);
			return bits;
		}
		finally
		{
			Native.ReleaseDC(IntPtr.Zero, dc);
		}
	}

	// Simple drawn icon if the shell gives us nothing.
	public static Texture2D Fallback(bool isFolder)
	{
		const int s = 32;
		Texture2D tex = new Texture2D(s, s, TextureFormat.RGBA32, mipChain: false) { filterMode = FilterMode.Point };
		Color32[] pixels = new Color32[s * s];
		Color32 body = isFolder ? new Color32(240, 196, 80, 255) : new Color32(250, 250, 250, 255);
		Color32 edge = isFolder ? new Color32(190, 140, 40, 255) : new Color32(120, 120, 120, 255);
		for (int y = 0; y < s; y++)
		{
			for (int x = 0; x < s; x++)
			{
				bool inside = isFolder ? (y >= 4 && y < 26 && x >= 2 && x < 30) || (y >= 26 && y < 29 && x >= 2 && x < 14) : x >= 6 && x < 26 && y >= 2 && y < 30;
				bool border = inside && (x == 2 || x == 29 || y == 4 || y == 25 || (!isFolder && (x == 6 || x == 25 || y == 2 || y == 29)));
				pixels[y * s + x] = inside ? (border ? edge : body) : new Color32(0, 0, 0, 0);
			}
		}
		tex.SetPixels32(pixels);
		tex.Apply();
		return tex;
	}
}
