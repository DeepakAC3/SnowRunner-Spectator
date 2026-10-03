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
        private static bool HasCameraScanBaseline = false;
        private static Dictionary<IntPtr, Vector3> CameraScanBaseline;
        private static Vector3 CameraScanCenter;
        private static List<IntPtr> LastCameraCandidateAddresses = new List<IntPtr>();

        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const uint MemoryCommit = 0x1000;
        private const uint PageGuard = 0x100;
        private const uint PageNoAccess = 0x01;
        private const int CameraScanRadius = 300;
        private const int CameraScanVerticalRadius = 100;
        private const int CameraScanChunkSize = 1024 * 1024;
        private const int PointerSearchRange = 0x400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr processHandle, IntPtr address, out MemoryBasicInformation buffer, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

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
                    Console.WriteLine($"Captured {CameraScanBaseline.Count} candidate vectors. Move the free camera, then press Ctrl+F6 again.");
                    return;
                }

                var changedCandidates = FindChangedCameraVectors(snowRunnerProcess, CameraScanBaseline);
                HasCameraScanBaseline = false;
                CameraScanBaseline = null;

                if (changedCandidates.Count == 0)
                {
                    Console.WriteLine("No changed camera candidates found. Move the free camera farther and try Ctrl+F6 twice again.");
                    return;
                }

                Console.WriteLine($"Changed camera candidates: {changedCandidates.Count}");
                var elevatedChanges = changedCandidates
                    .Where(candidate =>
                        Math.Abs(candidate.Before.Y - CameraScanCenter.Y) <= 20.0f &&
                        Math.Abs(candidate.After.Y - CameraScanCenter.Y) <= 20.0f &&
                        (Vector3.DistanceSquared(candidate.Before, CameraScanCenter) > 4.0f ||
                         Vector3.DistanceSquared(candidate.After, CameraScanCenter) > 4.0f))
                    .OrderByDescending(candidate => candidate.Distance)
                    .Take(20)
                    .ToList();
                var smallChanges = changedCandidates
                    .Where(candidate => candidate.Distance <= 25.0f)
                    .OrderBy(candidate => candidate.Distance)
                    .Take(20)
                    .ToList();
                var results = elevatedChanges.Count > 0
                    ? elevatedChanges
                    : smallChanges.Count > 0
                    ? smallChanges
                    : changedCandidates.Take(20).ToList();

                LastCameraCandidateAddresses = results.Select(candidate => candidate.Address).ToList();

                if (elevatedChanges.Count > 0)
                    Console.WriteLine("Camera-height movement candidates:");
                else if (smallChanges.Count > 0)
                    Console.WriteLine("Small movement candidates:");

                foreach (var candidate in results)
                {
                    Console.WriteLine($"0x{candidate.Address.ToInt64():X}: " +
                        $"{candidate.Before.X:F2},{candidate.Before.Y:F2},{candidate.Before.Z:F2} -> " +
                        $"{candidate.After.X:F2},{candidate.After.Y:F2},{candidate.After.Z:F2} " +
                        $"(delta {candidate.Distance:F2})");
                }
            }
            catch (Exception ex)
            {
                HasCameraScanBaseline = false;
                CameraScanBaseline = null;
                Console.WriteLine($"Camera scan failed: {ex.Message}");
            }
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
                Console.WriteLine("No camera candidates are available. Run the two-step Ctrl+F6 scan first.");
                return;
            }

            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not open SnowRunner for pointer scanning.");

            try
            {
                var targets = new Dictionary<long, List<PointerTarget>>();
                foreach (IntPtr candidate in LastCameraCandidateAddresses)
                {
                    long candidateAddress = candidate.ToInt64();
                    for (int delta = 0; delta <= PointerSearchRange; delta += 4)
                    {
                        long objectAddress = candidateAddress - delta;
                        if (!targets.TryGetValue(objectAddress, out List<PointerTarget> matches))
                        {
                            matches = new List<PointerTarget>();
                            targets.Add(objectAddress, matches);
                        }
                        matches.Add(new PointerTarget { Candidate = candidate, Delta = delta });
                    }
                }

                long moduleStart = snowRunnerProcess.MainModule.BaseAddress.ToInt64();
                long moduleEnd = moduleStart + snowRunnerProcess.MainModule.ModuleMemorySize;
                var references = ScanModuleForPointerReferences(processHandle, moduleStart, moduleEnd, targets);

                if (references.Count == 0)
                {
                    Console.WriteLine("No direct module references found within the first 0x400 bytes before the candidates.");
                    return;
                }

                Console.WriteLine($"Module pointer references: {references.Count}");
                foreach (var reference in references.Take(50))
                {
                    Console.WriteLine($"SnowRunner.exe+0x{reference.ReferenceOffset:X} -> " +
                        $"candidate 0x{reference.Candidate.ToInt64():X} + 0x{reference.Delta:X}");
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private struct PointerReference
        {
            public long ReferenceOffset;
            public IntPtr Candidate;
            public int Delta;
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
