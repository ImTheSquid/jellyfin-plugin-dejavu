using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using DejaVu.Config;

namespace DejaVu;

public class DejaVuPlugin : BasePlugin<PluginConfig>, IPlugin, IHasWebPages
{
    public DejaVuPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "DejaVu";

    public static DejaVuPlugin? Instance { get; private set; }

    public override Guid Id => Guid.Parse("7F12E12E-BC2D-4412-A5EC-543CC1F86D15");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new List<PluginPageInfo>
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Config.configPage.html"
            }
        };
    }
}
