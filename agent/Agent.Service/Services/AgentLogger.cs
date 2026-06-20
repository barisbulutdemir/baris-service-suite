using System;
using System.IO;

namespace Agent.Service.Services
{
    public static class AgentLogger
    {
        public static event Action<string>? OnLog;

        public static void Log(string component, string message)
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{component}] {message}";
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarisServiceSuite");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                string path = Path.Combine(folder, "debug_log.txt");
                File.AppendAllText(path, logLine + Environment.NewLine);
            }
            catch { }

            try
            {
                OnLog?.Invoke(logLine);
            }
            catch { }
        }
    }
}
