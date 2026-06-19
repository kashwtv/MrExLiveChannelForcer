using System.IO.Compression;

namespace MrExStrap.Utility
{
    // Per-Versions-Manager-profile fast flag storage. Each profile's flag set lives at
    // Paths.FastFlagProfiles\<profileId>.json, kept OUTSIDE Modifications\ so the launch
    // overlay copy never ships these into the Roblox install.
    //
    // At launch the ACTIVE profile's set is materialised into the canonical
    // Modifications\ClientSettings\ClientAppSettings.json that the existing overlay copy
    // applies — so the fragile Bootstrapper apply path stays exactly as it was.
    public static class FastFlagProfiles
    {
        private const string LOG_IDENT = "FastFlagProfiles";

        private const string DarkTextureReleaseUrl =
            "https://github.com/shourya-fx/Rivals-dark-texture/releases/download/texture/dark-textures.zip";

        public static readonly IReadOnlyDictionary<string, string> PerformanceModeFlags =
            new Dictionary<string, string>
            {
                { "DFIntTaskSchedulerTargetFps", "240" },
                { "FFlagDisablePostFx", "True" },
                { "DFIntDebugFRMQualityLevelOverride", "1" },
                { "FFlagEnableBetterShadows", "False" },
                { "DFIntRenderShadowIntensity", "0" },
                { "FFlagRenderInitShadowmaps", "False" },
                { "FFlagDebugForceMSAASamples", "-1" },
                { "DFIntTextureQualityOverride", "0" },
                { "DFIntMaxFrameBufferSize", "4" },
                { "FFlagEnableGPUSkinnedMeshes", "True" },
                { "DFIntRenderClampRoughnessMax", "-640000000" },
                { "FFlagEnableNewLightAttenuation", "False" },
                { "FFlagFastGPULightCulling3", "True" },
                { "DFIntRenderLocalLightUpdatesMax", "1" },
                { "DFIntRenderLocalLightUpdatesMin", "1" },
                { "DFIntCSGLevelOfDetailSwitchingDistance", "0" },
                { "DFIntCSGLevelOfDetailSwitchingDistanceL12", "0" },
                { "DFIntCSGLevelOfDetailSwitchingDistanceL23", "0" },
                { "DFIntCSGLevelOfDetailSwitchingDistanceL34", "0" },
                { "DFIntTerrainArraySliceSize", "4" },
                { "DFIntVisibilityCheckRayCastLimitPerFrame", "1" },
                { "FFlagTaskSchedulerLimitTargetFpsTo2402", "False" },
                { "DFIntTaskSchedulerBackgroundPriorityBase", "0" },
                { "DFIntTaskSchedulerBackgroundPriorityStep", "0" },
                { "FFlagSimAdaptiveTimestepGame", "True" },
                { "DFIntTimestepArbiterThresholdCFLThou", "300" },
                { "DFIntConnectionMTUSize", "900" },
                { "DFIntMaxDroppedPacketsToResend", "5" },
                { "DFIntMinimalNetClientSendRate", "36" },
                { "FFlagNetworkTransportSendWhenIdle2", "False" },
                { "DFIntRakNetResendBufferArrayLength", "128" },
                { "FFlagDebugDisableTelemetryEphemeralCounter", "True" },
                { "FFlagDebugDisableTelemetryEphemeralStat", "True" },
                { "FFlagDebugDisableTelemetryEventIngest", "True" },
                { "FFlagDebugDisableTelemetryPoint", "True" },
                { "FFlagDebugDisableTelemetryV2Counter", "True" },
                { "FFlagDebugDisableTelemetryV2Event", "True" },
                { "FFlagDebugDisableTelemetryV2Stat", "True" },
                { "FFlagEnableCEFLog", "False" },
                { "FFlagCrashOnThreadHang", "False" },
                { "FFlagVoiceChatUILogging", "False" },
                { "FFlagBrowserTrackerApiEnabled", "False" },
                { "FFlagSimCSGv3", "True" },
                { "DFIntS2PhysicsSenderRate", "30" },
                { "FFlagBatchAssetApi", "True" },
                { "DFIntBatchSizeGui", "32" },
                { "FFlagEnableQuickGameLaunch", "True" },
                { "FFlagPreloadTexturesPrefetchAll", "False" },
            };

        private static string CanonicalFile =>
            Path.Combine(Paths.Modifications, "ClientSettings", "ClientAppSettings.json");

        public static string PathFor(string profileId) =>
            Path.Combine(Paths.FastFlagProfiles, $"{profileId}.json");

        // First-run move of the old single global flag file onto the active (default LIVE)
        // profile so existing users keep their flags. No-op once any per-profile file exists.
        public static void MigrateGlobalIfNeeded()
        {
            try
            {
                Directory.CreateDirectory(Paths.FastFlagProfiles);

                if (Directory.EnumerateFiles(Paths.FastFlagProfiles, "*.json").Any())
                    return;

                if (!File.Exists(CanonicalFile))
                    return;

                string targetId = !string.IsNullOrEmpty(App.Settings.Prop.ActiveVersionProfileId)
                    ? App.Settings.Prop.ActiveVersionProfileId
                    : App.LiveBuiltInProfileId;

                File.Copy(CanonicalFile, PathFor(targetId), overwrite: false);
                App.Logger.WriteLine(LOG_IDENT, $"Migrated legacy global fast flags -> profile '{targetId}'.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::MigrateGlobalIfNeeded", ex);
            }
        }

        // Write the active profile's flags into the canonical file the launch overlay applies.
        // When the manager is off, or the active profile has no flag file, the canonical file
        // is emptied so nothing stale gets applied.
        public static void MaterializeActiveToCanonical()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CanonicalFile)!);

                if (!App.Settings.Prop.UseFastFlagManager)
                {
                    WriteEmpty();
                    return;
                }

                string activeId = App.Settings.Prop.ActiveVersionProfileId;
                string source = string.IsNullOrEmpty(activeId) ? "" : PathFor(activeId);

                Dictionary<string, object> merged;

                if (!string.IsNullOrEmpty(source) && File.Exists(source))
                {
                    string json = File.ReadAllText(source);
                    merged = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
                }
                else
                {
                    merged = new();
                }

                if (App.Settings.Prop.PerformanceModeEnabled)
                {
                    foreach (var kvp in PerformanceModeFlags)
                        merged[kvp.Key] = kvp.Value;

                    App.Logger.WriteLine(LOG_IDENT, "Performance Mode flags applied (overriding profile flags).");
                }

                File.WriteAllText(CanonicalFile, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));

                App.Logger.WriteLine(LOG_IDENT, $"Materialised fast flags for active profile '{activeId}'.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::MaterializeActiveToCanonical", ex);
            }
        }

        private static string DarkTexturesDir =>
            Path.Combine(Paths.Modifications, "PlatformContent", "pc", "textures");

        private static string DarkTexturesMarker =>
            Path.Combine(Paths.Modifications, ".dark-textures-installed");

        public static async Task InstallDarkTexturesAsync()
        {
            const string IDENT = LOG_IDENT + "::InstallDarkTextures";

            try
            {
                if (File.Exists(DarkTexturesMarker))
                {
                    App.Logger.WriteLine(IDENT, "Dark textures already installed, skipping.");
                    return;
                }

                App.Logger.WriteLine(IDENT, "Downloading dark textures...");

                using var response = await App.HttpClient.GetAsync(DarkTextureReleaseUrl);
                response.EnsureSuccessStatusCode();

                string tempZip = Path.Combine(Paths.Temp, "dark-textures.zip");
                Directory.CreateDirectory(Paths.Temp);

                await using (var fs = File.Create(tempZip))
                    await response.Content.CopyToAsync(fs);

                string destDir = DarkTexturesDir;
                Directory.CreateDirectory(destDir);

                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    const string zipPrefix = "Dark textures/";

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!entry.FullName.StartsWith(zipPrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string relativePath = entry.FullName.Substring(zipPrefix.Length);
                        if (string.IsNullOrEmpty(relativePath))
                            continue;

                        string destPath = Path.Combine(destDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

                        if (entry.FullName.EndsWith('/'))
                        {
                            Directory.CreateDirectory(destPath);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            entry.ExtractToFile(destPath, overwrite: true);
                        }
                    }
                }

                File.Delete(tempZip);

                await File.WriteAllTextAsync(DarkTexturesMarker, "installed");
                App.Logger.WriteLine(IDENT, "Dark textures installed successfully.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(IDENT, ex);
            }
        }

        public static void RemoveDarkTextures()
        {
            const string IDENT = LOG_IDENT + "::RemoveDarkTextures";

            try
            {
                if (File.Exists(DarkTexturesMarker))
                    File.Delete(DarkTexturesMarker);

                if (Directory.Exists(DarkTexturesDir))
                {
                    Directory.Delete(DarkTexturesDir, recursive: true);
                    App.Logger.WriteLine(IDENT, "Dark textures removed.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(IDENT, ex);
            }
        }

        private static void WriteEmpty() => File.WriteAllText(CanonicalFile, "{}");

        public static void Delete(string profileId)
        {
            try
            {
                string path = PathFor(profileId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    App.Logger.WriteLine(LOG_IDENT, $"Deleted fast flag file for profile '{profileId}'.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Delete", ex);
            }
        }
    }
}
