using System;
using System.IO;

namespace WTFnetApp
{
    public static class Logger
    {
        static readonly object lockObj = new object();
        static string logFile = "wtfnet_log.txt";

        public static void Log(string category, string message)
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";

            lock (lockObj)
            {
                File.AppendAllText(logFile, line + Environment.NewLine);
            }

            System.Diagnostics.Debug.WriteLine(line);
        }
    }
}