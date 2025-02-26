using MediaBrowser.Model.Plugins;

namespace DejaVu.Config;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfig : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfig"/> class.
    /// </summary>
    public PluginConfig()
    {
        MaxSkipSecs = 60;
    }

    /// <summary>
    /// Gets or sets the maximum number of seconds to start DejaVu.
    /// </summary>
    public int MaxSkipSecs { get; set; }
}
