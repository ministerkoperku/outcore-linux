using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace OutcoreLinux.Shell;

// Base for every window we own. Subclasses get their messages through one shared WndProc.
internal abstract class ShellWindow
{
	public IntPtr Hwnd { get; internal set; }

	public bool IsAlive => Hwnd != IntPtr.Zero && Native.IsWindow(Hwnd);

	protected internal abstract IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam);

	protected internal virtual void OnDestroyed()
	{
	}

	protected IntPtr Default(uint msg, IntPtr wParam, IntPtr lParam) => Native.DefWindowProcW(Hwnd, msg, wParam, lParam);
}

// One STA thread that owns all shell windows (folder windows, paint) and pumps their messages.
// Unity's main thread talks to it through Invoke/Post; Win32 calls that only read state
// (GetWindowRect, SendMessage to our controls) are fine from any thread.
internal static class ShellThread
{
	public const string FolderClass = "CabinetWClass";
	public const string PaintClass = "MSPaintApp";
	public const string PanelClass = "OutcoreLinuxPanel";
	private const string DispatcherClass = "OutcoreLinuxDispatcher";
	private const uint WM_RUN_QUEUE = Native.WM_APP + 1;

	private static readonly Native.WndProc WndProcDelegate = SharedWndProc;
	private static readonly Dictionary<IntPtr, ShellWindow> Windows = new Dictionary<IntPtr, ShellWindow>();
	private static readonly Queue<Action> Queue = new Queue<Action>();
	private static Thread _thread;
	private static IntPtr _dispatcher;
	private static ShellWindow _creating;
	private static readonly ManualResetEvent Ready = new ManualResetEvent(false);

	public static IntPtr Instance { get; private set; }

	public static IntPtr DefaultFont { get; private set; }

	public static bool IsShellThread => Thread.CurrentThread == _thread;

	public static void Start()
	{
		if (_thread != null)
		{
			return;
		}
		_thread = new Thread(Run)
		{
			Name = "OutcoreLinux shell",
			IsBackground = true
		};
		_thread.SetApartmentState(ApartmentState.STA);
		_thread.Start();
		Ready.WaitOne(5000);
	}

	public static void Post(Action action)
	{
		lock (Queue)
		{
			Queue.Enqueue(action);
		}
		Native.PostMessageW(_dispatcher, WM_RUN_QUEUE, IntPtr.Zero, IntPtr.Zero);
	}

	public static T Invoke<T>(Func<T> func)
	{
		if (IsShellThread)
		{
			return func();
		}
		T result = default;
		Exception error = null;
		using (ManualResetEvent done = new ManualResetEvent(false))
		{
			Post(() =>
			{
				try
				{
					result = func();
				}
				catch (Exception ex)
				{
					error = ex;
				}
				finally
				{
					done.Set();
				}
			});
			if (!done.WaitOne(10000))
			{
				throw new TimeoutException("Shell thread did not respond");
			}
		}
		if (error != null)
		{
			throw error;
		}
		return result;
	}

	public static void Invoke(Action action) => Invoke(() =>
	{
		action();
		return true;
	});

	public static List<T> All<T>() where T : ShellWindow
	{
		List<T> list = new List<T>();
		lock (Windows)
		{
			foreach (ShellWindow window in Windows.Values)
			{
				if (window is T typed && typed.IsAlive)
				{
					list.Add(typed);
				}
			}
		}
		return list;
	}

	public static ShellWindow Find(IntPtr hwnd)
	{
		lock (Windows)
		{
			Windows.TryGetValue(hwnd, out ShellWindow window);
			return window;
		}
	}

	// Must run on the shell thread.
	public static IntPtr Create(ShellWindow owner, uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent)
	{
		_creating = owner;
		IntPtr hwnd = Native.CreateWindowExW(exStyle, className, title, style, x, y, w, h, parent, IntPtr.Zero, Instance, IntPtr.Zero);
		_creating = null;
		if (hwnd != IntPtr.Zero)
		{
			owner.Hwnd = hwnd;
			lock (Windows)
			{
				Windows[hwnd] = owner;
			}
		}
		return hwnd;
	}

	public static IntPtr CreateControl(string className, string text, uint style, int x, int y, int w, int h, IntPtr parent, int id, uint exStyle = 0)
	{
		IntPtr hwnd = Native.CreateWindowExW(exStyle, className, text, style | Native.WS_CHILD | Native.WS_VISIBLE, x, y, w, h, parent, new IntPtr(id), Instance, IntPtr.Zero);
		if (hwnd != IntPtr.Zero && DefaultFont != IntPtr.Zero)
		{
			Native.SendMessageW(hwnd, Native.WM_SETFONT, DefaultFont, new IntPtr(1));
		}
		return hwnd;
	}

	private static void Run()
	{
		try
		{
			Instance = Native.GetModuleHandleW(null);
			Native.INITCOMMONCONTROLSEX icc = new Native.INITCOMMONCONTROLSEX
			{
				dwSize = Marshal.SizeOf(typeof(Native.INITCOMMONCONTROLSEX)),
				dwICC = 0x00000001 | 0x00000004 | 0x00004000 // listview, bar, standard
			};
			Native.InitCommonControlsEx(ref icc);
			DefaultFont = Native.GetStockObject(Native.DEFAULT_GUI_FONT);
			Register(DispatcherClass, IntPtr.Zero);
			Register(FolderClass, Native.GetSysColorBrush(Native.COLOR_BTNFACE));
			Register(PaintClass, Native.GetSysColorBrush(Native.COLOR_BTNFACE));
			Register(PanelClass, Native.GetSysColorBrush(Native.COLOR_BTNFACE));
			_dispatcher = Native.CreateWindowExW(0, DispatcherClass, "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, Instance, IntPtr.Zero);
		}
		catch (Exception ex)
		{
			Log.Error("Shell thread init failed: " + ex);
		}
		finally
		{
			Ready.Set();
		}
		while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
		{
			try
			{
				if (msg.message == Native.WM_KEYDOWN && ShellKeyboard.PreTranslate(ref msg))
				{
					continue;
				}
				Native.TranslateMessage(ref msg);
				Native.DispatchMessageW(ref msg);
			}
			catch (Exception ex)
			{
				Log.Error("Shell message error: " + ex);
			}
		}
	}

	private static void Register(string name, IntPtr background)
	{
		Native.WNDCLASSEX wc = new Native.WNDCLASSEX
		{
			cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
			style = 0x0008 | 0x0002 | 0x0001, // CS_DBLCLKS | CS_HREDRAW | CS_VREDRAW
			lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
			hInstance = Instance,
			hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(Native.IDC_ARROW)),
			hbrBackground = background,
			lpszClassName = name
		};
		if (Native.RegisterClassExW(ref wc) == 0)
		{
			Log.Error("RegisterClassEx failed for " + name + ": " + Marshal.GetLastWin32Error());
		}
	}

	private static void RunQueue()
	{
		while (true)
		{
			Action action;
			lock (Queue)
			{
				if (Queue.Count == 0)
				{
					return;
				}
				action = Queue.Dequeue();
			}
			try
			{
				action();
			}
			catch (Exception ex)
			{
				Log.Error("Shell task failed: " + ex);
			}
		}
	}

	private static IntPtr SharedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
	{
		try
		{
			if (msg == WM_RUN_QUEUE && hWnd == _dispatcher)
			{
				RunQueue();
				return IntPtr.Zero;
			}
			ShellWindow window;
			lock (Windows)
			{
				Windows.TryGetValue(hWnd, out window);
			}
			if (window == null && _creating != null)
			{
				// Messages sent during CreateWindowEx arrive before we know the handle.
				window = _creating;
				window.Hwnd = hWnd;
				lock (Windows)
				{
					Windows[hWnd] = window;
				}
			}
			if (window != null)
			{
				IntPtr result = window.WndProc(msg, wParam, lParam);
				if (msg == Native.WM_DESTROY)
				{
					lock (Windows)
					{
						Windows.Remove(hWnd);
					}
					window.OnDestroyed();
					window.Hwnd = IntPtr.Zero;
				}
				return result;
			}
		}
		catch (Exception ex)
		{
			Log.Error("WndProc error: " + ex);
		}
		return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
	}
}
