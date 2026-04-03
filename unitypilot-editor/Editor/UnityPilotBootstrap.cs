// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;

namespace codingriver.unity.pilot
{
    [InitializeOnLoad]
    internal static class UnityPilotBootstrap
    {
        internal const string EnabledPrefKey = "codingriver.unity.pilot.BridgeEnabled";

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
