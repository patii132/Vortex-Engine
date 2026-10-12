using System;
using Avalonia;

namespace VortexEditor
{
    internal static class Program
    {
        /// <summary>Command line for this run (project to open, smoke-test hooks).</summary>
        public static LaunchOptions Options { get; private set; } = new LaunchOptions();

        // Avalonia + the native renderer both live on the main thread.
        [STAThread]
        public static int Main(string[] args)
        {
            // first of all: an unhandled exception anywhere below (and in the module initialisers that run before Main's
            // first line resolves them) is logged before the process goes
            Shell.CrashGuard.Install();
            Options = LaunchOptions.Parse(args);
            // docs: the generated Claude tool reference (no UI, no engine)
            foreach (var a in args ?? Array.Empty<string>())
                if (a.StartsWith("--mcp-tools-md=", StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.WriteAllText(a.Substring(15).Trim('"'), Claude.ToolReference.Markdown());
                    return 0;
                }
            if (Options.SmokeSeconds > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VORTEX_APPDATA_DIR")))
                Environment.SetEnvironmentVariable("VORTEX_APPDATA_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex-smoke-appdata"));
            // the console speaks UTF-8 everywhere (Windows' default code page turned "—" in the log into "?")
            if (OperatingSystem.IsWindows()) { try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { } }
            // Windows: the installer's AppMutex — a silent update closes a running editor before replacing its files (the
            // WPF editor holds the same name; the mutex only has to exist while an editor runs)
            if (OperatingSystem.IsWindows()) { try { _installerMutex = new System.Threading.Mutex(false, "VortexEngineSingleInstance"); } catch { } }
            if (Options.SmokeSeconds > 0) AppDomain.CurrentDomain.ProcessExit += (s, e) => Stage("process exit");
            // An exception in a pointer/key/window handler must not take the editor (and unsaved work) down (hooks installed above).
            int code = Shell.CrashGuard.Run(BuildAvaloniaApp(), args);
            Stage("the UI ended (exit code " + code + ")");
            if (OperatingSystem.IsWindows()) EndProcessNow(code);
            return code;
        }

        /// <summary>
        /// Windows: end the process here. The editor has saved its state and shut the engine down; what follows Main
        /// is the native teardown, which Windows runs after it has already ended every other thread — there it hung
        /// (the CI smoke reached "process exit" and then sat for minutes). A lingering editor would also keep the
        /// installer's AppMutex, so a silent update would think it is still running. TerminateProcess skips that teardown.
        /// </summary>
        internal static void EndProcessNow(int code)
        {
            Stage("ending the process");
            try { Console.Out.Flush(); Console.Error.Flush(); } catch { }
            TerminateProcess(GetCurrentProcess(), (uint)code);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        private static System.Threading.Mutex _installerMutex;

        /// <summary>The last shutdown step reached — smoke runs print every step, and the exit watchdog reports the last
        /// one when the editor does not end.</summary>
        public static volatile string ShutdownStage = "running";

        public static void Stage(string step)
        {
            ShutdownStage = step;
            if (Options.SmokeSeconds > 0) { Console.WriteLine("[shutdown] " + step); Console.Out.Flush(); }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }

    public sealed class LaunchOptions
    {
        public string ProjectPath;
        public string SceneName;
        /// <summary>Smoke test: capture the viewport + window after this many seconds, then exit.</summary>
        public double SmokeSeconds;
        public string CaptureDir;

        public static LaunchOptions Parse(string[] args)
        {
            var o = new LaunchOptions();
            foreach (string raw in args ?? Array.Empty<string>())
            {
                string a = raw.Trim();
                if (a.StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) o.ProjectPath = a.Substring(10).Trim('"');
                else if (a.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase)) o.SceneName = a.Substring(8).Trim('"');
                else if (a.StartsWith("--smoke=", StringComparison.OrdinalIgnoreCase) && double.TryParse(a.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s)) o.SmokeSeconds = s;
                else if (a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase)) o.CaptureDir = a.Substring(10).Trim('"');
                else if (!a.StartsWith("-") && a.Length > 0)
                {
                    // A project handed over by the OS (double-clicked project.vortex, "Open with", `open -a`).
                    string p = a.Trim('"');
                    if (System.IO.File.Exists(p) && p.EndsWith(".vortex", StringComparison.OrdinalIgnoreCase)) o.ProjectPath = System.IO.Path.GetDirectoryName(p);
                    else if (System.IO.Directory.Exists(p)) o.ProjectPath = p;
                }
            }
            return o;
        }
    }
}
