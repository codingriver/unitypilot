// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;

namespace codingriver.unity.pilot
{
    internal sealed class UnityPilotCompileService
    {
        private readonly List<CompileErrorItemPayload> _lastErrors = new();
        private int _lastWarningCount;
        private string _lastRequestId = string.Empty;

        /// <summary>Active MCP compile.request id, or empty when compile was not started via MCP.</summary>
        public string LastRequestId => _lastRequestId;

        private TaskCompletionSource<bool> _compileTcs;
        private Action<string, CompilerMessage[]> _assemblyFinishedHandler;
        private Action<object> _compilationFinishedHandler;

        public bool IsCompiling { get; private set; }
        public long CompileStartedAt { get; private set; }
        public long CompileFinishedAt { get; private set; }
        public int LastErrorCount => _lastErrors.Count;

        /// <summary>
        /// Tries to begin a new compilation. Must be called from the main thread.
        /// Returns false (EDITOR_BUSY) if a compile is already in progress.
        /// </summary>
        public bool TryBeginCompile(string requestId)
        {
            if (IsCompiling || EditorApplication.isCompiling) return false;

            _lastRequestId = requestId;
            _lastErrors.Clear();
            _lastWarningCount = 0;

            IsCompiling = true;
            CompileStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            CompileFinishedAt = 0;

            _compileTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Collect messages as each assembly finishes
            _assemblyFinishedHandler = (_, messages) => OnAssemblyCompilationFinished(messages);
            CompilationPipeline.assemblyCompilationFinished += _assemblyFinishedHandler;

            // Know when ALL assemblies are done
            _compilationFinishedHandler = _ => OnCompilationFinished();
            CompilationPipeline.compilationFinished += _compilationFinishedHandler;

            // Refresh to detect newly added scripts before compiling
            AssetDatabase.Refresh();
            CompilationPipeline.RequestScriptCompilation();
            return true;
        }

        /// <summary>
        /// Returns a Task that completes when the current compilation finishes.
        /// If no compile is running, returns an already-completed Task.
        /// </summary>
        public Task WaitForCompileAsync() =>
            _compileTcs?.Task ?? Task.CompletedTask;

        // Called on main thread for each assembly as it finishes
        private void OnAssemblyCompilationFinished(CompilerMessage[] messages)
        {
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Warning)
                {
                    _lastWarningCount++;
                    continue;
                }
                if (msg.type != CompilerMessageType.Error) continue;

                _lastErrors.Add(new CompileErrorItemPayload
                {
                    file = msg.file,
                    line = msg.line,
                    column = msg.column,
                    message = msg.message,
                    severity = "error",
                });
            }
        }

        // Called on main thread when all assemblies are done
        private void OnCompilationFinished()
        {
            CompilationPipeline.assemblyCompilationFinished -= _assemblyFinishedHandler;
            CompilationPipeline.compilationFinished -= _compilationFinishedHandler;
            _assemblyFinishedHandler = null;
            _compilationFinishedHandler = null;

            IsCompiling = false;
            CompileFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _compileTcs?.TrySetResult(true);
        }

        public CompileStatusPayload BuildStartedStatusPayload(string requestId) =>
            new CompileStatusPayload
            {
                requestId = requestId,
                status = "started",
                errorCount = 0,
                warningCount = 0,
                startedAt = CompileStartedAt,
                finishedAt = 0,
            };

        public CompileStatusPayload BuildFinishedStatusPayload(string requestId) =>
            new CompileStatusPayload
            {
                requestId = requestId,
                status = "finished",
                errorCount = _lastErrors.Count,
                warningCount = _lastWarningCount,
                startedAt = CompileStartedAt,
                finishedAt = CompileFinishedAt,
            };

        public CompileErrorsPayload BuildCompileErrorsPayload(string requestId) =>
            new CompileErrorsPayload
            {
                requestId = requestId,
                total = _lastErrors.Count,
                errors = new List<CompileErrorItemPayload>(_lastErrors),
            };

        public CompileErrorsPayload BuildLastCompileErrorsPayload() =>
            BuildCompileErrorsPayload(_lastRequestId);
    }
}
