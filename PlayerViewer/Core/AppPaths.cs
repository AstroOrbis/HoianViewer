using System;
using System.Diagnostics;
using System.IO;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Per-user data directory, created on first access: %APPDATA%\PlayerViewer on Windows,
    /// ~/.config/PlayerViewer on Linux/macOS. Holds settings.json, an optional bundled ffmpeg, and
    /// the shader caches when the exe folder is not writable.
    /// </summary>
    public static class AppPaths
    {
        public static string DataDir { get; } = CreateDataDir();

        public static string ShaderCacheDir { get; } = ResolveCacheDir("ShaderCache");

        public static string UberSliceCacheDir { get; } = ResolveCacheDir("UberSliceCache");

        static string CreateDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PlayerViewer"
            );
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string ResolveCacheDir(string name)
        {
            return IsWritable(AppContext.BaseDirectory)
                ? Path.Combine(AppContext.BaseDirectory, name)
                : Path.Combine(DataDir, name);
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.Create(probe).Dispose();
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Opens the data directory in the OS file browser.</summary>
        public static void OpenDataDir()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(
                        new ProcessStartInfo { FileName = DataDir, UseShellExecute = true }
                    );
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", DataDir);
                else
                    Process.Start("xdg-open", DataDir);
            }
            catch { }
        }
    }
}
