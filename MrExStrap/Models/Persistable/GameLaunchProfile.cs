namespace MrExStrap.Models.Persistable
{
    public class GameLaunchProfile
    {
        public string Name { get; set; } = "";
        public string VersionProfileId { get; set; } = "";
        public StretchResolution StretchResolution { get; set; } = StretchResolution.Disabled;
        public int StretchResCustomWidth { get; set; } = 1440;
        public int StretchResCustomHeight { get; set; } = 1080;
        public GraphicsPreset GraphicsPreset { get; set; } = GraphicsPreset.Default;
        public bool PerformanceModeEnabled { get; set; } = false;
        public bool WindowAlwaysOnTop { get; set; } = false;
        public int FpsLimit { get; set; } = 0;
        public bool InputLagReducerEnabled { get; set; } = false;
    }
}
