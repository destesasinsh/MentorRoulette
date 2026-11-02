namespace MentorRoulette;

using System;
using System.Collections.Generic;

/// <summary>给界面用的最近日志（最多 100 条）。</summary>
internal static class DiagLog
{
    private static readonly Queue<string> Lines = new();
    private static readonly object Gate = new();

    internal static void Add(string message)
    {
        lock (Gate)
        {
            Lines.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");

            while (Lines.Count > 100)
                Lines.Dequeue();
        }
    }

    internal static void Clear()
    {
        lock (Gate)
            Lines.Clear();
    }

    internal static int Count
    {
        get
        {
            lock (Gate)
                return Lines.Count;
        }
    }

    internal static List<string> Snapshot()
    {
        lock (Gate)
            return [.. Lines];
    }
}
