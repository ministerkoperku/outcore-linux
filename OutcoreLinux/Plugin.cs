using System;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Kirurobo;
using OutcoreLinux.Desktop;
using OutcoreLinux.Shell;
using UnityEngine;

namespace OutcoreLinux;

[BepInPlugin(Guid, "Outcore Linux Bridge", Version)]
public sealed class Plugin : BaseUnityPlugin
{
	public const string Guid = "koper.outcore.linux";
	public const string Version = "0.1.2";

	private static int _mainThreadId;

	internal static Plugin Instance { get; private set; }

	internal static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

	internal ConfigEntry<string> Mode;
	internal ConfigEntry<string> WallpaperPath;
	internal ConfigEntry<float> IconScale;
	internal ConfigEntry<bool> ShowThisPcIcon;
	internal ConfigEntry<string> TaskbarMode;

	internal DesktopLayer Desktop { get; private set; }

	internal Runner Runner { get; private set; }

	internal bool IsAndroid { get; private set; }

	internal bool UseTaskbar => TaskbarMode.Value.Equals("on", StringComparison.OrdinalIgnoreCase) || (TaskbarMode.Value.Equals("auto", StringComparison.OrdinalIgnoreCase) && IsAndroid);

	private void Awake()
	{
		Instance = this;
		_mainThreadId = Thread.CurrentThread.ManagedThreadId;
		Log.Source = Logger;
		Mode = Config.Bind("General", "Mode", "auto", "auto = only when running under Wine/Proton, on = always, off = disabled");
		WallpaperPath = Config.Bind("Desktop", "Wallpaper", "", "Image (png/jpg) for the desktop. Linux paths work through Z:, e.g. Z:\\home\\me\\Pictures\\wall.png");
		IconScale = Config.Bind("Desktop", "IconScale", 1f, "Size of desktop icons (1 = Windows default at 1080p)");
		ShowThisPcIcon = Config.Bind("Desktop", "ShowThisPC", true, "Show a 'This PC' icon on the desktop (opens a folder window; the game asks you to open it)");
		TaskbarMode = Config.Bind("Desktop", "Taskbar", "auto", "auto = only on Android (GameNative), on, off");

		bool wine = Native.IsRunningUnderWine();
		IsAndroid = DetectAndroid();
		string mode = Mode.Value.Trim().ToLowerInvariant();
		bool enabled = mode == "on" || (mode != "off" && wine);
		Logger.LogInfo($"Outcore Linux Bridge {Version}: wine={wine} android={IsAndroid} mode={mode} -> {(enabled ? "enabled" : "disabled")}");
		if (!enabled)
		{
			return;
		}
		try
		{
			ShellThread.Start();
			GameObject host = new GameObject("OutcoreLinux")
			{
				hideFlags = HideFlags.HideAndDontSave
			};
			DontDestroyOnLoad(host);
			Runner = host.AddComponent<Runner>();
			Desktop = host.AddComponent<DesktopLayer>();
			new Harmony(Guid).PatchAll(typeof(Bridge));
			Logger.LogInfo("Patches applied");
		}
		catch (Exception ex)
		{
			Logger.LogError("Startup failed: " + ex);
		}
	}

	private static bool DetectAndroid()
	{
		foreach (string variable in new[] { "WINEPREFIX", "HOME", "PREFIX", "ANDROID_ROOT", "ANDROID_DATA" })
		{
			string value = Environment.GetEnvironmentVariable(variable) ?? "";
			if (variable.StartsWith("ANDROID") && value.Length > 0)
			{
				return true;
			}
			if (value.Contains("/data/data/") || value.Contains("/data/user/") || value.Contains("imagefs"))
			{
				return true;
			}
		}
		try
		{
			return File.Exists("Z:\\system\\build.prop") || Directory.Exists("Z:\\data\\data\\com.termux") || Directory.Exists("Z:\\data\\data\\app.gamenative");
		}
		catch
		{
			return false;
		}
	}

	// Raises UniWinApi's file-drop event, as if Windows had sent WM_DROPFILES to the game.
	internal void FireFilesDropped(string[] paths)
	{
		ExtendedWindowController controller = FindObjectOfType<ExtendedWindowController>();
		UniWinApi uniWin = controller != null ? controller.uniWin : null;
		if (uniWin == null)
		{
			Log.Warn("File dropped but the game has no window controller");
			return;
		}
		if (AccessTools.Field(typeof(UniWinApi), "OnFilesDropped")?.GetValue(uniWin) is UniWinApi.FilesDropped handler)
		{
			Log.Info("Dropping onto the game: " + string.Join(", ", paths));
			handler(paths);
		}
		else
		{
			Log.Info("File dropped, but the game is not waiting for one");
		}
	}
}

internal sealed class Runner : MonoBehaviour
{
	private void Update() => MainThread.Drain();
}
