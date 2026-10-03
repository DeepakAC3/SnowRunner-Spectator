using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ConsoleHotKey;
using System.Windows.Forms;
using Reloaded.Memory.Sigscan;
using Reloaded.Memory.Sources;
using System.Threading.Tasks;
using System.Numerics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SnowFlyer2
{

    class Program
    {
        /// <summary>
        /// Original code
        /// 
        /// Devmode checks:
        /// 7FF6B945000A - 80 B8 C8000000 00     - cmp byte ptr [rax+000000C8],00
        /// dev toggle also accessed via the following, but this isn't required for us here
        /// SnowRunner.exe+860CBF - 80 B8 C9000000 00     - cmp byte ptr [rax+000000C9],00
        /// 
        /// Freecam checks:
        /// SnowRunner.exe+860EA0 - 83 3D CD3E5C02 00     - cmp dword ptr [SnowRunner.exe+2E24D74],00
        /// SnowRunner.exe+88E9A6 - 83 3D C7635902 00     - cmp dword ptr [SnowRunner.exe+2E24D74],00
        /// 
        /// The original code checks a dev flag to enable free camera, 
        /// and checks a second flag to determine which camera should be used. 
        /// We flip those checks to toggle free camera at will
        /// A pattern search is used to hopefully make this compatible with future patches
        /// </summary>


        private static readonly string DevCheckPattern = "80 B8 C8 00 00 00 00 75"; //cmp byte ptr [rax+000000C8],00   75 ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] DevCheckPatch = { 0x80, 0xB8, 0xC8, 0x00, 0x00, 0x00, 0X01 }; //cmp byte ptr [rax+000000C8],01

        private static readonly string DevCheckRevertPattern = "80 B8 C8 00 00 00 01 75"; //cmp byte ptr [rax+000000C8],01   75 ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] DevCheckRevertPatch = { 0x80, 0xB8, 0xC8, 0x00, 0x00, 0x00, 0X00 }; //cmp byte ptr [rax+000000C8],00


        private static readonly string DevCheckPatternB = "80 B8 C8 00 00 00 00 0F 85"; //cmp byte ptr [rax+000000C8],00   0F 85 ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] DevCheckPatchB = { 0x80, 0xB8, 0xC8, 0x00, 0x00, 0x00, 0X01 }; //cmp byte ptr [rax+000000C8],01

        private static readonly string DevCheckRevertPatternB = "80 B8 C8 00 00 00 01 0F 85"; //cmp byte ptr [rax+000000C8],01   0F 85 ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] DevCheckRevertPatchB = { 0x80, 0xB8, 0xC8, 0x00, 0x00, 0x00, 0X00 }; //cmp byte ptr [rax+000000C8],00

        // To find the fly mode offset after patches, use Cheat Engine in a mod map, toggling the free camera button and scanning for a 4 byte boolean.
        // There will be two - one is the highlight state of the free camera button, the other is the actual freecam state.
        // NB- this mod can't be tested on mod maps, since those already have devmode flag set they will become unable to use freecam when we swizzle the devmode check
        private static readonly int FlyModeFlagOffset = 0x2A5412C;
        private static readonly byte[] FlyModeOnPatch = { 0x01 };
        private static readonly byte[] FlyModeRevertPatch = { 0x00 };

        // The following are intended to patch the flymode *check* rather than change the value, which would be more resilient to game patches.
        // Not yet working as intended, and the fly mode flag address is trivial to find again so this isn't a priority to fix
        private static readonly string FlyModeCheckPatternA = "83 3D C7 63 59 02 00 48"; //cmp dword ptr[SnowRunner.exe + 2E24D74],00  48 ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] FlyModeCheckPatchA = { 0x83, 0x3D, 0xC7, 0x63, 0x59, 0x02, 0X01 }; //cmp dword ptr[SnowRunner.exe + 2E24D74],01

        private static readonly string FlyModeCheckRevertPatternA = "83 3D C7 63 59 02 01 48"; //cmp dword ptr[SnowRunner.exe + 2E24D74],01  48 ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] FlyModeCheckRevertPatchA = { 0x83, 0x3D, 0xC7, 0x63, 0x59, 0x02, 0X00 }; //cmp dword ptr[SnowRunner.exe + 2E24D74],00

        private static readonly string FlyModeCheckPatternB = "83 3D CD 3E 5C 02 00 0F"; //cmp dword ptr [SnowRunner.exe+2E24D74],00    0F    ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] FlyModeCheckPatchB = { 0x83, 0x3D, 0xCD, 0x3E, 0x5C, 0x02, 0X01 }; //cmp dword ptr[SnowRunner.exe + 2E24D74],01

        private static readonly byte[] FlyModeCheckRevertPatchB = { 0x83, 0x3D, 0xCD, 0x3E, 0x5C, 0x02, 0X00 }; //cmp dword ptr[SnowRunner.exe + 2E24D74],00
        private static readonly string FlyModeCheckRevertPatternB = "83 3D CD 3E 5C 02 01 0F"; //cmp dword ptr [SnowRunner.exe+2E24D74],01    0F    ON NEXT BYTE FOR UNIQUENESS

        // Time Of Day    
        private static readonly string TODTickDisablePattern = "F3 41 0F 11 95 50 01 00 00"; //movss[r13 + 00000150],xmm2   optional F3 0F ON NEXT BYTE FOR UNIQUENESS
        private static readonly byte[] TODTickDisablePatch = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }; // 9 NOPS, prevent timer from advancing

        private static readonly string TODTickRevertPattern = "90 90 90 90 90 90 90 90 90 F3 0F 10 05"; //our NOPS, plus next instruction since there are definitely dupes of NOP 
        private static readonly byte[] TODTickRevertPatch = { 0xF3, 0x41, 0x0F, 0x11, 0x95, 0x50, 0x01, 0x00, 0x00 }; //original ticker code

        // To find the TOD pointer - use Cheat Engine with above TODTickDisablePattern, which should be editing one address (r13+ 000000150).
        // Memory view-> search for AOB, go to same address in the ASM section at top, ->'find out what addresses this instruction accesses'
        // Add it to addresslist, ->generate pointermap for it with offsets ending in 150 as below
        // ->'Pointerscan for this address' using the pointermap. Should be 3 top-level pointers available.
        // Can do this twice and 'compare results with other saved pointermaps', but so far the 3 top level ones are always valid and there's no need.
        private static readonly int TODOffset = 0x150;
        private static readonly int TODPointer = 0x2A8EB48; 

        // Player location
        private static string PatternPlayerCoords = "0F B6 5C 24 70 84 DB 75 43";

        private static bool IsFreeCamActive = false;
        private static bool ShouldLoopTime = false;
        private static bool IsTimePassing = true;
        private static bool IsTrackingPlayerCoords = false;
        private static bool IsTrackingCameraCoords = false;
        private static bool IsFollowingPlayer = false;
        private static Vector3 CameraFollowOffset;
        private static bool HasCameraNudge = false;
        private static Vector3 CameraNudgeOriginalPosition;
        private static byte[] PreviousCameraSnapshot;
        private static long PreviousCameraSnapshotBase;
        private static bool HasCameraScanBaseline = false;
        private static Dictionary<IntPtr, Vector3> CameraScanBaseline;
        private static Vector3 CameraScanCenter;
        private static List<IntPtr> LastCameraCandidateAddresses = new List<IntPtr>();
        private static HashSet<IntPtr> LastPointerReferenceAddresses = new HashSet<IntPtr>();
        private static CameraPointerChain? ActiveCameraPointerChain;

        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessVmOperation = 0x0008;
        private const uint MemoryCommit = 0x1000;
        private const uint PageGuard = 0x100;
        private const uint PageNoAccess = 0x01;
        private const uint MemPrivate = 0x20000;
        private const int CameraScanRadius = 300;
        private const int CameraScanVerticalRadius = 100;
        private const int CameraScanChunkSize = 1024 * 1024;
        private const int PointerSearchRange = 0x400;
        private const int CameraCandidateRetentionCount = 100;
        private const long KnownCameraModuleOffset = 0x2A99E80;
        private const int KnownCameraVectorOffset = 0x1B8;
        private const int KnownCameraRotationOffsetA = 0x170;
        private const int KnownCameraRotationOffsetB = 0x180;
        private const int KnownCameraRotationOffsetC = 0x190;
        private static readonly bool EnableExperimentalCameraWrites = false;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr processHandle, IntPtr address, out MemoryBasicInformation buffer, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private struct CameraCandidate
        {
            public IntPtr Address;
            public Vector3 Before;
            public Vector3 After;
            public float Distance;
        }

        [STAThread]
        static void Main(string[] args)
        {
            KeybindingManager keybindingManager = new KeybindingManager();
            KeybindingConfig config = keybindingManager.ReadKeybindingConfig();

            Process snowRunnerProcess = attachToSnowRunnerProcess();
            HotKeyManager.RegisterHotKey(config.ToggleFlyMode, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ToggleFreezeTime, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ToggleTimeLapseMode, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.GetPlayerLocation, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.TogglePlayerTracking, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.CaptureCameraScan, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.FindCameraPointers, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ResetCameraScan, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ProbeCameraChain, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ToggleCameraTracking, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ProbeCameraTransform, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.ToggleCameraFollow, KeyModifiers.Control);
            HotKeyManager.RegisterHotKey(config.NudgeCameraPosition, KeyModifiers.Control | KeyModifiers.Shift);
            HotKeyManager.HotKeyPressed += (sender2, e2) => Hotkey_Pressed(sender2, e2, config,snowRunnerProcess);

            bool showMenu = true;
            while (showMenu)
            {
                showMenu = MainMenu(snowRunnerProcess, config);
            }

        }
        private static bool MainMenu(Process snowRunnerProcess, KeybindingConfig config)
        {
            string logo = @"   _____                     ______ _                   ___  
  / ____|                   |  ____| |                 |__ \ 
 | (___  _ __   _____      _| |__  | |_   _  ___ _ __     ) |
  \___ \| '_ \ / _ \ \ /\ / /  __| | | | | |/ _ \ '__|   / / 
  ____) | | | | (_) \ V  V /| |    | | |_| |  __/ |     / /_ 
 |_____/|_| |_|\___/ \_/\_/ |_|    |_|\__, |\___|_|    |____|
                                       __/ |                 
                                      |___/                  ";
            Console.Clear();

            Console.WriteLine(logo);



            DisplayHotkeyInstructions(config);
            
            //Prevent app from closing
            Console.ReadKey();
            return true;
        }

        private static void StopTODTicker(Process snowRunnerProcess)
        {
            ShouldLoopTime = false;
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            try
            {
                SearchAndApplyPatch(snowRunnerProcess, scanner, TODTickDisablePattern, TODTickDisablePatch, "TODTick");

                Console.BackgroundColor = ConsoleColor.Blue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("\n\n Timer Stopped!");
                Console.ResetColor();
                IsTimePassing = false;

                float currentTime = GetCurrentTOD(snowRunnerProcess);

                Console.WriteLine("Current time is:{0}h", currentTime.ToString("0.00"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void ResumeTODTicker(Process snowRunnerProcess)
        {
            ShouldLoopTime = false;
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            try
            {
                SearchAndApplyPatch(snowRunnerProcess, scanner, TODTickRevertPattern, TODTickRevertPatch, "TODTick");

                Console.BackgroundColor = ConsoleColor.Blue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("\n\n Timer Resumed!");
                Console.ResetColor();
                IsTimePassing = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }


        private static void GetPlayerCoords(Process snowRunnerProcess)
        {
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            var coordsPtr = scanner.CompiledFindPattern(PatternPlayerCoords);

            IntPtr PlayerCoordsBasePtr = IntPtr.Zero;
            int[] PlayerCoordsOffsets = { 0x28, 0x18, 0x1A8 };
            var snowRunnerBase = snowRunnerProcess.MainModule.BaseAddress;

            StructPointerDescription<Vector3> PlayerCoord;
            if (coordsPtr.Found)
            {
                var memory = new ExternalMemory(snowRunnerProcess);
                PlayerCoordsBasePtr = ReadOffsetFromCallToLeaSingleton(memory, snowRunnerBase + coordsPtr.Offset + GetOpcodeLengthOfPattern(PatternPlayerCoords));
                if (PlayerCoordsBasePtr == IntPtr.Zero)
                    return;
                PlayerCoord = new StructPointerDescription<Vector3>(memory, PlayerCoordsBasePtr, PlayerCoordsOffsets);

                var playerVec = PlayerCoord.Read();
                Console.WriteLine($"Coord: {playerVec.X:F2} {playerVec.Y:F2} {playerVec.Z:F2}");
            }
        }

        private static void TogglePlayerCoordTracking(Process snowRunnerProcess)
        {
            if (IsTrackingPlayerCoords)
            {
                IsTrackingPlayerCoords = false;
                Console.WriteLine("Player coordinate tracking stopped.");
                return;
            }

            IsTrackingPlayerCoords = true;
            Task.Run(() => TrackPlayerCoords(snowRunnerProcess));
            Console.WriteLine("Player coordinate tracking started.");
        }

        private static void TrackPlayerCoords(Process snowRunnerProcess)
        {
            try
            {
                var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
                var coordsPtr = scanner.CompiledFindPattern(PatternPlayerCoords);

                if (!coordsPtr.Found)
                {
                    Console.WriteLine("Player coordinate pattern was not found.");
                    IsTrackingPlayerCoords = false;
                    return;
                }

                var memory = new ExternalMemory(snowRunnerProcess);
                var snowRunnerBase = snowRunnerProcess.MainModule.BaseAddress;
                var playerCoordsBasePtr = ReadOffsetFromCallToLeaSingleton(
                    memory,
                    snowRunnerBase + coordsPtr.Offset + GetOpcodeLengthOfPattern(PatternPlayerCoords));

                if (playerCoordsBasePtr == IntPtr.Zero)
                {
                    Console.WriteLine("Player coordinate pointer was null.");
                    IsTrackingPlayerCoords = false;
                    return;
                }

                int[] playerCoordsOffsets = { 0x28, 0x18, 0x1A8 };
                var playerCoord = new StructPointerDescription<Vector3>(
                    memory,
                    playerCoordsBasePtr,
                    playerCoordsOffsets);

                while (IsTrackingPlayerCoords)
                {
                    var playerVec = playerCoord.Read();
                    Console.WriteLine($"LOCAL: {playerVec.X:F2} {playerVec.Y:F2} {playerVec.Z:F2}");
                    Thread.Sleep(250);
                }
            }
            catch (Exception ex)
            {
                IsTrackingPlayerCoords = false;
                Console.WriteLine($"Player coordinate tracking failed: {ex.Message}");
            }
        }

        private static void CaptureCameraScan(Process snowRunnerProcess)
        {
            try
            {
                if (!TryReadPlayerCoords(snowRunnerProcess, out Vector3 playerPosition))
                {
                    Console.WriteLine("Could not read the local player position for the camera scan.");
                    return;
                }

                if (!HasCameraScanBaseline)
                {
                    Console.WriteLine($"Scanning for camera vectors near {playerPosition.X:F2} {playerPosition.Y:F2} {playerPosition.Z:F2}...");
                    CameraScanBaseline = ScanForVectorsNear(snowRunnerProcess, playerPosition, CameraScanRadius);
                    CameraScanCenter = playerPosition;
                    HasCameraScanBaseline = true;
                    LastCameraCandidateAddresses.Clear();
                    LastPointerReferenceAddresses.Clear();
                    ActiveCameraPointerChain = null;
                    Console.WriteLine($"Initial scan captured {CameraScanBaseline.Count} candidate vectors. Move the free camera, then press Ctrl+F6 again.");
                    return;
                }

                var changedCandidates = FindChangedCameraVectors(snowRunnerProcess, CameraScanBaseline);

                if (changedCandidates.Count == 0)
                {
                    HasCameraScanBaseline = false;
                    CameraScanBaseline = null;
                    LastCameraCandidateAddresses.Clear();
                    ActiveCameraPointerChain = null;
                    Console.WriteLine("No tracked candidates changed. Move the free camera farther and press Ctrl+F6 again.");
                    return;
                }

                CameraScanBaseline = changedCandidates.ToDictionary(candidate => candidate.Address, candidate => candidate.After);
                HasCameraScanBaseline = true;

                Console.WriteLine($"Changed camera candidates: {changedCandidates.Count}");
                var elevatedChanges = changedCandidates
                    .Where(candidate =>
                        Math.Abs(candidate.Before.Y - CameraScanCenter.Y) <= 20.0f &&
                        Math.Abs(candidate.After.Y - CameraScanCenter.Y) <= 20.0f &&
                        (Vector3.DistanceSquared(candidate.Before, CameraScanCenter) > 4.0f ||
                         Vector3.DistanceSquared(candidate.After, CameraScanCenter) > 4.0f))
                    .OrderByDescending(candidate => candidate.Distance)
                    .Take(CameraCandidateRetentionCount)
                    .ToList();
                var smallChanges = changedCandidates
                    .Where(candidate => candidate.Distance <= 25.0f)
                    .OrderBy(candidate => candidate.Distance)
                    .Take(CameraCandidateRetentionCount)
                    .ToList();
                var results = elevatedChanges.Count > 0
                    ? elevatedChanges
                    : smallChanges.Count > 0
                    ? smallChanges
                    : changedCandidates.Take(CameraCandidateRetentionCount).ToList();

                LastCameraCandidateAddresses = results.Select(candidate => candidate.Address)
                    .Distinct()
                    .Take(CameraCandidateRetentionCount)
                    .ToList();

                if (elevatedChanges.Count > 0)
                    Console.WriteLine("Camera-height movement candidates:");
                else if (smallChanges.Count > 0)
                    Console.WriteLine("Small movement candidates:");

                foreach (var candidate in results.Take(20))
                {
                    Console.WriteLine($"0x{candidate.Address.ToInt64():X}: " +
                        $"{candidate.Before.X:F2},{candidate.Before.Y:F2},{candidate.Before.Z:F2} -> " +
                        $"{candidate.After.X:F2},{candidate.After.Y:F2},{candidate.After.Z:F2} " +
                        $"(delta {candidate.Distance:F2})");
                }
                Console.WriteLine($"Tracking {CameraScanBaseline.Count} surviving addresses. Move the free camera, then press Ctrl+F6 again.");
            }
            catch (Exception ex)
            {
                HasCameraScanBaseline = false;
                CameraScanBaseline = null;
                LastCameraCandidateAddresses.Clear();
                ActiveCameraPointerChain = null;
                Console.WriteLine($"Camera scan failed: {ex.Message}");
            }
        }

        private static void ResetCameraScan()
        {
            HasCameraScanBaseline = false;
            CameraScanBaseline = null;
            CameraScanCenter = default;
            LastCameraCandidateAddresses.Clear();
            LastPointerReferenceAddresses.Clear();
            ActiveCameraPointerChain = null;
            Console.WriteLine("Camera scan reset. Press Ctrl+F6 for a new initial scan.");
        }

        private static void ProbeKnownCameraChain(Process snowRunnerProcess)
        {
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                Console.WriteLine("Could not open SnowRunner for the camera chain probe.");
                return;
            }

            try
            {
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                if (!TryReadKnownCameraVector(processHandle, moduleBase, out Vector3 value))
                {
                    Console.WriteLine("Known camera chain did not resolve.");
                    return;
                }

                TryReadPointer(processHandle,
                    new IntPtr(moduleBase.ToInt64() + KnownCameraModuleOffset),
                    out long stableSlot);
                TryReadPointer(processHandle, new IntPtr(stableSlot), out long vectorBase);
                Console.WriteLine($"Known camera chain: SnowRunner.exe+0x{KnownCameraModuleOffset:X} -> " +
                    $"0x{stableSlot:X} -> 0x{vectorBase + KnownCameraVectorOffset:X} = " +
                    $"{value.X:F2},{value.Y:F2},{value.Z:F2}");
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static void ToggleCameraCoordTracking(Process snowRunnerProcess)
        {
            if (IsTrackingCameraCoords)
            {
                IsTrackingCameraCoords = false;
                Console.WriteLine("Camera coordinate tracking stopped.");
                return;
            }

            IsTrackingCameraCoords = true;
            Task.Run(() => TrackCameraCoords(snowRunnerProcess));
            Console.WriteLine("Camera coordinate tracking started.");
        }

        private static void ToggleCameraFollow(Process snowRunnerProcess)
        {
            if (IsFollowingPlayer)
            {
                IsFollowingPlayer = false;
                Console.WriteLine("Camera follow stopped.");
                return;
            }

            if (!IsFreeCamActive)
            {
                Console.WriteLine("Camera follow requires freecam. Press Ctrl+F1 first.");
                return;
            }

            if (!EnableExperimentalCameraWrites)
            {
                Console.WriteLine("Camera follow writes are disabled while the writable camera state is being identified.");
                return;
            }

            if (!TryReadKnownCameraVector(snowRunnerProcess, out Vector3 cameraPosition) ||
                !TryReadPlayerCoords(snowRunnerProcess, out Vector3 playerPosition))
            {
                Console.WriteLine("Could not read the camera and player positions for follow mode.");
                return;
            }

            CameraFollowOffset = cameraPosition - playerPosition;
            IsFollowingPlayer = true;
            Task.Run(() => TrackCameraFollow(snowRunnerProcess));
            Console.WriteLine($"Camera follow started with offset {CameraFollowOffset.X:F2} " +
                $"{CameraFollowOffset.Y:F2} {CameraFollowOffset.Z:F2}.");
        }

        private static void NudgeCameraPosition(Process snowRunnerProcess)
        {
            if (!IsFreeCamActive)
            {
                Console.WriteLine("Camera position nudge requires freecam. Press Ctrl+F1 first.");
                return;
            }

            if (!EnableExperimentalCameraWrites)
            {
                Console.WriteLine("Camera position writes are disabled until the writable camera state is identified.");
                return;
            }

            IntPtr processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation,
                false,
                snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                Console.WriteLine("Could not open SnowRunner for the camera position nudge.");
                return;
            }

            try
            {
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                if (!TryReadKnownCameraBase(processHandle, moduleBase, out long cameraBase) ||
                    !TryReadVector3(processHandle,
                        new IntPtr(cameraBase + KnownCameraVectorOffset),
                        out Vector3 currentPosition))
                {
                    Console.WriteLine("Known camera position did not resolve.");
                    return;
                }

                Vector3 targetPosition;
                if (HasCameraNudge)
                {
                    targetPosition = CameraNudgeOriginalPosition;
                }
                else
                {
                    CameraNudgeOriginalPosition = currentPosition;
                    targetPosition = currentPosition + new Vector3(0.0f, 1.0f, 0.0f);
                }

                if (!TryWriteVector3(processHandle,
                    new IntPtr(cameraBase + KnownCameraVectorOffset),
                    targetPosition) ||
                    !TryReadVector3(processHandle,
                        new IntPtr(cameraBase + KnownCameraVectorOffset),
                        out Vector3 writtenPosition) ||
                    Vector3.DistanceSquared(targetPosition, writtenPosition) > 0.01f)
                {
                    Console.WriteLine("Camera position nudge did not read back correctly.");
                    return;
                }

                HasCameraNudge = !HasCameraNudge;
                Console.WriteLine($"Camera position {(HasCameraNudge ? "nudged" : "restored")}: " +
                    $"{writtenPosition.X:F2} {writtenPosition.Y:F2} {writtenPosition.Z:F2}");
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static void TrackCameraFollow(Process snowRunnerProcess)
        {
            IntPtr processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation,
                false,
                snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                IsFollowingPlayer = false;
                Console.WriteLine("Could not open SnowRunner for camera follow mode.");
                return;
            }

            try
            {
                if (!TryCreatePlayerCoordinateReader(snowRunnerProcess, out StructPointerDescription<Vector3> playerCoord))
                {
                    IsFollowingPlayer = false;
                    Console.WriteLine("Player coordinate pointer could not be resolved for follow mode.");
                    return;
                }

                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                while (IsFollowingPlayer)
                {
                    Vector3 playerPosition = playerCoord.Read();
                    if (!IsFiniteVector(playerPosition) ||
                        !TryReadKnownCameraBase(processHandle, moduleBase, out long cameraBase))
                    {
                        IsFollowingPlayer = false;
                        Console.WriteLine("Camera follow could not resolve the current game state.");
                        break;
                    }

                    Vector3 targetPosition = playerPosition + CameraFollowOffset;
                    if (!TryWriteVector3(processHandle,
                        new IntPtr(cameraBase + KnownCameraVectorOffset),
                        targetPosition) ||
                        !TryReadVector3(processHandle,
                            new IntPtr(cameraBase + KnownCameraVectorOffset),
                            out Vector3 writtenPosition) ||
                        Vector3.DistanceSquared(targetPosition, writtenPosition) > 0.25f)
                    {
                        IsFollowingPlayer = false;
                        Console.WriteLine("Camera follow could not write the camera position.");
                        break;
                    }

                    Console.WriteLine($"FOLLOW: {targetPosition.X:F2} {targetPosition.Y:F2} {targetPosition.Z:F2}");
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                IsFollowingPlayer = false;
                Console.WriteLine($"Camera follow failed: {ex.Message}");
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static void ProbeCameraTransform(Process snowRunnerProcess)
        {
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                Console.WriteLine("Could not open SnowRunner for the camera transform probe.");
                return;
            }

            try
            {
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                if (!TryReadKnownCameraBase(processHandle, moduleBase, out long cameraBase))
                {
                    Console.WriteLine("Known camera chain did not resolve.");
                    return;
                }

                const int snapshotStart = 0x100;
                const int snapshotSize = 0x200;
                byte[] snapshot = new byte[snapshotSize];
                if (!TryReadBytes(processHandle,
                    new IntPtr(cameraBase + snapshotStart),
                    snapshot))
                {
                    Console.WriteLine("Could not read a consistent camera-object snapshot.");
                    return;
                }

                Console.WriteLine($"Camera object base: 0x{cameraBase:X}. " +
                    $"Snapshot: +0x{snapshotStart:X}-+0x{snapshotStart + snapshotSize - 1:X}.");
                Vector3 positionCandidate = ReadSnapshotVector3(snapshot, snapshotStart, KnownCameraVectorOffset);
                Console.WriteLine($"Position candidate +0x{KnownCameraVectorOffset:X3}: " +
                    $"{positionCandidate.X:F2}, {positionCandidate.Y:F2}, {positionCandidate.Z:F2}");
                Console.WriteLine($"Basis candidates: +0x{KnownCameraRotationOffsetA:X3} " +
                    $"{ReadSnapshotVector3(snapshot, snapshotStart, KnownCameraRotationOffsetA)}, " +
                    $"+0x{KnownCameraRotationOffsetB:X3} " +
                    $"{ReadSnapshotVector3(snapshot, snapshotStart, KnownCameraRotationOffsetB)}, " +
                    $"+0x{KnownCameraRotationOffsetC:X3} " +
                    $"{ReadSnapshotVector3(snapshot, snapshotStart, KnownCameraRotationOffsetC)}");
                if (PreviousCameraSnapshot != null && PreviousCameraSnapshotBase == cameraBase)
                {
                    var changedFields = new List<Tuple<int, float>>();
                    for (int offset = snapshotStart; offset <= snapshotStart + snapshotSize - 4; offset += 4)
                    {
                        int snapshotOffset = offset - snapshotStart;
                        float previous = BitConverter.ToSingle(PreviousCameraSnapshot, snapshotOffset);
                        float current = BitConverter.ToSingle(snapshot, snapshotOffset);
                        if (IsFiniteFloat(previous) && IsFiniteFloat(current) &&
                            Math.Abs(current - previous) >= 0.01f)
                        {
                            changedFields.Add(Tuple.Create(offset, Math.Abs(current - previous)));
                        }
                    }

                    Console.WriteLine($"Changed scalar fields since last snapshot: {changedFields.Count}");
                    foreach (var changedField in changedFields.OrderByDescending(field => field.Item2).Take(40))
                    {
                        int offset = changedField.Item1;
                        int snapshotOffset = offset - snapshotStart;
                        float previous = BitConverter.ToSingle(PreviousCameraSnapshot, snapshotOffset);
                        float current = BitConverter.ToSingle(snapshot, snapshotOffset);
                        Console.WriteLine($"  +0x{offset:X3}: {previous:F4} -> {current:F4}");
                    }
                }

                PreviousCameraSnapshot = snapshot;
                PreviousCameraSnapshotBase = cameraBase;
                Console.WriteLine("Nearby normalized vectors from one read:");
                for (int offset = 0x100; offset <= 0x280; offset += 4)
                {
                    Vector3 value = ReadSnapshotVector3(snapshot, snapshotStart, offset);
                    if (!IsFiniteVector(value) ||
                        value.LengthSquared() < 0.0001f || value.LengthSquared() > 4.0f)
                        continue;

                    Console.WriteLine($"  +0x{offset:X3}: {value.X:F4}, {value.Y:F4}, {value.Z:F4}");
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static Vector3 ReadSnapshotVector3(byte[] snapshot, int snapshotStart, int offset)
        {
            int snapshotOffset = offset - snapshotStart;
            return new Vector3(
                BitConverter.ToSingle(snapshot, snapshotOffset),
                BitConverter.ToSingle(snapshot, snapshotOffset + 4),
                BitConverter.ToSingle(snapshot, snapshotOffset + 8));
        }

        private static void TrackCameraCoords(Process snowRunnerProcess)
        {
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                IsTrackingCameraCoords = false;
                Console.WriteLine("Could not open SnowRunner for camera tracking.");
                return;
            }

            try
            {
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                while (IsTrackingCameraCoords)
                {
                    if (!TryReadKnownCameraVector(processHandle, moduleBase, out Vector3 value))
                    {
                        Console.WriteLine("Camera coordinate chain no longer resolves.");
                        IsTrackingCameraCoords = false;
                        break;
                    }

                    if (TryReadKnownCameraRotation(processHandle, moduleBase,
                        out Vector3 rotationA, out Vector3 rotationB, out Vector3 rotationC))
                    {
                        Console.WriteLine($"CAMERA: {value.X:F2} {value.Y:F2} {value.Z:F2} | " +
                            $"ROT: {rotationA.X:F3},{rotationA.Y:F3},{rotationA.Z:F3} / " +
                            $"{rotationB.X:F3},{rotationB.Y:F3},{rotationB.Z:F3} / " +
                            $"{rotationC.X:F3},{rotationC.Y:F3},{rotationC.Z:F3}");
                    }
                    else
                    {
                        Console.WriteLine($"CAMERA: {value.X:F2} {value.Y:F2} {value.Z:F2}");
                    }
                    Thread.Sleep(250);
                }
            }
            catch (Exception ex)
            {
                IsTrackingCameraCoords = false;
                Console.WriteLine($"Camera coordinate tracking failed: {ex.Message}");
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static bool TryReadKnownCameraVector(IntPtr processHandle, IntPtr moduleBase, out Vector3 value)
        {
            value = default;
            if (!TryReadKnownCameraBase(processHandle, moduleBase, out long vectorBase))
            {
                return false;
            }

            return TryReadVector3(processHandle,
                new IntPtr(vectorBase + KnownCameraVectorOffset),
                out value);
        }

        private static bool TryReadKnownCameraVector(Process snowRunnerProcess, out Vector3 value)
        {
            value = default;
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                return false;

            try
            {
                return TryReadKnownCameraVector(
                    processHandle,
                    snowRunnerProcess.MainModule.BaseAddress,
                    out value);
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static bool TryReadKnownCameraBase(IntPtr processHandle, IntPtr moduleBase, out long vectorBase)
        {
            vectorBase = 0;
            if (!TryReadPointer(processHandle,
                new IntPtr(moduleBase.ToInt64() + KnownCameraModuleOffset),
                out long stableSlot))
                return false;

            return TryReadPointer(processHandle, new IntPtr(stableSlot), out vectorBase);
        }

        private static bool TryReadKnownCameraRotation(
            IntPtr processHandle,
            IntPtr moduleBase,
            out Vector3 rotationA,
            out Vector3 rotationB,
            out Vector3 rotationC)
        {
            rotationA = default;
            rotationB = default;
            rotationC = default;
            if (!TryReadKnownCameraBase(processHandle, moduleBase, out long cameraBase) ||
                !TryReadVector3(processHandle, new IntPtr(cameraBase + KnownCameraRotationOffsetA), out rotationA) ||
                !TryReadVector3(processHandle, new IntPtr(cameraBase + KnownCameraRotationOffsetB), out rotationB) ||
                !TryReadVector3(processHandle, new IntPtr(cameraBase + KnownCameraRotationOffsetC), out rotationC))
                return false;

            float lengthA = rotationA.Length();
            float lengthB = rotationB.Length();
            float lengthC = rotationC.Length();
            return lengthA > 0.8f && lengthA < 1.2f &&
                   lengthB > 0.8f && lengthB < 1.2f &&
                   lengthC > 0.8f && lengthC < 1.2f &&
                   Math.Abs(Vector3.Dot(rotationA, rotationB)) < 0.25f &&
                   Math.Abs(Vector3.Dot(rotationA, rotationC)) < 0.25f &&
                   Math.Abs(Vector3.Dot(rotationB, rotationC)) < 0.25f;
        }

        private static bool TryCreatePlayerCoordinateReader(
            Process snowRunnerProcess,
            out StructPointerDescription<Vector3> playerCoord)
        {
            playerCoord = null;
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            var coordsPtr = scanner.CompiledFindPattern(PatternPlayerCoords);
            if (!coordsPtr.Found)
                return false;

            var memory = new ExternalMemory(snowRunnerProcess);
            var playerCoordsBasePtr = ReadOffsetFromCallToLeaSingleton(
                memory,
                snowRunnerProcess.MainModule.BaseAddress + coordsPtr.Offset + GetOpcodeLengthOfPattern(PatternPlayerCoords));
            if (playerCoordsBasePtr == IntPtr.Zero)
                return false;

            playerCoord = new StructPointerDescription<Vector3>(
                memory,
                playerCoordsBasePtr,
                0x28,
                0x18,
                0x1A8);
            return true;
        }

        private static bool TryReadPlayerCoords(Process snowRunnerProcess, out Vector3 playerVector)
        {
            playerVector = default;
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            var coordsPtr = scanner.CompiledFindPattern(PatternPlayerCoords);
            if (!coordsPtr.Found)
                return false;

            var memory = new ExternalMemory(snowRunnerProcess);
            var snowRunnerBase = snowRunnerProcess.MainModule.BaseAddress;
            var playerCoordsBasePtr = ReadOffsetFromCallToLeaSingleton(
                memory,
                snowRunnerBase + coordsPtr.Offset + GetOpcodeLengthOfPattern(PatternPlayerCoords));
            if (playerCoordsBasePtr == IntPtr.Zero)
                return false;

            var playerCoord = new StructPointerDescription<Vector3>(
                memory,
                playerCoordsBasePtr,
                0x28,
                0x18,
                0x1A8);
            playerVector = playerCoord.Read();
            return IsFiniteVector(playerVector);
        }

        private static Dictionary<IntPtr, Vector3> ScanForVectorsNear(Process snowRunnerProcess, Vector3 center, float radius)
        {
            var candidates = new Dictionary<IntPtr, Vector3>();
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not open SnowRunner for memory scanning.");

            try
            {
                IntPtr address = IntPtr.Zero;
                int informationSize = Marshal.SizeOf(typeof(MemoryBasicInformation));
                while (VirtualQueryEx(processHandle, address, out MemoryBasicInformation region, (uint)informationSize) != 0)
                {
                    long regionBase = region.BaseAddress.ToInt64();
                    long regionSize = region.RegionSize.ToInt64();
                    if (regionSize <= 0)
                        break;

                    if (region.State == MemoryCommit && IsReadable(region.Protect))
                    {
                        ScanRegion(processHandle, regionBase, regionSize, center, radius, candidates);
                    }

                    long nextAddress = regionBase + regionSize;
                    if (nextAddress <= regionBase)
                        break;
                    address = new IntPtr(nextAddress);
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }

            return candidates;
        }

        private static void ScanRegion(IntPtr processHandle, long regionBase, long regionSize, Vector3 center, float radius, Dictionary<IntPtr, Vector3> candidates)
        {
            float radiusSquared = radius * radius;
            for (long offset = 0; offset < regionSize; offset += CameraScanChunkSize)
            {
                int bytesToRead = (int)Math.Min(CameraScanChunkSize, regionSize - offset);
                byte[] buffer = new byte[bytesToRead];
                if (!ReadProcessMemory(processHandle, new IntPtr(regionBase + offset), buffer, bytesToRead, out IntPtr bytesRead))
                    continue;

                int available = Math.Min(bytesToRead, bytesRead.ToInt32());
                for (int index = 0; index + 12 <= available; index += 4)
                {
                    Vector3 value = new Vector3(
                        BitConverter.ToSingle(buffer, index),
                        BitConverter.ToSingle(buffer, index + 4),
                        BitConverter.ToSingle(buffer, index + 8));
                    if (IsFiniteVector(value) && IsNearCameraSearchArea(value, center, radius, radiusSquared))
                    {
                        candidates[new IntPtr(regionBase + offset + index)] = value;
                    }
                }
            }
        }

        private static List<CameraCandidate> FindChangedCameraVectors(Process snowRunnerProcess, Dictionary<IntPtr, Vector3> baseline)
        {
            var changed = new List<CameraCandidate>();
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not open SnowRunner for the second camera scan.");

            try
            {
                foreach (var entry in baseline)
                {
                    byte[] buffer = new byte[12];
                    if (!ReadProcessMemory(processHandle, entry.Key, buffer, buffer.Length, out IntPtr bytesRead) || bytesRead.ToInt32() != buffer.Length)
                        continue;

                    Vector3 current = new Vector3(
                        BitConverter.ToSingle(buffer, 0),
                        BitConverter.ToSingle(buffer, 4),
                        BitConverter.ToSingle(buffer, 8));
                    if (!IsFiniteVector(current) || !IsNearCameraSearchArea(current, CameraScanCenter, CameraScanRadius, CameraScanRadius * CameraScanRadius))
                        continue;

                    float distance = Vector3.Distance(entry.Value, current);
                    if (distance >= 1.0f)
                    {
                        changed.Add(new CameraCandidate
                        {
                            Address = entry.Key,
                            Before = entry.Value,
                            After = current,
                            Distance = distance
                        });
                    }
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }

            changed.Sort((left, right) => right.Distance.CompareTo(left.Distance));
            return changed;
        }

        private struct PointerTarget
        {
            public IntPtr Candidate;
            public int Delta;
        }

        private static void FindCameraPointerReferences(Process snowRunnerProcess)
        {
            if (LastCameraCandidateAddresses.Count == 0)
            {
                Console.WriteLine("No filtered scan candidates are available; searching the known camera vector directly.");
            }

            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not open SnowRunner for pointer scanning.");

            try
            {
                var candidateAddresses = LastCameraCandidateAddresses.ToList();
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                if (TryReadKnownCameraBase(processHandle, moduleBase, out long knownCameraBase))
                {
                    IntPtr knownCameraVector = new IntPtr(knownCameraBase + KnownCameraVectorOffset);
                    if (!HasCameraScanBaseline &&
                        TryReadVector3(processHandle, knownCameraVector, out Vector3 knownCameraPosition))
                    {
                        CameraScanCenter = knownCameraPosition;
                    }

                    if (!candidateAddresses.Contains(knownCameraVector))
                    {
                        candidateAddresses.Add(knownCameraVector);
                        Console.WriteLine($"Including the known camera vector 0x{knownCameraVector.ToInt64():X} in pointer search.");
                    }
                }

                var targets = BuildPointerTargets(candidateAddresses);

                long moduleStart = moduleBase.ToInt64();
                long moduleEnd = moduleStart + snowRunnerProcess.MainModule.ModuleMemorySize;
                var references = ScanModuleForPointerReferences(processHandle, moduleStart, moduleEnd, targets);

                if (references.Count == 0)
                {
                    Console.WriteLine("No direct module references. Searching all readable memory for heap references...");
                    references = ScanAllMemoryForPointerReferences(processHandle, targets);
                }

                Console.WriteLine($"Pointer references: {references.Count}");
                var currentReferenceAddresses = new HashSet<IntPtr>(
                    references.Select(reference => reference.ReferenceAddress));
                if (LastPointerReferenceAddresses.Count > 0)
                {
                    var stableReferences = currentReferenceAddresses
                        .Where(address => LastPointerReferenceAddresses.Contains(address))
                        .ToList();
                    Console.WriteLine($"Stable reference addresses across scans: {stableReferences.Count}");
                    foreach (IntPtr stableAddress in stableReferences.Take(50))
                        Console.WriteLine($"Stable: 0x{stableAddress.ToInt64():X}");

                    var secondLevelTargets = BuildPointerTargets(stableReferences);
                    var secondLevelReferences = ScanModuleForPointerReferences(
                        processHandle, moduleStart, moduleEnd, secondLevelTargets);
                    if (secondLevelReferences.Count == 0)
                        secondLevelReferences = ScanAllMemoryForPointerReferences(processHandle, secondLevelTargets);

                    Console.WriteLine($"Second-level references to stable heap slots: {secondLevelReferences.Count}");
                    var verifiedChains = secondLevelReferences
                        .Select(reference => TryBuildCameraPointerChain(
                            processHandle, snowRunnerProcess.MainModule.BaseAddress, reference, references))
                        .Where(result => result.HasValue)
                        .Select(result => result.Value)
                        .GroupBy(result => result.Chain)
                        .Select(group => group.First())
                        .ToList();
                    Console.WriteLine($"Verified camera pointer chains: {verifiedChains.Count}");
                    foreach (var verified in verifiedChains.Take(20))
                    {
                        Console.WriteLine($"SnowRunner.exe+0x{verified.Chain.ModuleOffset:X} -> " +
                            $"[ptr]+0x{verified.Chain.StableOffset:X} -> " +
                            $"[ptr]+0x{verified.Chain.VectorOffset:X} = " +
                            $"{verified.Value.X:F2},{verified.Value.Y:F2},{verified.Value.Z:F2}");
                    }
                    if (verifiedChains.Count > 0)
                        ActiveCameraPointerChain = verifiedChains[0].Chain;

                    foreach (var reference in secondLevelReferences.Take(40))
                    {
                        string location = reference.ReferenceOffset >= 0
                            ? $"SnowRunner.exe+0x{reference.ReferenceOffset:X}"
                            : $"0x{reference.ReferenceAddress.ToInt64():X}";
                        long stableSlotAddress = reference.Candidate.ToInt64() - reference.Delta;
                        var firstLevelMatch = references.FirstOrDefault(
                            first => first.ReferenceAddress.ToInt64() == stableSlotAddress);
                        if (firstLevelMatch.ReferenceAddress != IntPtr.Zero)
                        {
                            Console.WriteLine($"{location} -> stable slot 0x{reference.Candidate.ToInt64():X} + 0x{reference.Delta:X} -> " +
                                $"candidate 0x{firstLevelMatch.Candidate.ToInt64():X} + 0x{firstLevelMatch.Delta:X}");
                        }
                        else
                        {
                            Console.WriteLine($"{location} -> stable slot 0x{reference.Candidate.ToInt64():X} + 0x{reference.Delta:X}");
                        }
                    }
                }

                foreach (var reference in references.Take(50))
                {
                    string location = reference.ReferenceOffset >= 0
                        ? $"SnowRunner.exe+0x{reference.ReferenceOffset:X}"
                        : $"0x{reference.ReferenceAddress.ToInt64():X}";
                    Console.WriteLine($"{location} -> " +
                        $"candidate 0x{reference.Candidate.ToInt64():X} + 0x{reference.Delta:X}");
                }
                LastPointerReferenceAddresses = currentReferenceAddresses;
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private struct PointerReference
        {
            public long ReferenceOffset;
            public IntPtr ReferenceAddress;
            public IntPtr Candidate;
            public int Delta;
        }

        private struct CameraPointerChain : IEquatable<CameraPointerChain>
        {
            public long ModuleOffset;
            public int StableOffset;
            public int VectorOffset;

            public bool Equals(CameraPointerChain other)
            {
                return ModuleOffset == other.ModuleOffset &&
                       StableOffset == other.StableOffset &&
                       VectorOffset == other.VectorOffset;
            }

            public override bool Equals(object obj)
            {
                return obj is CameraPointerChain other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = ModuleOffset.GetHashCode();
                    hash = (hash * 397) ^ StableOffset;
                    return (hash * 397) ^ VectorOffset;
                }
            }
        }

        private struct VerifiedCameraChain
        {
            public CameraPointerChain Chain;
            public Vector3 Value;
        }

        private static VerifiedCameraChain? TryBuildCameraPointerChain(
            IntPtr processHandle,
            IntPtr moduleBase,
            PointerReference secondLevelReference,
            List<PointerReference> firstLevelReferences)
        {
            if (secondLevelReference.ReferenceOffset < 0)
                return null;

            var firstLevelMatch = firstLevelReferences.FirstOrDefault(first =>
                first.ReferenceAddress.ToInt64() ==
                secondLevelReference.Candidate.ToInt64() - secondLevelReference.Delta);
            if (firstLevelMatch.ReferenceAddress == IntPtr.Zero)
                return null;

            if (!TryReadPointer(processHandle,
                new IntPtr(moduleBase.ToInt64() + secondLevelReference.ReferenceOffset),
                out long rootValue))
                return null;

            long stableSlotAddress = rootValue + secondLevelReference.Delta;
            if (stableSlotAddress != firstLevelMatch.ReferenceAddress.ToInt64())
                return null;

            if (!TryReadPointer(processHandle,
                new IntPtr(stableSlotAddress),
                out long vectorBaseValue))
                return null;

            long vectorAddress = vectorBaseValue + firstLevelMatch.Delta;
            if (vectorAddress != firstLevelMatch.Candidate.ToInt64())
                return null;

            if (!TryReadVector3(processHandle, new IntPtr(vectorAddress), out Vector3 value) ||
                !IsNearCameraSearchArea(value, CameraScanCenter, CameraScanRadius, CameraScanRadius * CameraScanRadius))
                return null;

            return new VerifiedCameraChain
            {
                Chain = new CameraPointerChain
                {
                    ModuleOffset = secondLevelReference.ReferenceOffset,
                    StableOffset = secondLevelReference.Delta,
                    VectorOffset = firstLevelMatch.Delta
                },
                Value = value
            };
        }

        private static bool TryReadPointer(IntPtr processHandle, IntPtr address, out long value)
        {
            value = 0;
            byte[] buffer = new byte[8];
            if (!ReadProcessMemory(processHandle, address, buffer, buffer.Length, out IntPtr bytesRead) ||
                bytesRead.ToInt32() != buffer.Length)
                return false;

            value = BitConverter.ToInt64(buffer, 0);
            return value != 0;
        }

        private static bool TryReadVector3(IntPtr processHandle, IntPtr address, out Vector3 value)
        {
            value = default;
            byte[] buffer = new byte[12];
            if (!TryReadBytes(processHandle, address, buffer))
                return false;

            value = new Vector3(
                BitConverter.ToSingle(buffer, 0),
                BitConverter.ToSingle(buffer, 4),
                BitConverter.ToSingle(buffer, 8));
            return IsFiniteVector(value);
        }

        private static bool TryReadBytes(IntPtr processHandle, IntPtr address, byte[] buffer)
        {
            return ReadProcessMemory(processHandle, address, buffer, buffer.Length, out IntPtr bytesRead) &&
                bytesRead.ToInt64() == buffer.Length;
        }

        private static bool TryWriteVector3(IntPtr processHandle, IntPtr address, Vector3 value)
        {
            byte[] buffer = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(value.X), 0, buffer, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(value.Y), 0, buffer, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(value.Z), 0, buffer, 8, 4);
            return WriteProcessMemory(processHandle, address, buffer, buffer.Length, out IntPtr bytesWritten) &&
                bytesWritten.ToInt32() == buffer.Length;
        }

        private static Dictionary<long, List<PointerTarget>> BuildPointerTargets(IEnumerable<IntPtr> addresses)
        {
            var targets = new Dictionary<long, List<PointerTarget>>();
            foreach (IntPtr address in addresses.Distinct())
            {
                long addressValue = address.ToInt64();
                for (int delta = 0; delta <= PointerSearchRange; delta += 4)
                {
                    long objectAddress = addressValue - delta;
                    if (!targets.TryGetValue(objectAddress, out List<PointerTarget> matches))
                    {
                        matches = new List<PointerTarget>();
                        targets.Add(objectAddress, matches);
                    }
                    matches.Add(new PointerTarget { Candidate = address, Delta = delta });
                }
            }
            return targets;
        }

        private static List<PointerReference> ScanModuleForPointerReferences(
            IntPtr processHandle,
            long moduleStart,
            long moduleEnd,
            Dictionary<long, List<PointerTarget>> targets)
        {
            var references = new List<PointerReference>();
            IntPtr address = new IntPtr(moduleStart);
            int informationSize = Marshal.SizeOf(typeof(MemoryBasicInformation));

            while (VirtualQueryEx(processHandle, address, out MemoryBasicInformation region, (uint)informationSize) != 0)
            {
                long regionBase = region.BaseAddress.ToInt64();
                long regionSize = region.RegionSize.ToInt64();
                if (regionSize <= 0)
                    break;

                long regionEnd = regionBase + regionSize;
                if (regionBase >= moduleEnd)
                    break;

                if (region.State == MemoryCommit && IsReadable(region.Protect))
                {
                    long readStart = Math.Max(regionBase, moduleStart);
                    long readEnd = Math.Min(regionEnd, moduleEnd);
                    for (long offset = readStart; offset < readEnd; offset += CameraScanChunkSize)
                    {
                        int bytesToRead = (int)Math.Min(CameraScanChunkSize, readEnd - offset);
                        byte[] buffer = new byte[bytesToRead];
                        if (!ReadProcessMemory(processHandle, new IntPtr(offset), buffer, bytesToRead, out IntPtr bytesRead))
                            continue;

                        int available = Math.Min(bytesToRead, bytesRead.ToInt32());
                        for (int index = 0; index + 8 <= available; index += 8)
                        {
                            long value = BitConverter.ToInt64(buffer, index);
                            if (!targets.TryGetValue(value, out List<PointerTarget> matches))
                                continue;

                            foreach (PointerTarget match in matches)
                            {
                                references.Add(new PointerReference
                                {
                                    ReferenceOffset = offset + index - moduleStart,
                                    ReferenceAddress = new IntPtr(offset + index),
                                    Candidate = match.Candidate,
                                    Delta = match.Delta
                                });
                            }
                        }
                    }
                }

                long nextAddress = regionBase + regionSize;
                if (nextAddress <= regionBase)
                    break;
                address = new IntPtr(nextAddress);
            }

            return references;
        }

        private static List<PointerReference> ScanAllMemoryForPointerReferences(
            IntPtr processHandle,
            Dictionary<long, List<PointerTarget>> targets)
        {
            var references = new List<PointerReference>();
            IntPtr address = IntPtr.Zero;
            int informationSize = Marshal.SizeOf(typeof(MemoryBasicInformation));

            while (VirtualQueryEx(processHandle, address, out MemoryBasicInformation region, (uint)informationSize) != 0)
            {
                long regionBase = region.BaseAddress.ToInt64();
                long regionSize = region.RegionSize.ToInt64();
                if (regionSize <= 0)
                    break;

                // Module references are searched separately. For the fallback heap
                // pass, skip image and mapped regions so a pointer search does not
                // spend minutes rereading executable and file-backed memory.
                if (region.State == MemoryCommit && region.Type == MemPrivate && IsReadable(region.Protect))
                {
                    for (long offset = 0; offset < regionSize && references.Count < 5000; offset += CameraScanChunkSize)
                    {
                        int bytesToRead = (int)Math.Min(CameraScanChunkSize, regionSize - offset);
                        byte[] buffer = new byte[bytesToRead];
                        if (!ReadProcessMemory(processHandle, new IntPtr(regionBase + offset), buffer, bytesToRead, out IntPtr bytesRead))
                            continue;

                        int available = Math.Min(bytesToRead, bytesRead.ToInt32());
                        for (int index = 0; index + 8 <= available && references.Count < 5000; index += 8)
                        {
                            long value = BitConverter.ToInt64(buffer, index);
                            if (!targets.TryGetValue(value, out List<PointerTarget> matches))
                                continue;

                            foreach (PointerTarget match in matches)
                            {
                                references.Add(new PointerReference
                                {
                                    ReferenceOffset = -1,
                                    ReferenceAddress = new IntPtr(regionBase + offset + index),
                                    Candidate = match.Candidate,
                                    Delta = match.Delta
                                });
                            }
                        }
                    }
                }

                long nextAddress = regionBase + regionSize;
                if (nextAddress <= regionBase)
                    break;
                address = new IntPtr(nextAddress);
            }

            return references;
        }

        private static bool IsReadable(uint protection)
        {
            if ((protection & PageGuard) != 0 || (protection & PageNoAccess) != 0)
                return false;
            return (protection & 0xFF) != 0;
        }

        private static bool IsFiniteVector(Vector3 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
                   !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
                   !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        }

        private static bool IsFiniteFloat(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsNearCameraSearchArea(Vector3 value, Vector3 center, float radius, float radiusSquared)
        {
            return Math.Abs(value.X - center.X) <= radius &&
                   Math.Abs(value.Z - center.Z) <= radius &&
                   Math.Abs(value.Y - center.Y) <= CameraScanVerticalRadius &&
                   Vector3.DistanceSquared(value, center) <= radiusSquared;
        }

        public static IntPtr ReadOffsetFromOpcode(IMemory memory, IntPtr opcodeBegin, int offsetUntilAddress)
        {
            var opcodeOffset = memory.Read<int>(opcodeBegin + offsetUntilAddress);
            var effectiveAddress = opcodeBegin + offsetUntilAddress + opcodeOffset;
            return effectiveAddress + sizeof(int);
        }
        public static IntPtr ReadOffsetFromCallToLeaSingleton(IMemory memory, IntPtr opcodeBegin) //qqtas opcodebegin is first byte after our player pattern
        {
            var callToGetPtr = ReadOffsetFromOpcode(memory, opcodeBegin, 1);//call offset 1
            callToGetPtr = ReadOffsetFromOpcode(memory, callToGetPtr, 3); //lea offset 3
            return callToGetPtr;
        }

        public static IntPtr GetLastPtrOfPtrChain(IMemory memory, IntPtr baseAddress, params int[] offsets)
        {
            if (baseAddress == IntPtr.Zero)
            {
                return baseAddress;
            }
            if (offsets.Length == 0)
            {
                return baseAddress;
            }

            var basePtr = memory.Read<IntPtr>(baseAddress);
            if (basePtr == IntPtr.Zero)
            {
                return default;
            }
            for (var i = 0; i < offsets.Length - 1; i++)
            {
                basePtr = memory.Read<IntPtr>(basePtr + offsets[i]);
                if (basePtr == IntPtr.Zero)
                {
                    return default;
                }
            }
            if (basePtr == IntPtr.Zero)
            {
                return default;
            }
            //return basePtr + offsets[^1];
            return basePtr + offsets[offsets.Length-1];
        }

        private static int GetOpcodeLengthOfPattern(string pattern)
        {
            //2 characters represent a byte
            return pattern.Replace(" ", "").Length / 2;
        }


        private static void EnableFreeCam(Process snowRunnerProcess)
        {
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            try
            {
                SearchAndApplyPatch(snowRunnerProcess, scanner, DevCheckPattern, DevCheckPatch, "DevMode");
                SearchAndApplyPatch(snowRunnerProcess, scanner, DevCheckPatternB, DevCheckPatchB, "DevModeB");
                ForceApplyPatchAtOffset(snowRunnerProcess, FlyModeFlagOffset, FlyModeOnPatch, "FlyMode");

                Console.BackgroundColor = ConsoleColor.Blue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("\n\n FreeCam Enabled!");
                Console.ResetColor();
                IsFreeCamActive = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void DisableFreeCam(Process snowRunnerProcess)
        {
            var scanner = new Scanner(snowRunnerProcess, snowRunnerProcess.MainModule);
            try
            {
                SearchAndApplyPatch(snowRunnerProcess, scanner, DevCheckRevertPattern, DevCheckRevertPatch, "DevMode");
                SearchAndApplyPatch(snowRunnerProcess, scanner, DevCheckRevertPatternB, DevCheckRevertPatchB, "DevModeB");
                ForceApplyPatchAtOffset(snowRunnerProcess, FlyModeFlagOffset, FlyModeRevertPatch, "FlyMode");

                Console.BackgroundColor = ConsoleColor.Blue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("\n\n FreeCam Disabled!");
                Console.ResetColor();
                IsFreeCamActive = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static Process attachToSnowRunnerProcess()
        {
            Console.WriteLine("Looking for SnowRunner.exe");
            var p = Process.GetProcessesByName("SnowRunner");
            while (p.Length == 0)
            {
                Console.WriteLine("Waiting for SnowRunner.exe");
                Thread.Sleep(1000);
                p = Process.GetProcessesByName("SnowRunner");
            }
            var snowRunnerProcess = p.First();
            return snowRunnerProcess;
        }

        private static void SearchAndApplyPatch(Process snowRunnerProcess, Scanner scanner, String searchPattern, byte[] patchBytes, String label)
        {
            var offset = scanner.CompiledFindPattern(searchPattern);
            Console.WriteLine("Searching for {0} patch location", label);
            if (offset.Found)
            {
                Console.WriteLine("Found {0} patch location at {1:X}. Patching game in memory...", label, offset.Offset);
                try
                {
                    var memory = new ExternalMemory(snowRunnerProcess);
                    var baseAddress = snowRunnerProcess.MainModule.BaseAddress + offset.Offset;
                    memory.WriteRaw(baseAddress, patchBytes);
                    Console.WriteLine("Patch in memory successful!");
                }
                catch (Exception)
                {
                    throw new Exception(String.Format("Patching in memory failed for {0} \n Try running again as Administrator", label));
                }

            }
            else
            {
                throw new Exception(String.Format("Could not find patch location for {0}", label));
            }
        }


        private static void ForceApplyPatchAtOffset(Process snowRunnerProcess, int offset, byte[] patchBytes, String label)
        {
            Console.WriteLine("Using known {0} patch location {1:X}. Patching game in memory...", label, offset);
            try
            {
                var memory = new ExternalMemory(snowRunnerProcess);
                var baseAddress = snowRunnerProcess.MainModule.BaseAddress + offset;
                memory.WriteRaw(baseAddress, patchBytes);

                Console.WriteLine("Patch in memory successful!");
            }
            catch (Exception)
            {
                throw new Exception(String.Format("Patching in memory failed for {0} \n Try running again as Administrator", label));
            }
        }


        private static float GetCurrentTOD(Process snowRunnerProcess)
        {
            try
            {
                var memory = new ExternalMemory(snowRunnerProcess);
                var valueAddress = GetTODMemoryAddress(snowRunnerProcess, memory);

                float outBytes;
                memory.Read<float>(valueAddress, out outBytes);
                return outBytes;
            }
            catch (Exception)
            {
                throw new Exception(String.Format("Could not read current time from memory\n Try running again as Administrator"));
            }
        }


        private static void SetCurrentTOD(Process snowRunnerProcess, float newTOD)
        {
            try
            {
                var memory = new ExternalMemory(snowRunnerProcess);
                var valueAddress = GetTODMemoryAddress(snowRunnerProcess, memory);

                memory.Write<float>(valueAddress, newTOD);
            }
            catch (Exception)
            {
                throw new Exception(String.Format("eek"));
            }
        }


        private static void CycleTOD(Process snowRunnerProcess)
        {
            try
            {
                // Spawn thread for time loop so we can interrupt it via hotkey
                Task worker = Task.Run(() => LoopTimeThread(snowRunnerProcess));
                ConsoleLogWithColour("Timelapse running!", ConsoleColor.White, ConsoleColor.Red);
            }
            catch (Exception ex)
            {
                throw new Exception(String.Format("Could not start timelapse, {0}", ex.Message));
            }
        }

        private static void StopCyclingTOD(Process snowRunnerProcess)
        {
            ShouldLoopTime = false;

            ConsoleLogWithColour("Timelapse stopped", ConsoleColor.White, ConsoleColor.Red);
            float currentTime = GetCurrentTOD(snowRunnerProcess);

            Console.WriteLine("Current time is:{0}h", currentTime.ToString("0.00"));
        }

        static void LoopTimeThread(Process snowRunnerProcess)
        {
            var memory = new ExternalMemory(snowRunnerProcess);
            var valueAddress = GetTODMemoryAddress(snowRunnerProcess, memory);

            var now = GetCurrentTOD(snowRunnerProcess);
            ShouldLoopTime = true;
            while (ShouldLoopTime)
            {
                memory.Write<float>(valueAddress, now);
                Thread.Sleep(10);
                now = now + 0.03f;
                if (now > 24f) now = now - 24f;
            }
        }

        private static IntPtr GetTODMemoryAddress(Process snowRunnerProcess, ExternalMemory memory)
        {
            var baseAddress = snowRunnerProcess.MainModule.BaseAddress + TODPointer;
            byte[] readBytes;
            memory.ReadRaw(baseAddress, out readBytes, 8);

            var addressInPointer = BitConverter.ToInt64(readBytes, 0);

            //This gives us 1E19DACD310, which is the right address pre-offset!!
            // So check the value there:
            var valueAddress = addressInPointer + TODOffset;
            return (IntPtr)valueAddress;
        }

        private static void DisplayHotkeyInstructions(KeybindingConfig config)
        {
            Console.WriteLine("\n \nHotkeys: \n");
            Console.WriteLine("Ctrl + "+ config.ToggleFlyMode +": \t Toggle free camera mode\n");
            Console.WriteLine("Ctrl + "+ config.ToggleFreezeTime +": \t Toggle in-game time\n");
            Console.WriteLine("Ctrl + "+ config.ToggleTimeLapseMode +": \t Toggle timelapse\n");
            Console.WriteLine("Ctrl + "+ config.GetPlayerLocation +": \t Get player location\n");
            Console.WriteLine("Ctrl + "+ config.TogglePlayerTracking +": \t Toggle continuous player tracking\n");
            Console.WriteLine("Ctrl + "+ config.CaptureCameraScan +": \t Capture/compare freecam vectors\n");
            Console.WriteLine("Ctrl + "+ config.FindCameraPointers +": \t Find module references to camera candidates\n");
            Console.WriteLine("Ctrl + "+ config.ResetCameraScan +": \t Reset camera scan candidates\n");
            Console.WriteLine("Ctrl + "+ config.ProbeCameraChain +": \t Probe the fast camera pointer chain\n");
            Console.WriteLine("Ctrl + "+ config.ToggleCameraTracking +": \t Toggle continuous camera tracking\n");
            Console.WriteLine("Ctrl + "+ config.ProbeCameraTransform +": \t Dump nearby camera transform vectors\n");
            Console.WriteLine("Ctrl + "+ config.ToggleCameraFollow +": \t Toggle camera follow mode\n");
            Console.WriteLine("Ctrl + Shift + "+ config.NudgeCameraPosition +": \t Nudge and restore the candidate camera position\n");
        }

        private static void ConsoleLogWithColour(string content, ConsoleColor foreground, ConsoleColor background)
        {
            Console.BackgroundColor = background;
            Console.ForegroundColor = foreground;
            Console.WriteLine(content);
            Console.ResetColor();
        }

        private static void Hotkey_Pressed(object sender2, HotKeyEventArgs e2, KeybindingConfig config, Process snowRunnerProcess)
        {
            Console.Clear();

            if (e2.Key == config.ToggleFlyMode)
            {
                Console.WriteLine("Toggling freecam...");
                if (IsFreeCamActive)
                {
                    DisableFreeCam(snowRunnerProcess);
                }
                else
                {
                    // Try disabling in case someone quits while in fly mode and then can't revert
                    DisableFreeCam(snowRunnerProcess);
                    EnableFreeCam(snowRunnerProcess);
                }
            }
            else if (e2.Key == config.ToggleFreezeTime)
            {
                if (IsTimePassing)
                {
                    Console.WriteLine("Disabling timer...");
                    StopTODTicker(snowRunnerProcess);
                }
                else
                {
                    // Try disabling in case someone quits while time is stopped and then can't revert
                    Console.WriteLine("Check timer was running...");
                    StopTODTicker(snowRunnerProcess);
                    Console.WriteLine("Resuming timer...");
                    ResumeTODTicker(snowRunnerProcess);
                }
            }
            else if (e2.Key == config.ToggleTimeLapseMode)
            {
                if (ShouldLoopTime)
                {
                    Console.WriteLine("Stopping timelapse");
                    StopCyclingTOD(snowRunnerProcess);
                }
                else
                {
                    Console.WriteLine("Starting timelapse");
                    CycleTOD(snowRunnerProcess);
                }
            }
            else if (e2.Key == config.GetPlayerLocation)
            {
                GetPlayerCoords(snowRunnerProcess);
            }
            else if (e2.Key == config.TogglePlayerTracking)
            {
                TogglePlayerCoordTracking(snowRunnerProcess);
            }
            else if (e2.Key == config.CaptureCameraScan)
            {
                CaptureCameraScan(snowRunnerProcess);
            }
            else if (e2.Key == config.FindCameraPointers)
            {
                FindCameraPointerReferences(snowRunnerProcess);
            }
            else if (e2.Key == config.ResetCameraScan)
            {
                ResetCameraScan();
            }
            else if (e2.Key == config.ProbeCameraChain)
            {
                ProbeKnownCameraChain(snowRunnerProcess);
            }
            else if (e2.Key == config.ToggleCameraTracking)
            {
                ToggleCameraCoordTracking(snowRunnerProcess);
            }
            else if (e2.Key == config.ProbeCameraTransform)
            {
                ProbeCameraTransform(snowRunnerProcess);
            }
            else if (e2.Key == config.ToggleCameraFollow && e2.Modifiers == KeyModifiers.Control)
            {
                ToggleCameraFollow(snowRunnerProcess);
            }
            else if (e2.Key == config.NudgeCameraPosition &&
                e2.Modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                NudgeCameraPosition(snowRunnerProcess);
            }
            else
            {
                Console.WriteLine("Unknown hotkey!");
            }

            DisplayHotkeyInstructions(config);
        }




        internal class StructPointerDescription<T>
            where T : struct
        {
            private readonly IMemory _memory;
            private readonly IntPtr _basePtr;
            private readonly int[] _offsets;
            private T _value;
            public StructPointerDescription(IMemory memory, IntPtr basePtr, params int[] offsets)
            {
                _memory = memory;
                _basePtr = basePtr;
                _offsets = offsets;
            }

            public T Read()
            {
                var ptr = GetLastPtrOfPtrChain(_memory, _basePtr, _offsets);
                if (ptr == IntPtr.Zero)
                    return default;
                //Console.WriteLine($"Player pointer is: {ptr}");
                _memory.Read<T>(ptr, out _value, false);
                return _value;
            }

            public void Write(T value)
            {
                var ptr = GetLastPtrOfPtrChain(_memory, _basePtr, _offsets);
                if (ptr == IntPtr.Zero)
                    return;
                _memory.Write<T>(ptr, value, false);
            }
        }



    }
}
