// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class TraceSerializerTests
{
    private static readonly EthereumJsonSerializer Json = new();

    [Test]
    public void can_deserialize_deep_graph()
    {
        List<ParityLikeTxTrace>? traces = Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance));
        Assert.That(traces?.Count, Is.EqualTo(36));
    }

    [Test]
    public void cant_deserialize_deep_graph()
    {
        Func<List<ParityLikeTxTrace>?> traces = () => Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance, 128));
        Assert.That(traces, Throws.TypeOf<JsonException>());
    }

    [Test]
    public void round_trips_reverted_frame_result()
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace trace = new()
        {
            Action = new ParityTraceAction
            {
                Type = "create",
                CallType = "create",
                Error = "Reverted",
                Result = new ParityTraceResult { GasUsed = 0x11, Output = [0xde, 0xad, 0xbe, 0xef] },
            },
        };

        ParityTraceAction action = serializer.Deserialize(serializer.Serialize([trace]))![0].Action!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(action.Error, Is.EqualTo("Reverted"));
            Assert.That(action.Result?.GasUsed, Is.EqualTo(0x11UL));
            Assert.That(action.Result?.Output, Is.EqualTo(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
            Assert.That(action.Result?.Address, Is.Null);
        }
    }

    [Test]
    public void round_trips_vm_trace()
    {
        ParityLikeTxTrace trace = CallTrace();
        trace.VmTrace = new ParityVmTrace
        {
            Code = [0x60, 0x00],
            Operations =
            [
                new ParityVmOperationTrace
                {
                    Cost = 3,
                    Pc = 0,
                    Used = 97,
                    Memory = new ParityMemoryChangeTrace { Offset = 1, Data = [0x01] },
                    Push = [[0x00, 0x02], [0x00], []],
                    Store = new ParityStorageChangeTrace { Key = [0x00], Value = [0x04] },
                    Sub = new ParityVmTrace { Code = [0x05], Operations = [new ParityVmOperationTrace { Cost = 2, Pc = 0, Used = 1, Push = [] }] },
                },
                new ParityVmOperationTrace { Cost = 1, Pc = 2, Halted = true },
                // A frame the tracer entered but never left keeps null operations.
                new ParityVmOperationTrace { Cost = 1, Pc = 3, Sub = new ParityVmTrace() },
            ],
        };

        AssertRoundTrips(trace);
    }

    [TestCase(typeof(ParityVmOperationTrace), """{"cost":0,"ex":{"mem":1,"push":null,"store":null,"used":0},"pc":0,"sub":null}""")]
    [TestCase(typeof(ParityVmOperationTrace), """{"cost":0,"ex":{"mem":null,"push":null,"store":1,"used":0},"pc":0,"sub":null}""")]
    [TestCase(typeof(ParityAccountStateChange), """{"balance":{"*":42},"code":"=","nonce":"=","storage":{}}""")]
    public void rejects_malformed_values(Type type, string json) =>
        Assert.That(() => JsonSerializer.Deserialize(json, type, EthereumJsonSerializer.JsonOptions), Throws.InstanceOf<JsonException>());

    [Test]
    public void round_trips_state_diff([ValueSource(nameof(AccountChanges))] ParityAccountStateChange change)
    {
        ParityLikeTxTrace trace = CallTrace();
        trace.StateChanges = new Dictionary<Address, ParityAccountStateChange> { [Address.Zero] = change };

        AssertRoundTrips(trace);
    }

    private static IEnumerable<ParityAccountStateChange> AccountChanges()
    {
        yield return new()
        {
            Balance = new ParityStateChange<UInt256?>(1, 2),
            Code = new ParityStateChange<byte[]>([0x00], [0x01]),
            // Slot 2 was set from zero, which the writer reports as a zero word; slot 3 is unchanged.
            Storage = new() { [1] = new([0x01], [0x02]), [2] = new(null, [0x05]), [3] = null!, [UInt256.MaxValue] = new([0x03], [0x04]) },
        };
        // Created and deleted: the writer derives the empty code, and marks a created account's storage as added.
        yield return new()
        {
            Balance = new ParityStateChange<UInt256?>(null, 1),
            Nonce = new ParityStateChange<UInt256?>(null, 1),
            Storage = new() { [1] = new(null, [0x02]) },
        };
        yield return new()
        {
            Balance = new ParityStateChange<UInt256?>(1, null),
            Nonce = new ParityStateChange<UInt256?>(1, null),
        };
        yield return new() { Storage = [] };
    }

    [Test]
    public void reads_vm_trace_at_max_call_depth_on_small_stack()
    {
        const int frames = VirtualMachineStatics.MaxCallDepth + 1;
        ParityVmTrace vmTrace = new() { Operations = [new ParityVmOperationTrace()] };
        for (int i = 1; i < frames; i++)
        {
            vmTrace = new ParityVmTrace { Operations = [new ParityVmOperationTrace { Sub = vmTrace }] };
        }

        ParityLikeTxTrace trace = CallTrace();
        trace.VmTrace = vmTrace;
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        // Only reading is under test, so the write gets a roomy stack.
        byte[] serialized = OnThread(16 * 1024 * 1024, () => serializer.Serialize([trace]));

        List<ParityLikeTxTrace>? restored = OnThread(1024 * 1024, () => serializer.Deserialize(serialized));

        int depth = 0;
        for (ParityVmTrace? frame = restored![0].VmTrace; frame is not null; frame = frame.Operations[0].Sub)
        {
            depth++;
        }

        Assert.That(depth, Is.EqualTo(frames));
    }

    private static T OnThread<T>(int maxStackSize, Func<T> func)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        Thread thread = new(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }, maxStackSize);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    private static ParityLikeTxTrace CallTrace() => new()
    {
        Output = [],
        Action = new ParityTraceAction
        {
            Type = "call",
            CallType = "call",
            From = Address.Zero,
            To = Address.Zero,
            Result = new ParityTraceResult { GasUsed = 1, Output = [] },
        },
    };

    // The stored form drops what the RPC form does, such as leading zeros, so compare what each would return.
    private static void AssertRoundTrips(ParityLikeTxTrace trace)
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        List<ParityLikeTxTrace>? restored = serializer.Deserialize(serializer.Serialize([trace]));
        Assert.That(Json.Serialize(restored), Is.EqualTo(Json.Serialize(new[] { trace })));
    }

    private List<ParityLikeTxTrace>? Deserialize(ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream($"{type.Assembly.GetName().Name}.xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = serializer.Deserialize(stream);
        return traces;
    }
}
