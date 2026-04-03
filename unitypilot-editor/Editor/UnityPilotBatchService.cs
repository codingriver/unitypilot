using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable]
    internal class BatchOperationItem
    {
        public string tool   = "";
        public string @params = ""; // JSON string of tool-specific parameters
    }

    [Serializable] internal class BatchExecuteMessage  { public BatchExecutePayload payload; }
    [Serializable]
    internal class BatchExecutePayload
    {
        public List<BatchOperationItem> operations = new();
        public string mode        = "sequential"; // sequential or parallel
        public bool   stopOnError = true;
    }

    [Serializable] internal class BatchCancelMessage  { public BatchCancelPayload payload; }
    [Serializable] internal class BatchCancelPayload  { public string batchId = ""; }

    [Serializable] internal class BatchResultsMessage { public BatchResultsPayload payload; }
    [Serializable] internal class BatchResultsPayload { public string batchId = ""; }

    [Serializable]
    internal class BatchOperationResultItem
    {
        public int    index;
        public string tool;
        public string status; // completed, failed, skipped
        public string result; // JSON result or null
        public string error;  // error message or null
    }

    [Serializable]
    internal class BatchExecuteResultPayload
    {
        public string batchId;
        public string status;    // running, completed, failed, cancelled
        public int    total;
        public int    completed;
        public int    failed;
        public List<BatchOperationResultItem> results = new();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotBatchService
    {
        private readonly UnityPilotBridge _bridge;
        private readonly ConcurrentDictionary<string, BatchExecuteResultPayload> _batches = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _batchCts = new();

        private const int MaxOperations  = 100;
        private const int TotalTimeoutMs = 60000;

        public UnityPilotBatchService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("batch.execute", HandleExecuteAsync);
            _bridge.Router.Register("batch.cancel",  HandleCancelAsync);
            _bridge.Router.Register("batch.results", HandleResultsAsync);
        }

        // ── batch.execute ───────────────────────────────────────────────────────

        private async Task HandleExecuteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<BatchExecuteMessage>(json);
            var p   = msg?.payload ?? new BatchExecutePayload();

            if (p.operations == null || p.operations.Count == 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "operations list is required and cannot be empty.", token, "batch.execute");
                return;
            }

            if (p.operations.Count > MaxOperations)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", $"Maximum {MaxOperations} operations allowed.", token, "batch.execute");
                return;
            }

            string batchId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TotalTimeoutMs);
            _batchCts[batchId] = cts;

            var batchResult = new BatchExecuteResultPayload
            {
                batchId   = batchId,
                status    = "running",
                total     = p.operations.Count,
                completed = 0,
                failed    = 0,
            };
            _batches[batchId] = batchResult;

            string mode = (p.mode ?? "sequential").ToLowerInvariant();

            try
            {
                if (mode == "parallel")
                {
                    await ExecuteParallelAsync(p, batchResult, cts.Token);
                }
                else
                {
                    await ExecuteSequentialAsync(p, batchResult, cts.Token);
                }

                batchResult.status = batchResult.failed > 0 ? "failed" : "completed";
            }
            catch (OperationCanceledException)
            {
                batchResult.status = "cancelled";
                // Mark remaining ops as skipped
                for (int i = batchResult.results.Count; i < p.operations.Count; i++)
                {
                    batchResult.results.Add(new BatchOperationResultItem
                    {
                        index  = i,
                        tool   = p.operations[i].tool,
                        status = "skipped",
                        error  = "Batch was cancelled or timed out.",
                    });
                }
            }
            catch (Exception ex)
            {
                batchResult.status = "failed";
                _bridge.AddLog("error", $"Batch {batchId} error: {ex.Message}");
            }
            finally
            {
                _batchCts.TryRemove(batchId, out _);
            }

            _batches[batchId] = batchResult;
            await _bridge.SendResultAsync(id, "batch.execute", batchResult, token);
        }

        private async Task ExecuteSequentialAsync(BatchExecutePayload p, BatchExecuteResultPayload batchResult, CancellationToken ct)
        {
            for (int i = 0; i < p.operations.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var op = p.operations[i];
                var opResult = await ExecuteSingleOperationAsync(i, op, ct);
                batchResult.results.Add(opResult);

                if (opResult.status == "completed") batchResult.completed++;
                else batchResult.failed++;

                if (opResult.status == "failed" && p.stopOnError)
                {
                    // Skip remaining
                    for (int j = i + 1; j < p.operations.Count; j++)
                    {
                        batchResult.results.Add(new BatchOperationResultItem
                        {
                            index  = j,
                            tool   = p.operations[j].tool,
                            status = "skipped",
                            error  = "Skipped due to stopOnError.",
                        });
                    }
                    break;
                }
            }
        }

        private async Task ExecuteParallelAsync(BatchExecutePayload p, BatchExecuteResultPayload batchResult, CancellationToken ct)
        {
            var tasks = new Task<BatchOperationResultItem>[p.operations.Count];
            for (int i = 0; i < p.operations.Count; i++)
            {
                tasks[i] = ExecuteSingleOperationAsync(i, p.operations[i], ct);
            }

            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                batchResult.results.Add(r);
                if (r.status == "completed") batchResult.completed++;
                else batchResult.failed++;
            }
        }

        private async Task<BatchOperationResultItem> ExecuteSingleOperationAsync(int index, BatchOperationItem op, CancellationToken ct)
        {
            var result = new BatchOperationResultItem
            {
                index = index,
                tool  = op.tool,
            };

            try
            {
                if (string.IsNullOrEmpty(op.tool))
                {
                    result.status = "failed";
                    result.error  = "tool is required in operation.";
                    return result;
                }

                // Build a fake envelope JSON to route through the command router
                // The router expects (id, json, token) where json is the full message envelope
                // We wrap the params as payload in a message envelope
                string wrappedJson = string.IsNullOrEmpty(op.@params)
                    ? "{\"payload\":{}}"
                    : $"{{\"payload\":{op.@params}}}";

                string opId = $"batch-{index}";
                bool handled = await _bridge.Router.TryHandleAsync(op.tool, opId, wrappedJson, ct);

                if (!handled)
                {
                    result.status = "failed";
                    result.error  = $"Unknown command: {op.tool}";
                }
                else
                {
                    result.status = "completed";
                    result.result = "ok";
                }
            }
            catch (Exception ex)
            {
                result.status = "failed";
                result.error  = ex.Message;
            }

            return result;
        }

        // ── batch.cancel ────────────────────────────────────────────────────────

        private async Task HandleCancelAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<BatchCancelMessage>(json);
            var p   = msg?.payload ?? new BatchCancelPayload();

            if (string.IsNullOrEmpty(p.batchId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "batchId is required.", token, "batch.cancel");
                return;
            }

            if (_batchCts.TryRemove(p.batchId, out var cts))
            {
                cts.Cancel();
                await _bridge.SendResultAsync(id, "batch.cancel", new GenericOkPayload(), token);
            }
            else
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Batch not found or already completed: {p.batchId}", token, "batch.cancel");
            }
        }

        // ── batch.results ───────────────────────────────────────────────────────

        private async Task HandleResultsAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<BatchResultsMessage>(json);
            var p   = msg?.payload ?? new BatchResultsPayload();

            if (string.IsNullOrEmpty(p.batchId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "batchId is required.", token, "batch.results");
                return;
            }

            if (_batches.TryGetValue(p.batchId, out var batchResult))
            {
                await _bridge.SendResultAsync(id, "batch.results", batchResult, token);
            }
            else
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Batch not found: {p.batchId}", token, "batch.results");
            }
        }
    }
}
