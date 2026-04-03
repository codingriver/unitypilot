using UnityEditor;

namespace SkillEditor.Editor.UnityPilot
{
    [InitializeOnLoad]
    internal static class UnityPilotBootstrap
    {
        internal const string EnabledPrefKey = "SkillEditor.UnityPilot.Enabled";

        internal static bool IsEnabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        static UnityPilotBootstrap()
        {
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode && IsEnabled)
                    UnityPilotBridge.Instance.EnsureStarted();
            };

            EditorApplication.quitting += () => UnityPilotBridge.Instance.Stop();
        }
    }
}
