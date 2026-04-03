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

        /// <summary>Try to dispatch a command. Returns true if a handler was found and invoked.</summary>
        public async Task<bool> TryHandleAsync(string commandName, string id, string json, CancellationToken token)
        {
            if (_handlers.TryGetValue(commandName, out var handler))
            {
                await handler(id, json, token);
                return true;
            }
            return false;
        }

        /// <summary>Number of registered commands (for diagnostics).</summary>
        public int Count => _handlers.Count;
    }
}
