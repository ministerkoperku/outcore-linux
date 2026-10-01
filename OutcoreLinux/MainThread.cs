using System;
using System.Collections.Generic;

namespace OutcoreLinux;

// Work that must run on Unity's main thread (game callbacks, textures, events).
internal static class MainThread
{
	private static readonly Queue<Action> Queue = new Queue<Action>();

	public static void Post(Action action)
	{
		lock (Queue)
		{
			Queue.Enqueue(action);
		}
	}

	public static void Drain()
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
				Log.Error("Main thread task failed: " + ex);
			}
		}
	}
}
