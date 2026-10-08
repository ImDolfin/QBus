using UnityEditor;

namespace QueuedEventBus.Editor
{
    [InitializeOnLoad]
    internal static class EventBusEditorLifecycle
    {
        static EventBusEditorLifecycle()
        {
            // Remove first so reinitialization never registers the same teardown twice.
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            AssemblyReloadEvents.beforeAssemblyReload -= Events.Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Events.Shutdown;

            EditorApplication.quitting -= Events.Shutdown;
            EditorApplication.quitting += Events.Shutdown;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                // Both transitions matter when a listener requests stopping Play mode mid-pump.
                Events.Shutdown();
            }
        }
    }
}
