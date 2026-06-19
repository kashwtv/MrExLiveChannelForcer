using System.Windows;
using MrExStrap.Enums.FlagPresets;
using MrExStrap.Utility;

namespace MrExStrap.UI.ViewModels.Settings
{
    public class FastFlagsViewModel : NotifyPropertyChangedViewModel
    {
        private Dictionary<string, object>? _preResetFlags;

        // Raised when a reset swap happens so the page can rebuild this VM (so the preset
        // controls re-read App.FastFlags) and refresh the raw editor grid.
        public event EventHandler? RequestPageReloadEvent;

        public bool UseFastFlagManager
        {
            get => App.Settings.Prop.UseFastFlagManager;
            set => App.Settings.Prop.UseFastFlagManager = value;
        }

        public IReadOnlyDictionary<MSAAMode, string?> MSAALevels => FastFlagManager.MSAAModes;

        public MSAAMode SelectedMSAALevel
        {
            get => MSAALevels.FirstOrDefault(x => x.Value == App.FastFlags.GetPreset("Rendering.MSAA")).Key;
            set => App.FastFlags.SetPreset("Rendering.MSAA", MSAALevels[value]);
        }

        public bool FixDisplayScaling
        {
            get => App.FastFlags.GetPreset("Rendering.DisableScaling") == "True";
            set => App.FastFlags.SetPreset("Rendering.DisableScaling", value ? "True" : null);
        }

        public IReadOnlyDictionary<TextureQuality, string?> TextureQualities => FastFlagManager.TextureQualityLevels;

        public TextureQuality SelectedTextureQuality
        {
            get => TextureQualities.Where(x => x.Value == App.FastFlags.GetPreset("Rendering.TextureQuality.Level")).FirstOrDefault().Key;
            set
            {
                if (value == TextureQuality.Default)
                {
                    App.FastFlags.SetPreset("Rendering.TextureQuality", null);
                }
                else
                {
                    App.FastFlags.SetPreset("Rendering.TextureQuality.OverrideEnabled", "True");
                    App.FastFlags.SetPreset("Rendering.TextureQuality.Level", TextureQualities[value]);
                }
            }
        }
        public bool PerformanceModeEnabled
        {
            get => App.Settings.Prop.PerformanceModeEnabled;
            set
            {
                if (App.Settings.Prop.PerformanceModeEnabled == value)
                    return;

                App.Settings.Prop.PerformanceModeEnabled = value;
                OnPropertyChanged(nameof(PerformanceModeEnabled));

                if (value)
                    _ = FastFlagProfiles.InstallDarkTexturesAsync();
                else
                    FastFlagProfiles.RemoveDarkTextures();
            }
        }

        public List<string> GraphicsPresetOptions { get; } =
            Enum.GetValues<GraphicsPreset>()
                .Select(GraphicsPresetsManager.GetDisplayName)
                .ToList();

        public string SelectedGraphicsPreset
        {
            get => GraphicsPresetsManager.GetDisplayName(App.Settings.Prop.GraphicsPreset);
            set
            {
                var match = Enum.GetValues<GraphicsPreset>()
                    .FirstOrDefault(p => GraphicsPresetsManager.GetDisplayName(p) == value);

                App.Settings.Prop.GraphicsPreset = match;
                GraphicsPresetsManager.ApplyPreset(match);
                OnPropertyChanged(nameof(SelectedGraphicsPreset));
            }
        }

        public List<string> StretchResOptions { get; } =
            Enum.GetValues<StretchResolution>()
                .Select(StretchResApplier.GetDisplayName)
                .ToList();

        public string SelectedStretchRes
        {
            get => StretchResApplier.GetDisplayName(App.Settings.Prop.StretchResolution);
            set
            {
                var match = Enum.GetValues<StretchResolution>()
                    .FirstOrDefault(r => StretchResApplier.GetDisplayName(r) == value);

                App.Settings.Prop.StretchResolution = match;
                OnPropertyChanged(nameof(SelectedStretchRes));
                OnPropertyChanged(nameof(CustomStretchResVisibility));
            }
        }

        public Visibility CustomStretchResVisibility =>
            App.Settings.Prop.StretchResolution == StretchResolution.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

        public int StretchResCustomWidth
        {
            get => App.Settings.Prop.StretchResCustomWidth;
            set
            {
                if (value > 0)
                    App.Settings.Prop.StretchResCustomWidth = value;
            }
        }

        public int StretchResCustomHeight
        {
            get => App.Settings.Prop.StretchResCustomHeight;
            set
            {
                if (value > 0)
                    App.Settings.Prop.StretchResCustomHeight = value;
            }
        }

        public bool WindowAlwaysOnTop
        {
            get => App.Settings.Prop.WindowAlwaysOnTop;
            set => App.Settings.Prop.WindowAlwaysOnTop = value;
        }

        public List<string> FpsLimitOptions { get; } = new()
        {
            "Unlimited",
            "240 FPS",
            "144 FPS",
            "120 FPS",
            "60 FPS"
        };

        public string SelectedFpsLimit
        {
            get => App.Settings.Prop.FpsLimit switch
            {
                0 => "Unlimited",
                60 => "60 FPS",
                120 => "120 FPS",
                144 => "144 FPS",
                240 => "240 FPS",
                _ => "Unlimited"
            };
            set
            {
                int newLimit = value switch
                {
                    "Unlimited" => 0,
                    "60 FPS" => 60,
                    "120 FPS" => 120,
                    "144 FPS" => 144,
                    "240 FPS" => 240,
                    _ => 0
                };

                App.Settings.Prop.FpsLimit = newLimit;
                if (newLimit > 0)
                    App.FastFlags.SetValue("DFIntTaskSchedulerTargetFps", newLimit.ToString());
                else
                    App.FastFlags.SetValue("DFIntTaskSchedulerTargetFps", null);

                OnPropertyChanged(nameof(SelectedFpsLimit));
            }
        }

        public bool InputLagReducerEnabled
        {
            get => App.Settings.Prop.InputLagReducerEnabled;
            set
            {
                if (App.Settings.Prop.InputLagReducerEnabled == value)
                    return;

                App.Settings.Prop.InputLagReducerEnabled = value;
                OnPropertyChanged(nameof(InputLagReducerEnabled));

                if (value)
                {
                    App.FastFlags.SetValue("FFlagDebugCheckRenderThreading", "True");
                    App.FastFlags.SetValue("FFlagRenderDebugCheckThreading2", "True");
                    App.FastFlags.SetValue("DFIntMinimalNetClientSendRate", "36");
                    App.FastFlags.SetValue("DFIntConnectionMTUSize", "900");
                    App.FastFlags.SetValue("FFlagNetworkTransportSendWhenIdle2", "False");
                    App.FastFlags.SetValue("DFIntReplicatorLagReportThreshold", "9999");
                }
                else
                {
                    App.FastFlags.SetValue("FFlagDebugCheckRenderThreading", null);
                    App.FastFlags.SetValue("FFlagRenderDebugCheckThreading2", null);
                }
            }
        }

        public bool ResetConfiguration
        {
            get => _preResetFlags is not null;

            set
            {
                if (value)
                {
                    _preResetFlags = new(App.FastFlags.Prop);
                    App.FastFlags.Prop.Clear();
                }
                else
                {
                    App.FastFlags.Prop = _preResetFlags!;
                    _preResetFlags = null;
                }

                RequestPageReloadEvent?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
