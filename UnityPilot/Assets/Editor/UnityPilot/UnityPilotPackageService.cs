using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class PackageAddMessage     { public PackageAddPayload payload; }
    [Serializable] internal class PackageAddPayload     { public string packageName = ""; public string version = ""; }

    [Serializable] internal class PackageRemoveMessage  { public PackageRemovePayload payload; }
    [Serializable] internal class PackageRemovePayload  { public string packageName = ""; }

    [Serializable] internal class PackageSearchMessage  { public PackageSearchPayload payload; }
    [Serializable] internal class PackageSearchPayload  { public string query = ""; }

    [Serializable]
    internal class PackageInfoPayload
    {
        public string packageName;
        public string version;
        public string displayName;
        public string description;
        public string source;
    }

    [Serializable]
    internal class PackageAddResultPayload
    {
        public string packageName;
        public string version;
        public string status;
    }

    [Serializable]
    internal class PackageListResultPayload
    {
        public List<PackageInfoPayload> packages = new List<PackageInfoPayload>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotPackageService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotPackageService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("package.add",    HandleAddAsync);
            _bridge.Router.Register("package.remove", HandleRemoveAsync);
            _bridge.Router.Register("package.list",   HandleListAsync);
            _bridge.Router.Register("package.search", HandleSearchAsync);
        }

        // ── package.add ─────────────────────────────────────────────────────────

        private async Task HandleAddAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PackageAddMessage>(json);
            var p   = msg?.payload ?? new PackageAddPayload();

            if (string.IsNullOrEmpty(p.packageName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "packageName is required.", token, "package.add");
                return;
            }

            string identifier = string.IsNullOrEmpty(p.version) ? p.packageName : $"{p.packageName}@{p.version}";

            AddRequest request = null;
            var tcs = new TaskCompletionSource<bool>();

            _bridge.MainThreadQueue.Enqueue(() =>
            {
                request = Client.Add(identifier);
            });

            // Wait for the request to be created
            await Task.Delay(100, token);

            // Poll for completion
            while (request == null || !request.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }

            if (request.Status == StatusCode.Failure)
            {
                string errMsg = request.Error != null ? request.Error.message : "Unknown error";
                await _bridge.SendErrorAsync(id, "PACKAGE_ADD_FAILED", errMsg, token, "package.add");
                return;
            }

            var result = new PackageAddResultPayload
            {
                packageName = request.Result?.name ?? p.packageName,
                version     = request.Result?.version ?? p.version,
                status      = "ok",
            };
            await _bridge.SendResultAsync(id, "package.add", result, token);
        }

        // ── package.remove ──────────────────────────────────────────────────────

        private async Task HandleRemoveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PackageRemoveMessage>(json);
            var p   = msg?.payload ?? new PackageRemovePayload();

            if (string.IsNullOrEmpty(p.packageName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "packageName is required.", token, "package.remove");
                return;
            }

            RemoveRequest request = null;
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                request = Client.Remove(p.packageName);
            });

            await Task.Delay(100, token);

            while (request == null || !request.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }

            if (request.Status == StatusCode.Failure)
            {
                string errMsg = request.Error != null ? request.Error.message : "Unknown error";
                await _bridge.SendErrorAsync(id, "PACKAGE_REMOVE_FAILED", errMsg, token, "package.remove");
                return;
            }

            await _bridge.SendResultAsync(id, "package.remove", new GenericOkPayload { status = "ok" }, token);
        }

        // ── package.list ────────────────────────────────────────────────────────

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            ListRequest request = null;
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                request = Client.List(true); // include dependencies
            });

            await Task.Delay(100, token);

            while (request == null || !request.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }

            if (request.Status == StatusCode.Failure)
            {
                string errMsg = request.Error != null ? request.Error.message : "Unknown error";
                await _bridge.SendErrorAsync(id, "PACKAGE_LIST_FAILED", errMsg, token, "package.list");
                return;
            }

            var result = new PackageListResultPayload();
            foreach (var pkg in request.Result)
            {
                result.packages.Add(new PackageInfoPayload
                {
                    packageName = pkg.name,
                    version     = pkg.version,
                    displayName = pkg.displayName,
                    description = pkg.description ?? "",
                    source      = pkg.source.ToString(),
                });
            }

            await _bridge.SendResultAsync(id, "package.list", result, token);
        }

        // ── package.search ──────────────────────────────────────────────────────

        private async Task HandleSearchAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PackageSearchMessage>(json);
            var p   = msg?.payload ?? new PackageSearchPayload();

            if (string.IsNullOrEmpty(p.query))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "query is required.", token, "package.search");
                return;
            }

            SearchRequest request = null;
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                request = Client.SearchAll();
            });

            await Task.Delay(100, token);

            while (request == null || !request.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }

            if (request.Status == StatusCode.Failure)
            {
                string errMsg = request.Error != null ? request.Error.message : "Unknown error";
                await _bridge.SendErrorAsync(id, "PACKAGE_SEARCH_FAILED", errMsg, token, "package.search");
                return;
            }

            var result = new PackageListResultPayload();
            foreach (var pkg in request.Result)
            {
                result.packages.Add(new PackageInfoPayload
                {
                    packageName = pkg.name,
                    version     = pkg.version,
                    displayName = pkg.displayName,
                    description = pkg.description ?? "",
                    source      = pkg.source.ToString(),
                });
            }

            await _bridge.SendResultAsync(id, "package.search", result, token);
        }
    }
}
