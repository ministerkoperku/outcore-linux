using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using Kirurobo;
using OutcoreLinux.Shell;
using ProcessCommunication;
using SharedOutcoreLibraries;
using UnityEngine;

namespace OutcoreLinux;

// Replaces Outcore's WindowsUtility.exe. The game asks that helper eight questions about the
// Windows shell (screens, desktop icons, explorer windows...) over shared memory; under Wine the
// helper hangs, so the desktop stages wait forever on a black screen. We answer in-process.
[HarmonyPatch]
internal static class Bridge
{
	private static void Later(Action action) => MainThread.Post(action);

	// ---- Steam -----------------------------------------------------------------------------

	// If SteamClient.Init throws (GameNative's Steam emulation, no Steam running...), the game
	// logs "Failed to initialize the steam store service" and never calls its callback, which
	// holds the entire startup: grey/black screen forever. Continue offline instead.
	[HarmonyPostfix]
	[HarmonyPatch(typeof(SteamGameStoreService), nameof(SteamGameStoreService.Initialize))]
	private static void SteamOffline(SteamGameStoreService __instance, Action onInitializationComplete)
	{
		if (!__instance.isInitialized && !Application.isEditor)
		{
			Log.Warn("Steam did not initialize; continuing without achievements/DLC");
			onInitializationComplete?.Invoke();
		}
	}

	// ---- helper process -------------------------------------------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(WindowsUtilityCommunicationManager), nameof(WindowsUtilityCommunicationManager.Initialize))]
	private static bool InitializeMessenger(WindowsUtilityCommunicationManager __instance, Action onComplete)
	{
		if (__instance.windowsUtilityComManager == null)
		{
			// Keep a channel object around; StoryModeManager calls StartRunning() on it.
			ProcessCommunicationManager.Settings settings = new ProcessCommunicationManager.Settings
			{
				inputMappedFileName = "desktopUtilityToOutcore_linux",
				outputMappedFileName = "outcoreToDesktopUtility_linux",
				reopenTargetProcessIfClosed = false,
				dontCreateWindowsUtilityProcess = true
			};
			ProcessCommunicationManager manager = new ProcessCommunicationManager();
			manager.Initialize("WindowsUtility", settings, null, new NullLogger(), debugMode: false);
			__instance.windowsUtilityComManager = manager;
		}
		__instance.dontCreateWindowsUtilityProcess = true;
		AccessTools.PropertySetter(typeof(WindowsUtilityCommunicationManager), nameof(WindowsUtilityCommunicationManager.isInitialized)).Invoke(__instance, new object[] { true });
		__instance.onInitialized?.Invoke();
		onComplete?.Invoke();
		Log.Info("WindowsUtility replaced by the in-process bridge");
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(WindowsUtilityCommunicationManager), "OpenWindowsUtilityProcess")]
	private static bool NoHelperProcess() => false;

	// ---- message 2: screens ---------------------------------------------------------------

	public static GetScreenSizeMessage.ResponseData BuildScreenInfo()
	{
		GetScreenSizeMessage.ResponseData data = new GetScreenSizeMessage.ResponseData { allScreens = new List<GetScreenSizeMessage.ScreenInfo>() };
		int taskbar = Plugin.Instance.Desktop != null ? Plugin.Instance.Desktop.TaskbarHeight : 0;
		foreach (ScreenData screen in Screens.All())
		{
			GetScreenSizeMessage.ScreenInfo info = new GetScreenSizeMessage.ScreenInfo
			{
				deviceName = screen.Device,
				left = screen.Bounds.left,
				up = screen.Bounds.top,
				realWidth = screen.Bounds.Width,
				realHeight = screen.Bounds.Height,
				right = screen.Bounds.right,
				bottom = screen.Bounds.bottom,
				virtualWidth = screen.Bounds.Width,
				virtualHeight = screen.Bounds.Height,
				isPrimary = screen.Primary
			};
			Native.RECT work = screen.Work;
			if (screen.Primary)
			{
				work.bottom -= taskbar;
			}
			int l = Math.Abs(screen.Bounds.left - work.left);
			int t = Math.Abs(screen.Bounds.top - work.top);
			int r = Math.Abs(screen.Bounds.right - work.right);
			int b = Math.Abs(screen.Bounds.bottom - work.bottom);
			if (l > 0)
			{
				info.taskbarOrientation = GetScreenSizeMessage.TaskbarOrientation.left;
				info.taskbarSize = l;
			}
			else if (t > 0)
			{
				info.taskbarOrientation = GetScreenSizeMessage.TaskbarOrientation.top;
				info.taskbarSize = t;
			}
			else if (r > 0)
			{
				info.taskbarOrientation = GetScreenSizeMessage.TaskbarOrientation.right;
				info.taskbarSize = r;
			}
			else if (b > 0)
			{
				info.taskbarOrientation = GetScreenSizeMessage.TaskbarOrientation.bottom;
				info.taskbarSize = b;
			}
			else
			{
				info.taskbarOrientation = GetScreenSizeMessage.TaskbarOrientation.noTaskbar;
			}
			data.allScreens.Add(info);
			if (screen.Primary)
			{
				data.mainScreen = info;
			}
		}
		return data;
	}

	private static void StoreScreenInfo(ExtendedWindowController controller, GetScreenSizeMessage.ResponseData data)
	{
		AccessTools.PropertySetter(typeof(ExtendedWindowController), nameof(ExtendedWindowController.screenInfo)).Invoke(controller, new object[] { data });
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.GetScreenInformation))]
	private static bool GetScreenInformation(ExtendedWindowController __instance, Action<GetScreenSizeMessage.ResponseData> onComplete)
	{
		GetScreenSizeMessage.ResponseData data = BuildScreenInfo();
		StoreScreenInfo(__instance, data);
		Later(() => onComplete?.Invoke(data));
		return false;
	}

	// ---- message 5: desktop icons ----------------------------------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.GetDesktopIcons))]
	private static bool GetDesktopIcons(Action<ExtendedWindowController.DesktopItem[]> onIconsInfoRetrieved)
	{
		List<KeyValuePair<string, Native.RECT>> rects = Plugin.Instance.Desktop.GetIconRects();
		ExtendedWindowController.DesktopItem[] items = new ExtendedWindowController.DesktopItem[rects.Count];
		for (int i = 0; i < rects.Count; i++)
		{
			Native.RECT r = rects[i].Value;
			items[i] = new ExtendedWindowController.DesktopItem
			{
				name = rects[i].Key,
				rect = new ExtendedWindowController.DesktopIconRect { left = r.left, top = r.top, right = r.right, bottom = r.bottom }
			};
		}
		Later(() => onIconsInfoRetrieved?.Invoke(items));
		return false;
	}

	// ---- message 6: the "hot and cold" folder hunt -----------------------------------------

	private static FolderWindow TopFolderWindow()
	{
		List<FolderWindow> folders = ShellThread.All<FolderWindow>();
		FolderWindow top = null;
		Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
		{
			FolderWindow match = folders.Find(f => f.Hwnd == hwnd);
			if (match != null && Native.IsWindowVisible(hwnd))
			{
				top = match;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		return top;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.GetHotColdGameData))]
	private static bool GetHotColdGameData(string[] correctFoldersHierarchy, Action<HotAndColdGameData> onComplete)
	{
		HotAndColdGameData data = new HotAndColdGameData { currentFolderIcons = new FolderItem[0], folderStatus = HotAndColdGameData.FolderStatus.unknown };
		try
		{
			FolderWindow window = TopFolderWindow();
			if (window != null && !Native.IsIconic(window.Hwnd) && correctFoldersHierarchy != null && correctFoldersHierarchy.Length > 0)
			{
				string location = window.Location;
				bool found = false;
				for (int i = correctFoldersHierarchy.Length - 1; i >= 0; i--)
				{
					if (!string.IsNullOrEmpty(correctFoldersHierarchy[i]) && location == ShellPaths.Normalize(correctFoldersHierarchy[i]))
					{
						data.folderStatus = HotAndColdGameData.FolderStatus.correct;
						found = true;
						break;
					}
				}
				if (!found && location.Contains(ShellPaths.Normalize(correctFoldersHierarchy[0])))
				{
					data.folderStatus = HotAndColdGameData.FolderStatus.incorrent;
					found = true;
				}
				if (found)
				{
					data.currentFolderURL = location;
					data.hwnd = (int)window.Hwnd;
					List<KeyValuePair<string, Native.RECT>> rects = window.GetItemRects();
					data.currentFolderIcons = new FolderItem[rects.Count];
					for (int i = 0; i < rects.Count; i++)
					{
						Native.RECT r = rects[i].Value;
						data.currentFolderIcons[i] = new FolderItem
						{
							name = rects[i].Key,
							rect = new SharedOutcoreLibraries.DesktopIconRect { left = r.left, top = r.top, right = r.right, bottom = r.bottom }
						};
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error("GetHotColdGameData: " + ex);
		}
		Later(() => onComplete?.Invoke(data));
		return false;
	}

	// ---- message 7: hwnd of a folder window --------------------------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.GetExplorerWindowHWMD))]
	private static bool GetExplorerWindowHWMD(string address, Action<int> onComplete)
	{
		FolderWindow window = ShellThread.All<FolderWindow>().Find(f => ShellPaths.Same(f.CurrentPath, address));
		int hwnd = window != null ? (int)window.Hwnd : 0;
		Later(() => onComplete?.Invoke(hwnd));
		return false;
	}

	// ---- message 3: wait until the player brings a folder to the front ------------------------

	private static bool Matches(FolderWindow window, WaitForExplorerWindowToBeInFrontMessage.WindowLocation request)
	{
		if (request == null)
		{
			return false;
		}
		if (ShellPaths.Same(window.CurrentPath, request.locationURL))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(request.locationName))
		{
			if (string.Equals(window.LocationName, request.locationName, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			// Explorer calls This PC "Quick access" when it opens there.
			if (window.CurrentPath == ShellPaths.ThisPc && request.locationName.Equals("Quick access", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static IntPtr FrontWindowIgnoringGame()
	{
		IntPtr game = GameWindow.Handle;
		IntPtr front = Native.GetForegroundWindow();
		if (front != IntPtr.Zero && front != game)
		{
			return Native.GetAncestor(front, Native.GA_ROOT);
		}
		IntPtr result = IntPtr.Zero;
		Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
		{
			if (hwnd == game || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
			{
				return true;
			}
			if (((long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0)
			{
				return true;
			}
			result = hwnd;
			return false;
		}, IntPtr.Zero);
		return result;
	}

	private static IEnumerator WaitForFolderInFront(WaitForExplorerWindowToBeInFrontMessage.WindowLocation[] requests, Action onComplete)
	{
		WaitForSecondsRealtime wait = new WaitForSecondsRealtime(0.15f);
		while (true)
		{
			if (ShellThread.Find(FrontWindowIgnoringGame()) is FolderWindow window && !Native.IsIconic(window.Hwnd))
			{
				foreach (WaitForExplorerWindowToBeInFrontMessage.WindowLocation request in requests)
				{
					if (Matches(window, request))
					{
						onComplete?.Invoke();
						yield break;
					}
				}
			}
			yield return wait;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.WaitForExplorerWindowToBeActive))]
	private static bool WaitForExplorerWindowToBeActive(WaitForExplorerWindowToBeInFrontMessage.WindowLocation[] addressRequests, Action onComplete)
	{
		Plugin.Instance.Runner.StartCoroutine(WaitForFolderInFront(addressRequests ?? new WaitForExplorerWindowToBeInFrontMessage.WindowLocation[0], onComplete));
		return false;
	}

	// ---- message 8: desktop screenshot ("The world above.jpg") --------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.CreateDesktopScreenshot))]
	private static bool CreateDesktopScreenshot(string address, Action<bool> onComplete)
	{
		bool ok = false;
		try
		{
			Texture2D shot = ScreenGrab.CapturePrimary();
			byte[] jpg = shot.EncodeToJPG(90);
			UnityEngine.Object.Destroy(shot);
			if (!File.Exists(address))
			{
				File.WriteAllBytes(address, jpg);
			}
			File.Copy(address, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "The world above.jpg"), overwrite: true);
			ok = true;
		}
		catch (Exception ex)
		{
			Log.Error("Screenshot failed: " + ex);
		}
		Later(() => onComplete?.Invoke(ok));
		return false;
	}

	// ---- message 4: shortcuts ---------------------------------------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.CreateShortcut))]
	private static bool CreateShortcut(string shortcutLocation, string fileNameWithoutExtension, string shortcutTargetLocation, Action onComplete)
	{
		try
		{
			ShortcutWriter.Write(Path.Combine(shortcutLocation, fileNameWithoutExtension + ".lnk"), shortcutTargetLocation);
		}
		catch (Exception ex)
		{
			Log.Error("CreateShortcut failed: " + ex);
		}
		Later(() => onComplete?.Invoke());
		return false;
	}

	// ---- message 1: open a folder window -------------------------------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.OpenExplorerFolder))]
	private static bool OpenExplorerFolder(string address, UnityEngine.Rect portion)
	{
		GetScreenSizeMessage.ScreenInfo main = BuildScreenInfo().mainScreen;
		int x = (int)(main.realWidth * portion.x);
		int y = (int)(main.realHeight * (1f - portion.y - portion.height));
		int w = (int)(main.realWidth * portion.width);
		int h = (int)(main.realHeight * portion.height);
		FolderWindow.Open(address, main.left + x, main.up + y, w, h, bringToFront: true);
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(DesktopControls), nameof(DesktopControls.CreateMyComputer))]
	private static bool CreateMyComputer(DesktopControls __instance)
	{
		GetScreenSizeMessage.ScreenInfo main = BuildScreenInfo().mainScreen;
		Vector2 position = new Vector2(0.25f, 0.1f);
		Vector2 size = new Vector2(0.5f, 0.4f);
		FolderWindow.Open(ShellPaths.ThisPc, main.left + (int)(main.realWidth * position.x), main.up + (int)(main.realHeight * (1f - position.y - size.y)), (int)(main.realWidth * size.x), (int)(main.realHeight * size.y), bringToFront: true);
		Later(() =>
		{
			Component player = Traverse.Create(__instance).Field("player").GetValue<Component>();
			if (player != null && Camera.main != null)
			{
				Vector2 top = new Vector2(position.x + size.x * 0.5f, position.y + size.y);
				player.transform.position = __instance.DeskptopScreenToWorldSpace(new Vector3(top.x, top.y, Camera.main.nearClipPlane));
			}
		});
		return false;
	}

	// ---- show desktop ------------------------------------------------------------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ExtendedWindowController), nameof(ExtendedWindowController.MinimizeAllToDesktop))]
	private static bool MinimizeAllToDesktop()
	{
		IntPtr game = GameWindow.Handle;
		List<IntPtr> targets = new List<IntPtr>();
		Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
		{
			if (hwnd != game && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd)
				&& ((long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) == 0
				&& Native.GetWindowTextLengthW(hwnd) > 0 && !(ShellThread.Find(hwnd) is Desktop.Taskbar))
			{
				targets.Add(hwnd);
			}
			return true;
		}, IntPtr.Zero);
		foreach (IntPtr hwnd in targets)
		{
			Native.PostMessageW(hwnd, 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF020 /* SC_MINIMIZE */), IntPtr.Zero);
		}
		return false;
	}

	// ---- window mode: no DWM, no layered/click-through, desktop at the bottom ------------------

	[HarmonyPrefix]
	[HarmonyPatch(typeof(UniWinApi), "EnableTransparentByDWM")]
	private static bool NoDwmOn() => false;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(UniWinApi), "DisableTransparentByDWM")]
	private static bool NoDwmOff() => false;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(UniWinApi), nameof(UniWinApi.EnableClickThrough))]
	private static bool NoClickThrough() => false;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(UniWinApi), nameof(UniWinApi.EnableTopmost))]
	private static bool Topmost() => Plugin.Instance.Desktop == null || !Plugin.Instance.Desktop.Active;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(WindowController), nameof(WindowController.SetTransparent))]
	private static void SetTransparent(bool transparent) => Plugin.Instance.Desktop?.SetActive(transparent);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(WindowController), "MyPostRender")]
	private static bool OpaquePixelTest(WindowController __instance)
	{
		if (Plugin.Instance.Desktop == null || !Plugin.Instance.Desktop.Active)
		{
			return true;
		}
		Traverse.Create(__instance).Field("onOpaquePixel").SetValue(Plugin.Instance.Desktop.GameHitAtCursor());
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(TransparentWindow), "Start")]
	private static bool TransparentWindowStart() => false;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(TransparentWindow), nameof(TransparentWindow.SetWindowAsClickThrough))]
	private static bool TransparentWindowClickThrough(bool canClick)
	{
		Cursor.visible = !canClick;
		return false;
	}

	// ---- programs the game launches ------------------------------------------------------------

	private static bool IsPaint(string fileName)
	{
		string name = Path.GetFileName(fileName ?? "").ToLowerInvariant();
		return name == "mspaint.exe" || name == "paintdotnet.exe" || name == "mspaint";
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Process), nameof(Process.Start), typeof(string), typeof(string))]
	private static bool StartWithArguments(string fileName, string arguments, ref Process __result)
	{
		if (IsPaint(fileName))
		{
			PaintWindow.Open((arguments ?? "").Trim().Trim('"'));
			__result = null;
			return false;
		}
		string exe = Path.GetFileName(fileName ?? "").ToLowerInvariant();
		if (exe == "explorer" || exe == "explorer.exe")
		{
			FolderWindow.OpenDefault((arguments ?? "").Trim().Trim('"'));
			__result = null;
			return false;
		}
		return true;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Process), nameof(Process.Start), typeof(string))]
	private static bool StartPath(string fileName, ref Process __result)
	{
		if (!string.IsNullOrEmpty(fileName) && Directory.Exists(fileName))
		{
			FolderWindow.OpenDefault(fileName);
			__result = null;
			return false;
		}
		if (IsPaint(fileName))
		{
			PaintWindow.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "untitled.png"));
			__result = null;
			return false;
		}
		return true;
	}
}
