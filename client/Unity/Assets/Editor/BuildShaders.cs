using System.IO;
using UnityEditor;

// Unity.exe -batchmode -quit -projectPath Unity -executeMethod BuildShaders.Build
public static class BuildShaders
{
    const string Out = "../build/out";

    public static void Build()
    {
        Directory.CreateDirectory(Out);
        var build = new AssetBundleBuild
        {
            assetBundleName = "fpanofusion.bundle",
            assetNames = new[] { "Assets/Shaders/EnvgOutline.shader", "Assets/Shaders/EnvgWhitePhosphor.shader", "Assets/Shaders/EnvgBodyMask.shader", "Assets/Shaders/EnvgLensMask.shader", "Assets/Shaders/FpanoGlassSurface.shader", "Assets/Shaders/FpanoStrobeGlow.shader", "Assets/Shaders/FpanoDomeLit.shader" },
        };
        if (BuildPipeline.BuildAssetBundles(Out, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64) == null)
            throw new System.Exception("Build failed");
        UnityEngine.Debug.Log("SHADER_BUNDLE_SUCCESS");
    }
}
