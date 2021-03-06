﻿using Meadow.CoverageReport.Debugging;
using Meadow.DebugAdapterServer;
using Meadow.DebugAdapterServer.DebuggerTransport;
using Meadow.JsonRpc.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Meadow.DebugAdapterServer
{

    public class SolidityDebugger : IDisposable
    {
        const string DEBUG_SESSION_ID = "DEBUG_SESSION_ID";
        const string DEBUG_STOP_ON_ENTRY = "DEBUG_STOP_ON_ENTRY";

        public static bool IsSolidityDebuggerAttached { get; set; }

        public static string SolidityDebugSessionID => Environment.GetEnvironmentVariable(DEBUG_SESSION_ID);

        public static bool DebugStopOnEntry => (Environment.GetEnvironmentVariable(DEBUG_STOP_ON_ENTRY) ?? string.Empty).Equals("true", StringComparison.OrdinalIgnoreCase);

        public static bool HasSolidityDebugAttachRequest => !string.IsNullOrWhiteSpace(SolidityDebugSessionID);

        public static IDisposable AttachSolidityDebugger(IDebuggerTransport debuggerTransport, CancellationTokenSource cancelToken = null, bool useContractsSubDir = true)
        {
            var debuggingInstance = new SolidityDebugger(debuggerTransport, useContractsSubDir);

            debuggingInstance.InitializeDebugConnection();
            IsSolidityDebuggerAttached = true;
            debuggingInstance.SetupRpcDebuggingHook();

            debuggingInstance.OnDebuggerDisconnect += () =>
            {
                // If the C# debugger is not attached, we don't care about running the rest of the tests
                // so exit program
                if (!Debugger.IsAttached)
                {
                    cancelToken?.Cancel();
                }
            };

            return debuggingInstance;
        }


        readonly MeadowSolidityDebugAdapter _debugAdapter;
        readonly IDebuggerTransport _debuggerTransport;

#pragma warning disable CA1710 // Identifiers should have correct suffix
        public event Action OnDebuggerDisconnect;
#pragma warning restore CA1710 // Identifiers should have correct suffix

        private SolidityDebugger(IDebuggerTransport debuggerTransport, bool useContractsSubDir)
        {
            _debuggerTransport = debuggerTransport;
            _debugAdapter = new MeadowSolidityDebugAdapter(useContractsSubDir);
            _debugAdapter.OnDebuggerDisconnect += DebugAdapter_OnDebuggerDisconnect;
            _debugAdapter.OnDebuggerDisconnect += TeardownRpcDebuggingHook;
        }

        public void InitializeDebugConnection()
        {
            // Connect IPC stream to debug adapter handler.
            _debugAdapter.InitializeStream(_debuggerTransport.InputStream, _debuggerTransport.OutputStream);

            // Starts the debug protocol dispatcher background thread.
            _debugAdapter.Protocol.Run();

            // Wait until the debug protocol handshake has completed.
            _debugAdapter.CompletedConfigurationDoneRequest.Task.Wait();

            _debugAdapter.Protocol.SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Breakpoint) { ThreadId = 1 });
        }

        public void SetupRpcDebuggingHook()
        {
            // Set our method to execute for our hook.
            JsonRpcClient.JsonRpcExecutionAnalysis = RpcExecutionCallback;
        }
        
        public void TeardownRpcDebuggingHook()
        {
            // Teardown our hook by setting the target as null.
            TeardownRpcDebuggingHook(null);
        }

        private void TeardownRpcDebuggingHook(MeadowSolidityDebugAdapter debugAdapter)
        {
            // Teardown our hook by setting the target as null.
            JsonRpcClient.JsonRpcExecutionAnalysis = null;
        }

        private void DebugAdapter_OnDebuggerDisconnect(MeadowSolidityDebugAdapter sender)
        {
            TeardownRpcDebuggingHook(sender);
            OnDebuggerDisconnect?.Invoke();
        }


        public async Task RpcExecutionCallback(IJsonRpcClient client, bool expectingException)
        {
            // Obtain an execution trace from our client.
            var executionTrace = await client.GetExecutionTrace();
            var executionTraceAnalysis = new ExecutionTraceAnalysis(executionTrace);

            // Process our execution trace in the debug adapter.
            await _debugAdapter.ProcessExecutionTraceAnalysis(client, executionTraceAnalysis, expectingException);

            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_debugAdapter.Protocol?.IsRunning == true)
            {
                // Cleanly close down debugging
                _debugAdapter.SendTerminateAndExit();
                _debugAdapter.Protocol.Stop(2000);
                _debugAdapter.Protocol.WaitForReader();
            }

            _debuggerTransport.Dispose();
        }
    }
}
