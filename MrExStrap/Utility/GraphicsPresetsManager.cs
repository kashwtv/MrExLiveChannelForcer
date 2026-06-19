namespace MrExStrap.Utility
{
    public static class GraphicsPresetsManager
    {
        public static readonly IReadOnlyDictionary<GraphicsPreset, IReadOnlyDictionary<string, string>> Presets =
            new Dictionary<GraphicsPreset, IReadOnlyDictionary<string, string>>
            {
                {
                    GraphicsPreset.Competitive, new Dictionary<string, string>
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
                        { "FFlagEnableNewLightAttenuation", "False" },
                        { "FFlagFastGPULightCulling3", "True" },
                        { "DFIntRenderLocalLightUpdatesMax", "1" },
                        { "DFIntRenderLocalLightUpdatesMin", "1" },
                        { "FFlagSimCSGv3", "True" },
                        { "FIntFRMMaxGrassDistance", "0" },
                        { "FIntFRMMinGrassDistance", "0" },
                        { "FFlagDebugSkyGray", "True" },
                        { "DFIntMaxActiveParticleCount", "0" },
                        { "DFIntMaxSoundsPerFrame", "2" },
                    }
                },
                {
                    GraphicsPreset.Balanced, new Dictionary<string, string>
                    {
                        { "DFIntTaskSchedulerTargetFps", "144" },
                        { "FFlagDisablePostFx", "False" },
                        { "DFIntDebugFRMQualityLevelOverride", "2" },
                        { "FFlagEnableBetterShadows", "False" },
                        { "DFIntRenderShadowIntensity", "0" },
                        { "FFlagDebugForceMSAASamples", "2" },
                        { "DFIntTextureQualityOverride", "1" },
                        { "DFIntMaxFrameBufferSize", "16" },
                        { "FFlagEnableNewLightAttenuation", "False" },
                        { "FFlagFastGPULightCulling3", "True" },
                        { "DFIntRenderLocalLightUpdatesMax", "16" },
                        { "FIntFRMMaxGrassDistance", "500" },
                        { "FIntFRMMinGrassDistance", "0" },
                        { "FFlagDebugSkyGray", "False" },
                        { "DFIntMaxActiveParticleCount", "256" },
                        { "DFIntMaxSoundsPerFrame", "8" },
                    }
                },
                {
                    GraphicsPreset.VisualQuality, new Dictionary<string, string>
                    {
                        { "DFIntTaskSchedulerTargetFps", "60" },
                        { "FFlagDisablePostFx", "False" },
                        { "DFIntDebugFRMQualityLevelOverride", "4" },
                        { "FFlagEnableBetterShadows", "True" },
                        { "DFIntRenderShadowIntensity", "2" },
                        { "FFlagDebugForceMSAASamples", "4" },
                        { "DFIntTextureQualityOverride", "3" },
                        { "DFIntMaxFrameBufferSize", "64" },
                        { "FFlagEnableNewLightAttenuation", "True" },
                        { "FFlagFastGPULightCulling3", "False" },
                        { "DFIntRenderLocalLightUpdatesMax", "64" },
                        { "FIntFRMMaxGrassDistance", "1000" },
                        { "FIntFRMMinGrassDistance", "100" },
                        { "FFlagDebugSkyGray", "False" },
                        { "DFIntMaxActiveParticleCount", "1024" },
                        { "DFIntMaxSoundsPerFrame", "16" },
                    }
                }
            };

        public static string GetDisplayName(GraphicsPreset preset)
        {
            return preset switch
            {
                GraphicsPreset.Default => "Default",
                GraphicsPreset.Competitive => "Competitive (Max FPS, Min Graphics)",
                GraphicsPreset.Balanced => "Balanced (144 FPS, Medium Graphics)",
                GraphicsPreset.VisualQuality => "Visual Quality (60 FPS, Max Graphics)",
                _ => preset.ToString()
            };
        }

        public static void ApplyPreset(GraphicsPreset preset)
        {
            if (preset == GraphicsPreset.Default)
            {
                App.FastFlags.Prop.Clear();
                return;
            }

            if (Presets.TryGetValue(preset, out var flags))
            {
                foreach (var kvp in flags)
                    App.FastFlags.SetValue(kvp.Key, kvp.Value);

                App.Logger.WriteLine("GraphicsPresetsManager", $"Applied graphics preset: {GetDisplayName(preset)}");
            }
        }
    }
}
