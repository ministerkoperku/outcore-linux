using System;
using System.IO;
using UnityEngine;

namespace OutcoreLinux.Desktop;

internal static class Wallpaper
{
	public static Texture2D Load()
	{
		string path = Plugin.Instance.WallpaperPath.Value;
		if (!string.IsNullOrEmpty(path))
		{
			try
			{
				if (File.Exists(path))
				{
					Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
					if (tex.LoadImage(File.ReadAllBytes(path)))
					{
						tex.wrapMode = TextureWrapMode.Clamp;
						return tex;
					}
				}
				Log.Warn("Wallpaper not found or unreadable: " + path);
			}
			catch (Exception ex)
			{
				Log.Warn("Wallpaper failed: " + ex.Message);
			}
		}
		return Default();
	}

	// A Windows-10-ish blue gradient with a soft light beam.
	private static Texture2D Default()
	{
		const int w = 320;
		const int h = 180;
		Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
		{
			wrapMode = TextureWrapMode.Clamp,
			filterMode = FilterMode.Bilinear
		};
		Color32[] pixels = new Color32[w * h];
		Color top = new Color(0.02f, 0.13f, 0.32f);
		Color bottom = new Color(0.0f, 0.05f, 0.16f);
		Color beam = new Color(0.25f, 0.55f, 0.95f);
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float v = y / (float)(h - 1);
				Color c = Color.Lerp(bottom, top, v);
				float d = Mathf.Abs((x - w * 0.62f) - (y - h * 0.5f) * 0.55f) / (w * 0.22f);
				c = Color.Lerp(c, beam, Mathf.Clamp01(1f - d) * 0.55f);
				pixels[y * w + x] = c;
			}
		}
		tex.SetPixels32(pixels);
		tex.Apply();
		return tex;
	}
}
