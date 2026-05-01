using System;
using System.Collections.Generic;

namespace ByeWhiteList.Windows.Services
{
    public static class LogManager
    {
        private static readonly List<string> _logs = new List<string>();
        private static readonly int MaxLogs = 500;

        public static event Action<string>? OnLogAdded;

        public static void Add(string message)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var logLine = $"[{time}] {message}";

            lock (_logs)
            {
                _logs.Insert(0, logLine);
                while (_logs.Count > MaxLogs)
                    _logs.RemoveAt(_logs.Count - 1);
            }

            System.Diagnostics.Debug.WriteLine(logLine);
            OnLogAdded?.Invoke(logLine);
        }

        public static string GetLog()
        {
            lock (_logs)
            {
                return string.Join("\n", _logs);
            }
        }

        public static void Clear()
        {
            lock (_logs)
            {
                _logs.Clear();
            }
        }
    }
}