﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Client;
using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// This class is the public entry point for executing builds in msbuild server.
    /// It processes command-line arguments and invokes the build engine.
    /// </summary>
    public sealed class MSBuildClient
    {
        /// <summary>
        /// The build inherits all the environment variables from the client process.
        /// This property allows to add extra environment variables or reset some of the existing ones.
        /// </summary>
        private readonly Dictionary<string, string> _serverEnvironmentVariables;

        /// <summary>
        /// Full path to current MSBuild.exe if executable is MSBuild.exe,
        /// or to version of MSBuild.dll found to be associated with the current process.
        /// </summary>
        private readonly string _msbuildLocation;

        /// <summary>
        /// The MSBuild client execution result.
        /// </summary>
        private readonly MSBuildClientExitResult _exitResult;

        /// <summary>
        /// Whether MSBuild server finished the build.
        /// </summary>
        private bool _buildFinished = false;

        /// <summary>
        /// Handshake between server and client.
        /// </summary>
        private readonly ServerNodeHandshake _handshake;

        /// <summary>
        /// The named pipe name for client-server communication.
        /// </summary>
        private readonly string _pipeName;

        /// <summary>
        /// The named pipe stream for client-server communication.
        /// </summary>
        private readonly NamedPipeClientStream _nodeStream;

        /// <summary>
        /// A way to cache a byte array when writing out packets
        /// </summary>
        private readonly MemoryStream _packetMemoryStream;

        /// <summary>
        /// A binary writer to help write into <see cref="_packetMemoryStream"/>
        /// </summary>
        private readonly BinaryWriter _binaryWriter;

        /// <summary>
        /// Used to estimate the size of the build with an ETW trace.
        /// </summary>
        private int _numConsoleWritePackets;
        private long _sizeOfConsoleWritePackets;

        /// <summary>
        /// Public constructor with parameters.
        /// </summary>
        /// <param name="msbuildLocation"> Full path to current MSBuild.exe if executable is MSBuild.exe,
        /// or to version of MSBuild.dll found to be associated with the current process.</param>
        public MSBuildClient(string msbuildLocation)
        {
            _serverEnvironmentVariables = new();
            _exitResult = new();

            // dll & exe locations
            _msbuildLocation = msbuildLocation;

            // Client <-> Server communication stream
            _handshake = GetHandshake();
            _pipeName = OutOfProcServerNode.GetPipeName(_handshake);
            _nodeStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous
#if FEATURE_PIPEOPTIONS_CURRENTUSERONLY
                                                                         | PipeOptions.CurrentUserOnly
#endif
            );

            _packetMemoryStream = new MemoryStream();
            _binaryWriter = new BinaryWriter(_packetMemoryStream);
        }

        /// <summary>
        /// Orchestrates the execution of the build on the server,
        /// responsible for client-server communication.
        /// </summary>
        /// <param name="commandLine">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and
        /// is ignored.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A value of type <see cref="MSBuildClientExitResult"/> that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        public MSBuildClientExitResult Execute(string commandLine, CancellationToken cancellationToken)
        {
            CommunicationsUtilities.Trace("Executing build with command line '{0}'", commandLine);
            string serverRunningMutexName = OutOfProcServerNode.GetRunningServerMutexName(_handshake);
            string serverBusyMutexName = OutOfProcServerNode.GetBusyServerMutexName(_handshake);

            // Start server it if is not running.
            bool serverIsAlreadyRunning = ServerNamedMutex.WasOpen(serverRunningMutexName);
            if (!serverIsAlreadyRunning)
            {
                CommunicationsUtilities.Trace("Server was not running. Starting server now.");
                if (!TryLaunchServer())
                {
                    return _exitResult;
                }
            }

            // Check that server is not busy.
            var serverWasBusy = ServerNamedMutex.WasOpen(serverBusyMutexName);
            if (serverWasBusy)
            {
                CommunicationsUtilities.Trace("Server is busy, falling back to former behavior.");
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ServerBusy;
                return _exitResult;
            }

            // Connect to server.
            if (!TryConnectToServer(serverIsAlreadyRunning ? 1_000 : 20_000))
            {
                CommunicationsUtilities.Trace("Failure to connect to a server.");
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ConnectionError;
                return _exitResult;
            }

            // Send build command.
            // Let's send it outside the packet pump so that we easier and quicker deal with possible issues with connection to server.
            MSBuildEventSource.Log.MSBuildServerBuildStart(commandLine);
            if (!TrySendBuildCommand(commandLine))
            {
                CommunicationsUtilities.Trace("Failure to connect to a server.");
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ConnectionError;
                return _exitResult;
            }

            _numConsoleWritePackets = 0;
            _sizeOfConsoleWritePackets = 0;

            try
            {
                // Start packet pump
                using MSBuildClientPacketPump packetPump = new(_nodeStream);

                packetPump.RegisterPacketHandler(NodePacketType.ServerNodeConsoleWrite, ServerNodeConsoleWrite.FactoryForDeserialization, packetPump);
                packetPump.RegisterPacketHandler(NodePacketType.ServerNodeBuildResult, ServerNodeBuildResult.FactoryForDeserialization, packetPump);
                packetPump.Start();

                WaitHandle[] waitHandles =
                {
                    cancellationToken.WaitHandle,
                    packetPump.PacketPumpErrorEvent,
                    packetPump.PacketReceivedEvent
                };

                if (NativeMethodsShared.IsWindows)
                {
                    SupportVT100();
                }

                while (!_buildFinished)
                {
                    int index = WaitHandle.WaitAny(waitHandles);
                    switch (index)
                    {
                        case 0:
                            HandleCancellation();
                            // After the cancelation, we want to wait to server gracefuly finish the build.
                            // We have to replace the cancelation handle, because WaitAny would cause to repeatedly hit this branch of code.
                            waitHandles[0] = CancellationToken.None.WaitHandle;
                            break;

                        case 1:
                            HandlePacketPumpError(packetPump);
                            break;

                        case 2:
                            while (packetPump.ReceivedPacketsQueue.TryDequeue(out INodePacket? packet) &&
                                   !_buildFinished)
                            {
                                if (packet != null)
                                {
                                    HandlePacket(packet);
                                }
                            }

                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("MSBuild client error: problem during packet handling occurred: {0}.", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.Unexpected;
            }

            MSBuildEventSource.Log.MSBuildServerBuildStop(commandLine, _numConsoleWritePackets, _sizeOfConsoleWritePackets, _exitResult.MSBuildClientExitType.ToString(), _exitResult.MSBuildAppExitTypeString);
            CommunicationsUtilities.Trace("Build finished.");
            return _exitResult;
        }

        private void SupportVT100()
        {
            IntPtr stdOut = NativeMethodsShared.GetStdHandle(NativeMethodsShared.STD_OUTPUT_HANDLE);
            if (NativeMethodsShared.GetConsoleMode(stdOut, out uint consoleMode))
            {
                consoleMode |= NativeMethodsShared.ENABLE_VIRTUAL_TERMINAL_PROCESSING | NativeMethodsShared.DISABLE_NEWLINE_AUTO_RETURN;
                NativeMethodsShared.SetConsoleMode(stdOut, consoleMode);
            }
        }

        private bool TrySendPacket(Func<INodePacket> packetResolver)
        {
            INodePacket? packet = null;
            try
            {
                packet = packetResolver();
                WritePacket(_nodeStream, packet);
                CommunicationsUtilities.Trace($"Command packet of type '{packet.Type}' sent...");
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace($"Failed to send command packet of type '{packet?.Type.ToString() ?? "Unknown"}' to server: {0}", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ConnectionError;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Launches MSBuild server. 
        /// </summary>
        /// <returns> Whether MSBuild server was started successfully.</returns>
        private bool TryLaunchServer()
        {
            string serverLaunchMutexName = $@"Global\server-launch-{_handshake.ComputeHash()}";
            using var serverLaunchMutex = ServerNamedMutex.OpenOrCreateMutex(serverLaunchMutexName, out bool mutexCreatedNew);
            if (!mutexCreatedNew)
            {
                // Some other client process launching a server and setting a build request for it. Fallback to usual msbuild app build.
                CommunicationsUtilities.Trace("Another process launching the msbuild server, falling back to former behavior.");
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ServerBusy;
                return false;
            }

            string[] msBuildServerOptions = new string[] {
                "/nologo",
                "/nodemode:8"
            };

            string? useMSBuildServerEnvVarValue = Environment.GetEnvironmentVariable(Traits.UseMSBuildServerEnvVarName);
            try
            {
                // Disable MSBuild server for a child process, preventing an infinite recurson.
                Environment.SetEnvironmentVariable(Traits.UseMSBuildServerEnvVarName, "");

                NodeLauncher nodeLauncher = new NodeLauncher();
                CommunicationsUtilities.Trace("Starting Server...");
                Process msbuildProcess = nodeLauncher.Start(_msbuildLocation, string.Join(" ", msBuildServerOptions));
                CommunicationsUtilities.Trace("Server started with PID: {0}", msbuildProcess?.Id);
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("Failed to launch the msbuild server: {0}", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.LaunchError;
                return false;
            }
            finally
            {
                Environment.SetEnvironmentVariable(Traits.UseMSBuildServerEnvVarName, useMSBuildServerEnvVarValue);
            }

            return true;
        }

        private bool TrySendBuildCommand(string commandLine) => TrySendPacket(() => GetServerNodeBuildCommand(commandLine));

        private bool TrySendCancelCommand() => TrySendPacket(() => new ServerNodeBuildCancel());

        private ServerNodeBuildCommand GetServerNodeBuildCommand(string commandLine)
        {
            Dictionary<string, string> envVars = new();

            foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
            {
                envVars[(string)envVar.Key] = (envVar.Value as string) ?? string.Empty;
            }

            foreach (var pair in _serverEnvironmentVariables)
            {
                envVars[pair.Key] = pair.Value;
            }

            // We remove env variable used to invoke MSBuild server as that might be equal to 1, so we do not get an infinite recursion here. 
            envVars[Traits.UseMSBuildServerEnvVarName] = "0";

            return new ServerNodeBuildCommand(
                        commandLine,
                        startupDirectory: Directory.GetCurrentDirectory(),
                        buildProcessEnvironment: envVars,
                        CultureInfo.CurrentCulture,
                        CultureInfo.CurrentUICulture);
        }

        private ServerNodeHandshake GetHandshake()
        {
            return new ServerNodeHandshake(CommunicationsUtilities.GetHandshakeOptions(taskHost: false, architectureFlagToSet: XMakeAttributes.GetCurrentMSBuildArchitecture()));
        }

        /// <summary>
        /// Handle cancellation.
        /// </summary>
        private void HandleCancellation()
        {
            TrySendCancelCommand();

            CommunicationsUtilities.Trace("MSBuild client sent cancelation command.");
        }

        /// <summary>
        /// Handle packet pump error.
        /// </summary>
        private void HandlePacketPumpError(MSBuildClientPacketPump packetPump)
        {
            CommunicationsUtilities.Trace("MSBuild client error: packet pump unexpectedly shut down: {0}", packetPump.PacketPumpException);
            throw packetPump.PacketPumpException ?? new Exception("Packet pump unexpectedly shut down");
        }

        /// <summary>
        /// Dispatches the packet to the correct handler.
        /// </summary>
        private void HandlePacket(INodePacket packet)
        {
            switch (packet.Type)
            {
                case NodePacketType.ServerNodeConsoleWrite:
                    ServerNodeConsoleWrite writePacket = (packet as ServerNodeConsoleWrite)!;
                    HandleServerNodeConsoleWrite(writePacket);
                    _numConsoleWritePackets++;
                    _sizeOfConsoleWritePackets += writePacket.Text.Length;
                    break;
                case NodePacketType.ServerNodeBuildResult:
                    HandleServerNodeBuildResult((ServerNodeBuildResult)packet);
                    break;
                default: throw new InvalidOperationException($"Unexpected packet type {packet.GetType().Name}");
            }
        }

        private void HandleServerNodeConsoleWrite(ServerNodeConsoleWrite consoleWrite)
        {
            switch (consoleWrite.OutputType)
            {
                case ConsoleOutput.Standard:
                    Console.Write(consoleWrite.Text);
                    break;
                case ConsoleOutput.Error:
                    Console.Error.Write(consoleWrite.Text);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected console output type {consoleWrite.OutputType}");
            }
        }

        private void HandleServerNodeBuildResult(ServerNodeBuildResult response)
        {
            CommunicationsUtilities.Trace("Build response received: exit code {0}, exit type '{1}'", response.ExitCode, response.ExitType);
            _exitResult.MSBuildClientExitType = MSBuildClientExitType.Success;
            _exitResult.MSBuildAppExitTypeString = response.ExitType;
            _buildFinished = true;
        }

        /// <summary>
        /// Connects to MSBuild server.
        /// </summary>
        /// <returns> Whether the client connected to MSBuild server successfully.</returns>
        private bool TryConnectToServer(int timeout)
        {
            try
            {
                _nodeStream.Connect(timeout);

                int[] handshakeComponents = _handshake.RetrieveHandshakeComponents();
                for (int i = 0; i < handshakeComponents.Length; i++)
                {
                    CommunicationsUtilities.Trace("Writing handshake part {0} ({1}) to pipe {2}", i, handshakeComponents[i], _pipeName);
                    _nodeStream.WriteIntForHandshake(handshakeComponents[i]);
                }

                // This indicates that we have finished all the parts of our handshake; hopefully the endpoint has as well.
                _nodeStream.WriteEndOfHandshakeSignal();

                CommunicationsUtilities.Trace("Reading handshake from pipe {0}", _pipeName);

#if NETCOREAPP2_1_OR_GREATER || MONO
                _nodeStream.ReadEndOfHandshakeSignal(false, 1000); 
#else
                _nodeStream.ReadEndOfHandshakeSignal(false);
#endif

                CommunicationsUtilities.Trace("Successfully connected to pipe {0}...!", _pipeName);
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("Failed to connect to server: {0}", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ConnectionError;
                return false;
            }

            return true;
        }

        private void WritePacket(Stream nodeStream, INodePacket packet)
        {
            MemoryStream memoryStream = _packetMemoryStream;
            memoryStream.SetLength(0);

            ITranslator writeTranslator = BinaryTranslator.GetWriteTranslator(memoryStream);

            // Write header
            memoryStream.WriteByte((byte)packet.Type);

            // Pad for packet length
            _binaryWriter.Write(0);

            // Reset the position in the write buffer.
            packet.Translate(writeTranslator);

            int packetStreamLength = (int)memoryStream.Position;

            // Now write in the actual packet length
            memoryStream.Position = 1;
            _binaryWriter.Write(packetStreamLength - 5);

            nodeStream.Write(memoryStream.GetBuffer(), 0, packetStreamLength);
        }
    }
}