using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace SnowFlyer2
{
    // The game UI only sends commands. All truck validation and camera writes
    // continue to run in the helper, through the same guarded follow functions.
    partial class Program
    {
        private const uint SpectatorProtocolMagic = 0x53504631;
        private const int SpectatorMaximumTargets = 64;
        private static readonly object SpectatorBridgeGate = new object();
        private static IReadOnlyList<FollowTargetInfo> SpectatorBridgeTargets =
            Array.Empty<FollowTargetInfo>();
        private static bool SpectatorBridgeScanning;
        private static string SpectatorBridgeMessage = "Select a truck and spectate.";

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientPid);

        private static void StartSpectatorBridge(Process game)
        {
            Task.Run(() => RunSpectatorBridge(game));
        }

        private static void RunSpectatorBridge(Process game)
        {
            string name = "SnowRunnerSpectator-" + game.Id;
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl,
                AccessControlType.Allow));
            while (!game.HasExited)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut,
                        1, PipeTransmissionMode.Byte, PipeOptions.None, 16384, 16384, security))
                    {
                        pipe.WaitForConnection();
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),
                            out uint peer) || peer != (uint)game.Id)
                            continue;
                        using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                        using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                        {
                            while (pipe.IsConnected && !game.HasExited)
                            {
                                uint magic = reader.ReadUInt32();
                                uint command = reader.ReadUInt32();
                                ulong owner = reader.ReadUInt64();
                                float value = reader.ReadSingle();
                                float value2 = reader.ReadSingle();
                                uint requestFlags = reader.ReadUInt32();
                                reader.ReadUInt32();
                                if (magic != SpectatorProtocolMagic || command > 9)
                                    break;
                                lock (CameraWriteGate)
                                {
                                    SpectatorNativeMenuVisible = (requestFlags & 1u) != 0;
                                    SpectatorNativeInputTimestamp = DateTime.UtcNow.Ticks;
                                    if (SpectatorNativeMenuVisible)
                                    {
                                        CameraOrbitPendingMouseX = 0;
                                        CameraOrbitPendingMouseY = 0;
                                    }
                                }
                                HandleSpectatorCommand(game, command, owner, value, value2);
                                WriteSpectatorState(writer);
                            }
                        }
                    }
                }
                catch (IOException) { /* The native UI disconnected; accept a new connection. */ }
                catch (Exception ex)
                {
                    Console.WriteLine("In-game UI bridge: " + ex.Message);
                    if (game.HasExited)
                        return;
                    System.Threading.Thread.Sleep(250);
                }
                finally
                {
                    lock (CameraWriteGate)
                    {
                        SpectatorNativeInputTimestamp = 0;
                        SpectatorNativeMenuVisible = true;
                        CameraOrbitPendingMouseX = 0;
                        CameraOrbitPendingMouseY = 0;
                    }
                }
            }
        }

        private static void HandleSpectatorCommand(Process game, uint command,
            ulong owner, float value, float value2)
        {
            switch (command)
            {
                case 0: return;
                case 1:
                    RefreshSpectatorBridgeTargets(game);
                    return;
                case 2:
                    FollowTargetInfo target;
                    lock (SpectatorBridgeGate)
                        target = SpectatorBridgeTargets.FirstOrDefault(
                            item => unchecked((ulong)item.OwnerAddress.ToInt64()) == owner);
                    string result;
                    if (target == null)
                        result = "Truck is no longer in the list. Refresh and select it again.";
                    else
                    {
                        SelectFollowTarget(target);
                        result = AttachSelectedTruck(game);
                    }
                    lock (SpectatorBridgeGate) SpectatorBridgeMessage = result;
                    return;
                case 3:
                    lock (CameraWriteGate)
                        StopCameraFollowLocked(CameraFollowGeneration);
                    lock (SpectatorBridgeGate) SpectatorBridgeMessage = "Free camera: follow stopped.";
                    return;
                case 4: SetFaceTruckEnabled(value != 0); return;
                case 5: return; // Legacy orbit toggle; gestures are inherent in follow.
                case 6:
                    if (!float.IsNaN(value) && !float.IsInfinity(value) &&
                        !float.IsNaN(value2) && !float.IsInfinity(value2))
                        SetCameraFollowFraming(value, value2);
                    return;
                case 7:
                    if (!float.IsNaN(value) && !float.IsInfinity(value))
                        SetCameraFollowSmoothing(value / 1000.0f);
                    return;
                case 8:
                    QueueCameraOrbitMouse(game, value, value2);
                    return;
                case 9:
                    string freeCameraResult = ToggleFreeCameraFromMenu(game);
                    lock (SpectatorBridgeGate) SpectatorBridgeMessage = freeCameraResult;
                    return;
            }
        }

        private static string ToggleFreeCameraFromMenu(Process game)
        {
            // Serialize the decision with follow writes and freecam patching. The
            // map watcher temporarily disables freecam and restores it on exit;
            // enabling it while that transition is pending would black out the map.
            lock (CameraWriteGate)
            lock (FreeCamTransitionGate)
            {
                if (MapOpenFromObservedKeys || MapFreeCamRestorePending)
                    return "Close the map before toggling freecamera.";

                if (IsFreeCamActive)
                {
                    bool wasFollowing = IsFollowingPlayer;
                    StopCameraFollowLocked(CameraFollowGeneration);
                    if (wasFollowing)
                        RecordCameraFollowStoppedLocked("Freecamera disabled from menu.");
                    return DisableFreeCam(game)
                        ? "Freecamera off: game camera restored."
                        : "Freecamera could not be disabled; follow has stopped.";
                }

                return EnableFreeCam(game)
                    ? "Freecamera on."
                    : "Freecamera could not be enabled.";
            }
        }

        private static void RefreshSpectatorBridgeTargets(Process game)
        {
            lock (SpectatorBridgeGate)
            {
                if (SpectatorBridgeScanning) return;
                SpectatorBridgeScanning = true;
                SpectatorBridgeMessage = "Scanning trucks on the loaded map...";
            }
            Task.Run(() =>
            {
                try
                {
                    var targets = ScanValidatedTruckTargets(game).Take(SpectatorMaximumTargets).ToArray();
                    lock (SpectatorBridgeGate)
                    {
                        SpectatorBridgeTargets = targets;
                        SpectatorBridgeMessage = targets.Length + " trucks available.";
                    }
                }
                catch (Exception ex)
                {
                    lock (SpectatorBridgeGate)
                        SpectatorBridgeMessage = "Truck scan failed: " + ex.Message;
                }
                finally
                {
                    lock (SpectatorBridgeGate) SpectatorBridgeScanning = false;
                }
            });
        }

        private static void WriteSpectatorState(BinaryWriter writer)
        {
            uint flags;
            ulong selected;
            string followStatus;
            lock (CameraWriteGate)
            {
                flags = (IsFollowingPlayer ? 1u : 0u) | (FaceTruckEnabled ? 2u : 0u) |
                    (IsFreeCamActive ? 8u : 0u);
                selected = unchecked((ulong)SelectedFollowOwner.ToInt64());
                followStatus = GetCameraFollowStatus();
            }
            IReadOnlyList<FollowTargetInfo> targets;
            string status;
            lock (SpectatorBridgeGate)
            {
                if (SpectatorBridgeScanning) flags |= 16u;
                targets = SpectatorBridgeTargets;
                status = SpectatorBridgeScanning ? SpectatorBridgeMessage :
                    string.IsNullOrEmpty(followStatus) ? SpectatorBridgeMessage :
                    followStatus + " | " + SpectatorBridgeMessage;
            }
            writer.Write(SpectatorProtocolMagic);
            writer.Write(flags);
            writer.Write(selected);
            WriteFixedUtf8(writer, status, 256);
            writer.Write((uint)targets.Count);
            foreach (FollowTargetInfo target in targets)
            {
                writer.Write(unchecked((ulong)target.OwnerAddress.ToInt64()));
                writer.Write(target.Position.X);
                writer.Write(target.Position.Y);
                writer.Write(target.Position.Z);
                writer.Write(target.IsActive ? 1u : 0u);
                WriteFixedUtf8(writer, target.AssetId, 128);
            }
            writer.Flush();
        }

        private static void WriteFixedUtf8(BinaryWriter writer, string text, int length)
        {
            byte[] output = new byte[length];
            Encoding.UTF8.GetEncoder().Convert((text ?? string.Empty).ToCharArray(), 0,
                (text ?? string.Empty).Length, output, 0, length - 1, true,
                out int usedChars, out int usedBytes, out bool completed);
            writer.Write(output);
        }
    }
}
