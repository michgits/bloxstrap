﻿using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using Bloxstrap.Models.SettingTasks.Base;

namespace Bloxstrap
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public const string ProjectName = "Bloxstrap";
        public const string ProjectRepository = "michgits/bloxstrap";
        
        public const string RobloxPlayerAppName = "RobloxPlayerBeta";
        public const string RobloxStudioAppName = "RobloxStudioBeta";

        // simple shorthand for extremely frequently used and long string - this goes under HKCU
        public const string UninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProjectName}";

        public static LaunchSettings LaunchSettings { get; private set; } = null!;

        public static BuildMetadataAttribute BuildMetadata = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildMetadataAttribute>()!;

        public static string Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString()[..^2];

        public static readonly MD5 MD5Provider = MD5.Create();

        public static NotifyIconWrapper? NotifyIcon { get; set; }

        public static readonly Logger Logger = new();

        public static readonly Dictionary<string, BaseTask> PendingSettingTasks = new();

        public static readonly JsonManager<Settings> Settings = new();

        public static readonly JsonManager<State> State = new();

        public static readonly FastFlagManager FastFlags = new();

        public static readonly HttpClient HttpClient = new(
            new HttpClientLoggingHandler(
                new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }
            )
        );

#if RELEASE
        private static bool _showingExceptionDialog = false;
#endif

        public static void Terminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::Terminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            NotifyIcon?.Dispose();

            Environment.Exit(exitCodeNum);
        }

        void GlobalExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            Logger.WriteLine("App::GlobalExceptionHandler", "An exception occurred");

            FinalizeExceptionHandling(e.Exception);
        }

        public static void FinalizeExceptionHandling(Exception exception, bool log = true)
        {
            if (log)
                Logger.WriteException("App::FinalizeExceptionHandling", exception);

#if DEBUG
            throw exception;
#else
            if (_showingExceptionDialog)
                return;

            _showingExceptionDialog = true;

            if (!LaunchSettings.IsQuiet)
                Frontend.ShowExceptionDialog(exception);

            Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
#endif
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            const string LOG_IDENT = "App::OnStartup";

            Locale.Initialize();

            base.OnStartup(e);

            Logger.WriteLine(LOG_IDENT, $"Starting {ProjectName} v{Version}");

            if (String.IsNullOrEmpty(BuildMetadata.CommitHash))
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from {BuildMetadata.Machine}");
            else
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from commit {BuildMetadata.CommitHash} ({BuildMetadata.CommitRef})");

            Logger.WriteLine(LOG_IDENT, $"Loaded from {Paths.Process}");

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            HttpClient.Timeout = TimeSpan.FromSeconds(30);
            HttpClient.DefaultRequestHeaders.Add("User-Agent", ProjectRepository);

            LaunchSettings = new LaunchSettings(e.Args);

            // installation check begins here
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKey);
            string? installLocation = null;
            bool fixInstallLocation = false;
            
            if (uninstallKey?.GetValue("InstallLocation") is string value)
            {
                if (Directory.Exists(value))
                {
                    installLocation = value;
                }
                else
                {
                    // check if user profile folder has been renamed
                    // honestly, i'll be expecting bugs from this
                    var match = Regex.Match(value, @"^[a-zA-Z]:\\Users\\([^\\]+)", RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        string newLocation = value.Replace(match.Value, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase);

                        if (Directory.Exists(newLocation))
                        {
                            installLocation = newLocation;
                            fixInstallLocation = true;
                        }
                    }
                }
            }

            // silently change install location if we detect a portable run
            if (installLocation is null && Directory.GetParent(Paths.Process)?.FullName is string processDir)
            {
                var files = Directory.GetFiles(processDir).Select(x => Path.GetFileName(x)).ToArray();

                // check if settings.json and state.json are the only files in the folder
                if (files.Length <= 3 && files.Contains("Settings.json") && files.Contains("State.json"))
                {
                    installLocation = processDir;
                    fixInstallLocation = true;
                }
            }

            if (installLocation is null)
            {
                Logger.Initialize(true);
                LaunchHandler.LaunchInstaller();
            }
            else
            {
                if (fixInstallLocation)
                {
                    var installer = new Installer
                    {
                        InstallLocation = installLocation,
                        IsImplicitInstall = true
                    };

                    if (installer.CheckInstallLocation())
                    {
                        Logger.WriteLine(LOG_IDENT, $"Changing install location to '{installLocation}'");
                        installer.DoInstall();
                    }
                }

                Paths.Initialize(installLocation);

                // ensure executable is in the install directory
                if (Paths.Process != Paths.Application && !File.Exists(Paths.Application))
                    File.Copy(Paths.Process, Paths.Application);

                Logger.Initialize(LaunchSettings.IsUninstall);

                if (!Logger.Initialized && !Logger.NoWriteMode)
                {
                    Logger.WriteLine(LOG_IDENT, "Possible duplicate launch detected, terminating.");
                    Terminate();
                }

                Settings.Load();
                State.Load();
                FastFlags.Load();

                // we can only parse them now as settings need
                // to be loaded first to know what our channel is
                LaunchSettings.ParseRoblox();

                if (!Locale.SupportedLocales.ContainsKey(Settings.Prop.Locale))
                {
                    Settings.Prop.Locale = "nil";
                    Settings.Save();
                }

                Locale.Set(Settings.Prop.Locale);

                if (!LaunchSettings.IsUninstall)
                    Installer.HandleUpgrade();

                LaunchHandler.ProcessLaunchArgs();
            }

            if (Settings.Prop.ConfirmLaunches && Mutex.TryOpenExisting("ROBLOX_singletonMutex", out var _))
            {
                // this currently doesn't work very well since it relies on checking the existence of the singleton mutex
                // which often hangs around for a few seconds after the window closes
                // it would be better to have this rely on the activity tracker when we implement IPC in the planned refactoring

                var result = Frontend.ShowMessageBox(Settings.Prop.MultiInstanceLaunching ? Bloxstrap.Resources.Strings.Bootstrapper_ConfirmLaunch_MultiInstanceEnabled : Bloxstrap.Resources.Strings.Bootstrapper_ConfirmLaunch, MessageBoxImage.Warning, MessageBoxButton.YesNo);

                if (result != MessageBoxResult.Yes)
                {
                    StartupFinished();
                    return;
                }
            }

            // handle roblox singleton mutex for multi-instance launching
            // note we're handling it here in the main thread and NOT in the
            // bootstrapper as handling mutexes in async contexts suuuuuucks

            Mutex? singletonMutex = null;

            if (Settings.Prop.MultiInstanceLaunching && LaunchSettings.RobloxLaunchMode == LaunchMode.Player)
            {
                Logger.WriteLine(LOG_IDENT, "Creating singleton mutex");

                try
                {
                    Mutex.OpenExisting("ROBLOX_singletonMutex");
                    Logger.WriteLine(LOG_IDENT, "Warning - singleton mutex already exists!");
                }
                catch
                {
                    // create the singleton mutex before the game client does
                    singletonMutex = new Mutex(true, "ROBLOX_singletonMutex");
                }
            }

            
            Terminate();
        }
    }
}
