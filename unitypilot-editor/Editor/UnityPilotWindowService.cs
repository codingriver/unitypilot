// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    [Serializable]
    internal class EditorWindowInfo
    {
        public int instanceId;
        public string typeName;
        public string fullTypeName;
        public string title;
        public float posX;
        public float posY;
        public float width;
        public float height;
        public bool hasFocus;
        public bool docked;
        public bool hasUIToolkit;
    }

    [Serializable]
    internal class EditorWindowsListPayload
    {
        public List<EditorWindowInfo> windows = new();
        public int total;
    }

    [Serializable]
    internal class EditorWindowsListMessage
    {
        public string id;
        public string type;
        public string name;
        public EditorWindowsListFilterPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class EditorWindowsListFilterPayload
    {
        public string typeFilter;
        public string titleFilter;
    }

    internal sealed class UnityPilotWindowService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotWindowService(UnityPilotBridge bridge) => _bridge = bridge;

        public void RegisterCommands()
        {
            _bridge.Router.Register("editor.windows.list", HandleWindowsListAsync);
        }

        private async Task HandleWindowsListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowsListMessage>(json);
            var filter = msg?.payload ?? new EditorWindowsListFilterPayload();

            var tcs = new TaskCompletionSource<EditorWindowsListPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try { tcs.TrySetResult(ListWindows(filter)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "editor.windows.list", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"枚举窗口失败：{ex.Message}", token, "editor.windows.list");
            }
        }

        private static EditorWindowsListPayload ListWindows(EditorWindowsListFilterPayload filter)
        {
            var result = new EditorWindowsListPayload();
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            foreach (var w in windows)
            {
                var typeName = w.GetType().Name;
                var fullTypeName = w.GetType().FullName ?? typeName;
                var title = w.titleContent?.text ?? "";

                if (!string.IsNullOrEmpty(filter.typeFilter) &&
                    typeName.IndexOf(filter.typeFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    fullTypeName.IndexOf(filter.typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(filter.titleFilter) &&
                    title.IndexOf(filter.titleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var rect = w.position;
                result.windows.Add(new EditorWindowInfo
                {
                    instanceId = w.GetInstanceID(),
                    typeName = typeName,
                    fullTypeName = fullTypeName,
                    title = title,
                    posX = rect.x,
                    posY = rect.y,
                    width = rect.width,
                    height = rect.height,
                    hasFocus = w.hasFocus,
                    docked = w.docked,
                    hasUIToolkit = w.rootVisualElement != null,
                });
            }

            result.total = result.windows.Count;
            return result;
        }
    }
}
