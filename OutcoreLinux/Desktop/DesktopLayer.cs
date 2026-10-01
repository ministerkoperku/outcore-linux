using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OutcoreLinux.Shell;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OutcoreLinux.Desktop;

internal sealed class DesktopIcon
{
	public string Path;
	public string Name;
	public bool IsFolder;
	public bool IsThisPc;
	public int Col;
	public int Row;
	public Texture2D Texture;
	public GameObject Root;
	public SpriteRenderer IconRenderer;
	public SpriteRenderer Highlight;
	public TextMesh Label;
	public TextMesh Shadow;
}

// The desktop, drawn by the game itself.
//
// On Windows the game is a transparent always-on-top window over the real desktop. Wine cannot
// composite DWM transparency and GameNative's X server forces every window opaque, so instead
// the game window becomes the desktop: fullscreen, at the bottom of the z-order, with the
// wallpaper and the Desktop folder's icons rendered as sprites underneath the characters.
// Folder windows, Notepad and Paint are ordinary windows above it.
internal sealed class DesktopLayer : MonoBehaviour
{
	private const int SortBase = -32760;
	private const float RefreshInterval = 1f;
	private const uint WM_WINDOWPOSCHANGING = 0x0046;

	private readonly List<DesktopIcon> _icons = new List<DesktopIcon>();
	private readonly Dictionary<string, Vector2Int> _savedCells = new Dictionary<string, Vector2Int>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Vector2Int> _pendingCells = new Dictionary<string, Vector2Int>(StringComparer.OrdinalIgnoreCase);
	private string _signature;
	private float _nextRefresh;
	private GameObject _root;
	private SpriteRenderer _wallpaper;
	private Texture2D _white;
	private Font _font;
	private Camera _camera;
	private int _renderLayer;
	private int _sortingLayer;
	private DesktopIcon _selected;
	private DesktopIcon _pressed;
	private Native.POINT _pressPoint;
	private bool _dragging;
	private bool _wasDown;
	private float _lastClickTime;
	private DesktopIcon _lastClickIcon;
	private bool _gameHitCache;
	private int _gameHitFrame = -1;
	private float _nextZCheck;
	private static Native.WndProc _hookProc;
	private static IntPtr _originalProc;
	private static IntPtr _hookedWindow;
	private Taskbar _taskbar;
	private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();
	private RenderTexture _probeTarget;
	private Texture2D _probeRead;
	private int _probeFrame = -1;
	private bool _probeBroken;

	public bool Active { get; private set; }

	public int TaskbarHeight => _taskbar != null && _taskbar.Visible ? _taskbar.Height : 0;

	private string SavePath => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "OutcoreLinux.desktop.txt");

	public float Scale
	{
		get
		{
			ScreenData primary = Screens.Primary();
			return Mathf.Clamp(primary.Bounds.Height / 1080f * Plugin.Instance.IconScale.Value, 0.6f, 3f);
		}
	}

	private int CellWidth => Mathf.RoundToInt(76 * Scale);

	private int CellHeight => Mathf.RoundToInt(96 * Scale);

	private void Awake()
	{
		_white = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
		_white.SetPixel(0, 0, Color.white);
		_white.Apply();
		try
		{
			_font = Resources.GetBuiltinResource<Font>("Arial.ttf");
		}
		catch
		{
			_font = null;
		}
		if (_font == null)
		{
			try
			{
				_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			}
			catch
			{
				_font = null;
			}
		}
		LoadCells();
		if (Plugin.Instance.UseTaskbar)
		{
			_taskbar = Taskbar.Create();
		}
	}

	public void SetActive(bool active)
	{
		if (Active == active)
		{
			return;
		}
		Active = active;
		Log.Info("Desktop mode " + (active ? "on" : "off"));
		if (active)
		{
			HookWindowProc();
			SendGameToBottom();
			_signature = null;
			_nextRefresh = 0f;
		}
		if (_root != null)
		{
			_root.SetActive(active);
		}
		_taskbar?.SetVisible(active);
	}

	// ---- model ----------------------------------------------------------------------------

	public static IEnumerable<string> DesktopFolders()
	{
		string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (!string.IsNullOrEmpty(user))
		{
			yield return user;
		}
		string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
		if (!string.IsNullOrEmpty(common) && !ShellPaths.Same(common, user))
		{
			yield return common;
		}
	}

	private List<ShellItem> ListDesktop()
	{
		List<ShellItem> items = new List<ShellItem>();
		if (Plugin.Instance.ShowThisPcIcon.Value)
		{
			items.Add(new ShellItem { Path = ShellPaths.ThisPc, DisplayName = ShellPaths.ThisPcName, IsFolder = true, IsVirtual = true });
		}
		foreach (string folder in DesktopFolders())
		{
			if (Directory.Exists(folder))
			{
				items.AddRange(ShellPaths.List(folder));
			}
		}
		return items;
	}

	private string DesktopSignature()
	{
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		foreach (string folder in DesktopFolders())
		{
			sb.Append(ShellPaths.Signature(folder)).Append('#');
		}
		return sb.ToString();
	}

	public void Sync(bool force = false)
	{
		string signature = DesktopSignature();
		if (!force && signature == _signature)
		{
			return;
		}
		_signature = signature;
		List<ShellItem> items = ListDesktop();
		HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ShellItem item in items)
		{
			keep.Add(Key(item.Path, item.IsVirtual));
		}
		for (int i = _icons.Count - 1; i >= 0; i--)
		{
			DesktopIcon icon = _icons[i];
			if (!keep.Contains(Key(icon.Path, icon.IsThisPc)))
			{
				DestroyIcon(icon);
				_icons.RemoveAt(i);
			}
		}
		foreach (ShellItem item in items)
		{
			DesktopIcon existing = _icons.Find(icon => Key(icon.Path, icon.IsThisPc) == Key(item.Path, item.IsVirtual));
			if (existing != null)
			{
				existing.Name = item.DisplayName;
				continue;
			}
			DesktopIcon created = new DesktopIcon
			{
				Path = item.Path,
				Name = item.DisplayName,
				IsFolder = item.IsFolder,
				IsThisPc = item.IsVirtual
			};
			string name = System.IO.Path.GetFileName(item.Path);
			Vector2Int cell;
			if (_pendingCells.TryGetValue(name, out cell))
			{
				_pendingCells.Remove(name);
			}
			else if (!_savedCells.TryGetValue(item.IsVirtual ? "::thispc" : name, out cell) || IsCellTaken(cell, null))
			{
				cell = FirstFreeCell();
			}
			created.Col = cell.x;
			created.Row = cell.y;
			_icons.Add(created);
		}
		SaveCells();
	}

	private static string Key(string path, bool isThisPc) => isThisPc ? "::thispc" : ShellPaths.Normalize(path);

	private int Rows
	{
		get
		{
			Native.RECT work = Screens.PrimaryWorkArea();
			return Mathf.Max(1, work.Height / Mathf.Max(1, CellHeight));
		}
	}

	private int Cols
	{
		get
		{
			Native.RECT work = Screens.PrimaryWorkArea();
			return Mathf.Max(1, work.Width / Mathf.Max(1, CellWidth));
		}
	}

	private bool IsCellTaken(Vector2Int cell, DesktopIcon except)
	{
		foreach (DesktopIcon icon in _icons)
		{
			if (icon != except && icon.Col == cell.x && icon.Row == cell.y)
			{
				return true;
			}
		}
		return false;
	}

	// Windows fills the desktop column by column from the top-left corner.
	private Vector2Int FirstFreeCell()
	{
		int rows = Rows;
		for (int col = 0; col < 64; col++)
		{
			for (int row = 0; row < rows; row++)
			{
				Vector2Int cell = new Vector2Int(col, row);
				if (!IsCellTaken(cell, null))
				{
					return cell;
				}
			}
		}
		return Vector2Int.zero;
	}

	public Native.RECT CellRect(int col, int row)
	{
		Native.RECT work = Screens.PrimaryWorkArea();
		int left = work.left + col * CellWidth;
		int top = work.top + row * CellHeight;
		return new Native.RECT { left = left, top = top, right = left + CellWidth, bottom = top + CellHeight };
	}

	private Vector2Int CellAt(Native.POINT p)
	{
		Native.RECT work = Screens.PrimaryWorkArea();
		int col = Mathf.Clamp((p.x - work.left) / Mathf.Max(1, CellWidth), 0, Cols - 1);
		int row = Mathf.Clamp((p.y - work.top) / Mathf.Max(1, CellHeight), 0, Rows - 1);
		return new Vector2Int(col, row);
	}

	private DesktopIcon IconAt(Native.POINT p)
	{
		foreach (DesktopIcon icon in _icons)
		{
			if (CellRect(icon.Col, icon.Row).Contains(p.x, p.y))
			{
				return icon;
			}
		}
		return null;
	}

	// What GetDesktopIcons answers: names as explorer shows them and their screen rectangles.
	public List<KeyValuePair<string, Native.RECT>> GetIconRects()
	{
		Sync();
		List<KeyValuePair<string, Native.RECT>> list = new List<KeyValuePair<string, Native.RECT>>();
		foreach (DesktopIcon icon in _icons)
		{
			list.Add(new KeyValuePair<string, Native.RECT>(icon.Name, CellRect(icon.Col, icon.Row)));
		}
		return list;
	}

	private void LoadCells()
	{
		try
		{
			if (!File.Exists(SavePath))
			{
				return;
			}
			foreach (string line in File.ReadAllLines(SavePath))
			{
				string[] parts = line.Split(new[] { ',' }, 3);
				if (parts.Length == 3 && int.TryParse(parts[0], out int col) && int.TryParse(parts[1], out int row))
				{
					_savedCells[parts[2]] = new Vector2Int(col, row);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn("Could not read desktop layout: " + ex.Message);
		}
	}

	private void SaveCells()
	{
		try
		{
			foreach (DesktopIcon icon in _icons)
			{
				_savedCells[icon.IsThisPc ? "::thispc" : System.IO.Path.GetFileName(icon.Path)] = new Vector2Int(icon.Col, icon.Row);
			}
			List<string> lines = new List<string>();
			foreach (KeyValuePair<string, Vector2Int> pair in _savedCells)
			{
				lines.Add(pair.Value.x + "," + pair.Value.y + "," + pair.Key);
			}
			File.WriteAllLines(SavePath, lines.ToArray());
		}
		catch (Exception ex)
		{
			Log.Warn("Could not save desktop layout: " + ex.Message);
		}
	}

	// ---- rendering --------------------------------------------------------------------------

	private bool EnsureScene()
	{
		Camera cam = Camera.main;
		if (cam == null)
		{
			return false;
		}
		if (_root != null && _camera == cam)
		{
			return true;
		}
		_camera = cam;
		_renderLayer = 0;
		for (int i = 0; i < 32; i++)
		{
			if ((cam.cullingMask & (1 << i)) != 0)
			{
				_renderLayer = i;
				break;
			}
		}
		SortingLayer[] layers = SortingLayer.layers;
		_sortingLayer = layers.Length > 0 ? layers[0].id : 0;
		if (_root != null)
		{
			Destroy(_root);
		}
		foreach (DesktopIcon icon in _icons)
		{
			icon.Root = null;
		}
		_root = new GameObject("OutcoreLinux Desktop");
		_root.layer = _renderLayer;
		_wallpaper = new GameObject("Wallpaper").AddComponent<SpriteRenderer>();
		_wallpaper.gameObject.layer = _renderLayer;
		_wallpaper.transform.SetParent(_root.transform, false);
		Texture2D wallpaper = Wallpaper.Load();
		_wallpaper.sprite = Sprite.Create(wallpaper, new Rect(0, 0, wallpaper.width, wallpaper.height), new Vector2(0.5f, 0.5f), 100f);
		Configure(_wallpaper, 0);
		return true;
	}

	private void Configure(Renderer renderer, int order)
	{
		renderer.sortingLayerID = _sortingLayer;
		renderer.sortingOrder = SortBase + order;
	}

	private float PlaneDistance()
	{
		float z = Mathf.Abs(_camera.transform.position.z);
		return z > _camera.nearClipPlane && z < _camera.farClipPlane ? z : (_camera.nearClipPlane + _camera.farClipPlane) * 0.5f;
	}

	private Vector3 DesktopToWorld(float x, float y)
	{
		ScreenData primary = Screens.Primary();
		float sx = (x - primary.Bounds.left) * Screen.width / Mathf.Max(1, primary.Bounds.Width);
		float sy = Screen.height - (y - primary.Bounds.top) * Screen.height / Mathf.Max(1, primary.Bounds.Height);
		return _camera.ScreenToWorldPoint(new Vector3(sx, sy, PlaneDistance()));
	}

	private float WorldPerDesktopPixel()
	{
		Vector3 a = DesktopToWorld(0, 0);
		Vector3 b = DesktopToWorld(100, 0);
		return Mathf.Abs(b.x - a.x) / 100f;
	}

	private void BuildIcon(DesktopIcon icon)
	{
		icon.Root = new GameObject("Icon " + icon.Name);
		icon.Root.layer = _renderLayer;
		icon.Root.transform.SetParent(_root.transform, false);
		if (icon.Texture == null)
		{
			icon.Texture = icon.IsThisPc ? IconTextures.ForPath("C:\\", isFolder: true) : IconTextures.ForPath(icon.Path, icon.IsFolder);
		}
		icon.Highlight = NewSprite(icon.Root, "Highlight", _white, 1);
		icon.Highlight.color = new Color(0.6f, 0.8f, 1f, 0.35f);
		icon.IconRenderer = NewSprite(icon.Root, "Image", icon.Texture, 2);
		icon.Shadow = NewText(icon.Root, "Shadow", Color.black, 3);
		icon.Label = NewText(icon.Root, "Label", Color.white, 4);
		string text = WrapLabel(icon.Name);
		icon.Label.text = text;
		icon.Shadow.text = text;
	}

	private SpriteRenderer NewSprite(GameObject parent, string name, Texture2D tex, int order)
	{
		SpriteRenderer sr = new GameObject(name).AddComponent<SpriteRenderer>();
		sr.gameObject.layer = _renderLayer;
		sr.transform.SetParent(parent.transform, false);
		sr.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
		Configure(sr, order);
		return sr;
	}

	private TextMesh NewText(GameObject parent, string name, Color color, int order)
	{
		TextMesh tm = new GameObject(name).AddComponent<TextMesh>();
		tm.gameObject.layer = _renderLayer;
		tm.transform.SetParent(parent.transform, false);
		tm.font = _font;
		tm.fontSize = 48;
		tm.anchor = TextAnchor.UpperCenter;
		tm.alignment = TextAlignment.Center;
		tm.color = color;
		MeshRenderer mr = tm.GetComponent<MeshRenderer>();
		if (_font != null)
		{
			mr.sharedMaterial = _font.material;
		}
		Configure(mr, order);
		return tm;
	}

	private static string WrapLabel(string name)
	{
		const int width = 13;
		if (name.Length <= width)
		{
			return name;
		}
		int split = name.LastIndexOf(' ', width);
		if (split <= 0)
		{
			split = width;
		}
		string first = name.Substring(0, split).TrimEnd();
		string rest = name.Substring(split).TrimStart();
		if (rest.Length > width)
		{
			rest = rest.Substring(0, width - 1) + "\u2026";
		}
		return first + "\n" + rest;
	}

	private static void DestroyIcon(DesktopIcon icon)
	{
		if (icon.Root != null)
		{
			Destroy(icon.Root);
		}
		if (icon.Texture != null)
		{
			Destroy(icon.Texture);
		}
	}

	private void LayoutScene()
	{
		ScreenData primary = Screens.Primary();
		Vector3 center = DesktopToWorld(primary.Bounds.left + primary.Bounds.Width * 0.5f, primary.Bounds.top + primary.Bounds.Height * 0.5f);
		Vector3 corner = DesktopToWorld(primary.Bounds.left, primary.Bounds.top);
		float worldW = Mathf.Abs(center.x - corner.x) * 2f;
		float worldH = Mathf.Abs(center.y - corner.y) * 2f;
		Sprite wp = _wallpaper.sprite;
		float spriteW = wp.rect.width / wp.pixelsPerUnit;
		float spriteH = wp.rect.height / wp.pixelsPerUnit;
		float cover = Mathf.Max(worldW / spriteW, worldH / spriteH) * 1.01f;
		_wallpaper.transform.position = center;
		_wallpaper.transform.localScale = new Vector3(cover, cover, 1f);

		float unit = WorldPerDesktopPixel();
		float scale = Scale;
		Native.POINT cursor = default;
		if (_dragging)
		{
			Native.GetCursorPos(out cursor);
		}
		foreach (DesktopIcon icon in _icons)
		{
			if (icon.Root == null)
			{
				BuildIcon(icon);
			}
			Native.RECT cell = CellRect(icon.Col, icon.Row);
			float cx = (cell.left + cell.right) * 0.5f;
			float top = cell.top;
			if (_dragging && icon == _pressed)
			{
				cx += cursor.x - _pressPoint.x;
				top += cursor.y - _pressPoint.y;
			}
			float iconPx = 48f * scale;
			float iconCenterY = top + 4f * scale + iconPx * 0.5f;
			icon.IconRenderer.transform.position = DesktopToWorld(cx, iconCenterY);
			float texW = icon.Texture.width / 100f;
			icon.IconRenderer.transform.localScale = Vector3.one * (iconPx * unit / texW);
			Vector3 cellCenter = DesktopToWorld(cx, (cell.top + cell.bottom) * 0.5f);
			icon.Highlight.transform.position = cellCenter;
			icon.Highlight.transform.localScale = new Vector3(cell.Width * unit / 0.01f, cell.Height * unit / 0.01f, 1f);
			icon.Highlight.enabled = icon == _selected || (_dragging && icon == _pressed);
			float labelTop = top + 8f * scale + iconPx;
			float textPx = 12f * scale;
			float charSize = textPx * unit / (48f * 0.1f) * 1.15f;
			icon.Label.characterSize = charSize;
			icon.Shadow.characterSize = charSize;
			icon.Label.transform.position = DesktopToWorld(cx, labelTop);
			icon.Shadow.transform.position = DesktopToWorld(cx + Mathf.Max(1f, scale), labelTop + Mathf.Max(1f, scale));
		}
	}

	// ---- input --------------------------------------------------------------------------

	// True when the point hits something of the game. Same rule as the original click-through
	// test (opaque pixel || BlockDesktopClickthrough collider), plus screen-space UI which on
	// Windows is just more opaque pixels. The pixel test renders the game camera without our
	// desktop into a small texture, because with the wallpaper every visible pixel is opaque.
	public bool GameHitAt(Native.POINT desktop)
	{
		if (_camera == null)
		{
			return false;
		}
		ScreenData primary = Screens.Primary();
		Vector2 screen = new Vector2((desktop.x - primary.Bounds.left) * Screen.width / (float)Mathf.Max(1, primary.Bounds.Width), Screen.height - (desktop.y - primary.Bounds.top) * Screen.height / (float)Mathf.Max(1, primary.Bounds.Height));
		if (screen.x < 0 || screen.y < 0 || screen.x >= Screen.width || screen.y >= Screen.height)
		{
			return false;
		}
		EventSystem events = EventSystem.current;
		if (events != null)
		{
			_raycastResults.Clear();
			events.RaycastAll(new PointerEventData(events) { position = screen }, _raycastResults);
			foreach (RaycastResult result in _raycastResults)
			{
				if (result.gameObject != null && (_root == null || !result.gameObject.transform.IsChildOf(_root.transform)))
				{
					return true;
				}
			}
		}
		Vector3 world = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, PlaneDistance()));
		foreach (Collider2D collider in Physics2D.OverlapPointAll(world))
		{
			if (collider != null && collider.enabled && collider.GetComponent<BlockDesktopClickthrough>() != null)
			{
				return true;
			}
		}
		return ProbeAlpha(screen) >= 0.1f;
	}

	private float ProbeAlpha(Vector2 screen)
	{
		if (_probeBroken || _root == null)
		{
			return 0f;
		}
		int frame = Time.frameCount;
		const int divisor = 4;
		int w = Mathf.Max(1, Screen.width / divisor);
		int h = Mathf.Max(1, Screen.height / divisor);
		try
		{
			if (_probeTarget == null || _probeTarget.width != w || _probeTarget.height != h)
			{
				if (_probeTarget != null)
				{
					_probeTarget.Release();
					Destroy(_probeTarget);
				}
				_probeTarget = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
				_probeRead = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
				_probeFrame = -1;
			}
			// An extra camera render is not free on a phone; a frame or two of staleness is fine.
			if (_probeFrame < 0 || frame - _probeFrame >= 3)
			{
				_probeFrame = frame;
				bool wasActive = _root.activeSelf;
				RenderTexture previous = _camera.targetTexture;
				_root.SetActive(false);
				try
				{
					_camera.targetTexture = _probeTarget;
					_camera.Render();
				}
				finally
				{
					_camera.targetTexture = previous;
					_root.SetActive(wasActive);
				}
			}
			RenderTexture active = RenderTexture.active;
			RenderTexture.active = _probeTarget;
			_probeRead.ReadPixels(new Rect(Mathf.Clamp((int)(screen.x / divisor), 0, w - 1), Mathf.Clamp((int)(screen.y / divisor), 0, h - 1), 1, 1), 0, 0, recalculateMipMaps: false);
			RenderTexture.active = active;
			return _probeRead.GetPixel(0, 0).a;
		}
		catch (Exception ex)
		{
			_probeBroken = true;
			Log.Warn("Pixel probe disabled: " + ex.Message);
			return 0f;
		}
	}

	public bool GameHitAtCursor()
	{
		if (_gameHitFrame != Time.frameCount)
		{
			_gameHitFrame = Time.frameCount;
			Native.GetCursorPos(out Native.POINT p);
			_gameHitCache = GameHitAt(p);
		}
		return _gameHitCache;
	}

	private bool CursorOverGameWindow(Native.POINT p)
	{
		IntPtr hwnd = Native.WindowFromPoint(p);
		return hwnd != IntPtr.Zero && Native.GetAncestor(hwnd, Native.GA_ROOT) == GameWindow.Handle;
	}

	private void HandleInput()
	{
		Native.GetCursorPos(out Native.POINT p);
		bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
		bool pressed = down && !_wasDown;
		bool released = !down && _wasDown;
		_wasDown = down;
		if (pressed)
		{
			if (!CursorOverGameWindow(p) || GameHitAt(p))
			{
				_pressed = null;
				return;
			}
			DesktopIcon icon = IconAt(p);
			_selected = icon;
			_pressed = icon;
			_pressPoint = p;
			_dragging = false;
			if (icon != null)
			{
				float now = Time.unscaledTime;
				if (_lastClickIcon == icon && now - _lastClickTime <= Native.GetDoubleClickTime() / 1000f)
				{
					_lastClickIcon = null;
					_pressed = null;
					OpenIcon(icon);
					return;
				}
				_lastClickIcon = icon;
				_lastClickTime = now;
			}
		}
		if (down && _pressed != null && !_dragging && (Mathf.Abs(p.x - _pressPoint.x) > 4 || Mathf.Abs(p.y - _pressPoint.y) > 4))
		{
			_dragging = true;
		}
		if (released && _pressed != null)
		{
			DesktopIcon icon = _pressed;
			bool wasDragging = _dragging;
			_pressed = null;
			_dragging = false;
			if (wasDragging)
			{
				DropDesktopIcon(icon, p);
			}
		}
		if (_selected != null && !_selected.IsThisPc && Input.GetKeyDown(KeyCode.Delete) && Native.GetForegroundWindow() == GameWindow.Handle)
		{
			ShellPaths.Recycle(_selected.Path);
			_selected = null;
			Sync(force: true);
		}
	}

	private void OpenIcon(DesktopIcon icon)
	{
		if (icon.IsThisPc)
		{
			FolderWindow.OpenDefault(ShellPaths.ThisPc);
			return;
		}
		string path = icon.Path;
		ShellThread.Post(() => ShellOpen.Open(path, IntPtr.Zero));
	}

	private void DropDesktopIcon(DesktopIcon icon, Native.POINT p)
	{
		if (!CursorOverGameWindow(p))
		{
			if (!icon.IsThisPc)
			{
				DropResolver.Drop(new[] { icon.Path }, p, this);
			}
			Sync(force: true);
			return;
		}
		if (!icon.IsThisPc && GameHitAt(p))
		{
			Plugin.Instance.FireFilesDropped(new[] { icon.Path });
			return;
		}
		DesktopIcon target = IconAt(p);
		if (target != null && target != icon && target.IsFolder && !target.IsThisPc && !icon.IsThisPc)
		{
			ShellPaths.MoveInto(icon.Path, target.Path);
			Sync(force: true);
			return;
		}
		Vector2Int cell = CellAt(p);
		if (!IsCellTaken(cell, icon))
		{
			icon.Col = cell.x;
			icon.Row = cell.y;
			SaveCells();
		}
	}

	// A file dragged out of a folder window and released over the game window.
	public void DropFromShell(string[] paths, Native.POINT p, object source)
	{
		if (Active && GameHitAt(p))
		{
			Plugin.Instance.FireFilesDropped(paths);
			return;
		}
		if (!Active)
		{
			Plugin.Instance.FireFilesDropped(paths);
			return;
		}
		DesktopIcon target = IconAt(p);
		string destination = target != null && target.IsFolder && !target.IsThisPc ? target.Path : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		foreach (string path in paths)
		{
			string moved = ShellPaths.MoveInto(path, destination);
			if (moved != null && target == null)
			{
				_pendingCells[System.IO.Path.GetFileName(moved)] = CellAt(p);
			}
		}
		(source as FolderWindow)?.Refresh();
		Sync(force: true);
	}

	// ---- window order ---------------------------------------------------------------------

	private void HookWindowProc()
	{
		IntPtr hwnd = GameWindow.Handle;
		if (hwnd == IntPtr.Zero || hwnd == _hookedWindow)
		{
			return;
		}
		_hookProc = GameWndProc;
		_originalProc = Native.SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_hookProc));
		_hookedWindow = hwnd;
	}

	// While the desktop is shown, any z-order change of the game window sends it to the bottom,
	// like a real desktop: clicking it must not cover the folder windows.
	private static IntPtr GameWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
	{
		if (msg == WM_WINDOWPOSCHANGING && Plugin.Instance?.Desktop != null && Plugin.Instance.Desktop.Active && lParam != IntPtr.Zero)
		{
			uint flags = (uint)Marshal.ReadInt32(lParam, 32);
			if ((flags & Native.SWP_NOZORDER) == 0)
			{
				Marshal.WriteIntPtr(lParam, 8, Native.HWND_BOTTOM);
			}
		}
		return Native.CallWindowProcW(_originalProc, hWnd, msg, wParam, lParam);
	}

	private void SendGameToBottom()
	{
		IntPtr game = GameWindow.Handle;
		if (game == IntPtr.Zero)
		{
			return;
		}
		long ex = (long)Native.GetWindowLongPtr(game, Native.GWL_EXSTYLE);
		if ((ex & (Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT)) != 0)
		{
			Native.SetWindowLongPtr(game, Native.GWL_EXSTYLE, new IntPtr(ex & ~(long)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT)));
			Native.SetWindowPos(game, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
		}
		for (IntPtr next = Native.GetWindow(game, Native.GW_HWNDNEXT); next != IntPtr.Zero; next = Native.GetWindow(next, Native.GW_HWNDNEXT))
		{
			if (Native.IsWindowVisible(next) && !Native.IsIconic(next))
			{
				Native.SetWindowPos(game, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
				return;
			}
		}
	}

	// ---- loop -----------------------------------------------------------------------------

	private void Update()
	{
		if (!Active)
		{
			return;
		}
		if (Time.unscaledTime >= _nextRefresh)
		{
			_nextRefresh = Time.unscaledTime + RefreshInterval;
			Sync();
		}
		if (Time.unscaledTime >= _nextZCheck)
		{
			_nextZCheck = Time.unscaledTime + 0.25f;
			HookWindowProc();
			SendGameToBottom();
		}
		HandleInput();
	}

	private void LateUpdate()
	{
		if (Active && EnsureScene())
		{
			if (!_root.activeSelf)
			{
				_root.SetActive(true);
			}
			LayoutScene();
		}
	}
}
