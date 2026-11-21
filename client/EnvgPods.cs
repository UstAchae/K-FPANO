namespace EnvgFusion;

// The F-PANO has no pods that swing away: its thermal picture is shown whenever the device is down.
internal static class EnvgPods
{
    // (none either with no battery in the rails: the thermal off with the tubes)
    internal static float RightShown => EnvgFusion.EnvgbDown && EnvgFusion.Unpowered ? 0f : 1f;
}
