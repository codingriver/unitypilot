// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace codingriver.unity.pilot
{
    /// <summary>
    /// Central command router: maps command names to async handler delegates.
    /// Handlers are registered by service modules during Bridge initialization.
    /// Automatically wraps every invocation with <see cref="OperationContext"/> for full lifecycle tracking.
    /// </summary>
    internal sealed class UnityPilotCommandRouter
    {
        public delegate Task CommandHandler(string id, string json, CancellationToken token);

        private readonly Dictionary<string, CommandHandler> _handlers = new();

        /// <summary>Register a handler for a command name. Overwrites if already registered.</summary>
        public void Register(string commandName, CommandHandler handler)
        {
            if (string.IsNullOrEmpty(commandName))
                throw new ArgumentNullException(nameof(commandName));
            _handlers[commandName] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Try to dispatch a command. Returns true if a handler was found and invoked.
        /// Automatically creates OperationContext, tracks lifecycle, and handles exceptions.
        /// </summary>
        public async Task<bool> TryHandleAsync(string commandName, string id, string json, CancellationToken token)
        {
            if (!_handlers.TryGetValue(commandName, out var handler))
                return false;

            var tracker = UnityPilotOperationTracker.Instance;
            var ctx = tracker.BeginOperation(id, commandName);

            try
            {
                await handler(id, json, token);

                if (ctx != null)
                {
                    // Only auto-complete if handler didn't already Complete/Fail
                    var entry = GetEntryForContext(id);
                    if (entry != null && !entry.CompletedAt.HasValue)
                        ctx.Complete();
                }
            }
            catch (OperationCanceledException)
            {
                ctx?.Fail("CANCELLED", "操作已取消");
                throw;
            }
            catch (Exception ex)
            {
                ctx?.Fail("EXCEPTION", ex.Message);
                throw;
            }
            finally
            {
                tracker.EndOperation(id);
            }

            return true;
        }

        /// <summary>Number of registered commands (for diagnostics).</summary>
        public int Count => _handlers.Count;

        private static OperationLogEntry GetEntryForContext(string commandId)
        {
            var entries = UnityPilotOperationTracker.Instance.GetEntriesCopy();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].CommandId == commandId)
                    return entries[i];
            }
            return null;
        }
    }
}
