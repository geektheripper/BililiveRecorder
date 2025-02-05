using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BililiveRecorder.ToolBox;
using Sentry;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

#nullable enable
namespace BililiveRecorder.WPF
{
    internal static class Program
    {
        private const int CODE__WPF = 0x5F_57_50_46;

        internal static readonly LoggingLevelSwitch levelSwitchGlobal;
        internal static readonly LoggingLevelSwitch levelSwitchConsole;
        internal static readonly Logger logger;
        internal static readonly Update update;
        internal static Task? updateTask;

#if DEBUG
        internal static readonly bool DebugMode = Debugger.IsAttached;
#else
        internal static readonly bool DebugMode = false;
#endif

        static Program()
        {
            AttachConsole(-1);
            levelSwitchGlobal = new LoggingLevelSwitch(Serilog.Events.LogEventLevel.Debug);
            if (DebugMode)
                levelSwitchGlobal.MinimumLevel = Serilog.Events.LogEventLevel.Verbose;
            levelSwitchConsole = new LoggingLevelSwitch(Serilog.Events.LogEventLevel.Error);
            logger = BuildLogger();
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            Log.Logger = logger;
            SentrySdk.ConfigureScope(s =>
            {
                s.SetTag("fullsemver", GitVersionInformation.FullSemVer);
            });
            _ = SentrySdk.ConfigureScopeAsync(async s =>
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "..", "packages", ".betaId"));
                for (var i = 0; i < 10; i++)
                {
                    if (i != 0)
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (!File.Exists(path))
                            continue;
                        var content = File.ReadAllText(path);
                        if (Guid.TryParse(content, out var id))
                        {
                            s.User.Id = id.ToString();
                            return;
                        }
                    }
                    catch (Exception)
                    { }
                }
            });
            update = new Update(logger);
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                logger.Debug("Starting, Version: {Version}, CurrentDirectory: {CurrentDirectory}, CommandLine: {CommandLine}",
                             GitVersionInformation.InformationalVersion,
                             Environment.CurrentDirectory,
                             Environment.CommandLine);
                var code = BuildCommand().Invoke(args);
                logger.Debug("Exit code: {ExitCode}, RunWpf: {RunWpf}", code, code == CODE__WPF);
                return code == CODE__WPF ? Commands.RunWpfReal() : code;
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static RootCommand BuildCommand()
        {
            var run = new Command("run", "Run BililiveRecorder at path")
            {
                new Argument<string?>("path", () => null, "Work directory"),
                new Option<bool>("--ask-path", "Ask path in GUI even when \"don't ask again\" is selected before."),
                new Option<bool>("--hide", "Minimize to tray")
            };
            run.Handler = CommandHandler.Create((string? path, bool askPath, bool hide) => Commands.RunWpfHandler(path: path, squirrelFirstrun: false, askPath: askPath, hide: hide));

            var root = new RootCommand("")
            {
                run,
                new Option<bool>("--squirrel-firstrun")
                {
                    IsHidden = true
                },
                new ToolCommand(),
            };
            root.Handler = CommandHandler.Create((bool squirrelFirstrun) => Commands.RunWpfHandler(path: null, squirrelFirstrun: squirrelFirstrun, askPath: false, hide: false));
            return root;
        }

        private static class Commands
        {
            internal static int RunWpfHandler(string? path, bool squirrelFirstrun, bool askPath, bool hide)
            {
                Pages.RootPage.CommandArgumentRecorderPath = path;
                Pages.RootPage.CommandArgumentFirstRun = squirrelFirstrun;
                Pages.RootPage.CommandArgumentAskPath = askPath;
                Pages.RootPage.CommandArgumentHide = hide;
                return CODE__WPF;
            }

            internal static int RunWpfReal()
            {
                var cancel = new CancellationTokenSource();
                var token = cancel.Token;
                try
                {
                    SleepBlocker.Start();

                    var app = new App();
                    app.InitializeComponent();
                    app.DispatcherUnhandledException += App_DispatcherUnhandledException;

                    updateTask = Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await update.UpdateAsync().ConfigureAwait(false);
                            await Task.Delay(TimeSpan.FromDays(1), token).ConfigureAwait(false);
                        }
                    });

                    return app.Run();
                }
                finally
                {
                    cancel.Cancel();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                    update.WaitForUpdatesOnShutdownAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
                }
            }
        }

        private static class SleepBlocker
        {
            internal static void Start()
            {
                var t = new Thread(EntryPoint)
                {
                    Name = "SystemSleepBlocker",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };
                t.Start();
            }

            [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

            [Flags]
            private enum EXECUTION_STATE : uint
            {
                ES_AWAYMODE_REQUIRED = 0x00000040,
                ES_CONTINUOUS = 0x80000000,
                ES_DISPLAY_REQUIRED = 0x00000002,
                ES_SYSTEM_REQUIRED = 0x00000001
            }

            private static void EntryPoint()
            {
                try
                {
                    while (true)
                    {
                        try
                        {
                            _ = SetThreadExecutionState(EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_CONTINUOUS);
                        }
                        catch (Exception) { }
                        Thread.Sleep(millisecondsTimeout: 30 * 1000);
                    }
                }
                catch (Exception) { }
            }
        }

        private static Logger BuildLogger() => new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitchGlobal)
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .Enrich.WithThreadName()
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails()
            .Destructure.ByTransforming<Flv.Xml.XmlFlvFile.XmlFlvFileMeta>(x => new
            {
                x.Version,
                x.ExportTime,
                x.FileSize,
                x.FileCreationTime,
                x.FileModificationTime,
            })
            .Destructure.AsScalar<IPAddress>()
            .WriteTo.Console(levelSwitch: levelSwitchConsole)
#if DEBUG
            .WriteTo.Debug()
            .WriteTo.Sink<WpfLogEventSink>(Serilog.Events.LogEventLevel.Debug)
#else
            .WriteTo.Sink<WpfLogEventSink>(Serilog.Events.LogEventLevel.Information)
#endif
            .WriteTo.File(new CompactJsonFormatter(), "./logs/bilirec.txt", shared: true, rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true)
            .WriteTo.Sentry(o =>
            {
                o.Dsn = "https://38036b2031474b8ba0a728ac2a961cfa@o210546.ingest.sentry.io/5556540";
                o.SendDefaultPii = true;
                o.IsGlobalModeEnabled = true;
                o.DisableAppDomainUnhandledExceptionCapture();
                o.DisableTaskUnobservedTaskExceptionCapture();
                o.AddExceptionFilterForType<System.Net.Http.HttpRequestException>();

                o.TextFormatter = new MessageTemplateTextFormatter("[{RoomId}] {Message}{NewLine}{Exception}{@ExceptionDetail:j}");

                o.MinimumBreadcrumbLevel = Serilog.Events.LogEventLevel.Debug;
                o.MinimumEventLevel = Serilog.Events.LogEventLevel.Error;

#if DEBUG
                o.Environment = "debug-build";
#else
                o.Environment = "release-build";
#endif
            })
            .CreateLogger();

        [DllImport("kernel32")]
        private static extern bool AttachConsole(int pid);

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                logger.Fatal(ex, "Unhandled exception from AppDomain.UnhandledException");
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e) =>
            logger.Error(e.Exception, "Unobserved exception from TaskScheduler.UnobservedTaskException");

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            logger.Fatal(e.Exception, "Unhandled exception from Application.DispatcherUnhandledException");
    }
}
