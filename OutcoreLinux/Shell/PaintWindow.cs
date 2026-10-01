using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OutcoreLinux.Shell;

// Stand-in for mspaint.exe (Wine has none, and the bundled Paint.NET needs .NET 8 + WPF).
// Enough for the keyhole puzzle: pencil, fill, eraser, colors, brush sizes, Ctrl+S.
internal sealed class PaintWindow : ShellWindow
{
	private enum Tool
	{
		Pencil,
		Fill,
		Eraser
	}

	private const int ToolbarHeight = 44;
	private const int ButtonSize = 30;

	private static readonly uint[] Palette =
	{
		0xFF000000, 0xFF7F7F7F, 0xFF880015, 0xFFED1C24, 0xFFFF7F27, 0xFFFFF200,
		0xFF22B14C, 0xFF00A2E8, 0xFF3F48CC, 0xFFA349A4, 0xFFB97A57, 0xFFFFFFFF
	};

	private static readonly int[] Sizes = { 1, 3, 8 };

	private string _path;
	private int _width;
	private int _height;
	private uint[] _pixels;
	private IntPtr _dib;
	private IntPtr _dibBits;
	private Tool _tool = Tool.Pencil;
	private int _sizeIndex = 1;
	private uint _color = 0xFF000000;
	private bool _drawing;
	private bool _secondary;
	private int _lastX;
	private int _lastY;
	private bool _dirty;
	private int _zoom = 1;
	private int _originX;
	private int _originY;

	// Called from any thread. Decoding needs Unity, so it hops to the main thread first.
	public static void Open(string path)
	{
		if (!Plugin.IsMainThread)
		{
			MainThread.Post(() => Open(path));
			return;
		}
		int w = 200;
		int h = 200;
		uint[] pixels;
		try
		{
			Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
			if (File.Exists(path) && tex.LoadImage(File.ReadAllBytes(path)))
			{
				w = tex.width;
				h = tex.height;
				Color32[] colors = tex.GetPixels32();
				pixels = new uint[w * h];
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						Color32 c = colors[(h - 1 - y) * w + x];
						// Paint has no transparency; flatten onto white like mspaint does.
						float a = c.a / 255f;
						byte r = (byte)(c.r * a + 255 * (1 - a));
						byte g = (byte)(c.g * a + 255 * (1 - a));
						byte b = (byte)(c.b * a + 255 * (1 - a));
						pixels[y * w + x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
					}
				}
			}
			else
			{
				pixels = NewBlank(w, h);
			}
			UnityEngine.Object.Destroy(tex);
		}
		catch (Exception ex)
		{
			Log.Warn("Paint could not read " + path + ": " + ex.Message);
			pixels = NewBlank(w, h);
		}
		ShellThread.Post(() =>
		{
			PaintWindow window = new PaintWindow { _path = path, _width = w, _height = h, _pixels = pixels };
			window.Create();
		});
	}

	private static uint[] NewBlank(int w, int h)
	{
		uint[] pixels = new uint[w * h];
		for (int i = 0; i < pixels.Length; i++)
		{
			pixels[i] = 0xFFFFFFFF;
		}
		return pixels;
	}

	private void Create()
	{
		Native.RECT work = Screens.PrimaryWorkArea();
		int clientW = Math.Max(560, Math.Min(work.Width - 80, _width * 3 + 40));
		int clientH = Math.Max(360, Math.Min(work.Height - 120, _height * 3 + ToolbarHeight + 60));
		ShellThread.Create(this, 0, ShellThread.PaintClass, Title(), Native.WS_OVERLAPPEDWINDOW, work.left + 60, work.top + 40, clientW, clientH, IntPtr.Zero);
		if (Hwnd == IntPtr.Zero)
		{
			return;
		}
		Native.BITMAPINFOHEADER bmi = new Native.BITMAPINFOHEADER
		{
			biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
			biWidth = _width,
			biHeight = -_height,
			biPlanes = 1,
			biBitCount = 32
		};
		_dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
		SyncDib();
		Native.ShowWindow(Hwnd, Native.SW_SHOWNORMAL);
		Native.SetForegroundWindow(Hwnd);
	}

	private string Title() => (_dirty ? "*" : "") + Path.GetFileName(_path) + " - Paint";

	private void SyncDib()
	{
		if (_dibBits != IntPtr.Zero)
		{
			Marshal.Copy((int[])(object)_pixels, 0, _dibBits, _pixels.Length);
		}
	}

	public void Save()
	{
		if (!ShellThread.IsShellThread)
		{
			ShellThread.Post(Save);
			return;
		}
		uint[] copy = (uint[])_pixels.Clone();
		int w = _width;
		int h = _height;
		string path = _path;
		MainThread.Post(() =>
		{
			try
			{
				Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
				Color32[] colors = new Color32[w * h];
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						uint p = copy[y * w + x];
						colors[(h - 1 - y) * w + x] = new Color32((byte)(p >> 16), (byte)(p >> 8), (byte)p, 255);
					}
				}
				tex.SetPixels32(colors);
				tex.Apply();
				string ext = Path.GetExtension(path).ToLowerInvariant();
				byte[] data = ext == ".jpg" || ext == ".jpeg" ? tex.EncodeToJPG(95) : tex.EncodeToPNG();
				UnityEngine.Object.Destroy(tex);
				File.WriteAllBytes(path, data);
				Log.Info("Paint saved " + path);
			}
			catch (Exception ex)
			{
				Log.Error("Paint save failed: " + ex);
			}
		});
		_dirty = false;
		Native.SetWindowTextW(Hwnd, Title());
	}

	private void ComputeLayout()
	{
		Native.GetClientRect(Hwnd, out Native.RECT client);
		int availW = Math.Max(1, client.Width - 16);
		int availH = Math.Max(1, client.Height - ToolbarHeight - 16);
		_zoom = Math.Max(1, Math.Min(availW / _width, availH / _height));
		_originX = (client.Width - _width * _zoom) / 2;
		_originY = ToolbarHeight + (client.Height - ToolbarHeight - _height * _zoom) / 2;
	}

	protected internal override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
	{
		switch (msg)
		{
		case Native.WM_SIZE:
			ComputeLayout();
			Native.InvalidateRect(Hwnd, IntPtr.Zero, true);
			return IntPtr.Zero;
		case Native.WM_PAINT:
			OnPaint();
			return IntPtr.Zero;
		case Native.WM_LBUTTONDOWN:
		case Native.WM_RBUTTONDOWN:
			OnMouseDown(Native.LoWord(lParam), Native.HiWord(lParam), msg == Native.WM_RBUTTONDOWN);
			return IntPtr.Zero;
		case Native.WM_MOUSEMOVE:
			if (_drawing)
			{
				DrawTo(Native.LoWord(lParam), Native.HiWord(lParam));
			}
			return IntPtr.Zero;
		case Native.WM_LBUTTONUP:
		case Native.WM_RBUTTONUP:
			if (_drawing)
			{
				_drawing = false;
				Native.ReleaseCapture();
			}
			return IntPtr.Zero;
		case Native.WM_CLOSE:
			if (_dirty)
			{
				int answer = MessageBoxW(Hwnd, "Save changes to " + Path.GetFileName(_path) + "?", "Paint", 0x3 | 0x30);
				if (answer == 2)
				{
					return IntPtr.Zero;
				}
				if (answer == 6)
				{
					Save();
				}
			}
			break;
		case Native.WM_DESTROY:
			if (_dib != IntPtr.Zero)
			{
				Native.DeleteObject(_dib);
				_dib = IntPtr.Zero;
			}
			return IntPtr.Zero;
		}
		return Default(msg, wParam, lParam);
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

	private bool ToCanvas(int cx, int cy, out int x, out int y)
	{
		x = (int)Math.Floor((cx - _originX) / (double)_zoom);
		y = (int)Math.Floor((cy - _originY) / (double)_zoom);
		return x >= 0 && y >= 0 && x < _width && y < _height;
	}

	private void OnMouseDown(int cx, int cy, bool secondary)
	{
		if (cy < ToolbarHeight)
		{
			OnToolbarClick(cx, cy);
			return;
		}
		if (!ToCanvas(cx, cy, out int x, out int y))
		{
			return;
		}
		_secondary = secondary;
		if (_tool == Tool.Fill)
		{
			FloodFill(x, y, CurrentColor());
			Changed();
			return;
		}
		_drawing = true;
		_lastX = x;
		_lastY = y;
		Native.SetCapture(Hwnd);
		Stamp(x, y);
		Changed();
	}

	private uint CurrentColor()
	{
		if (_tool == Tool.Eraser)
		{
			return _secondary ? _color : 0xFFFFFFFF;
		}
		return _secondary ? 0xFFFFFFFF : _color;
	}

	private void DrawTo(int cx, int cy)
	{
		ToCanvas(cx, cy, out int x, out int y);
		int dx = Math.Abs(x - _lastX);
		int dy = -Math.Abs(y - _lastY);
		int sx = _lastX < x ? 1 : -1;
		int sy = _lastY < y ? 1 : -1;
		int err = dx + dy;
		int px = _lastX;
		int py = _lastY;
		while (true)
		{
			Stamp(px, py);
			if (px == x && py == y)
			{
				break;
			}
			int e2 = 2 * err;
			if (e2 >= dy)
			{
				err += dy;
				px += sx;
			}
			if (e2 <= dx)
			{
				err += dx;
				py += sy;
			}
		}
		_lastX = x;
		_lastY = y;
		Changed();
	}

	private void Stamp(int x, int y)
	{
		int size = _tool == Tool.Eraser ? Sizes[_sizeIndex] * 2 : Sizes[_sizeIndex];
		int half = size / 2;
		uint color = CurrentColor();
		for (int yy = y - half; yy < y - half + size; yy++)
		{
			for (int xx = x - half; xx < x - half + size; xx++)
			{
				if (xx >= 0 && yy >= 0 && xx < _width && yy < _height)
				{
					_pixels[yy * _width + xx] = color;
				}
			}
		}
	}

	private void FloodFill(int x, int y, uint color)
	{
		uint target = _pixels[y * _width + x];
		if (target == color)
		{
			return;
		}
		Stack<int> stack = new Stack<int>();
		stack.Push(y * _width + x);
		while (stack.Count > 0)
		{
			int i = stack.Pop();
			if (_pixels[i] != target)
			{
				continue;
			}
			_pixels[i] = color;
			int px = i % _width;
			int py = i / _width;
			if (px > 0)
			{
				stack.Push(i - 1);
			}
			if (px < _width - 1)
			{
				stack.Push(i + 1);
			}
			if (py > 0)
			{
				stack.Push(i - _width);
			}
			if (py < _height - 1)
			{
				stack.Push(i + _width);
			}
		}
	}

	private void Changed()
	{
		SyncDib();
		Native.RECT canvas = new Native.RECT { left = _originX, top = _originY, right = _originX + _width * _zoom, bottom = _originY + _height * _zoom };
		IntPtr rect = Marshal.AllocHGlobal(16);
		Marshal.StructureToPtr(canvas, rect, false);
		Native.InvalidateRect(Hwnd, rect, false);
		Marshal.FreeHGlobal(rect);
		if (!_dirty)
		{
			_dirty = true;
			Native.SetWindowTextW(Hwnd, Title());
		}
	}

	// Toolbar: [pencil][fill][eraser] | [size x3] | [12 colors] | [save]
	private IEnumerable<KeyValuePair<Native.RECT, Action>> ToolbarButtons()
	{
		int x = 8;
		int y = (ToolbarHeight - ButtonSize) / 2;
		foreach (Tool tool in new[] { Tool.Pencil, Tool.Fill, Tool.Eraser })
		{
			Tool t = tool;
			yield return new KeyValuePair<Native.RECT, Action>(Rect(x, y), () => _tool = t);
			x += ButtonSize + 4;
		}
		x += 10;
		for (int i = 0; i < Sizes.Length; i++)
		{
			int index = i;
			yield return new KeyValuePair<Native.RECT, Action>(Rect(x, y), () => _sizeIndex = index);
			x += ButtonSize + 4;
		}
		x += 10;
		foreach (uint color in Palette)
		{
			uint c = color;
			yield return new KeyValuePair<Native.RECT, Action>(Rect(x, y), () =>
			{
				_color = c;
				if (_tool == Tool.Eraser)
				{
					_tool = Tool.Pencil;
				}
			});
			x += ButtonSize + 2;
		}
		x += 10;
		yield return new KeyValuePair<Native.RECT, Action>(new Native.RECT { left = x, top = y, right = x + 70, bottom = y + ButtonSize }, Save);
	}

	private static Native.RECT Rect(int x, int y) => new Native.RECT { left = x, top = y, right = x + ButtonSize, bottom = y + ButtonSize };

	private void OnToolbarClick(int cx, int cy)
	{
		foreach (KeyValuePair<Native.RECT, Action> button in ToolbarButtons())
		{
			if (button.Key.Contains(cx, cy))
			{
				button.Value();
				Native.InvalidateRect(Hwnd, IntPtr.Zero, false);
				return;
			}
		}
	}

	private void OnPaint()
	{
		IntPtr hdc = Native.BeginPaint(Hwnd, out Native.PAINTSTRUCT ps);
		try
		{
			Native.GetClientRect(Hwnd, out Native.RECT client);
			IntPtr background = Native.CreateSolidBrush(Native.Rgb(200, 209, 225));
			Native.RECT area = new Native.RECT { left = 0, top = ToolbarHeight, right = client.right, bottom = client.bottom };
			Native.FillRect(hdc, ref area, background);
			Native.DeleteObject(background);
			Native.RECT bar = new Native.RECT { left = 0, top = 0, right = client.right, bottom = ToolbarHeight };
			Native.FillRect(hdc, ref bar, Native.GetSysColorBrush(Native.COLOR_BTNFACE));
			DrawToolbar(hdc);
			if (_dib != IntPtr.Zero)
			{
				IntPtr mem = Native.CreateCompatibleDC(hdc);
				IntPtr old = Native.SelectObject(mem, _dib);
				Native.SetStretchBltMode(hdc, 3); // COLORONCOLOR, keeps pixels sharp
				Native.StretchBlt(hdc, _originX, _originY, _width * _zoom, _height * _zoom, mem, 0, 0, _width, _height, Native.SRCCOPY);
				Native.SelectObject(mem, old);
				Native.DeleteDC(mem);
			}
		}
		finally
		{
			Native.EndPaint(Hwnd, ref ps);
		}
	}

	private void DrawToolbar(IntPtr hdc)
	{
		Native.SelectObject(hdc, ShellThread.DefaultFont);
		Native.SetBkMode(hdc, Native.TRANSPARENT);
		int index = 0;
		foreach (KeyValuePair<Native.RECT, Action> button in ToolbarButtons())
		{
			Native.RECT r = button.Key;
			bool selected;
			uint fill;
			string label = null;
			if (index < 3)
			{
				selected = (int)_tool == index;
				fill = selected ? Native.Rgb(204, 228, 247) : Native.Rgb(240, 240, 240);
				label = index == 0 ? "✎" : index == 1 ? "▧" : "▯";
			}
			else if (index < 3 + Sizes.Length)
			{
				selected = _sizeIndex == index - 3;
				fill = selected ? Native.Rgb(204, 228, 247) : Native.Rgb(240, 240, 240);
				label = Sizes[index - 3] + "px";
			}
			else if (index < 3 + Sizes.Length + Palette.Length)
			{
				uint argb = Palette[index - 3 - Sizes.Length];
				selected = argb == _color;
				fill = Native.Rgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
			}
			else
			{
				selected = false;
				fill = Native.Rgb(240, 240, 240);
				label = "Save";
			}
			IntPtr brush = Native.CreateSolidBrush(fill);
			IntPtr pen = Native.CreatePen(0, selected ? 3 : 1, selected ? Native.Rgb(0, 120, 215) : Native.Rgb(160, 160, 160));
			IntPtr oldBrush = Native.SelectObject(hdc, brush);
			IntPtr oldPen = Native.SelectObject(hdc, pen);
			Native.Rectangle(hdc, r.left, r.top, r.right, r.bottom);
			Native.SelectObject(hdc, oldBrush);
			Native.SelectObject(hdc, oldPen);
			Native.DeleteObject(brush);
			Native.DeleteObject(pen);
			if (label != null)
			{
				Native.SetTextColor(hdc, 0);
				Native.DrawTextW(hdc, label, -1, ref r, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE);
			}
			index++;
		}
	}
}
