using BepInEx.Logging;

namespace OutcoreLinux;

internal static class Log
{
	public static ManualLogSource Source;

	public static void Info(string message) => Source?.LogInfo(message);

	public static void Warn(string message) => Source?.LogWarning(message);

	public static void Error(string message) => Source?.LogError(message);
}
