using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OutcoreLinux.Shell;

internal static class ScreenGrab
{
	// Main thread only. Whole primary screen through GDI (what CopyFromScreen does), falling
	// back to the game's own frame if the X server will not give us pixels.
	public static Texture2D CapturePrimary()
	{
		ScreenData primary = Screens.Primary();
		int w = primary.Bounds.Width;
		int h = primary.Bounds.Height;
		IntPtr screen = Native.GetDC(IntPtr.Zero);
		IntPtr mem = Native.CreateCompatibleDC(screen);
		Native.BITMAPINFOHEADER bmi = new Native.BITMAPINFOHEADER
		{
			biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
			biWidth = w,
			biHeight = -h,
			biPlanes = 1,
			biBitCount = 32
		};
		IntPtr dib = Native.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
		try
		{
			IntPtr old = Native.SelectObject(mem, dib);
			bool ok = Native.BitBlt(mem, 0, 0, w, h, screen, primary.Bounds.left, primary.Bounds.top, Native.SRCCOPY);
			Native.GdiFlush();
			Native.SelectObject(mem, old);
			byte[] data = new byte[w * h * 4];
			Marshal.Copy(bits, data, 0, data.Length);
			bool anything = false;
			for (int i = 0; i < data.Length && !anything; i += 4 * 97)
			{
				anything = data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 0;
			}
			if (!ok || !anything)
			{
				return ScreenCapture.CaptureScreenshotAsTexture();
			}
			Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
			Color32[] pixels = new Color32[w * h];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int s = (y * w + x) * 4;
					pixels[(h - 1 - y) * w + x] = new Color32(data[s + 2], data[s + 1], data[s], 255);
				}
			}
			tex.SetPixels32(pixels);
			tex.Apply();
			return tex;
		}
		finally
		{
			Native.DeleteObject(dib);
			Native.DeleteDC(mem);
			Native.ReleaseDC(IntPtr.Zero, screen);
		}
	}
}
