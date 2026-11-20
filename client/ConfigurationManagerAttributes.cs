namespace EnvgFusion;

// Read by BepInEx's Configuration Manager (by name) to order settings and fold some under "Advanced settings".
#pragma warning disable 0649
internal sealed class ConfigurationManagerAttributes
{
    public bool? IsAdvanced;
    public int? Order;
}
#pragma warning restore 0649
