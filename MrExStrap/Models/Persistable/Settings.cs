using System.Collections.ObjectModel;

namespace MrExStrap.Models.Persistable
{
    public class Settings
    {
        // bloxstrap configuration
        public BootstrapperStyle BootstrapperStyle { get; set; } = BootstrapperStyle.FluentDialog;
        public BootstrapperIcon BootstrapperIcon { get; set; } = BootstrapperIcon.IconBloxstrap;
        public string BootstrapperTitle { get; set; } = App.ProjectName;
        public string BootstrapperIconCustomLocation { get; set; } = "";
        public Theme Theme { get; set; } = Theme.Default;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DeveloperMode { get; set; } = false;
        public bool CheckForUpdates { get; set; } = true;
        public bool ConfirmLaunches { get; set; } = false;
        public string Locale { get; set; } = "nil";
        public bool UseFastFlagManager { get; set; } = true;
        public bool WPFSoftwareRender { get; set; } = false;
        public bool EnableAnalytics { get; set; } = true;
        public bool BackgroundUpdatesEnabled { get; set; } = false;
        public bool DebugDisableVersionPackageCleanup { get; set; } = false;
        public string? SelectedCustomTheme { get; set; } = null;
        public WebEnvironment WebEnvironment { get; set; } = WebEnvironment.Production;

        // integration configuration
        public bool EnableActivityTracking { get; set; } = true;
        public bool UseDiscordRichPresence { get; set; } = true;
        public bool HideRPCButtons { get; set; } = true;
        public bool ShowAccountOnRichPresence { get; set; } = false;
        public bool ShowServerDetails { get; set; } = false;
        public ObservableCollection<CustomIntegration> CustomIntegrations { get; set; } = new();

        // mod preset configuration
        public bool UseDisableAppPatch { get; set; } = false;

        // version downgrade (MrExStrap fork feature)
        public bool UseCustomVersion { get; set; } = false;
        public string CustomVersionGuid { get; set; } = "";

        // Downgrade tab "Match your executor/exploit" list source. Default OFF = weao.xyz first,
        // robloxscripts.com mirror as fallback. ON = robloxscripts.com first (for users whose
        // network/ISP blocks weao.xyz, so they skip the dead attempt), weao.xyz as the fallback.
        // Either way both are tried before giving up. See WeaoClient.
        public bool PreferRobloxScriptsApi { get; set; } = false;

        // Versions Manager (v420.19+). Multiple named profiles each pointing at a
        // Roblox version hash. ActiveVersionProfileId picks which one applies on
        // launch — the legacy UseCustomVersion / CustomVersionGuid pair above
        // remains as a fallback only for users who never touched the new tab.
        public ObservableCollection<VersionProfile> VersionProfiles { get; set; } = new();
        public string ActiveVersionProfileId { get; set; } = "";

        // v420.22+: when ON, every Roblox launch through MrExBloxstrap pops a small
        // version-picker dialog right before the bootstrapper starts (and after the
        // VIP server picker, when that's also enabled). Saves the user a trip to
        // the Versions Manager tab when they just want to switch executor on a
        // single launch.
        public bool ShowVersionPickerOnLaunch { get; set; } = false;

        // v420.22+: companion toggle. When ON and the user picks (or has already
        // active) a profile pinned to a non-LIVE Roblox build, prompt for explicit
        // confirmation. LIVE-channel launches never prompt. Default ON because a
        // downgrade launch is a meaningful event.
        public bool ConfirmNonLiveLaunch { get; set; } = true;

        // post-launch "Channel: LIVE" toast (MrExStrap fork feature)
        public bool ShowLiveChannelToast { get; set; } = true;

        // privacy mode — truncate RobloxCookies.dat before every launch (MrExStrap fork feature)
        public bool EnablePrivacyMode { get; set; } = false;

        // v420.28+: Stream Mode hides Roblox-account info from Discord Rich Presence,
        // the place ID from the bootstrapper dialog, and rewrites the Roblox window
        // title to a generic "Roblox" string. For users who stream / record / share
        // their screen and don't want viewers to see account-identifying info.
        public bool EnableStreamMode { get; set; } = false;

        // v420.28+: persistent system tray launcher. When ON, MrExBloxstrap registers
        // itself to start with Windows and lives in the notification area with a
        // right-click menu for quick-launching the active profile or switching
        // profiles without opening the full settings UI.
        public bool EnableTrayLauncher { get; set; } = false;

        // v420.28+: opt-in Windows balloon-tip toasts.
        // NotifyOnLiveChange: pops a toast when Roblox's LIVE channel hash changes
        //   (polled on launcher open + every 30 min when the tray launcher is on).
        // NotifyOnExecutorUpdate: pops a toast when any tracked executor profile
        //   gets a new Roblox version on WEAO.
        // Both default off so the launcher stays quiet unless the user asks for it.
        public bool NotifyOnLiveChange { get; set; } = false;
        public bool NotifyOnExecutorUpdate { get; set; } = false;

        // v420.29.5+: pop a toast when a newer MrExBloxstrap release is available on
        // GitHub. Default ON so users always find out about updates even if they never
        // open the launch menu (e.g. tray-only users). Independent of the existing
        // menu-open "install now?" prompt — this is the passive heads-up.
        public bool NotifyOnAppUpdate { get; set; } = true;

        // multi-instance: hold ROBLOX_singletonMutex while clients run so they can start side
        // by side instead of closing each other (MrExStrap fork feature, see Utility.MultiInstance)
        public bool MultiInstanceEnabled { get; set; } = false;

        // Multi Instance tab: when on, account launches open to the Roblox home screen instead
        // of joining a place, so no Place ID is needed. Place ID / Job ID stay available for
        // when this is off. Default off (join a game, the original behavior).
        public bool MultiInstanceLaunchToHome { get; set; } = false;

        // v420.30.3+: Froststrap-style memory saver. When ON, the watcher closes Roblox's
        // RobloxCrashHandler.exe background process while the game runs, freeing the memory it
        // holds. Default off. (With this on we can't use the crash handler to detect crashes.)
        public bool CloseRobloxCrashHandler { get; set; } = false;

        // AltGen tab: the user's OWN BloxGen API key (https://bloxgen.net). Stored locally only —
        // we never ship a key. Each user supplies their own (signs up via the affiliate link on
        // the tab). Empty until entered.
        public string BloxGenApiKey { get; set; } = "";

        // auto-tile Roblox windows in a grid once they're visible (MrExStrap fork feature)
        public bool WindowTilingEnabled { get; set; } = false;
        public WindowTilingLayout WindowTilingLayout { get; set; } = WindowTilingLayout.Auto;

        // Multi Instance tab — bulk-launch preferences (not sensitive; the accounts themselves
        // live DPAPI-encrypted in Accounts.json, never here). MrExStrap fork feature.
        public string LastBulkPlaceId { get; set; } = "";
        public string LastBulkJobId { get; set; } = "";
        public int BulkLaunchDelaySeconds { get; set; } = 5;

        // Performance Mode — overrides all fast flags with a curated high-performance
        // preset and installs dark textures for visual clarity. Persists across updates.
        public bool PerformanceModeEnabled { get; set; } = false;

        // Stretch Resolution — resize Roblox window to a non-native aspect ratio after
        // launch for a wider FOV feel, popular in Rivals PvP.
        public StretchResolution StretchResolution { get; set; } = StretchResolution.Disabled;
        public int StretchResCustomWidth { get; set; } = 1440;
        public int StretchResCustomHeight { get; set; } = 1080;

        // Graphics preset — "Competitive", "Balanced", "VisualQuality"
        public GraphicsPreset GraphicsPreset { get; set; } = GraphicsPreset.Default;

        // Window always-on-top after launch
        public bool WindowAlwaysOnTop { get; set; } = false;

        // FPS cap — 60, 120, 144, 240, or unlimited
        public int FpsLimit { get; set; } = 0;

        // Input lag reducer — applies low-latency fast flags
        public bool InputLagReducerEnabled { get; set; } = false;

        // Streamer Mode — hides account username from launcher UI
        public bool StreamerModeEnabled { get; set; } = false;

        // Custom Game Profiles — saved configurations (version profile + stretch res + graphics preset)
        public Dictionary<string, GameLaunchProfile> CustomGameProfiles { get; set; } = new();

        // Hotkeys — global keybinds for quick actions
        public bool HotkeysEnabled { get; set; } = false;
        public string HotkeyTogglePerfMode { get; set; } = "Ctrl+Shift+P";
        public string HotkeyQuickLaunch { get; set; } = "Ctrl+Shift+L";
        public string HotkeyToggleStreamerMode { get; set; } = "Ctrl+Shift+S";

        // user-visible debug mode — reveals the Run health check button (MrExStrap fork feature)
        public bool DebugModeEnabled { get; set; } = false;

        // VIP server picker — pop a WebView2 dialog before player launches and offer a free
        // shared VIP server pulled from rbxservers.xyz. Off by default. (MrExStrap fork feature)
        public bool EnableVipServerPrompt { get; set; } = false;

        // BanAsync tab — trace cleaner + MAC/MachineGuid spoofer. (MrExStrap fork feature)
        public bool BanAsyncPreserveInGameSettings { get; set; } = true;
        public bool BanAsyncPreserveFastFlags { get; set; } = true;
        public bool BanAsyncIncludeStudioFolders { get; set; } = false;
        // Opt-in (default off): "Clean traces" also wipes MrExBloxstrap's downloaded Roblox
        // installs under Versions\. Destructive — forces a full re-download next launch.
        public bool BanAsyncCleanVersions { get; set; } = false;
        public bool BanAsyncClearBrowserCookies { get; set; } = false;
        // Off by default in v420.11+: the netsh adapter cycle already releases the old DHCP
        // lease, so the extra ipconfig /release+/renew tends to do nothing useful and can
        // interrupt VPNs, voice chat, or captive-portal sessions. Existing users keep their
        // saved value — only the initial default changed.
        public bool BanAsyncDhcpRefreshAfterSpoof { get; set; } = false;

        // Default on. Spoofed MACs are written to HKLM\SYSTEM\...\NetworkAddress which the
        // Windows driver reads at every load, so the change naturally outlives MrExBloxstrap
        // closing, being uninstalled, or the machine rebooting. Toggle off if you want
        // MrExBloxstrap to clear the registry override on its own exit (the MAC stays applied
        // for the current Windows session and reverts on next reboot).
        public bool BanAsyncPersistent { get; set; } = true;
        public bool BanAsyncAdvancedMode { get; set; } = false;
        public bool BanAsyncOuiMirror { get; set; } = true;
        public bool BanAsyncMachineGuidAcknowledged { get; set; } = false;
        public string BanAsyncOriginalMachineGuid { get; set; } = "";
        public ObservableCollection<string> BanAsyncSpoofedAdapterGuids { get; set; } = new();

        // Original (pre-spoof) MAC per adapter, keyed by adapter Id. Captured the first
        // time an adapter is spoofed so the UI can show the real hardware MAC next to the
        // current spoofed one, and cleared on revert.
        public Dictionary<string, string> BanAsyncOriginalMacByGuid { get; set; } = new();
    }
}
