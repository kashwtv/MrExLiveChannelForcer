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
