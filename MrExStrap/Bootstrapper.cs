// To debug the automatic updater:
// - Uncomment the definition below
// - Publish the executable
// - Launch the executable (click no when it asks you to upgrade)
// - Launch Roblox (for testing web launches, run it from the command prompt)
// - To re-test the same executable, delete it from the installation folder

// #define DEBUG_UPDATER

#if DEBUG_UPDATER
#warning "Automatic updater debugging is enabled"
#endif

using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Shell;

using Microsoft.Win32;

using MrExStrap.AppData;
using MrExStrap.RobloxInterfaces;
using MrExStrap.UI.Elements.Bootstrapper.Base;

using ICSharpCode.SharpZipLib.Zip;

namespace MrExStrap
{
    public class Bootstrapper
    {
        #region Properties
        private const int ProgressBarMaximum = 10000;

        private const double TaskbarProgressMaximumWpf = 1; // this can not be changed. keep it at 1.
        private const int TaskbarProgressMaximumWinForms = WinFormsDialogBase.TaskbarProgressMaximum;

        private const string AppSettings =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<Settings>\r\n" +
            "	<ContentFolder>content</ContentFolder>\r\n" +
            "	<BaseUrl>http://www.roblox.com</BaseUrl>\r\n" +
            "</Settings>\r\n";

        private readonly FastZipEvents _fastZipEvents = new();
        private readonly CancellationTokenSource _cancelTokenSource = new();

        private IAppData AppData = default!;
        private LaunchMode _launchMode;

        private string _launchCommandLine = App.LaunchSettings.RobloxLaunchArgs;
        private Version? _latestVersion = null;
        private string _latestVersionGuid = null!;
        private string _latestVersionDirectory = null!;
        private PackageManifest _versionPackageManifest = null!;
        private bool _channelFetched = false;

        // Versions Manager profile that drives this launch, if any. Resolved against
        // Settings on demand so we don't go stale if the user activates a different
        // profile mid-launch (shouldn't happen, but cheap to re-read).
        // Studio launches deliberately bypass profile mode — the profile system only
        // applies to LaunchMode.Player.
        // Multi-instance is active for this launch when the user's global toggle is on OR the
        // launch carried -multiinstance (every Multi Instance tab launch does — see
        // AccountLauncher). The latter guarantees account launches start an independent client
        // even when the toggle is off, instead of being swallowed by a running client.
        private static bool MultiInstanceActive =>
            App.Settings.Prop.MultiInstanceEnabled || App.LaunchSettings.MultiInstanceFlag.Active;

        private VersionProfile? GetActiveProfileForBootstrap()
        {
            if (_launchMode != LaunchMode.Player)
                return null;

            var profiles = App.Settings.Prop.VersionProfiles;

            // Per-account override from the Multi Instance tab (-versionprofile <id>). Applies to
            // THIS launch only — the global ActiveVersionProfileId is never written. An unknown id
            // (e.g. the profile was deleted) falls through to the global active profile below.
            if (App.LaunchSettings.VersionProfileFlag.Active
                && !string.IsNullOrEmpty(App.LaunchSettings.VersionProfileFlag.Data))
            {
                var overridden = profiles.FirstOrDefault(p => p.Id == App.LaunchSettings.VersionProfileFlag.Data);
                if (overridden != null)
                    return overridden;
            }

            if (string.IsNullOrEmpty(App.Settings.Prop.ActiveVersionProfileId))
                return null;
            return profiles.FirstOrDefault(p => p.Id == App.Settings.Prop.ActiveVersionProfileId);
        }

        // What Roblox version is actually installed for this launch?
        //
        // For a profile-driven launch the answer lives on the profile, NOT on the
        // global DistributionState. DistributionState.VersionGuid holds whichever
        // profile launched last, so reading it here made switching from an executor
        // profile (e.g. Synapse Z) to "Latest LIVE" redownload Roblox on every launch
        // even though the Latest LIVE profile's own folder already had the right build.
        //
        // When the profile has no recorded hash, recover from the actual client on
        // disk: if its file version matches the build we're about to launch, adopt it
        // instead of redownloading. The exe version is authoritative, so a genuinely
        // stale install (a newer LIVE build shipped) still fails the match and upgrades.
        private string ResolveInstalledVersionForLaunch(VersionProfile? activeProfile)
        {
            const string LOG_IDENT = "Bootstrapper::ResolveInstalledVersionForLaunch";

            if (activeProfile is null)
                return AppData.DistributionState.VersionGuid;

            if (!string.IsNullOrEmpty(activeProfile.InstalledVersionGuid))
                return activeProfile.InstalledVersionGuid;

            if (InstalledExeMatchesLatest())
            {
                activeProfile.InstalledVersionGuid = _latestVersionGuid;
                App.Settings.Save();
                App.Logger.WriteLine(LOG_IDENT, $"Recovered InstalledVersionGuid for profile '{activeProfile.Name}' from on-disk client v{_latestVersion} -> {_latestVersionGuid}");
                return _latestVersionGuid;
            }

            return "";
        }

        // True when the Roblox client already present at the resolved install dir reports
        // the same file version as the build we're about to launch. Lets us recognise an
        // existing, current install whose per-profile bookkeeping was lost without ever
        // trusting a stale build.
        private bool InstalledExeMatchesLatest()
        {
            const string LOG_IDENT = "Bootstrapper::InstalledExeMatchesLatest";
            try
            {
                if (_latestVersion is null)
                    return false;

                string exePath = AppData.ExecutablePath;
                if (!File.Exists(exePath))
                    return false;

                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                var onDisk = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
                bool match = onDisk == _latestVersion;
                App.Logger.WriteLine(LOG_IDENT, $"On-disk {exePath} v{onDisk} vs latest v{_latestVersion}: {(match ? "match" : "differs")}");
                return match;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        // v420.24: each profile owns its real Roblox install at
        // Versions\profile-<id>\, and the active profile's launch exposes that
        // dir under the standard Versions\version-<active-hash>\ name via a
        // directory junction. Executors that detect the Roblox version from
        // the install-dir name (Severe, etc.) still get "version-<16hex>", and
        // same-hash profiles don't leak files into each other anymore — each
        // has its own real folder, only the junction changes per launch.
        //
        // Called once per Player launch from GetLatestVersionInfo, after
        // _latestVersionGuid is resolved. Best-effort throughout: any failure
        // logs and falls back to the standard version-<hash> path.

        // If the user is upgrading from v420.23 (or anyone else dropped a real
        // dir at Versions\version-<hash>\), claim it as the active profile's
        // install via a zero-copy Directory.Move so they don't redownload.
        // Only the active profile gets to inherit; other profiles pinning the
        // same hash redownload into their own profile-<id> dirs on first
        // launch (correct, since the v420.23 layout shared content and we
        // can't tell which profile "really" owns the existing files).
        private void AdoptOrphanRealDirIfApplicable(VersionProfile profile, string profileDir, string junctionPath)
        {
            const string LOG_IDENT = "Bootstrapper::AdoptOrphanRealDirIfApplicable";

            if (!Directory.Exists(junctionPath))
                return;
            if (VersionJunctionManager.IsJunction(junctionPath))
                return;

            bool profileDirEmpty = !Directory.Exists(profileDir)
                || !Directory.EnumerateFileSystemEntries(profileDir).Any();

            try
            {
                if (profileDirEmpty)
                {
                    if (Directory.Exists(profileDir))
                        Directory.Delete(profileDir, true);
                    Directory.Move(junctionPath, profileDir);
                    profile.InstalledVersionGuid = _latestVersionGuid;
                    App.Settings.Save();
                    App.Logger.WriteLine(LOG_IDENT, $"Adopted orphan {junctionPath} for profile '{profile.Name}' -> {profileDir}");
                }
                else
                {
                    // The profile already has its real install in profileDir, so this
                    // stray real dir at the junction path is redundant — just delete it
                    // in place so RecreateActiveProfileJunction can lay the junction back
                    // down. Previously we renamed it to <name>.orphan-<utc>, but the very
                    // next CleanupVersionsFolder pass deletes .orphan- dirs, and on a
                    // machine where this fired against what was really the live junction
                    // it nuked the profile's install — causing a full re-extract every
                    // launch (laptop logs 2026-06-01). Deleting the redundant real dir
                    // directly avoids ever creating an .orphan- that cleanup chases.
                    Directory.Delete(junctionPath, true);
                    App.Logger.WriteLine(LOG_IDENT, $"Profile '{profile.Name}' already has its own install dir; removed redundant real dir {junctionPath} so the junction can be recreated.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        // Clear the contents of a junction's target without deleting the
        // junction itself. Used by UpgradeRoblox and the cancel-cleanup path
        // since Directory.Delete on a junction (even with recursive=true)
        // unlinks the junction, and Directory.CreateDirectory on the same path
        // would then create a real dir — which is exactly what tripped up
        // v420.24 (flippi's bug report 2026-05-24): the install landed in
        // Versions\version-<hash>\ as a real dir while the per-profile dir
        // stayed empty.
        private static void ClearJunctionTargetContents(string junctionPath)
        {
            const string LOG_IDENT = "Bootstrapper::ClearJunctionTargetContents";

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(junctionPath))
                {
                    try { Directory.Delete(sub, true); }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); }
                }
                foreach (string file in Directory.EnumerateFiles(junctionPath))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        // Tear down any existing entry at junctionPath (junction or stray real
        // dir we couldn't adopt) and lay down a fresh junction pointing at
        // profileDir. Delegates to VersionJunctionManager so this code path
        // and the Versions Manager's "Set as install target" button take the
        // same route. If mklink fails, log and continue — downstream file ops
        // will hit the absent junctionPath and the standard install path kicks
        // in to populate it.
        private void RecreateActiveProfileJunction(string junctionPath, string profileDir)
        {
            const string LOG_IDENT = "Bootstrapper::RecreateActiveProfileJunction";

            if (!VersionJunctionManager.RepointJunction(junctionPath, profileDir))
            {
                App.Logger.WriteLine(LOG_IDENT, $"Junction creation failed for {junctionPath} -> {profileDir}; falling back to direct profile dir path.");
            }
        }

        private bool _isInstalling = false;
        private double _progressIncrement;
        private double _taskbarProgressIncrement;
        private double _taskbarProgressMaximum;
        private long _totalDownloadedBytes = 0;
        private long _totalPackedBytes = 0;

        // Speed/ETA tracking for the loading dialog. Sampled every UpdateProgressBar call;
        // smoothed via exponential moving average so the rate doesn't whiplash on each chunk.
        private DateTime? _speedSampleTime = null;
        private long _speedSampleBytes = 0;
        private double _smoothedBytesPerSec = 0;
        private bool _packageExtractionSuccess = true;

        private bool _mustUpgrade => App.LaunchSettings.ForceFlag.Active || App.State.Prop.ForceReinstall || String.IsNullOrEmpty(AppData.DistributionState.VersionGuid) || !File.Exists(AppData.ExecutablePath);
        private bool _noConnection = false;

        private AsyncMutex? _mutex;

        private int _appPid = 0;

        public IBootstrapperDialog? Dialog = null;

        public bool IsStudioLaunch => _launchMode != LaunchMode.Player;

        public string MutexName => $"{MutexNamePrefix}-{_launchMode}";
        public string BackgroundUpdaterMutexName => $"MrExStrap-BackgroundUpdater-{_launchMode}";

        public string MutexNamePrefix { get; set; } = "MrExStrap-Bootstrapper";
        public bool QuitIfMutexExists { get; set; } = false;
        #endregion

        #region Core
        public Bootstrapper(LaunchMode launchMode)
        {
            _launchMode = launchMode;

            // https://github.com/icsharpcode/SharpZipLib/blob/master/src/ICSharpCode.SharpZipLib/Zip/FastZip.cs/#L669-L680
            // exceptions don't get thrown if we define events without actually binding to the failure events. probably a bug. ¯\_(ツ)_/¯
            _fastZipEvents.FileFailure += (_, e) =>
            {
                // only give a pass to font files (no idea whats wrong with them)
                if (!e.Name.EndsWith(".ttf"))
                    throw e.Exception;

                App.Logger.WriteLine("FastZipEvents::OnFileFailure", $"Failed to extract {e.Name}");
                _packageExtractionSuccess = false;
            };
            _fastZipEvents.DirectoryFailure += (_, e) => throw e.Exception;
            _fastZipEvents.ProcessFile += (_, e) => e.ContinueRunning = !_cancelTokenSource.IsCancellationRequested;

            SetupAppData();
        }

        private void SetupAppData()
        {
            AppData = IsStudioLaunch ? new RobloxStudioData() : new RobloxPlayerData();
            Deployment.BinaryType = AppData.BinaryType;
        }

        private void SetStatus(string message)
        {
            App.Logger.WriteLine("Bootstrapper::SetStatus", message);

            message = message.Replace("{product}", AppData.ProductName);

            if (Dialog is not null)
                Dialog.Message = message;
        }

        private void UpdateProgressBar()
        {
            if (Dialog is null)
                return;

            // Parallel downloads call this from worker threads — bounce to the WPF dispatcher
            // before touching dialog properties (especially TaskbarItemProgressState, which is
            // strictly UI-thread-only). BeginInvoke is fire-and-forget so the download loop
            // doesn't block on the UI.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke((Action)UpdateProgressBar);
                return;
            }

            // UI progress
            int progressValue = (int)Math.Floor(_progressIncrement * _totalDownloadedBytes);

            // bugcheck: if we're restoring a file from a package, it'll incorrectly increment the progress beyond 100
            // too lazy to fix properly so lol
            progressValue = Math.Clamp(progressValue, 0, ProgressBarMaximum);

            Dialog.ProgressValue = progressValue;

            // taskbar progress
            double taskbarProgressValue = _taskbarProgressIncrement * _totalDownloadedBytes;
            taskbarProgressValue = Math.Clamp(taskbarProgressValue, 0, _taskbarProgressMaximum);

            Dialog.TaskbarProgressValue = taskbarProgressValue;

            // MrExStrap fork: show "X MB / Y MB" next to the progress bar plus a smoothed
            // speed/ETA line. The speed line is what tells you "this is slow but progressing"
            // vs "this is genuinely stuck" — the gap that confused users on USB installs.
            if (_totalPackedBytes > 0 && Dialog is UI.Elements.Bootstrapper.FluentDialog fluent)
            {
                long clampedDownloaded = Math.Clamp(_totalDownloadedBytes, 0, _totalPackedBytes);
                fluent.DownloadSizeText = $"{FormatBytes(clampedDownloaded)} / {FormatBytes(_totalPackedBytes)}";
                fluent.DownloadSpeedText = ComputeSpeedAndEtaText(clampedDownloaded, _totalPackedBytes);
            }
        }

        // Sample bytes-over-time and produce a "3.2 MB/s · ~30s remaining" string.
        // Uses an exponential moving average (alpha 0.3) so the rate is responsive but not jumpy.
        private string ComputeSpeedAndEtaText(long downloaded, long total)
        {
            DateTime now = DateTime.UtcNow;

            if (_speedSampleTime is null)
            {
                _speedSampleTime = now;
                _speedSampleBytes = downloaded;
                return ""; // need a second sample before we can show a rate
            }

            double secs = (now - _speedSampleTime.Value).TotalSeconds;
            if (secs < 0.25)
                return FormatSpeedAndEta(_smoothedBytesPerSec, downloaded, total);

            long deltaBytes = downloaded - _speedSampleBytes;
            if (deltaBytes < 0) deltaBytes = 0;

            double instantBps = deltaBytes / secs;
            // Seed with the first real reading; smooth thereafter.
            _smoothedBytesPerSec = _smoothedBytesPerSec == 0
                ? instantBps
                : (0.3 * instantBps) + (0.7 * _smoothedBytesPerSec);

            _speedSampleTime = now;
            _speedSampleBytes = downloaded;

            return FormatSpeedAndEta(_smoothedBytesPerSec, downloaded, total);
        }

        private static string FormatSpeedAndEta(double bytesPerSec, long downloaded, long total)
        {
            if (bytesPerSec <= 0)
                return ""; // no speed yet, show nothing rather than "0 B/s · forever"

            string speed = $"{FormatBytes((long)bytesPerSec)}/s";

            long remaining = total - downloaded;
            if (remaining <= 0)
                return speed;

            double etaSecs = remaining / bytesPerSec;
            string eta;
            if (etaSecs < 5) eta = "almost done";
            else if (etaSecs < 60) eta = $"~{(int)etaSecs}s remaining";
            else if (etaSecs < 3600) eta = $"~{(int)(etaSecs / 60)}m {(int)(etaSecs % 60)}s remaining";
            else eta = "over 1 hour remaining";

            return $"{speed}  ·  {eta}";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double n = bytes;
            int u = 0;
            while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
            return $"{n:0.#} {units[u]}";
        }

        private void HandleConnectionError(Exception exception)
        {
            const string LOG_IDENT = "Bootstrapper::HandleConnectionError";

            _noConnection = true;

            App.Logger.WriteLine(LOG_IDENT, "Connectivity check failed");
            App.Logger.WriteException(LOG_IDENT, exception);

            string message = Strings.Dialog_Connectivity_BadConnection;

            if (exception is AggregateException)
                exception = exception.InnerException!;

            // https://gist.github.com/pizzaboxer/4b58303589ee5b14cc64397460a8f386
            if (exception is HttpRequestException && exception.InnerException is null)
                message = String.Format(Strings.Dialog_Connectivity_RobloxDown, "[status.roblox.com](https://status.roblox.com)");

            if (_mustUpgrade)
                message += $"\n\n{Strings.Dialog_Connectivity_RobloxUpgradeNeeded}\n\n{Strings.Dialog_Connectivity_TryAgainLater}";
            else
                message += $"\n\n{Strings.Dialog_Connectivity_RobloxUpgradeSkip}";

            Frontend.ShowConnectivityDialog(
                String.Format(Strings.Dialog_Connectivity_UnableToConnect, "Roblox"), 
                message, 
                _mustUpgrade ? MessageBoxImage.Error : MessageBoxImage.Warning,
                exception);

            if (_mustUpgrade)
                App.Terminate(ErrorCode.ERROR_CANCELLED);
        }
        
        public async Task Run()
        {
            const string LOG_IDENT = "Bootstrapper::Run";

            App.Logger.WriteLine(LOG_IDENT, "Running bootstrapper");

            // this is now always enabled as of v2.8.0
            if (Dialog is not null)
                Dialog.CancelEnabled = true;

            SetStatus(Strings.Bootstrapper_Status_Connecting);

            var connectionResult = await Deployment.InitializeConnectivity();

            App.Logger.WriteLine(LOG_IDENT, "Connectivity check finished");

            if (connectionResult is not null)
                HandleConnectionError(connectionResult);
            
#if (!DEBUG || DEBUG_UPDATER) && !QA_BUILD
            if (App.Settings.Prop.CheckForUpdates && !App.LaunchSettings.UpgradeFlag.Active)
            {
                bool updatePresent = await CheckForUpdates();
                
                if (updatePresent)
                    return;
            }
#endif

            App.AssertWindowsOSVersion();

            // if we dont know our launch type, find out now!
            if (_launchMode == LaunchMode.Unknown)
            {
                await SafeGetLatestVersionInfo();

                if (_launchMode == LaunchMode.Unknown)
                    throw new ApplicationException("Failed to deduce launch type");
            }

            // ensure only one instance of the bootstrapper is running at the time
            // so that we don't have stuff like two updates happening simultaneously

            bool mutexExists = Utilities.DoesMutexExist(MutexName);

            if (mutexExists)
            {
                if (!QuitIfMutexExists)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{MutexName} mutex exists, waiting...");
                    SetStatus(Strings.Bootstrapper_Status_WaitingOtherInstances);
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{MutexName} mutex exists, exiting!");
                    return;
                }
            }

            // wait for mutex to be released if it's not yet
            await using var mutex = new AsyncMutex(false, MutexName);
            await mutex.AcquireAsync(_cancelTokenSource.Token);

            _mutex = mutex;

            // reload our configs since they've likely changed by now
            if (mutexExists)
            {
                App.Settings.Load();
                App.State.Load();
                AppData.DistributionStateManager.Load();
            }

            await SafeGetLatestVersionInfo();

            CleanupVersionsFolder(); // cleanup after background updater

            bool allModificationsApplied = true;

            if (!_noConnection)
            {
                // v420.20+: when a Versions Manager profile is driving this launch, the
                // "currently installed version" lives on the profile itself rather than
                // the global DistributionState — that way two profiles whose Roblox
                // hashes happen to match still install into separate dirs and the
                // up-to-date check stays accurate per profile.
                //
                // v420.24 fix: the previous null-coalesce (?? on the property value)
                // only fired when the profile object itself was null. A freshly-created
                // profile's InstalledVersionGuid defaults to "" (empty string), which
                // is NOT null, so it would short-circuit installedForThisLaunch to ""
                // and fail the equality check below — triggering a spurious reinstall
                // every single launch (flippi's 2026-05-24 report). Explicit
                // IsNullOrEmpty check fixes the fallback path.
                var activeProfileForCheck = GetActiveProfileForBootstrap();
                string installedForThisLaunch = ResolveInstalledVersionForLaunch(activeProfileForCheck);

                if (installedForThisLaunch != _latestVersionGuid || _mustUpgrade)
                {
                    bool backgroundUpdaterMutexOpen = !App.LaunchSettings.BackgroundUpdaterFlag.Active && Utilities.DoesMutexExist(BackgroundUpdaterMutexName);

                    App.Logger.WriteLine(LOG_IDENT, $"Background updater running: {backgroundUpdaterMutexOpen}");

                    if (backgroundUpdaterMutexOpen && _mustUpgrade)
                    {
                        // I am Forced Upgrade, killer of Background Updates
                        Utilities.KillBackgroundUpdater();
                        backgroundUpdaterMutexOpen = false;
                    }
                   
                    if (!backgroundUpdaterMutexOpen)
                    {
                        if (IsEligibleForBackgroundUpdate())
                            StartBackgroundUpdater();
                        else
                            await UpgradeRoblox();
                    }
                }

                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                // Per-profile fast flags: materialise the active Versions Manager profile's
                // flag set into the canonical ClientAppSettings.json that ApplyModifications
                // copies into the install. Keeps the overlay-copy path itself unchanged.
                Utility.FastFlagProfiles.MaterializeActiveToCanonical();

                // we require deployment details for applying modifications for a worst case scenario,
                // where we'd need to restore files from a package that isn't present on disk and needs to be redownloaded
                allModificationsApplied = await ApplyModifications();
            }

            // check registry entries for every launch, just in case the stock bootstrapper changes it back

            if (IsStudioLaunch)
                WindowsRegistry.RegisterStudio();
            else
                WindowsRegistry.RegisterPlayer();

            if (_launchMode != LaunchMode.Player)
                await mutex.ReleaseAsync();

            if (!App.LaunchSettings.NoLaunchFlag.Active && !_cancelTokenSource.IsCancellationRequested)
            {
                if (!App.LaunchSettings.QuietFlag.Active)
                {
                    // show some balloon tips
                    if (!_packageExtractionSuccess)
                        Frontend.ShowBalloonTip(Strings.Bootstrapper_ExtractionFailed_Title, Strings.Bootstrapper_ExtractionFailed_Message, ToolTipIcon.Warning);
                    else if (!allModificationsApplied)
                        Frontend.ShowBalloonTip(Strings.Bootstrapper_ModificationsFailed_Title, Strings.Bootstrapper_ModificationsFailed_Message, ToolTipIcon.Warning);
                }

                StartRoblox();
            }

            await mutex.ReleaseAsync();

            Dialog?.CloseBootstrapper();
        }

        private void FetchCurrentChannel()
        {
            // Fork behavior: channel is locked to LIVE. Ignore CLI flags, registry state,
            // and any other override source. See also UpdateChannelRegistry().
            const string LOG_IDENT = "Bootstrapper::FetchCurrentChannel";

            if (_channelFetched)
                return;

            Deployment.Channel = Deployment.DefaultChannel;
            App.Logger.WriteLine(LOG_IDENT, $"Channel forced to {Deployment.DefaultChannel}");
            _channelFetched = true;
        }

        private void UpdateChannelRegistry()
        {
            // Always blank the Roblox-side channel key on launch, then verify the write.
            // Roblox interprets an empty value (or "production") as the LIVE channel.
            // Any non-empty, non-"production" value means some other tool flipped the key;
            // we overwrite it regardless.
            const string LOG_IDENT = "Bootstrapper::UpdateChannelRegistry";
            string subKeyPath = $"SOFTWARE\\ROBLOX Corporation\\Environments\\{AppData.RegistryName}\\Channel";
            const string valueName = "www.roblox.com";

            // Captured so we can forward the most informative failure to the toast instead
            // of the user only seeing "couldn't be verified" with no detail.
            string? lastReason = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (RegistryKey writeKey = Registry.CurrentUser.CreateSubKey(subKeyPath))
                    {
                        writeKey.SetValueSafe(valueName, "");
                    }

                    string? readBack;
                    using (RegistryKey? verifyKey = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false))
                    {
                        readBack = verifyKey?.GetValue(valueName) as string;
                    }

                    bool locked = string.IsNullOrEmpty(readBack)
                        || string.Equals(readBack, Deployment.DefaultChannel, StringComparison.OrdinalIgnoreCase);

                    if (locked)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Channel lock verified: LIVE (attempt {attempt})");
                        return;
                    }

                    App.Logger.WriteLine(LOG_IDENT, $"Verification MISMATCH on attempt {attempt}: read back '{readBack}', expected empty or '{Deployment.DefaultChannel}'");
                    lastReason = $"another process wrote '{readBack}' back into the key";
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Registry access failed on attempt {attempt}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                    lastReason = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            App.Logger.WriteLine(LOG_IDENT, "WARNING: Channel lock could not be verified after retry. Roblox will still launch.");
            Utility.LiveChannelToast.ShowChannelLockFailed(lastReason);
        }

        /// <summary>
        /// Will throw whatever HttpClient can throw
        /// </summary>
        /// <returns></returns>
        private async Task GetLatestVersionInfo()
        {
            const string LOG_IDENT = "Bootstrapper::GetLatestVersionInfo";

            // before we do anything, we need to query our channel
            // if it's set in the launch uri, we need to use it and set the registry key for it
            // else, check if the registry key for it exists, and use it
            FetchCurrentChannel();

            string? newVersionGuid = null;
            Version? newVersion = null;

            // Version-resolution priority:
            //   1. CLI --version flag (session-scoped override)
            //   2. Versions Manager active profile (v420.19+) — preferred when set
            //   3. Settings.UseCustomVersion + CustomVersionGuid (legacy single-pin) — fallback only
            //   4. Fetch latest from clientsettingscdn
            // UpdateChannelRegistry() is called in every branch — channel lock must stay active
            // regardless of which version we're launching.

            bool cliVersion = App.LaunchSettings.VersionFlag.Active && !string.IsNullOrEmpty(App.LaunchSettings.VersionFlag.Data);

            // v420.23: if the active profile is executor-tracked (came from the WEAO
            // dropdown), refresh its VersionGuid from WEAO before resolving. Bounded
            // to 3s so a slow/dead WEAO never blocks launch — we just fall through to
            // the cached value.
            if (_launchMode == LaunchMode.Player && !cliVersion)
                await ExecutorProfileRefresher.RefreshActiveAsync(TimeSpan.FromSeconds(3));

            // Resolve the Versions Manager profile for this launch (honors a per-account
            // -versionprofile override; otherwise the global active profile).
            string? activeProfileGuid = null;
            string? activeProfileName = null;
            var resolvedProfile = GetActiveProfileForBootstrap();
            if (resolvedProfile != null
                && !string.IsNullOrEmpty(resolvedProfile.VersionGuid)
                && Utility.VersionGuidValidator.IsWellFormed(resolvedProfile.VersionGuid))
            {
                activeProfileGuid = resolvedProfile.VersionGuid;
                activeProfileName = resolvedProfile.Name;
            }

            bool pinnedVersion = activeProfileGuid != null
                || (App.Settings.Prop.UseCustomVersion
                    && Utility.VersionGuidValidator.IsWellFormed(App.Settings.Prop.CustomVersionGuid));

            string pinnedGuid = activeProfileGuid ?? App.Settings.Prop.CustomVersionGuid;
            string pinnedSource = activeProfileGuid != null
                ? $"Versions Manager profile '{activeProfileName}'"
                : "Downgrading single-pin";

            // Captured for the downgrade-badge comparison further down. In the no-pin/no-CLI
            // branch we already fetch LIVE via Deployment.GetInfo and reuse that result; in
            // the pin/CLI branches we fetch separately so we can tell whether the pinned hash
            // is genuinely older than LIVE. If the comparison fetch fails we leave this null
            // and the badge stays hidden — better than misclaiming a downgrade.
            string? liveVersionGuid = null;

            if (cliVersion)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Version set to {App.LaunchSettings.VersionFlag.Data} from arguments");
                newVersionGuid = App.LaunchSettings.VersionFlag.Data;
                UpdateChannelRegistry();
            }
            else if (pinnedVersion)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Version pinned to {pinnedGuid} via {pinnedSource}");
                newVersionGuid = pinnedGuid;
                UpdateChannelRegistry();
            }
            else
            {
                ClientVersion clientVersion;

                try
                {
                    clientVersion = await Deployment.GetInfo();
                }
                catch (InvalidChannelException ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Resetting channel from {Deployment.Channel} because {ex.StatusCode}");

                    Deployment.Channel = Deployment.DefaultChannel;
                    clientVersion = await Deployment.GetInfo();
                }

                UpdateChannelRegistry();

                newVersionGuid = clientVersion.VersionGuid;
                newVersion = Utilities.ParseVersionSafe(clientVersion.Version);
                liveVersionGuid = clientVersion.VersionGuid;
            }

            if (liveVersionGuid is null && (cliVersion || pinnedVersion))
            {
                try
                {
                    var liveInfo = await Deployment.GetInfo();
                    liveVersionGuid = liveInfo.VersionGuid;
                    App.Logger.WriteLine(LOG_IDENT, $"LIVE comparison hash: {liveVersionGuid}");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not fetch LIVE hash for downgrade comparison; badge will stay hidden.");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }

            if (newVersionGuid != _latestVersionGuid)
            {
                _latestVersionGuid = newVersionGuid!;
                _latestVersion = newVersion;

                // v420.24: per-profile real dirs at Versions\profile-<id>\ plus a
                // launch-time junction at Versions\version-<active-hash>\ that points
                // at the active profile's dir. Executors still see a standard
                // version-<hash> install path (junction is transparent to most APIs),
                // and same-hash profiles no longer share storage — flippi's wave/syn z
                // file-leak scenario can't happen anymore because each profile has its
                // own real folder. Studio launches stay on the legacy version-hash
                // layout (no profile system there).
                Directory.CreateDirectory(Paths.Versions);

                var activeProfileForLaunch = GetActiveProfileForBootstrap();
                if (activeProfileForLaunch != null)
                {
                    string profileDir = Path.Combine(Paths.Versions, "profile-" + activeProfileForLaunch.Id);
                    string junctionPath = Path.Combine(Paths.Versions, _latestVersionGuid);

                    AdoptOrphanRealDirIfApplicable(activeProfileForLaunch, profileDir, junctionPath);

                    Directory.CreateDirectory(profileDir);

                    RecreateActiveProfileJunction(junctionPath, profileDir);

                    // All downstream file ops go through the junction so Process.Start /
                    // executors see the standard version-<hash> path. Files actually land
                    // in profileDir via the junction redirect.
                    _latestVersionDirectory = junctionPath;
                }
                else
                {
                    _latestVersionDirectory = Path.Combine(Paths.Versions, _latestVersionGuid);
                }

                // Override AppData.Directory regardless: DistributionState.VersionGuid
                // is global and only updates after a download, so on profile switches
                // (no download needed) it'd still resolve to the previously active
                // profile's hash. Pinning the override here keeps Process.Start and
                // File.Exists honest.
                AppData.InstallDirectoryOverride = _latestVersionDirectory;

                string pkgManifestUrl = Deployment.GetLocation($"/{_latestVersionGuid}-rbxPkgManifest.txt");
                var pkgManifestData = await App.HttpClient.GetStringAsync(pkgManifestUrl);

                _versionPackageManifest = new(pkgManifestData);
            }

            // MrExStrap fork: surface version info + downgrade state on the loading screen.
            if (Dialog is UI.Elements.Bootstrapper.FluentDialog fluent)
            {
                string versionLabel = _latestVersion is not null
                    ? $"Roblox v{_latestVersion} \u00B7 {_latestVersionGuid}"
                    : _latestVersionGuid;
                fluent.VersionInfoText = versionLabel;

                // Only flag as downgraded when we can prove it: there's a CLI/pinned override,
                // we fetched a LIVE hash to compare against, and the launching hash differs.
                // Pinning to the actual LIVE hash (e.g. via "Pin this version" or picking an
                // up-to-date executor) intentionally hides the badge.
                bool launchingOverride = cliVersion || pinnedVersion;
                bool launchingDiffersFromLive = !string.IsNullOrEmpty(liveVersionGuid)
                    && !string.Equals(_latestVersionGuid, liveVersionGuid, StringComparison.OrdinalIgnoreCase);
                fluent.IsDowngraded = launchingOverride && launchingDiffersFromLive;

                // Place info (player launches only): parse placeId from the raw launch args.
                // We don't know the game name without a network call — just show the place id so
                // the user can confirm they're joining the right experience.
                if (!IsStudioLaunch)
                {
                    long? placeId = Utility.LaunchArgsUtility.TryExtractPlaceId(_launchCommandLine);
                    if (placeId.HasValue)
                    {
                        fluent.PlaceInfoText = Utility.StreamMode.IsActive
                            ? Utility.StreamMode.MaskedPlaceInfo
                            : $"Joining Roblox place #{placeId.Value}";
                    }
                }
            }

            // this can happen if version is set through arguments
            if (_launchMode == LaunchMode.Unknown)
            {
                if (_versionPackageManifest.Count != 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Identifying launch mode from package manifest");

                    bool isPlayer = _versionPackageManifest.Exists(x => x.Name == "RobloxApp.zip");
                    App.Logger.WriteLine(LOG_IDENT, $"isPlayer: {isPlayer}");

                    _launchMode = isPlayer ? LaunchMode.Player : LaunchMode.Studio;

                    SetupAppData(); // we need to set it up again

                    // lets set the registry now
                    UpdateChannelRegistry();
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not identify launch mode as package manifest is empty");
                }
            }
        }

        private async Task SafeGetLatestVersionInfo()
        {
            if (!_noConnection)
            {
                try
                {
                    await GetLatestVersionInfo();
                }
                catch (Exception ex)
                {
                    HandleConnectionError(ex);
                }
            }
        }

        private bool IsEligibleForBackgroundUpdate()
        {
            const string LOG_IDENT = "Bootstrapper::IsEligibleForBackgroundUpdate";

            if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Is the background updater process");
                return false;
            }

            if (!App.Settings.Prop.BackgroundUpdatesEnabled)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Background updates disabled");
                return false;
            }

            if (_mustUpgrade)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Must upgrade is true");
                return false;
            }

            // at least 5GB of free space
            const long minimumFreeSpace = 5_000_000_000;
            long space = Filesystem.GetFreeDiskSpace(Paths.Base);
            if (space < minimumFreeSpace)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not eligible: User has {space} free space, at least {minimumFreeSpace} is required");
                return false;
            }

            if (_latestVersion == default)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Latest version is undefined");
                return false;
            }

            Version? currentVersion = Utilities.GetRobloxVersion(AppData);
            if (currentVersion == default)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Current version is undefined");
                return false;
            }

            // always normally upgrade for downgrades
            if (currentVersion.Minor > _latestVersion.Minor)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Downgrade");
                return false;
            }

            // only background update if we're:
            // - one major update behind
            // - the same major update
            int diff = _latestVersion.Minor - currentVersion.Minor;
            if (diff == 0 || diff == 1)
            {
                App.Logger.WriteLine(LOG_IDENT, "Eligible");
                return true;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not eligible: Major version diff is {diff}");
                return false;
            }
        }

        private void StartRoblox()
        {
            const string LOG_IDENT = "Bootstrapper::StartRoblox";

            // Privacy mode: wipe Roblox's cookie cache right before the player process spawns.
            // Best-effort, never throws up to the caller — a file-locked cookie file shouldn't
            // prevent a launch.
            if (App.Settings.Prop.EnablePrivacyMode)
            {
                App.Logger.WriteLine(LOG_IDENT, "Privacy mode enabled — truncating RobloxCookies.dat");
                Utility.PrivacyMode.TruncateRobloxCookies();
            }

            // Multi-instance: hold Roblox's single-instance lock BEFORE the client starts.
            // While an MrExStrap process owns it, no client can elect itself the primary
            // instance, which is what triggers "the previous instance will be closed" and
            // kills the older client. Also sweeps the singleton event of any client that
            // became primary earlier (launched while this setting was off), so turning the
            // setting on mid-session works too.
            if (MultiInstanceActive && _launchMode == LaunchMode.Player)
                MrExStrap.Utility.MultiInstance.PrepareForLaunch();

            SetStatus(Strings.Bootstrapper_Status_Starting);

            var startInfo = new ProcessStartInfo()
            {
                FileName = AppData.ExecutablePath,
                Arguments = _launchCommandLine,
                WorkingDirectory = AppData.Directory
            };

            if (_launchMode == LaunchMode.Player && ShouldRunAsAdmin())
            {
                startInfo.Verb = "runas";
                startInfo.UseShellExecute = true;
            }
            else if (_launchMode == LaunchMode.StudioAuth)
            {
                Process.Start(startInfo);
                return;
            }

            string? logFileName = null;

            string rbxDir = Path.Combine(Paths.LocalAppData, "Roblox");
            if (!Directory.Exists(rbxDir))
                Directory.CreateDirectory(rbxDir);

            string rbxLogDir = Path.Combine(rbxDir, "logs");
            if (!Directory.Exists(rbxLogDir))
                Directory.CreateDirectory(rbxLogDir);

            var logWatcher = new FileSystemWatcher()
            {
                Path = rbxLogDir,
                Filter = "*.log",
                EnableRaisingEvents = true
            };

            var logCreatedEvent = new AutoResetEvent(false);

            logWatcher.Created += (_, e) =>
            {
                logWatcher.EnableRaisingEvents = false;
                logFileName = e.FullPath;
                logCreatedEvent.Set();
            };

            // v2.2.0 - byfron will trip if we keep a process handle open for over a minute, so we're doing this now
            try
            {
                using var process = Process.Start(startInfo)!;
                _appPid = process.Id;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED, gets thrown if a UAC prompt is cancelled
                return;
            }
            catch (Exception)
            {
                // Attempt a reinstall on next launch by deleting the exe so the package
                // pass redownloads it. Defensive try/catch so a missing exe (or parent
                // dir) doesn't replace the original Process.Start exception with a
                // misleading DirectoryNotFoundException — the user needs to see WHY
                // the launch actually failed.
                try
                {
                    if (File.Exists(AppData.ExecutablePath))
                        File.Delete(AppData.ExecutablePath);
                }
                catch (Exception cleanupEx)
                {
                    App.Logger.WriteException("Bootstrapper::StartRoblox::CleanupDelete", cleanupEx);
                }
                throw;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Started Roblox (PID {_appPid}), waiting for log file");

            // Fork feature: single post-launch toast confirming the LIVE channel.
            // Runs once per launch. Handles its own dispatch and cleanup.
            MrExStrap.Utility.LiveChannelToast.Show();

            // Multi-instance safety net: the held mutex (see PrepareForLaunch above) should
            // keep this client from ever becoming the primary instance. If it became primary
            // anyway, this background probe closes its singleton event so the next launch
            // doesn't kill it. No-op when the mutex did its job.
            if (MultiInstanceActive && _launchMode == LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Multi-instance active — scheduling singleton sweep (PID {_appPid})");
                MrExStrap.Utility.MultiInstance.ScheduleSingletonSweep();
            }

            // Window tiling: arrange all Roblox windows into a grid after a short delay.
            if (App.Settings.Prop.WindowTilingEnabled && _launchMode == LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Window tiling enabled — scheduling tile pass with layout {App.Settings.Prop.WindowTilingLayout}");
                MrExStrap.Utility.WindowTiler.ScheduleTilePass(App.Settings.Prop.WindowTilingLayout);
            }

            // Stretch resolution: resize the Roblox window to a custom resolution after launch.
            if (App.Settings.Prop.StretchResolution != StretchResolution.Disabled && _launchMode == LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Stretch resolution enabled — scheduling {App.Settings.Prop.StretchResolution}");
                MrExStrap.Utility.StretchResApplier.ScheduleApply(
                    App.Settings.Prop.StretchResolution,
                    App.Settings.Prop.StretchResCustomWidth,
                    App.Settings.Prop.StretchResCustomHeight);
            }

            // Always-on-top: keep Roblox window on top of other windows after launch.
            if (App.Settings.Prop.WindowAlwaysOnTop && _launchMode == LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, "Always-on-top enabled — scheduling window positioning.");
                MrExStrap.Utility.WindowHelper.SetAlwaysOnTop(true);
            }

            logCreatedEvent.WaitOne(TimeSpan.FromSeconds(15));

            if (String.IsNullOrEmpty(logFileName))
            {
                App.Logger.WriteLine(LOG_IDENT, "Unable to identify log file");
                Frontend.ShowPlayerErrorDialog();
                return;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Got log file as {logFileName}");
            }

            _mutex?.ReleaseAsync();

            if (IsStudioLaunch)
                return;

            var autoclosePids = new List<int>();

            // launch custom integrations now
            foreach (var integration in App.Settings.Prop.CustomIntegrations)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Launching custom integration '{integration.Name}' ({integration.Location} {integration.LaunchArgs} - autoclose is {integration.AutoClose})");

                int pid = 0;

                try
                {
                    var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = integration.Location,
                        Arguments = integration.LaunchArgs.Replace("\r\n", " "),
                        WorkingDirectory = Path.GetDirectoryName(integration.Location),
                        UseShellExecute = true
                    })!;

                    pid = process.Id;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to launch integration '{integration.Name}'!");
                    App.Logger.WriteLine(LOG_IDENT, ex.Message);
                }

                if (integration.AutoClose && pid != 0)
                    autoclosePids.Add(pid);
            }

            // v420.23: always spawn the watcher so RobloxPlayerBeta never gets left
            // running in the background after the user closes the window. Pre-v420.23
            // this only ran when EnableActivityTracking was on (or autoclose pids
            // existed), which meant users without activity tracking enabled saw the
            // Roblox process zombie out in Task Manager.
            {
                using var ipl = new InterProcessLock("Watcher", TimeSpan.FromSeconds(5));

                var watcherData = new WatcherData
                {
                    ProcessId = _appPid,
                    LogFile = logFileName,
                    AutoclosePids = autoclosePids
                };

                string watcherDataArg = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(watcherData)));

                string args = $"-watcher \"{watcherDataArg}\"";

                if (App.LaunchSettings.TestModeFlag.Active)
                    args += " -testmode";

                // Propagate multi-instance so the watcher (the longest-lived process this
                // session) keeps holding Roblox's single-instance lock — even for account
                // launches that only set the flag and not the global toggle.
                if (MultiInstanceActive)
                    args += " -multiinstance";

                if (ipl.IsAcquired)
                    Process.Start(Paths.Process, args);
            }

            // allow for window to show, since the log is created pretty far beforehand
            Thread.Sleep(1000);
        }

        private bool ShouldRunAsAdmin()
        {
            foreach (var root in WindowsRegistry.Roots)
            {
                using var key = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");

                if (key is null)
                    continue;

                string? flags = (string?)key.GetValue(AppData.ExecutablePath);

                if (flags is not null && flags.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public void Cancel()
        {
            const string LOG_IDENT = "Bootstrapper::Cancel";

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            App.Logger.WriteLine(LOG_IDENT, "Cancelling launch...");

            _cancelTokenSource.Cancel();

            if (Dialog is not null)
                Dialog.CancelEnabled = false;

            if (_isInstalling)
            {
                try
                {
                    // clean up install — junction-aware: clear the target's contents
                    // rather than deleting the directory itself (would unlink the
                    // junction). See v420.25 notes in UpgradeRoblox.
                    if (Directory.Exists(_latestVersionDirectory))
                    {
                        if (Utility.VersionJunctionManager.IsJunction(_latestVersionDirectory))
                            ClearJunctionTargetContents(_latestVersionDirectory);
                        else
                            Directory.Delete(_latestVersionDirectory, true);
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not fully clean up installation!");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
            else if (_appPid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(_appPid);
                    process.Kill();
                }
                catch (Exception ex)
                {
                    // Best-effort kill of the Roblox process we spawned. Failures here are
                    // usually "process already exited" (ArgumentException) — benign but log
                    // them so a real Kill failure stops being invisible during diagnostics.
                    App.Logger.WriteException("Bootstrapper::CancelKill", ex);
                }
            }

            Dialog?.CloseBootstrapper();

            App.SoftTerminate(ErrorCode.ERROR_CANCELLED);
        }
#endregion

        #region App Install
        private async Task<bool> CheckForUpdates()
        {
            const string LOG_IDENT = "Bootstrapper::CheckForUpdates";

            // Portable mode: the user is running in-place (often from a USB stick). We never
            // auto-replace the running exe here — they update by downloading the new portable
            // ZIP themselves.
            if (App.IsPortableMode)
            {
                App.Logger.WriteLine(LOG_IDENT, "Portable mode: skipping auto-update replace.");
                return false;
            }

            // don't update if there's another instance running (likely running in the background)
            // i don't like this, but there isn't much better way of doing it /shrug
            if (Process.GetProcessesByName(App.ProjectName).Length > 1)
            {
                App.Logger.WriteLine(LOG_IDENT, $"More than one MrExStrap instance running, aborting update check");
                return false;
            }

            App.Logger.WriteLine(LOG_IDENT, "Checking for updates...");

#if !DEBUG_UPDATER
            var releaseInfo = await App.GetLatestRelease();

            if (releaseInfo is null)
                return false;

            VersionComparison versionComparison;
            try
            {
                versionComparison = Utilities.CompareVersions(App.Version, releaseInfo.TagName);
            }
            catch (Exception ex)
            {
                // Don't let a version-string parse failure block launch. Skip the update check
                // this session and move on — users can still manually update from the GitHub release.
                App.Logger.WriteException(LOG_IDENT, ex);
                App.Logger.WriteLine(LOG_IDENT, $"Update check aborted: couldn't compare '{App.Version}' with '{releaseInfo.TagName}'. Continuing launch.");
                return false;
            }

            // Skip update if our local version is already at or ahead of GitHub's latest.
            // The previous condition gated Equal on IsProductionBuild, which meant
            // locally-published builds with the same version as GitHub got force-replaced
            // by the GitHub release on every launch — making iterative dev impossible.
            if (versionComparison == VersionComparison.Equal || versionComparison == VersionComparison.GreaterThan)
            {
                App.Logger.WriteLine(LOG_IDENT, "No updates found");
                return false;
            }

            if (Dialog is not null)
                Dialog.CancelEnabled = false;

            string version = releaseInfo.TagName;
#else
            string version = App.Version;
#endif

            SetStatus(Strings.Bootstrapper_Status_UpgradingBloxstrap);

            try
            {
#if DEBUG_UPDATER
                string downloadLocation = Path.Combine(Paths.TempUpdates, "MrExStrap.exe");

                Directory.CreateDirectory(Paths.TempUpdates);

                File.Copy(Paths.Process, downloadLocation, true);
#else
                // Pick the .exe asset explicitly. GitHub returns assets in upload order, which
                // can put the portable zip first — blindly grabbing Assets[0] downloads the
                // wrong artifact and Process.Start fails on the zip. This bit users coming
                // from v420.1/2/3 trying to auto-update.
                var asset = releaseInfo.Assets?.FirstOrDefault(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (asset is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, "No .exe asset on the latest release — cannot auto-update.");
                    return false;
                }

                string downloadLocation = Path.Combine(Paths.TempUpdates, asset.Name);

                Directory.CreateDirectory(Paths.TempUpdates);

                App.Logger.WriteLine(LOG_IDENT, $"Downloading {releaseInfo.TagName}...");
                
                if (!File.Exists(downloadLocation))
                {
                    using var response = await App.HttpClient.GetAsync(asset.BrowserDownloadUrl);

                    await using var fileStream = new FileStream(downloadLocation, FileMode.OpenOrCreate, FileAccess.Write);
                    await response.Content.CopyToAsync(fileStream);
                }
#endif

                App.Logger.WriteLine(LOG_IDENT, $"Starting {version}...");

                ProcessStartInfo startInfo = new()
                {
                    FileName = downloadLocation,
                };

                startInfo.ArgumentList.Add("-upgrade");

                foreach (string arg in App.LaunchSettings.Args)
                    startInfo.ArgumentList.Add(arg);

                if (_launchMode == LaunchMode.Player && !startInfo.ArgumentList.Contains("-player"))
                    startInfo.ArgumentList.Add("-player");
                else if (_launchMode == LaunchMode.Studio && !startInfo.ArgumentList.Contains("-studio"))
                    startInfo.ArgumentList.Add("-studio");

                App.Settings.Save();

                new InterProcessLock("AutoUpdater");
                
                Process.Start(startInfo);

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the auto-updater");
                App.Logger.WriteException(LOG_IDENT, ex);

                // Same idea as the menu-open update path — include the actual reason so the
                // user has something to act on instead of "auto-update failed, sorry".
                string reasonLine = $"Reason: {ex.GetType().Name}: {ex.Message}";

                Frontend.ShowMessageBox(
                    string.Format(Strings.Bootstrapper_AutoUpdateFailed, version)
                        + "\n\n" + reasonLine
                        + "\n\nOpening the GitHub releases page so you can grab the installer manually.",
                    MessageBoxImage.Information
                );

                Utilities.ShellExecute(App.ProjectDownloadLink);
            }

            return false;
        }
        #endregion

        #region Roblox Install
        private static bool TryDeleteRobloxInDirectory(string dir)
        {
            // check if the roblox executable is present in the directory
            string clientPath = Path.Combine(dir, "RobloxPlayerBeta.exe");
            if (!File.Exists(clientPath))
            {
                clientPath = Path.Combine(dir, "RobloxStudioBeta.exe");
                if (!File.Exists(clientPath))
                    return true; // ok???
            }

            try
            {
                File.Delete(clientPath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void CleanupVersionsFolder()
        {
            const string LOG_IDENT = "Bootstrapper::CleanupVersionsFolder";

            if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Background updater tried to cleanup, stopping!");
                return;
            }

            if (!Directory.Exists(Paths.Versions))
            {
                App.Logger.WriteLine(LOG_IDENT, "Versions directory does not exist, skipping cleanup.");
                return;
            }

            // v420.24 layout. Three kinds of entries can live under Paths.Versions:
            //   - profile-<id>\         : the per-profile real Roblox install. Keep
            //                             while a VersionProfile with that id exists;
            //                             delete when the user removes the profile.
            //   - version-<hash>\       : either a junction (active profile's
            //                             facade — keep if its hash matches some
            //                             profile.VersionGuid AND the target dir
            //                             exists), or a real dir (Studio install,
            //                             or the legacy player install for users
            //                             who never opened the Versions Manager).
            //   - version-<hash>.orphan-<utc>\ : v420.24-era leftover from when
            //                             a real dir at the version path got
            //                             set aside instead of adopted. Safe to
            //                             auto-delete from v420.27 onward —
            //                             v420.25+ no longer creates new ones.
            var profileIds = new HashSet<string>(
                App.Settings.Prop.VersionProfiles.Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase);
            // Keep any version-<hash> dir/junction a profile either PINS (VersionGuid)
            // or currently HAS INSTALLED (InstalledVersionGuid). The built-in "Latest
            // LIVE" profile has an empty VersionGuid (it always tracks current LIVE) but
            // a populated InstalledVersionGuid — without including the latter, its active
            // junction was treated as unreferenced and pruned every launch, which made
            // the exe path vanish and forced a full re-extract on every launch for users
            // whose only profile is the built-in LIVE one. (Confirmed via laptop logs
            // 2026-06-01: "Pruned stale junction version-<hash>" each launch.)
            var profileVersionGuids = new HashSet<string>(
                App.Settings.Prop.VersionProfiles
                    .SelectMany(p => new[] { p.VersionGuid, p.InstalledVersionGuid })
                    .Where(g => !string.IsNullOrEmpty(g)),
                StringComparer.OrdinalIgnoreCase);

            foreach (string dir in Directory.GetDirectories(Paths.Versions))
            {
                string dirName = Path.GetFileName(dir);

                if (dirName.Contains(".orphan-"))
                {
                    // v420.27+: auto-delete v420.24's orphan-* leftovers. v420.25+
                    // no longer creates them, so anything here is from an upgrade
                    // and is known-safe to remove (documented as such in the
                    // v420.25 release notes — users can free a few GB).
                    if (!TryDeleteRobloxInDirectory(dir))
                        continue;

                    try
                    {
                        Directory.Delete(dir, true);
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted orphan leftover {dirName} (v420.24 cleanup)");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to delete orphan {dir}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                    continue;
                }

                if (dirName.StartsWith("profile-", StringComparison.OrdinalIgnoreCase))
                {
                    string profileId = dirName.Substring("profile-".Length);
                    if (profileIds.Contains(profileId))
                    {
                        // Keep silently — common case, no need to spam the log.
                        continue;
                    }

                    if (!TryDeleteRobloxInDirectory(dir))
                        continue;

                    try
                    {
                        Directory.Delete(dir, true);
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted {dirName} (its profile was removed)");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {dir}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                    continue;
                }

                bool isJunction = VersionJunctionManager.IsJunction(dir);
                bool referencedByProfile = profileVersionGuids.Contains(dirName);
                bool isCurrentState = dirName == App.PlayerState.Prop.VersionGuid
                                       || dirName == App.StudioState.Prop.VersionGuid;

                if (isJunction)
                {
                    // Junctions point at a profile-<id> dir under Paths.Versions. If
                    // the target is gone OR no profile claims this hash anymore,
                    // prune the dangling reparse point. Cheap, safe.
                    bool keep = referencedByProfile;
                    if (keep)
                    {
                        // Verify target still resolves — broken junctions are useless.
                        try
                        {
                            keep = Directory.EnumerateFileSystemEntries(dir).Any()
                                   || Directory.Exists(dir);
                        }
                        catch
                        {
                            keep = false;
                        }
                    }

                    if (!keep)
                    {
                        if (VersionJunctionManager.DeleteJunction(dir))
                            App.Logger.WriteLine(LOG_IDENT, $"Pruned stale junction {dirName}");
                    }
                    continue;
                }

                // Real version-<hash>\ dir. Could be Studio, the legacy Player
                // install, or a v420.23 leftover we couldn't adopt at launch.
                if (isCurrentState || referencedByProfile)
                {
                    if (referencedByProfile && !isCurrentState)
                        App.Logger.WriteLine(LOG_IDENT, $"Keeping {dirName} (referenced by a Versions Manager profile — will adopt on its next launch)");
                    continue;
                }

                if (!TryDeleteRobloxInDirectory(dir))
                    continue;

                try
                {
                    Directory.Delete(dir, true);
                    App.Logger.WriteLine(LOG_IDENT, $"Deleted orphan {dirName}");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {dir}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
        }

        private void MigrateCompatibilityFlags()
        {
            const string LOG_IDENT = "Bootstrapper::MigrateCompatibilityFlags";

            string oldClientLocation = Path.Combine(Paths.Versions, AppData.DistributionState.VersionGuid, AppData.ExecutableName);
            string newClientLocation = Path.Combine(_latestVersionDirectory, AppData.ExecutableName);

            // move old compatibility flags for the old location
            using RegistryKey appFlagsKey = Registry.CurrentUser.CreateSubKey($"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");
            string? appFlags = appFlagsKey.GetValue(oldClientLocation) as string;

            if (appFlags is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Migrating app compatibility flags from {oldClientLocation} to {newClientLocation}...");
                appFlagsKey.SetValueSafe(newClientLocation, appFlags);
                appFlagsKey.DeleteValueSafe(oldClientLocation);
            }
        }

        private void KillRobloxInstances()
        {
            const string LOG_IDENT = "Bootstrapper::KillRobloxInstances";

            List<Process> processes = new List<Process>();
            processes.AddRange(Process.GetProcessesByName(AppData.ProcessName));
            processes.AddRange(Process.GetProcessesByName("RobloxCrashHandler")); // roblox studio doesnt depend on crash handler being open, so this should be fine

            foreach (Process process in processes)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to close process {process.Id}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
        }

        private async Task GracefullyCloseRobloxInstances()
        {
            const string LOG_IDENT = "Bootstrapper::GracefullyCloseRobloxInstances";

            while (true)
            {
                Process[] processes = Process.GetProcessesByName(AppData.ProcessName);
                if (processes.Length == 0)
                    break;

                foreach (Process process in processes)
                {
                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to close process {process.Id}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }

                try
                {
                    await Task.Delay(1000, _cancelTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private async Task UpgradeRoblox()
        {
            const string LOG_IDENT = "Bootstrapper::UpgradeRoblox";

            Directory.CreateDirectory(Paths.Base);
            Directory.CreateDirectory(Paths.Downloads);
            Directory.CreateDirectory(Paths.Versions);

            _isInstalling = true;

            if (!App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                SetStatus(Strings.Bootstrapper_Status_ShuttingDown);

                if (IsStudioLaunch)
                    await GracefullyCloseRobloxInstances();
                else
                    KillRobloxInstances();

                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                // get a fully clean install
                if (Directory.Exists(_latestVersionDirectory))
                {
                    try
                    {
                        if (Utility.VersionJunctionManager.IsJunction(_latestVersionDirectory))
                        {
                            // v420.25 fix: _latestVersionDirectory is a junction (set up
                            // by GetLatestVersionInfo) — Directory.Delete on a junction
                            // would unlink it, then the CreateDirectory below would put
                            // a *real* dir at the junction path while the profile dir
                            // stays empty. (flippi's 2026-05-24 reproduction.) Clear the
                            // junction's target contents instead so the junction stays
                            // intact and the next install lands in the profile dir as
                            // intended.
                            ClearJunctionTargetContents(_latestVersionDirectory);
                        }
                        else
                        {
                            Directory.Delete(_latestVersionDirectory, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to clear the latest version directory");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }
            }

            if (String.IsNullOrEmpty(AppData.DistributionState.VersionGuid))
                SetStatus(Strings.Bootstrapper_Status_Installing);
            else
                SetStatus(Strings.Bootstrapper_Status_Upgrading);

            Directory.CreateDirectory(_latestVersionDirectory);

            var cachedPackageHashes = Directory.GetFiles(Paths.Downloads).Select(x => Path.GetFileName(x));

            // package manifest states packed size and uncompressed size in exact bytes
            int totalSizeRequired = 0;

            // packed size only matters if we don't already have the package cached on disk
            totalSizeRequired += _versionPackageManifest.Where(x => !cachedPackageHashes.Contains(x.Signature)).Sum(x => x.PackedSize);
            totalSizeRequired += _versionPackageManifest.Sum(x => x.Size);
            
            if (Filesystem.GetFreeDiskSpace(Paths.Base) < totalSizeRequired)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_NotEnoughSpace, MessageBoxImage.Error);
                App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                return;
            }

            if (Dialog is not null)
            {
                Dialog.ProgressStyle = ProgressBarStyle.Continuous;
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Normal;

                Dialog.ProgressMaximum = ProgressBarMaximum;

                // compute total bytes to download
                int totalPackedSize = _versionPackageManifest.Sum(package => package.PackedSize);
                _totalPackedBytes = totalPackedSize;
                _progressIncrement = (double)ProgressBarMaximum / totalPackedSize;

                if (Dialog is WinFormsDialogBase)
                    _taskbarProgressMaximum = (double)TaskbarProgressMaximumWinForms;
                else
                    _taskbarProgressMaximum = (double)TaskbarProgressMaximumWpf;

                _taskbarProgressIncrement = _taskbarProgressMaximum / (double)totalPackedSize;
            }

            // MrExStrap fork: parallelize package downloads. Upstream Bloxstrap downloads
            // packages one at a time, which is the dominant install bottleneck (~30-50 packages,
            // ~200 MB). With a small concurrency window the same install completes in a fraction
            // of the wall time on any reasonable connection. 6 is a sweet spot — enough to
            // saturate residential bandwidth, not so many that we hammer the CDN or starve the
            // disk on slower drives.
            const int maxConcurrentDownloads = 6;
            using var downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads);

            var pipelineTasks = _versionPackageManifest.Select(async package =>
            {
                await downloadSemaphore.WaitAsync(_cancelTokenSource.Token);
                try
                {
                    if (_cancelTokenSource.IsCancellationRequested)
                        return;

                    await DownloadPackage(package);

                    if (_cancelTokenSource.IsCancellationRequested)
                        return;

                    // WebView2 runtime is unpacked separately later (its installer needs a
                    // dedicated flow), so leave it on disk for now.
                    if (package.Name == "WebView2RuntimeInstaller.zip")
                        return;

                    // Extract on a background thread so it overlaps with the next package's
                    // download — same pipelined behaviour as upstream, just now multiple
                    // downloads in flight at once.
                    await Task.Run(() => ExtractPackage(package), _cancelTokenSource.Token);
                }
                finally
                {
                    downloadSemaphore.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(pipelineTasks);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            if (Dialog is not null)
            {
                Dialog.ProgressStyle = ProgressBarStyle.Marquee;
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Indeterminate;
                SetStatus(Strings.Bootstrapper_Status_Configuring);
            }
            
            App.Logger.WriteLine(LOG_IDENT, "Writing AppSettings.xml...");
            await File.WriteAllTextAsync(Path.Combine(_latestVersionDirectory, "AppSettings.xml"), AppSettings);

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            if (App.State.Prop.PromptWebView2Install)
            {
                using var hklmKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
                using var hkcuKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");

                if (hklmKey is not null || hkcuKey is not null)
                {
                    // reset prompt state if the user has it installed
                    App.State.Prop.PromptWebView2Install = true;
                }   
                else
                {
                    var result = Frontend.ShowMessageBox(Strings.Bootstrapper_WebView2NotFound, MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.Yes);

                    if (result != MessageBoxResult.Yes)
                    {
                        App.State.Prop.PromptWebView2Install = false;
                    }
                    else
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Installing WebView2 runtime...");

                        var package = _versionPackageManifest.Find(x => x.Name == "WebView2RuntimeInstaller.zip");

                        if (package is null)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Aborted runtime install because package does not exist, has WebView2 been added in this Roblox version yet?");
                            return;
                        }

                        string baseDirectory = Path.Combine(_latestVersionDirectory, AppData.PackageDirectoryMap[package.Name]);

                        ExtractPackage(package);

                        SetStatus(Strings.Bootstrapper_Status_InstallingWebView2);

                        var startInfo = new ProcessStartInfo()
                        {
                            WorkingDirectory = baseDirectory,
                            FileName = Path.Combine(baseDirectory, "MicrosoftEdgeWebview2Setup.exe"),
                            Arguments = "/silent /install"
                        };

                        await Process.Start(startInfo)!.WaitForExitAsync();

                        App.Logger.WriteLine(LOG_IDENT, "Finished installing runtime");

                        Directory.Delete(baseDirectory, true);
                    }
                }
            }

            // finishing and cleanup

            MigrateCompatibilityFlags();

            AppData.DistributionState.VersionGuid = _latestVersionGuid;

            // v420.20: mirror the installed version onto the active Versions Manager
            // profile so the per-launch up-to-date check stays accurate per profile.
            // Without this the next launch would compare the global state against the
            // wanted version and skip the install even though THIS profile's dir is
            // empty / stale.
            var bootstrapProfile = GetActiveProfileForBootstrap();
            if (bootstrapProfile != null)
            {
                bootstrapProfile.InstalledVersionGuid = _latestVersionGuid;
                App.Settings.Save();
            }

            AppData.DistributionState.PackageHashes.Clear();

            foreach (var package in _versionPackageManifest)
                AppData.DistributionState.PackageHashes.Add(package.Name, package.Signature);

            CleanupVersionsFolder();

            var allPackageHashes = new List<string>();

            allPackageHashes.AddRange(App.PlayerState.Prop.PackageHashes.Values);
            allPackageHashes.AddRange(App.StudioState.Prop.PackageHashes.Values);

            if (!App.Settings.Prop.DebugDisableVersionPackageCleanup)
            {
                foreach (string hash in cachedPackageHashes)
                {
                    if (!allPackageHashes.Contains(hash))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Deleting unused package {hash}");

                        try
                        {
                            File.Delete(Path.Combine(Paths.Downloads, hash));
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {hash}!");
                            App.Logger.WriteException(LOG_IDENT, ex);
                        }
                    }
                }
            }

            App.Logger.WriteLine(LOG_IDENT, "Registering approximate program size...");

            int distributionSize = _versionPackageManifest.Sum(x => x.Size + x.PackedSize) / 1024;

            AppData.DistributionState.Size = distributionSize;

            int totalSize = App.PlayerState.Prop.Size + App.PlayerState.Prop.Size;

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("EstimatedSize", totalSize);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Registered as {totalSize} KB");

            App.State.Prop.ForceReinstall = false;

            App.State.Save();
            AppData.DistributionStateManager.Save();

            _isInstalling = false;
        }

        private void StartBackgroundUpdater()
        {
            const string LOG_IDENT = "Bootstrapper::StartBackgroundUpdater";

            if (Utilities.DoesMutexExist(BackgroundUpdaterMutexName))
            {
                App.Logger.WriteLine(LOG_IDENT, "Background updater already running");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Starting background updater");

            Process.Start(Paths.Process, $"-backgroundupdater {_launchMode}");
        }

        private async Task<bool> ApplyModifications()
        {
            const string LOG_IDENT = "Bootstrapper::ApplyModifications";

            bool success = true;

            SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);

            // handle file mods
            App.Logger.WriteLine(LOG_IDENT, "Checking file mods...");

            // manifest has been moved to State.json
            File.Delete(Path.Combine(Paths.Base, "ModManifest.txt"));

            List<string> modFolderFiles = new();

            Directory.CreateDirectory(Paths.Modifications);

            // check custom font mod
            // instead of replacing the fonts themselves, we'll just alter the font family manifests

            string modFontFamiliesFolder = Path.Combine(Paths.Modifications, "content\\fonts\\families");

            if (File.Exists(Paths.CustomFont))
            {
                App.Logger.WriteLine(LOG_IDENT, "Begin font check");

                Directory.CreateDirectory(modFontFamiliesFolder);

                const string path = "rbxasset://fonts/CustomFont.ttf";

                // lets make sure the content/fonts/families path exists in the version directory
                string contentFolder = Path.Combine(_latestVersionDirectory, "content");
                Directory.CreateDirectory(contentFolder);

                string fontsFolder = Path.Combine(contentFolder, "fonts");
                Directory.CreateDirectory(fontsFolder);

                string familiesFolder = Path.Combine(fontsFolder, "families");
                Directory.CreateDirectory(familiesFolder);

                foreach (string jsonFilePath in Directory.GetFiles(familiesFolder))
                {
                    string jsonFilename = Path.GetFileName(jsonFilePath);
                    string modFilepath = Path.Combine(modFontFamiliesFolder, jsonFilename);

                    if (File.Exists(modFilepath))
                        continue;

                    App.Logger.WriteLine(LOG_IDENT, $"Setting font for {jsonFilename}");

                    var fontFamilyData = JsonSerializer.Deserialize<FontFamily>(File.ReadAllText(jsonFilePath));

                    if (fontFamilyData is null)
                        continue;

                    bool shouldWrite = false;

                    foreach (var fontFace in fontFamilyData.Faces)
                    {
                        if (fontFace.AssetId != path)
                        {
                            fontFace.AssetId = path;
                            shouldWrite = true;
                        }
                    }

                    if (shouldWrite)
                        File.WriteAllText(modFilepath, JsonSerializer.Serialize(fontFamilyData, new JsonSerializerOptions { WriteIndented = true }));
                }

                App.Logger.WriteLine(LOG_IDENT, "End font check");
            }
            else if (Directory.Exists(modFontFamiliesFolder))
            {
                Directory.Delete(modFontFamiliesFolder, true);
            }

            foreach (string file in Directory.GetFiles(Paths.Modifications, "*.*", SearchOption.AllDirectories))
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return true;

                // get relative directory path
                string relativeFile = file.Substring(Paths.Modifications.Length + 1);

                // v1.7.0 - README has been moved to the preferences menu now
                if (relativeFile == "README.txt")
                {
                    File.Delete(file);
                    continue;
                }

                if (!App.Settings.Prop.UseFastFlagManager && String.Equals(relativeFile, "ClientSettings\\ClientAppSettings.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (relativeFile.EndsWith(".lock"))
                    continue;

                modFolderFiles.Add(relativeFile);

                string fileModFolder = Path.Combine(Paths.Modifications, relativeFile);
                string fileVersionFolder = Path.Combine(_latestVersionDirectory, relativeFile);

                if (File.Exists(fileVersionFolder) && MD5Hash.FromFile(fileModFolder) == MD5Hash.FromFile(fileVersionFolder))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{relativeFile} already exists in the version folder, and is a match");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fileVersionFolder)!);

                Filesystem.AssertReadOnly(fileVersionFolder);
                try
                {
                    File.Copy(fileModFolder, fileVersionFolder, true);
                    Filesystem.AssertReadOnly(fileVersionFolder);
                    App.Logger.WriteLine(LOG_IDENT, $"{relativeFile} has been copied to the version folder");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to apply modification ({relativeFile})");
                    App.Logger.WriteException(LOG_IDENT, ex);
                    success = false;
                }
            }

            // the manifest is primarily here to keep track of what files have been
            // deleted from the modifications folder, so that we know when to restore the original files from the downloaded packages
            // now check for files that have been deleted from the mod folder according to the manifest

            var fileRestoreMap = new Dictionary<string, List<string>>();

            foreach (string fileLocation in AppData.DistributionState.ModManifest)
            {
                if (modFolderFiles.Contains(fileLocation))
                    continue;

                var packageMapEntry = AppData.PackageDirectoryMap.SingleOrDefault(x => !String.IsNullOrEmpty(x.Value) && fileLocation.StartsWith(x.Value));
                string packageName = packageMapEntry.Key;

                // package doesn't exist, likely mistakenly placed file
                if (String.IsNullOrEmpty(packageName))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{fileLocation} was removed as a mod but does not belong to a package");

                    string versionFileLocation = Path.Combine(_latestVersionDirectory, fileLocation);

                    if (File.Exists(versionFileLocation))
                        File.Delete(versionFileLocation);

                    continue;
                }

                string fileName = fileLocation.Substring(packageMapEntry.Value.Length);

                if (!fileRestoreMap.ContainsKey(packageName))
                    fileRestoreMap[packageName] = new();

                fileRestoreMap[packageName].Add(fileName);

                App.Logger.WriteLine(LOG_IDENT, $"{fileLocation} was removed as a mod, restoring from {packageName}");
            }

            foreach (var entry in fileRestoreMap)
            {
                var package = _versionPackageManifest.Find(x => x.Name == entry.Key);

                if (package is not null)
                {
                    if (_cancelTokenSource.IsCancellationRequested)
                        return true;

                    await DownloadPackage(package);
                    ExtractPackage(package, entry.Value);
                }
            }

            // make sure we're not overwriting a new update
            // if we're the background update process, always overwrite
            if (App.LaunchSettings.BackgroundUpdaterFlag.Active || !AppData.DistributionStateManager.HasFileOnDiskChanged())
            {
                AppData.DistributionState.ModManifest = modFolderFiles;
                AppData.DistributionStateManager.Save();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"{AppData.DistributionStateManager.ClassName} disk mismatch, not saving ModManifest");
            }

            App.Logger.WriteLine(LOG_IDENT, $"Finished checking file mods");

            if (!success)
                App.Logger.WriteLine(LOG_IDENT, "Failed to apply all modifications");

            return success;
        }

        private async Task DownloadPackage(Package package)
        {
            string LOG_IDENT = $"Bootstrapper::DownloadPackage.{package.Name}";
            
            if (_cancelTokenSource.IsCancellationRequested)
                return;

            Directory.CreateDirectory(Paths.Downloads);

            string packageUrl = Deployment.GetLocation($"/{_latestVersionGuid}-{package.Name}");
            string robloxPackageLocation = Path.Combine(Paths.LocalAppData, "Roblox", "Downloads", package.Signature);

            if (File.Exists(package.DownloadPath))
            {
                var file = new FileInfo(package.DownloadPath);

                string calculatedMD5 = MD5Hash.FromFile(package.DownloadPath);

                if (calculatedMD5 != package.Signature)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Package is corrupted ({calculatedMD5} != {package.Signature})! Deleting and re-downloading...");
                    file.Delete();
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Package is already downloaded, skipping...");

                    Interlocked.Add(ref _totalDownloadedBytes, package.PackedSize);
                    UpdateProgressBar();

                    return;
                }
            }
            else if (File.Exists(robloxPackageLocation))
            {
                // let's cheat! if the stock bootstrapper already previously downloaded the file,
                // then we can just copy the one from there

                App.Logger.WriteLine(LOG_IDENT, $"Found existing copy at '{robloxPackageLocation}'! Copying to Downloads folder...");
                File.Copy(robloxPackageLocation, package.DownloadPath);

                Interlocked.Add(ref _totalDownloadedBytes, package.PackedSize);
                UpdateProgressBar();

                return;
            }

            if (File.Exists(package.DownloadPath))
                return;

            const int maxTries = 5;

            App.Logger.WriteLine(LOG_IDENT, "Downloading...");

            var buffer = new byte[4096];

            for (int i = 1; i <= maxTries; i++)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                int totalBytesRead = 0;

                try
                {
                    using var response = await App.HttpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, _cancelTokenSource.Token);
                    await using var stream = await response.Content.ReadAsStreamAsync(_cancelTokenSource.Token);
                    await using var fileStream = new FileStream(package.DownloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Delete);

                    while (true)
                    {
                        if (_cancelTokenSource.IsCancellationRequested)
                        {
                            stream.Close();
                            fileStream.Close();
                            return;
                        }

                        int bytesRead = await stream.ReadAsync(buffer, _cancelTokenSource.Token);

                        if (bytesRead == 0)
                            break;

                        totalBytesRead += bytesRead;

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cancelTokenSource.Token);

                        Interlocked.Add(ref _totalDownloadedBytes, bytesRead);
                        UpdateProgressBar();
                    }

                    string hash = MD5Hash.FromStream(fileStream);

                    if (hash != package.Signature)
                        throw new ChecksumFailedException($"Failed to verify download of {packageUrl}\n\nExpected hash: {package.Signature}\nGot hash: {hash}");

                    App.Logger.WriteLine(LOG_IDENT, $"Finished downloading! ({totalBytesRead} bytes total)");
                    break;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"An exception occurred after downloading {totalBytesRead} bytes. ({i}/{maxTries})");
                    App.Logger.WriteException(LOG_IDENT, ex);

                    bool isChecksumFailure = ex.GetType() == typeof(ChecksumFailedException);

                    // A checksum mismatch means the bytes arrived altered, not that the request was
                    // blocked outright. The usual culprit is an antivirus doing HTTPS/TLS inspection:
                    // it re-encrypts the stream and corrupts the payload, so the hash never matches.
                    // That's recoverable, so treat it like any other transient failure here - delete
                    // the partial file and retry, falling back to plain HTTP below (which AV TLS
                    // inspection can't touch). Only once every attempt is exhausted do we give up and
                    // show the connectivity dialog. Previously a single checksum failure terminated
                    // immediately with no retry, so a user behind an inspecting AV could never launch
                    // even though the HTTP fallback would have rescued them.
                    if (i >= maxTries)
                    {
                        if (isChecksumFailure)
                        {
                            App.SendStat("packageDownloadState", "httpFail");

                            Frontend.ShowConnectivityDialog(
                                Strings.Dialog_Connectivity_UnableToDownload,
                                String.Format(Strings.Dialog_Connectivity_UnableToDownloadReason, $"[{App.ProjectSupportLink}]({App.ProjectSupportLink})"),
                                MessageBoxImage.Error,
                                ex
                            );

                            App.Terminate(ErrorCode.ERROR_CANCELLED);
                        }

                        throw;
                    }

                    if (File.Exists(package.DownloadPath))
                        File.Delete(package.DownloadPath);

                    Interlocked.Add(ref _totalDownloadedBytes, -totalBytesRead);
                    UpdateProgressBar();

                    // attempt download over HTTP
                    // this isn't actually that unsafe - signatures were fetched earlier over HTTPS
                    // so we've already established that our signatures are legit, and that there's very likely no MITM anyway
                    // A checksum failure is the strongest signal that something (usually AV HTTPS
                    // inspection) is corrupting the encrypted stream, so switch to HTTP for those too,
                    // not just IOExceptions.
                    if ((ex.GetType() == typeof(IOException) || isChecksumFailure) && !packageUrl.StartsWith("http://"))
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Retrying download over HTTP...");
                        packageUrl = packageUrl.Replace("https://", "http://");
                    }
                }
            }
        }

        private void ExtractPackage(Package package, List<string>? files = null)
        {
            const string LOG_IDENT = "Bootstrapper::ExtractPackage";

            string? packageDir = AppData.PackageDirectoryMap.GetValueOrDefault(package.Name);

            if (packageDir is null)
            {
                // Standalone executables like RobloxPlayerInstaller.exe ship in the manifest but
                // are never extracted (there's nothing to unzip), so they legitimately have no
                // package-map entry. Don't cry WARNING about it in every user's logs — only flag
                // an archive that's genuinely missing a mapping.
                if (package.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    App.Logger.WriteLine(LOG_IDENT, $"{package.Name} is not an extractable package, skipping");
                else
                    App.Logger.WriteLine(LOG_IDENT, $"WARNING: {package.Name} was not found in the package map!");

                return;
            }

            string packageFolder = Path.Combine(_latestVersionDirectory, packageDir);
            string? fileFilter = null;

            // for sharpziplib, each file in the filter needs to be a regex
            if (files is not null)
            {
                var regexList = new List<string>();

                foreach (string file in files)
                    regexList.Add("^" + file.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + "$");

                fileFilter = String.Join(';', regexList);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Extracting {package.Name}...");

            var fastZip = new FastZip(_fastZipEvents);
            fastZip.RestoreDateTimeOnExtract = false;
            fastZip.RestoreAttributesOnExtract = false;

            fastZip.ExtractZip(package.DownloadPath, packageFolder, fileFilter);

            App.Logger.WriteLine(LOG_IDENT, $"Finished extracting {package.Name}");
        }
        #endregion
    }
}
