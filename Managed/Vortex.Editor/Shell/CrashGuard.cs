using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Services;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Keeps the editor alive when a handler throws. Avalonia.Native re-throws an exception from a native callback
    /// (pointer, key and window events on macOS) out of the main run loop, so one bug in one button used to close
    /// the whole editor — unsaved scene included. The guard reports the failure (Console, a toast, and
    /// <c>VortexEngine/logs/editor-errors.log</c> in the app-data folder) and resumes the run loop; only a real
    /// shutdown ends it. A burst of failures (a handler that throws every frame) still ends the process, so a
    /// broken state can't spin forever.
    /// </summary>
    internal static class CrashGuard
    {
        /// <summary>Failures caught this session (the smoke check reads it).</summary>
        public static int Count;
        public static string LastMessage;

        private const int BurstLimit = 40;
        private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);
        private static readonly Queue<DateTime> _recent = new Queue<DateTime>();
        private static DateTime _lastToast = DateTime.MinValue;

        /// <summary>Hooks for failures that don't pass through the run loop: never-awaited tasks, plus a last-chance
        /// log for fatal ones.</summary>
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            // (The dispatcher hook is added in Run, after the platform set-up: touching Dispatcher.UIThread before
            // the native platform registers its dispatcher would bind the UI thread to the wrong implementation.)
            TaskScheduler.UnobservedTaskException += (s, e) => { Report(e.Exception, "background task", toast: false); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteLog(e.ExceptionObject as Exception, "fatal");
        }

        /// <summary>Run the desktop lifetime. Dispatcher callbacks (timers, posted work, async continuations) that
        /// throw are reported and marked handled; when an exception escapes the run loop while the editor is still
        /// meant to run, it is reported and the loop pumps on. Returns the lifetime's exit code.</summary>
        public static int Run(AppBuilder builder, string[] args, ShutdownMode shutdownMode = ShutdownMode.OnLastWindowClose)
        {
            var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args, ShutdownMode = shutdownMode };
            builder.SetupWithLifetime(lifetime);
            Dispatcher.UIThread.UnhandledException += (s, e) => { Report(e.Exception, "UI callback"); e.Handled = true; };
            var exit = new CancellationTokenSource();
            int code = 0;
            lifetime.Exit += (s, e) => { code = e.ApplicationExitCode; exit.Cancel(); };

            try { return lifetime.Start(args); }
            catch (Exception ex) when (Survive(ex, exit)) { }

            // The lifetime's own loop is gone; pump the dispatcher until the lifetime shuts down (Exit).
            while (!exit.IsCancellationRequested)
            {
                try { Dispatcher.UIThread.MainLoop(exit.Token); }
                catch (Exception ex) when (Survive(ex, exit)) { }
            }
            return code;
        }

        // Filter: report, then decide whether the editor keeps running (false rethrows = the old crash).
        private static bool Survive(Exception ex, CancellationTokenSource exit)
        {
            if (exit.IsCancellationRequested) { WriteLog(ex, "during shutdown"); return true; }   // quitting anyway: no crash dialog
            var now = DateTime.UtcNow;
            _recent.Enqueue(now);
            while (_recent.Count > 0 && now - _recent.Peek() > BurstWindow) _recent.Dequeue();
            if (_recent.Count > BurstLimit) { WriteLog(ex, "burst limit reached — giving up"); return false; }
            Report(ex, "input handler");
            return true;
        }

        /// <summary>Log a caught failure (Console + error log) and tell the user once every few seconds.</summary>
        public static void Report(Exception ex, string where, bool toast = true)
        {
            if (ex == null) return;
            Interlocked.Increment(ref Count);
            var root = ex is AggregateException ag && ag.InnerExceptions.Count == 1 ? ag.InnerException : ex;
            LastMessage = root.GetType().Name + ": " + root.Message;
            WriteLog(ex, where);
            try { ConsoleService.Instance.LogError("Editor error in " + where + " (the editor kept running): " + LastMessage + Environment.NewLine + FirstFrames(root)); } catch { }
            if (!toast || (DateTime.UtcNow - _lastToast).TotalSeconds < 3) return;
            _lastToast = DateTime.UtcNow;
            try { Dispatcher.UIThread.Post(() => EditorCommands.Toast("Something went wrong (" + root.GetType().Name + ") — the editor kept running. Details in the Console.")); } catch { }
        }

        private static string FirstFrames(Exception ex)
        {
            var lines = (ex.StackTrace ?? "").Split('\n');
            return string.Join("\n", lines, 0, Math.Min(6, lines.Length)).TrimEnd();
        }

        private static void WriteLog(Exception ex, string where)
        {
            try
            {
                string dir = Path.Combine(EditorPaths.VortexAppData, "logs");
                Directory.CreateDirectory(dir);
                string tail = where == "fatal" || where.StartsWith("burst") ? ConsoleTail(40) : "";
                File.AppendAllText(Path.Combine(dir, "editor-errors.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " + ex + Environment.NewLine + tail + Environment.NewLine);
            }
            catch { }
            try { Console.Error.WriteLine("[crash-guard] " + where + ": " + ex?.GetType().Name + ": " + ex?.Message); } catch { }
        }

        /// <summary>The last console lines — what the editor was doing when it went (the fatal log carries them).</summary>
        private static string ConsoleTail(int lines)
        {
            try
            {
                var entries = ConsoleService.Instance.Entries;
                var sb = new System.Text.StringBuilder();
                sb.Append("  --- the console before it (last ").Append(lines).Append(" lines) ---").Append(Environment.NewLine);
                int start = Math.Max(0, entries.Count - lines);
                for (int i = start; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;
                    sb.Append("  ").Append(e.Time).Append(' ').Append(e.LevelTag).Append("  ").Append(e.Message).Append(Environment.NewLine);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        [ModuleInitializer]
        internal static void RegisterSmoke()
        {
            // A handler that throws on a REAL (AppKit) click — the native-callback path that used to end the process.
            SmokeRegistry.Add("crash guard: a throwing click handler leaves the editor running", async () =>
            {
                if (!OperatingSystem.IsMacOS()) return true;
                var target = new Border { Background = new SolidColorBrush(Color.FromRgb(0x5a, 0x20, 0x24)) };
                target.PointerPressed += (s, e) => throw new InvalidOperationException("smoke: deliberate handler failure");
                var w = new Window { Width = 320, Height = 200, Title = "Crash guard check", Content = target };
                int before = Count;
                try
                {
                    w.Show();
                    await SmokeRegistry.Settle(400);
                    var view = (w.TryGetPlatformHandle() as Avalonia.Platform.IMacOSTopLevelPlatformHandle)?.NSView ?? IntPtr.Zero;
                    if (view == IntPtr.Zero) return false;
                    Viewport.MacViewProbe.Click(view, 160, 100);
                    await SmokeRegistry.Settle(600);
                    ConsoleService.Instance.Log("crash guard: caught " + (Count - before) + " (" + LastMessage + ")");
                    return Count > before && w.IsVisible;
                }
                finally { w.Close(); }
            });
        }
    }
}
