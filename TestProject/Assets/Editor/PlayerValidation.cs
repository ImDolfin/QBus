using UnityEditor;
using UnityEngine;

/// <summary>
/// Provides batch-mode entry points for configuring the Unity validation project.
/// </summary>
public static class PlayerValidation
{
    /// <summary>
    /// Configures the validation project to use the normal domain-reloading Play Mode lifecycle.
    /// </summary>
    public static void ConfigureDomainReload()
    {
        EditorSettings.enterPlayModeOptionsEnabled = false;
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Configures the validation project to retain the managed domain between Play sessions.
    /// </summary>
    public static void ConfigureNoDomainReload()
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Configures standalone validation builds for IL2CPP, high managed stripping, and .NET Standard.
    /// </summary>
    public static void ConfigureIl2Cpp()
    {
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.High);
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.runInBackground = true;
        AssetDatabase.SaveAssets();
        Debug.Log("QueuedEventBus player tests configured: IL2CPP, high managed stripping, .NET Standard.");
    }
}
