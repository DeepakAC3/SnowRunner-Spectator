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
using System.Text;

namespace SnowFlyer2
{

    partial class Program
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

        private static volatile bool IsFreeCamActive = false;
        private static volatile bool MapFreeCamRestorePending = false;
        private static int MapTransitionGeneration;
        private static bool MapKeyWasDown;
        private static bool EscapeKeyWasDown;
        private static bool ShouldLoopTime = false;
        private static bool IsTimePassing = true;
        private static bool IsTrackingPlayerCoords = false;
        private static bool IsTrackingCameraCoords = false;
        private static bool IsFollowingPlayer = false;
        private static Vector3 CameraFollowOffset;
        private static Vector3 CameraFollowLocalOffset;
        private static readonly object CameraWriteGate = new object();
        private static readonly object FreeCamTransitionGate = new object();
        private static int CameraFollowGeneration;
        private static Vector3 CameraFollowLastRequested;
        private static bool CameraFollowHasLastRequested;
        private static Vector3 CameraFollowSmoothedPosition;
        private static bool CameraFollowHasSmoothedPosition;
        private static Vector3 CameraFollowSmoothedForward;
        private static bool CameraFollowHasSmoothedForward;
        private static long CameraFollowLastSampleTimestamp;
        private static float CameraOrbitAzimuth;
        private static float CameraOrbitElevation;
        private static float CameraOrbitRadius;
        private static bool CameraOrbitHasState;
        private static volatile bool FaceTruckEnabled;
        private static float CameraOrbitPendingMouseX;
        private static float CameraOrbitPendingMouseY;
        private static bool CameraOrbitXInputUnavailable;
        private static bool SpectatorNativeMenuVisible;
        private static long SpectatorNativeInputTimestamp;

        [StructLayout(LayoutKind.Sequential)]
        private struct CameraGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CameraGamepadState
        {
            public uint Packet;
            public CameraGamepad Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint CameraXInputGetState(uint index, out CameraGamepadState state);
        private static DateTime LastMatrixReadbackAdjustmentLog = DateTime.MinValue;
        // Follow diagnostics are kept independently from the worker's lifetime so
        // the overlay (or another UI surface) can explain a stalled follow after
        // the worker has stopped.
        private static string CameraFollowDiagnosticStatus = "Idle";
        private static string CameraFollowDiagnosticReason = "";
        private static int CameraFollowConsecutiveTransientFailures;
        private static int CameraFollowTransientFailureCount;
        private static int CameraFollowWriteFailureCount;
        private static int CameraFollowSkippedTickCount;
        private static long CameraFollowTickCount;
        private static DateTime CameraFollowDiagnosticUpdatedUtc = DateTime.UtcNow;
        private static Vector3 CameraFollowDiagnosticSourcePosition;
        private static Vector3 CameraFollowDiagnosticTargetPosition;
        private static Vector3 CameraFollowDiagnosticOffset;
        private static IntPtr SelectedFollowOwner;
        private static IntPtr SelectedFollowAction;
        private static IntPtr SelectedFollowBody;
        private static IntPtr SelectedFollowFrame;
        private static string SelectedFollowAssetId;
        private static TargetOverlayForm TargetOverlay;
        private static readonly object TargetScanGate = new object();
        private static bool HasCameraNudge = false;
        private static IntPtr CameraNudgeTransform;
        private static byte[] CameraNudgeOriginalTranslation;
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
        private const uint PageReadOnly = 0x02;
        private const uint PageReadWrite = 0x04;
        private const uint PageWriteCopy = 0x08;
        private const uint PageExecuteRead = 0x20;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageExecuteWriteCopy = 0x80;
        private const int CameraScanRadius = 300;
        private const int CameraScanVerticalRadius = 100;
        private const int CameraScanChunkSize = 1024 * 1024;
        private const int PointerSearchRange = 0x400;
        private const int CameraCandidateRetentionCount = 100;
        // Rendered transform discovered through reversible freecam movements.
        // Read-only: this render-state copy is not yet a validated writable source.
        private const long RenderedCameraModuleOffset = 0x2A97EE0;
        private const int RenderedCameraBasisOffsetA = 0x50;
        private const int RenderedCameraBasisOffsetB = 0x60;
        private const int RenderedCameraBasisOffsetC = 0x70;
        // Validated writable freecam source view matrix.  The matrix is
        // row-major; its inverse position is stored Z,Y,X, like the renderer.
        private const long CameraSourceOwnerModuleOffset = 0x2A8EB90;
        // The player singleton used by the loaded position accessor.  The
        // signature can occur in unrelated helper code, so prefer the
        // validated module-global directly and keep the scan as a fallback.
        private const long PlayerSingletonModuleOffset = 0x2A8EB68;
        private const int CameraSourceTransformOffset = 0x08;
        private const int CameraSourceControllerOffset = 0x18;
        private const int CameraSourceControllerTransformOffset = 0x08;
        private const int FollowTargetScanAlignment = 0x08;
        private const int FollowTargetScanChunkSize = 1024 * 1024;
        private const long FollowTargetScanMaxBytes = 8L * 1024 * 1024 * 1024;
        private const int FollowTargetScanMaxMilliseconds = 40000;
        private const int FollowTargetMaxAssetLength = 160;
        private const int FollowTargetTransformSize = 0x40;
        private const int FollowTargetNodeTransformOffset = 0x50;
        private const int FollowTargetBodyTransformOffset = 0x170;
        private const int FollowTargetActionOwnerOffset = 0x30;
        private const int FollowTargetOwnerActionOffset = 0x68;
        private const int FollowTargetOwnerBodyOffset = 0x5D0;
        private const int FollowTargetOwnerModelOffset = 0x60;
        private const int FollowTargetModelResourceOffset = 0x60;
        private const int FollowTargetResourceAssetOffset = 0x10;
        private const int FollowTargetResourceDefinitionOffset = 0x50;
        private const int FollowTargetOwnerNodeOffset = 0x2A0;
        private const int FollowTargetNodeDefinitionOffset = 0x00;
        private const int FollowTargetNodeModelOffset = 0xD0;
        private const int FollowTargetNodeSelfOffset = 0xE8;
        private const int CameraSourceMatrixSize = 0x40;
        private const int CameraSourceWorldPositionOffset = 0xB0;
        private const int CameraSourceTranslationOffset = 0x30;
        private const int CameraSourceCacheFlagOffsetA = 0x80;
        private const int CameraSourceCacheFlagOffsetB = 0xC0;
        private const float CameraSourceHeightNudge = 1.0f;
        private const float CameraSourcePositionTolerance = 0.1f;
        private const float CameraSourceBasisTolerance = 0.02f;
        private const float FollowTargetBasisTolerance = 0.02f;
        private const float CameraSourceMatrixGuardTolerance = 0.001f;
        private const float CameraFollowNoiseThreshold = 0.001f;
        private const float CameraFollowMaxIncrement = 50.0f;
        private const float CameraFollowMaxTruckJump = 100.0f;
        // The follow worker uses a monotonic deadline instead of sleeping for a
        // fixed interval after its memory reads. This keeps camera writes evenly
        // spaced when a read pass takes a little longer than usual.
        private const int CameraFollowPeriodMilliseconds = 6;
        private const float CameraFollowDefaultPositionSmoothingSeconds = 0.045f;
        private const float CameraFollowDefaultRotationSmoothingSeconds = 0.045f;
        private static float CameraFollowPositionSmoothingSeconds =
            CameraFollowDefaultPositionSmoothingSeconds;
        private static float CameraFollowRotationSmoothingSeconds =
            CameraFollowDefaultRotationSmoothingSeconds;
        private const int CameraFollowMaxMatrixGuardSkips = 80;
        // A torn pointer/matrix read is expected occasionally while the game is
        // rebuilding a frame. Keep follow alive through a short burst of those
        // reads; a persistent failure still stops the worker deterministically.
        private const int CameraFollowMaxTransientFailures = 80;
        private const int CameraFollowTranslationWriteAttempts = 3;
        private const float CameraFollowManualRebaseMaxIncrement = CameraFollowMaxIncrement;
        private const float CameraFollowTranslationBasisGuardTolerance = 0.02f;
        private const float CameraFollowTranslationPositionGuardTolerance = 0.20f;
        private const float CameraSourceReadbackBasisTolerance = 0.00005f;
        private const float CameraSourceReadbackTranslationResidualTolerance = CameraSourcePositionTolerance;
        private const float CameraSourceReadbackPositionTolerance = CameraSourcePositionTolerance;
        private const float CameraOrbitAngularSpeed = 1.8f;
        private const float CameraOrbitMouseRadiansPerPixel = 0.003f;
        private const float CameraOrbitMaximumMouseDelta = 512.0f;
        private const float CameraOrbitStickDeadzone = 8689.0f / 32767.0f;
        private const float CameraOrbitMaxElevation = 1.3962634f; // 80 degrees
        private const int CameraSourceStableReadAttempts = 3;
        private const int CameraSourceStableReadDelayMilliseconds = 2;
        private const int VirtualKeyLeft = 0x25;
        private const int VirtualKeyUp = 0x26;
        private const int VirtualKeyRight = 0x27;
        private const int VirtualKeyDown = 0x28;
        private const int VirtualKeyM = 0x4D;
        private const int VirtualKeyEscape = 0x1B;
        // Legacy rig chain; never use it for rendered-camera diagnostics.
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

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

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

        private struct CameraSourceSnapshot
        {
            public IntPtr Owner;
            public IntPtr Transform;
            public IntPtr Controller;
            public float[] Matrix;
            public Vector3 ReconstructedZyx;
            public Vector3 WorldPosition;
            public Vector3 CachedZyx;
            public Vector3 RenderedZyx;
        }

        private sealed class TruckTargetSnapshot
        {
            public IntPtr Owner;
            public IntPtr Action;
            public IntPtr ChassisBody;
            public IntPtr Frame;
            public IntPtr ActionVtable;
            public IntPtr ChassisVtable;
            public string AssetId;
            public string BoneName;
            public Vector3 Position;
            public float[] Basis;
            public bool IsActive;
        }

        // Immutable snapshot for a UI/status bridge.  Keep this type free of
        // controls so callers can poll it from any thread.
        internal sealed class CameraFollowDiagnosticState
        {
            internal CameraFollowDiagnosticState(bool isFollowing, string status,
                string reason, int consecutiveTransientFailures,
                int transientFailureCount, int writeFailureCount,
                int skippedTickCount, long tickCount, DateTime updatedUtc,
                Vector3 sourcePosition, Vector3 targetPosition, Vector3 offset,
                bool faceTruck, bool orbitMode)
            {
                IsFollowing = isFollowing;
                Status = status ?? string.Empty;
                Reason = reason ?? string.Empty;
                ConsecutiveTransientFailures = consecutiveTransientFailures;
                TransientFailureCount = transientFailureCount;
                WriteFailureCount = writeFailureCount;
                SkippedTickCount = skippedTickCount;
                TickCount = tickCount;
                UpdatedUtc = updatedUtc;
                SourcePosition = sourcePosition;
                TargetPosition = targetPosition;
                Offset = offset;
                FaceTruck = faceTruck;
                OrbitMode = orbitMode;
            }

            internal bool IsFollowing { get; }
            internal string Status { get; }
            internal string Reason { get; }
            internal int ConsecutiveTransientFailures { get; }
            internal int TransientFailureCount { get; }
            internal int WriteFailureCount { get; }
            internal int SkippedTickCount { get; }
            internal long TickCount { get; }
            internal DateTime UpdatedUtc { get; }
            internal Vector3 SourcePosition { get; }
            internal Vector3 TargetPosition { get; }
            internal Vector3 Offset { get; }
            internal bool FaceTruck { get; }
            internal bool OrbitMode { get; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            InitializeSpectatorDiagnostics();
            KeybindingManager keybindingManager = new KeybindingManager();
            KeybindingConfig config = keybindingManager.ReadKeybindingConfig();

            Process snowRunnerProcess = attachToSnowRunnerProcess();
            IsFreeCamActive = ReadActualFreeCamFlag(snowRunnerProcess);
            StartSpectatorBridge(snowRunnerProcess);
            if (args.Any(argument => string.Equals(argument, "--ingame-ui",
                StringComparison.OrdinalIgnoreCase)))
                LoadSpectatorUi(snowRunnerProcess);
            Task.Run(() => WatchMapTransitions(snowRunnerProcess));
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
            HotKeyManager.RegisterHotKey(config.ToggleTargetOverlay, KeyModifiers.Control | KeyModifiers.Shift);
            HotKeyManager.HotKeyPressed += (sender2, e2) => Hotkey_Pressed(sender2, e2, config,snowRunnerProcess);

            if (args.Any(argument => string.Equals(argument, "--headless",
                StringComparison.OrdinalIgnoreCase)))
            {
                while (!snowRunnerProcess.HasExited)
                    Thread.Sleep(250);
                return;
            }

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
            int[] PlayerCoordsOffsets = { 0x28, 0x18, 0x1A0 };
            var snowRunnerBase = snowRunnerProcess.MainModule.BaseAddress;

            StructPointerDescription<Vector3> PlayerCoord;
            if (coordsPtr.Found)
            {
                var memory = new ExternalMemory(snowRunnerProcess);
                PlayerCoordsBasePtr = ReadOffsetFromCallToLeaSingleton(memory, snowRunnerBase + coordsPtr.Offset + GetOpcodeLengthOfPattern(PatternPlayerCoords));
                if (PlayerCoordsBasePtr == IntPtr.Zero)
                    return;
                PlayerCoord = new StructPointerDescription<Vector3>(memory, PlayerCoordsBasePtr, PlayerCoordsOffsets);

                var stored = PlayerCoord.Read();
                if (IsFiniteVector(stored))
                {
                    var playerVec = StoredZyxToWorld(stored);
                    Console.WriteLine($"Coord: {playerVec.X:F2} {playerVec.Y:F2} {playerVec.Z:F2}");
                }
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

                int[] playerCoordsOffsets = { 0x28, 0x18, 0x1A0 };
                var playerCoord = new StructPointerDescription<Vector3>(
                    memory,
                    playerCoordsBasePtr,
                    playerCoordsOffsets);

                while (IsTrackingPlayerCoords)
                {
                    var stored = playerCoord.Read();
                    if (IsFiniteVector(stored))
                    {
                        var playerVec = StoredZyxToWorld(stored);
                        Console.WriteLine($"LOCAL: {playerVec.X:F2} {playerVec.Y:F2} {playerVec.Z:F2}");
                    }
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

        private static void ProbeRenderedCameraChain(Process snowRunnerProcess)
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
                if (!TryReadRenderedCameraVector(processHandle, moduleBase, out Vector3 value, out Vector3 stored))
                {
                    Console.WriteLine("Rendered camera chain did not resolve.");
                    return;
                }

                TryReadRenderedCameraBase(processHandle, moduleBase, out long cameraBase);
                Console.WriteLine($"RENDERED CAMERA: SnowRunner.exe+0x{RenderedCameraModuleOffset:X} -> " +
                    $"0x{cameraBase:X}; world XYZ = {value.X:F2},{value.Y:F2},{value.Z:F2}; " +
                    $"stored ZYX = {stored.X:F2},{stored.Y:F2},{stored.Z:F2}");
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
            lock (CameraWriteGate)
            {
                if (IsFollowingPlayer)
                {
                    IsFollowingPlayer = false;
                    CameraFollowGeneration++;
                    ResetCameraFollowSmoothingLocked(default(Vector3));
                    ResetCameraOrbitLocked();
                    RecordCameraFollowStoppedLocked("Stopped by user.");
                    Console.WriteLine("Camera follow stopped.");
                    return;
                }
            }

            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead,
                false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                Console.WriteLine("Could not open SnowRunner for camera follow mode.");
                return;
            }

            try
            {
                lock (CameraWriteGate)
                {
                    if (IsFollowingPlayer)
                        return;
                    if (!TryReadTruckScanIdentity(processHandle,
                            snowRunnerProcess.MainModule.BaseAddress,
                            out IntPtr activeOwner, out IntPtr actionVtable,
                            out IntPtr chassisVtable))
                    {
                        Console.WriteLine("No valid spectator target could be resolved for follow mode.");
                        return;
                    }

                    IntPtr owner = SelectedFollowOwner;
                    if (!IsValidCameraPointer(owner.ToInt64()))
                        owner = activeOwner;
                    if (!TryReadValidatedTruckOwner(processHandle, owner,
                            actionVtable, chassisVtable, activeOwner,
                            out TruckTargetSnapshot target))
                    {
                        Console.WriteLine("Selected target is no longer a validated truck chassis; follow refused.");
                        return;
                    }
                    if (IsValidCameraPointer(SelectedFollowOwner.ToInt64()) &&
                        !MatchesSelectedTruck(target))
                    {
                        Console.WriteLine("Selected truck identity changed; follow refused.");
                        return;
                    }

                    CameraSourceSnapshot source;
                    if (!TryReadCameraSourceSnapshot(processHandle,
                            snowRunnerProcess.MainModule.BaseAddress,
                            true, true, out source))
                    {
                        Console.WriteLine("Validated source camera state could not be read for follow mode.");
                        return;
                    }

                    CameraFollowOffset = source.WorldPosition - target.Position;
                    CameraFollowLocalOffset = WorldOffsetToLocal(CameraFollowOffset, target.Basis);
                    CameraFollowLastRequested = source.WorldPosition;
                    CameraFollowHasLastRequested = true;
                    ResetCameraFollowSmoothingLocked(source.WorldPosition);
                    ResetCameraOrbitLocked();
                    IsFollowingPlayer = true;
                    ResetCameraFollowDiagnosticsLocked("Starting");
                    CameraFollowDiagnosticSourcePosition = source.WorldPosition;
                    CameraFollowDiagnosticTargetPosition = source.WorldPosition;
                    CameraFollowDiagnosticOffset = CameraFollowOffset;

                    int generation = ++CameraFollowGeneration;
                    Console.WriteLine($"Camera follow started on {target.AssetId} with offset {CameraFollowOffset.X:F2} " +
                        $"{CameraFollowOffset.Y:F2} {CameraFollowOffset.Z:F2}.");
                    Task.Run(() => TrackCameraFollow(snowRunnerProcess, owner,
                        target.Action, target.ChassisBody, target.Frame,
                        target.AssetId,
                        snowRunnerProcess.MainModule.BaseAddress, source.Transform, generation));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Camera follow could not start: {ex.Message}");
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        internal static CameraFollowDiagnosticState GetCameraFollowDiagnosticState()
        {
            lock (CameraWriteGate)
            {
                return new CameraFollowDiagnosticState(IsFollowingPlayer,
                    CameraFollowDiagnosticStatus, CameraFollowDiagnosticReason,
                    CameraFollowConsecutiveTransientFailures,
                    CameraFollowTransientFailureCount,
                    CameraFollowWriteFailureCount,
                    CameraFollowSkippedTickCount,
                    CameraFollowTickCount,
                    CameraFollowDiagnosticUpdatedUtc,
                    CameraFollowDiagnosticSourcePosition,
                    CameraFollowDiagnosticTargetPosition,
                    CameraFollowDiagnosticOffset,
                    FaceTruckEnabled,
                    IsFollowingPlayer);
            }
        }

        internal static string GetCameraFollowStatus()
        {
            CameraFollowDiagnosticState state = GetCameraFollowDiagnosticState();
            return string.IsNullOrEmpty(state.Reason)
                ? state.Status
                : state.Status + ": " + state.Reason;
        }

        internal static void SetCameraFollowSmoothing(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds))
                return;
            float clamped = Math.Max(0.005f, Math.Min(0.5f, seconds));
            lock (CameraWriteGate)
            {
                CameraFollowPositionSmoothingSeconds = clamped;
                CameraFollowRotationSmoothingSeconds = clamped;
                CameraFollowHasSmoothedPosition = false;
                CameraFollowHasSmoothedForward = false;
            }
        }

        // Reframe the selected truck while preserving the current local azimuth.
        // Distance is the horizontal camera radius and height is the local Y
        // offset, both in game world units.
        internal static void SetCameraFollowFraming(float distance, float height)
        {
            if (float.IsNaN(distance) || float.IsInfinity(distance) ||
                float.IsNaN(height) || float.IsInfinity(height))
                return;
            float clampedDistance = Math.Max(0.5f, Math.Min(500.0f, distance));
            float clampedHeight = Math.Max(-100.0f, Math.Min(100.0f, height));
            lock (CameraWriteGate)
            {
                Vector3 local = CameraFollowLocalOffset;
                float horizontalLength = (float)Math.Sqrt(
                    local.X * local.X + local.Z * local.Z);
                if (horizontalLength < 0.001f)
                {
                    local.X = 0.0f;
                    local.Z = clampedDistance;
                }
                else
                {
                    float scale = clampedDistance / horizontalLength;
                    local.X *= scale;
                    local.Z *= scale;
                }
                local.Y = clampedHeight;
                CameraFollowLocalOffset = local;
                CameraFollowHasSmoothedPosition = false;
                CameraOrbitHasState = false;
            }
        }

        private static void ResetCameraFollowDiagnosticsLocked(string status)
        {
            CameraFollowDiagnosticStatus = status ?? "Idle";
            CameraFollowDiagnosticReason = string.Empty;
            CameraFollowConsecutiveTransientFailures = 0;
            CameraFollowTransientFailureCount = 0;
            CameraFollowWriteFailureCount = 0;
            CameraFollowSkippedTickCount = 0;
            CameraFollowTickCount = 0;
            CameraFollowDiagnosticSourcePosition = default(Vector3);
            CameraFollowDiagnosticTargetPosition = default(Vector3);
            CameraFollowDiagnosticOffset = default(Vector3);
            CameraFollowDiagnosticUpdatedUtc = DateTime.UtcNow;
        }

        private static bool RecordCameraFollowTransientFailureLocked(string reason)
        {
            CameraFollowDiagnosticStatus = "Recovering";
            CameraFollowDiagnosticReason = reason ?? "Transient follow read/write failure.";
            CameraFollowConsecutiveTransientFailures++;
            CameraFollowTransientFailureCount++;
            CameraFollowSkippedTickCount++;
            CameraFollowDiagnosticUpdatedUtc = DateTime.UtcNow;
            return CameraFollowConsecutiveTransientFailures >=
                CameraFollowMaxTransientFailures;
        }

        private static void RecordCameraFollowWriteFailureLocked(string reason)
        {
            CameraFollowWriteFailureCount++;
            CameraFollowDiagnosticReason = reason ?? "Camera source write failed.";
            CameraFollowDiagnosticUpdatedUtc = DateTime.UtcNow;
        }

        private static void RecordCameraFollowSuccessLocked(Vector3 sourcePosition,
            Vector3 targetPosition, Vector3 offset)
        {
            CameraFollowDiagnosticStatus = "Following";
            CameraFollowDiagnosticReason = string.Empty;
            CameraFollowConsecutiveTransientFailures = 0;
            CameraFollowTickCount++;
            CameraFollowDiagnosticSourcePosition = sourcePosition;
            CameraFollowDiagnosticTargetPosition = targetPosition;
            CameraFollowDiagnosticOffset = offset;
            CameraFollowDiagnosticUpdatedUtc = DateTime.UtcNow;
        }

        private static void RecordCameraFollowStoppedLocked(string reason)
        {
            CameraFollowDiagnosticStatus = "Stopped";
            CameraFollowDiagnosticReason = reason ?? string.Empty;
            CameraFollowConsecutiveTransientFailures = 0;
            CameraFollowDiagnosticUpdatedUtc = DateTime.UtcNow;
        }

        private static void ToggleTargetOverlay(Process snowRunnerProcess)
        {
            if (TargetOverlay != null && !TargetOverlay.IsDisposed)
            {
                TargetOverlay.Close();
                TargetOverlay = null;
                return;
            }

            TargetOverlay = new TargetOverlayForm(
                () => ScanValidatedTruckTargets(snowRunnerProcess),
                SelectFollowTarget,
                () => AttachSelectedTruck(snowRunnerProcess),
                value => SetFaceTruckEnabled(value),
                () => FaceTruckEnabled,
                snowRunnerProcess.MainWindowHandle,
                () => GetCameraFollowStatus());
            TargetOverlay.FormClosed += (sender, args) => TargetOverlay = null;
            TargetOverlay.Show();
        }

        private static void SetFaceTruckEnabled(bool enabled)
        {
            lock (CameraWriteGate)
            {
                FaceTruckEnabled = enabled;
                CameraFollowHasSmoothedForward = false;
            }
            Console.WriteLine(enabled
                ? "Face truck enabled for validated follow."
                : "Face truck disabled; follow preserves native camera rotation.");
        }

        private static void QueueCameraOrbitMouse(Process game, float x, float y)
        {
            if (!IsFiniteFloat(x) || !IsFiniteFloat(y) ||
                Math.Abs(x) > CameraOrbitMaximumMouseDelta ||
                Math.Abs(y) > CameraOrbitMaximumMouseDelta || !IsSnowRunnerForeground(game))
                return;
            lock (CameraWriteGate)
            {
                if (!IsFollowingPlayer || SpectatorNativeMenuVisible)
                    return;
                CameraOrbitPendingMouseX = Math.Max(-CameraOrbitMaximumMouseDelta,
                    Math.Min(CameraOrbitMaximumMouseDelta, CameraOrbitPendingMouseX + x));
                CameraOrbitPendingMouseY = Math.Max(-CameraOrbitMaximumMouseDelta,
                    Math.Min(CameraOrbitMaximumMouseDelta, CameraOrbitPendingMouseY + y));
            }
        }

        private static void ResetCameraFollowSmoothingLocked(Vector3 initialPosition)
        {
            CameraFollowSmoothedPosition = initialPosition;
            CameraFollowHasSmoothedPosition = initialPosition != default(Vector3);
            CameraFollowSmoothedForward = default(Vector3);
            CameraFollowHasSmoothedForward = false;
            CameraFollowLastSampleTimestamp = 0;
        }

        private static void ResetCameraOrbitLocked()
        {
            CameraOrbitAzimuth = 0.0f;
            CameraOrbitElevation = 0.0f;
            CameraOrbitRadius = 0.0f;
            CameraOrbitHasState = false;
            CameraOrbitPendingMouseX = 0;
            CameraOrbitPendingMouseY = 0;
        }

        private static void InitializeCameraOrbitLocked()
        {
            if (CameraFollowLocalOffset.LengthSquared() < 0.01f)
            {
                ResetCameraOrbitLocked();
                return;
            }

            CameraOrbitRadius = CameraFollowLocalOffset.Length();
            CameraOrbitAzimuth = (float)Math.Atan2(CameraFollowLocalOffset.X,
                CameraFollowLocalOffset.Z);
            CameraOrbitElevation = (float)Math.Asin(Math.Max(-1.0f,
                Math.Min(1.0f, CameraFollowLocalOffset.Y / CameraOrbitRadius)));
            CameraOrbitHasState = true;
        }

        private static void SelectFollowTarget(FollowTargetInfo target)
        {
            if (target == null || target.Address == IntPtr.Zero)
                return;
            lock (CameraWriteGate)
            {
                bool sameTarget = SelectedFollowOwner == target.OwnerAddress &&
                    SelectedFollowAction == target.ActionAddress &&
                    SelectedFollowBody == target.ChassisBodyAddress &&
                    SelectedFollowFrame == target.FrameAddress &&
                    string.Equals(SelectedFollowAssetId, target.AssetId,
                        StringComparison.Ordinal);
                if (IsFollowingPlayer && !sameTarget)
                {
                    IsFollowingPlayer = false;
                    CameraFollowGeneration++;
                    ResetCameraFollowSmoothingLocked(default(Vector3));
                    ResetCameraOrbitLocked();
                    RecordCameraFollowStoppedLocked("Stopped before changing target.");
                    Console.WriteLine("Camera follow stopped before changing target.");
                }
                SelectedFollowOwner = target.OwnerAddress;
                SelectedFollowAction = target.ActionAddress;
                SelectedFollowBody = target.ChassisBodyAddress;
                SelectedFollowFrame = target.FrameAddress;
                SelectedFollowAssetId = target.AssetId;
                if (!sameTarget)
                {
                    ResetCameraFollowSmoothingLocked(default(Vector3));
                    ResetCameraOrbitLocked();
                }
            }
            Console.WriteLine($"Selected truck chassis {target.AssetId} {target.Address.ToInt64():X} at " +
                $"{target.Position.X:F2} {target.Position.Y:F2} {target.Position.Z:F2}.");
        }

        private static bool MatchesSelectedTruck(TruckTargetSnapshot target)
        {
            return target != null &&
                target.Owner == SelectedFollowOwner &&
                target.Action == SelectedFollowAction &&
                target.ChassisBody == SelectedFollowBody &&
                target.Frame == SelectedFollowFrame &&
                string.Equals(target.AssetId, SelectedFollowAssetId,
                    StringComparison.Ordinal);
        }

        private static string AttachSelectedTruck(Process snowRunnerProcess)
        {
            IntPtr processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation,
                false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                return "Could not open SnowRunner for truck attach.";

            try
            {
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                lock (CameraWriteGate)
                {
                    if (!TryReadTruckScanIdentity(processHandle, moduleBase,
                            out IntPtr activeOwner, out IntPtr actionVtable,
                            out IntPtr chassisVtable))
                        return "Active truck identity is unavailable; attach refused.";
                    if (!IsValidCameraPointer(SelectedFollowOwner.ToInt64()))
                        return "Select a validated truck chassis first.";
                    if (!TryReadValidatedTruckOwner(processHandle, SelectedFollowOwner,
                            actionVtable, chassisVtable, activeOwner,
                            out TruckTargetSnapshot target))
                        return "Selected truck identity changed; refresh and select it again.";
                    if (!MatchesSelectedTruck(target))
                        return "Selected truck identity changed; refresh and select it again.";

                    if (!IsFreeCamActive && !EnableFreeCam(snowRunnerProcess))
                        return "Freecam could not be enabled; attach refused.";

                    CameraSourceSnapshot source;
                    CameraSourceSnapshot prewrite;
                    if (!TryReadCameraSourceSnapshot(processHandle, moduleBase,
                            true, true, out source) ||
                        !TryReadCameraSourceSnapshot(processHandle, moduleBase,
                            true, true, out prewrite) ||
                        MaxAbsDifference(source.Matrix, prewrite.Matrix) >
                            CameraSourceMatrixGuardTolerance)
                        return "Validated source camera state changed during attach guard.";
                    source = prewrite;

                    if (!TryReadValidatedTruckOwner(processHandle, SelectedFollowOwner,
                            actionVtable, chassisVtable, activeOwner,
                            out target))
                        return "Selected truck identity changed during attach guard.";
                    if (!MatchesSelectedTruck(target))
                        return "Selected truck identity changed during attach guard.";

                    Vector3 forward = new Vector3(source.Matrix[10],
                        source.Matrix[6], source.Matrix[2]);
                    if (!IsFiniteVector(forward) || forward.LengthSquared() < 0.25f)
                        return "Camera forward vector is invalid; attach refused.";
                    forward = Vector3.Normalize(forward);
                    Vector3 attachPosition = target.Position +
                        new Vector3(0.0f, 2.0f, 0.0f) - forward * 12.0f;
                    if (!TryWriteSourceTranslation(processHandle, source.Transform,
                            source.Matrix, attachPosition, out _))
                        return "Attach recenter write failed validation.";

                    if (IsFollowingPlayer)
                    {
                        IsFollowingPlayer = false;
                        CameraFollowGeneration++;
                        ResetCameraFollowSmoothingLocked(default(Vector3));
                        ResetCameraOrbitLocked();
                        RecordCameraFollowStoppedLocked("Stopped before attach recenter.");
                    }
                    CameraFollowOffset = attachPosition - target.Position;
                    CameraFollowLocalOffset = WorldOffsetToLocal(CameraFollowOffset,
                        target.Basis);
                    CameraFollowLastRequested = attachPosition;
                    CameraFollowHasLastRequested = true;
                    ResetCameraFollowSmoothingLocked(attachPosition);
                    ResetCameraOrbitLocked();
                    IsFollowingPlayer = true;
                    ResetCameraFollowDiagnosticsLocked("Starting");
                    CameraFollowDiagnosticSourcePosition = attachPosition;
                    CameraFollowDiagnosticTargetPosition = attachPosition;
                    CameraFollowDiagnosticOffset = CameraFollowOffset;
                    int generation = ++CameraFollowGeneration;
                    Task.Run(() => TrackCameraFollow(snowRunnerProcess,
                        target.Owner, target.Action, target.ChassisBody, target.Frame,
                        target.AssetId, moduleBase, source.Transform, generation));
                    return $"Attached to {target.AssetId}; follow is active.";
                }
            }
            catch (Exception ex)
            {
                return $"Attach failed: {ex.Message}";
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static void WatchMapTransitions(Process snowRunnerProcess)
        {
            try
            {
                while (!snowRunnerProcess.HasExited)
                {
                    if (!IsSnowRunnerForeground(snowRunnerProcess))
                    {
                        MapKeyWasDown = false;
                        EscapeKeyWasDown = false;
                        Thread.Sleep(10);
                        continue;
                    }
                    bool mapDown = GetAsyncKeyState(VirtualKeyM) < 0;
                    bool escapeDown = GetAsyncKeyState(VirtualKeyEscape) < 0;
                    if (mapDown && !MapKeyWasDown)
                    {
                        if (IsFreeCamActive)
                            BeginMapFreeCamSafety(snowRunnerProcess);
                        else if (MapFreeCamRestorePending)
                            RequestMapFreeCamRestore(snowRunnerProcess);
                    }
                    if (escapeDown && !EscapeKeyWasDown && MapFreeCamRestorePending)
                        RequestMapFreeCamRestore(snowRunnerProcess);
                    MapKeyWasDown = mapDown;
                    EscapeKeyWasDown = escapeDown;
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Map transition watcher stopped: {ex.Message}");
            }
        }

        private static bool IsSnowRunnerForeground(Process snowRunnerProcess)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(foreground, out uint foregroundPid);
            return foregroundPid == (uint)snowRunnerProcess.Id;
        }

        private static void BeginMapFreeCamSafety(Process snowRunnerProcess)
        {
            bool disable;
            lock (CameraWriteGate)
            {
                disable = IsFreeCamActive && !MapFreeCamRestorePending;
                if (!disable)
                    return;
                MapFreeCamRestorePending = true;
                MapTransitionGeneration++;
                if (IsFollowingPlayer)
                {
                    IsFollowingPlayer = false;
                    CameraFollowGeneration++;
                    ResetCameraFollowSmoothingLocked(default(Vector3));
                    ResetCameraOrbitLocked();
                    RecordCameraFollowStoppedLocked("Map transition stopped follow.");
                }
            }
            Console.WriteLine("Map opening: follow stopped and freecam is being disabled.");
            bool disabled = DisableFreeCam(snowRunnerProcess);
            lock (CameraWriteGate)
            {
                if (!disabled || IsFreeCamActive)
                {
                    MapFreeCamRestorePending = false;
                    Console.WriteLine("Map opening: freecam disable failed; restore state cleared.");
                }
            }
        }

        private static void RequestMapFreeCamRestore(Process snowRunnerProcess)
        {
            int generation;
            lock (CameraWriteGate)
            {
                if (!MapFreeCamRestorePending)
                    return;
                generation = ++MapTransitionGeneration;
            }
            Task.Run(() =>
            {
                Thread.Sleep(500);
                bool restore;
                lock (CameraWriteGate)
                {
                    restore = MapFreeCamRestorePending &&
                        generation == MapTransitionGeneration &&
                        !IsFreeCamActive;
                }
                if (!restore)
                    return;
                Console.WriteLine("Map closed: restoring freecam after the world camera is ready.");
                bool enabled = EnableFreeCam(snowRunnerProcess);
                lock (CameraWriteGate)
                {
                    if (enabled && generation == MapTransitionGeneration)
                        MapFreeCamRestorePending = false;
                }
            });
        }

        private static IReadOnlyList<FollowTargetInfo> ScanValidatedTruckTargets(Process snowRunnerProcess)
        {
            lock (TargetScanGate)
            {
                var results = new List<FollowTargetInfo>();
                IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead,
                    false, snowRunnerProcess.Id);
                if (processHandle == IntPtr.Zero)
                    return results;

                try
                {
                    if (!TryReadTruckScanIdentity(processHandle,
                            snowRunnerProcess.MainModule.BaseAddress,
                            out IntPtr activeOwner, out IntPtr actionVtable,
                            out IntPtr chassisVtable))
                    {
                        Console.WriteLine("Truck chassis scan: active action/body identity is not available.");
                        return results;
                    }

                    var byOwner = new Dictionary<IntPtr, TruckTargetSnapshot>();
                    var stopwatch = Stopwatch.StartNew();
                    long scannedBytes = 0;
                    bool truncated = false;
                    IntPtr address = IntPtr.Zero;
                    byte[] chunk = new byte[FollowTargetScanChunkSize];
                    while (true)
                    {
                        int queryResult = VirtualQueryEx(processHandle, address,
                            out MemoryBasicInformation region,
                            (uint)Marshal.SizeOf(typeof(MemoryBasicInformation)));
                        if (queryResult == 0 || region.RegionSize.ToInt64() <= 0)
                            break;

                        long regionBase = region.BaseAddress.ToInt64();
                        long regionSize = region.RegionSize.ToInt64();
                        long next = regionBase + regionSize;
                        if (next <= regionBase)
                            break;

                        if (region.State == MemoryCommit && region.Type == MemPrivate &&
                            IsReadableMemoryProtection(region.Protect))
                        {
                            long regionOffset = 0;
                            while (regionOffset < regionSize)
                            {
                                if (stopwatch.ElapsedMilliseconds >= FollowTargetScanMaxMilliseconds ||
                                    scannedBytes >= FollowTargetScanMaxBytes)
                                {
                                    truncated = true;
                                    break;
                                }

                                int bytesToRead = (int)Math.Min(
                                    FollowTargetScanChunkSize, regionSize - regionOffset);
                                if (scannedBytes + bytesToRead > FollowTargetScanMaxBytes)
                                    bytesToRead = (int)(FollowTargetScanMaxBytes - scannedBytes);
                                if (bytesToRead < 8)
                                    break;

                                if (ReadProcessMemory(processHandle,
                                        new IntPtr(regionBase + regionOffset), chunk,
                                        bytesToRead, out IntPtr bytesRead))
                                {
                                    int readable = Math.Min(bytesToRead, bytesRead.ToInt32());
                                    long absoluteStart = regionBase + regionOffset;
                                    int alignment = (int)((FollowTargetScanAlignment -
                                        (absoluteStart & (FollowTargetScanAlignment - 1))) &
                                        (FollowTargetScanAlignment - 1));
                                    for (int offset = alignment; offset + 8 <= readable;
                                        offset += FollowTargetScanAlignment)
                                    {
                                        if (BitConverter.ToInt64(chunk, offset) != actionVtable.ToInt64())
                                            continue;
                                        IntPtr action = new IntPtr(absoluteStart + offset);
                                        if (TryReadValidatedTruckTarget(processHandle, action,
                                                actionVtable, chassisVtable, activeOwner,
                                                out TruckTargetSnapshot target) &&
                                            !byOwner.ContainsKey(target.Owner))
                                        {
                                            byOwner.Add(target.Owner, target);
                                        }
                                    }
                                }

                                scannedBytes += bytesToRead;
                                regionOffset += bytesToRead;
                            }
                            if (truncated)
                                break;
                        }

                        address = new IntPtr(next);
                    }

                    foreach (TruckTargetSnapshot target in byOwner.Values)
                    {
                        results.Add(new FollowTargetInfo
                        {
                            Address = target.ChassisBody,
                            OwnerAddress = target.Owner,
                            ActionAddress = target.Action,
                            FrameAddress = target.Frame,
                            ChassisBodyAddress = target.ChassisBody,
                            ChassisBodyVtable = target.ChassisVtable,
                            AssetId = target.AssetId,
                            BoneName = target.BoneName,
                            Position = new Vector3Display(target.Position.X,
                                target.Position.Y, target.Position.Z),
                            IsActive = target.IsActive,
                            Name = target.AssetId
                        });
                    }

                    results.Sort((left, right) =>
                    {
                        if (left.IsActive != right.IsActive)
                            return left.IsActive ? -1 : 1;
                        return string.Compare(left.AssetId, right.AssetId,
                            StringComparison.OrdinalIgnoreCase);
                    });
                    Console.WriteLine($"Truck chassis scan: {results.Count} validated targets, " +
                        $"{scannedBytes / (1024 * 1024)} MiB in {stopwatch.Elapsed.TotalSeconds:F1}s" +
                        (truncated ? " (TRUNCATED at 40s/8GiB)." : "."));
                }
                finally
                {
                    CloseHandle(processHandle);
                }
                return results;
            }
        }

        private static bool IsReadableMemoryProtection(uint protection)
        {
            if ((protection & PageGuard) != 0 || (protection & PageNoAccess) != 0)
                return false;
            uint baseProtection = protection & 0xFF;
            return baseProtection == PageReadOnly || baseProtection == PageReadWrite ||
                baseProtection == PageWriteCopy || baseProtection == PageExecuteRead ||
                baseProtection == PageExecuteReadWrite || baseProtection == PageExecuteWriteCopy;
        }

        private static bool TryReadTruckScanIdentity(IntPtr processHandle,
            IntPtr moduleBase, out IntPtr activeOwner, out IntPtr actionVtable,
            out IntPtr chassisVtable)
        {
            activeOwner = IntPtr.Zero;
            actionVtable = IntPtr.Zero;
            chassisVtable = IntPtr.Zero;
            IntPtr singleton = new IntPtr(moduleBase.ToInt64() + PlayerSingletonModuleOffset);
            if (!TryReadPointer(processHandle, singleton, out long rootValue) ||
                !IsValidCameraPointer(rootValue) ||
                !TryReadPointer(processHandle, new IntPtr(rootValue + 0x20),
                    out long ownerValue) || !IsValidCameraPointer(ownerValue) ||
                !TryReadPointer(processHandle, new IntPtr(ownerValue + FollowTargetOwnerActionOffset),
                    out long actionValue) || !IsValidCameraPointer(actionValue) ||
                !TryReadPointer(processHandle, new IntPtr(actionValue),
                    out long actionVtableValue) ||
                !TryReadPointer(processHandle, new IntPtr(actionValue + FollowTargetActionOwnerOffset),
                    out long reciprocalOwner) || reciprocalOwner != ownerValue ||
                !TryReadPointer(processHandle, new IntPtr(rootValue + 0x28),
                    out long firstValue) || !IsValidCameraPointer(firstValue) ||
                !TryReadPointer(processHandle, new IntPtr(firstValue + 0x18),
                    out long bodyValue) || !IsValidCameraPointer(bodyValue) ||
                !TryReadPointer(processHandle, new IntPtr(ownerValue + FollowTargetOwnerBodyOffset),
                    out long ownerBodyValue) || bodyValue != ownerBodyValue ||
                !TryReadPointer(processHandle, new IntPtr(bodyValue),
                    out long chassisVtableValue))
                return false;

            activeOwner = new IntPtr(ownerValue);
            actionVtable = new IntPtr(actionVtableValue);
            chassisVtable = new IntPtr(chassisVtableValue);
            return IsValidCameraPointer(actionVtableValue) &&
                IsValidCameraPointer(chassisVtableValue);
        }

        private static bool TryReadValidatedTruckTarget(IntPtr processHandle,
            IntPtr action, IntPtr actionVtable, IntPtr chassisVtable,
            IntPtr activeOwner, out TruckTargetSnapshot target)
        {
            target = null;
            if (!IsValidCameraPointer(action.ToInt64()) ||
                !IsValidCameraPointer(actionVtable.ToInt64()) ||
                !IsValidCameraPointer(chassisVtable.ToInt64()) ||
                !TryReadPointer(processHandle, action, out long currentVtable) ||
                currentVtable != actionVtable.ToInt64() ||
                !TryReadPointer(processHandle,
                    new IntPtr(action.ToInt64() + FollowTargetActionOwnerOffset),
                    out long ownerValue) || !IsValidCameraPointer(ownerValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(ownerValue + FollowTargetOwnerActionOffset),
                    out long reciprocalAction) || reciprocalAction != action.ToInt64() ||
                !TryReadPointer(processHandle,
                    new IntPtr(ownerValue + FollowTargetOwnerBodyOffset),
                    out long bodyValue) || !IsValidCameraPointer(bodyValue) ||
                !TryReadPointer(processHandle, new IntPtr(bodyValue),
                    out long bodyVtable) || bodyVtable != chassisVtable.ToInt64())
                return false;

            if (!TryReadPointer(processHandle,
                    new IntPtr(ownerValue + FollowTargetOwnerModelOffset),
                    out long modelValue) || !IsValidCameraPointer(modelValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(modelValue + FollowTargetModelResourceOffset),
                    out long resourceValue) || !IsValidCameraPointer(resourceValue) ||
                !TryReadAsciiPointer(processHandle,
                    new IntPtr(resourceValue + FollowTargetResourceAssetOffset),
                    out string assetId) ||
                !assetId.StartsWith("trucks_", StringComparison.Ordinal) ||
                assetId.StartsWith("trucks_trailers_", StringComparison.Ordinal))
                return false;

            if (!TryReadPointer(processHandle,
                    new IntPtr(ownerValue + FollowTargetOwnerNodeOffset),
                    out long nodeValue) || !IsValidCameraPointer(nodeValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(nodeValue + FollowTargetNodeDefinitionOffset),
                    out long definitionValue) || !IsValidCameraPointer(definitionValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(definitionValue), out long boneValue) ||
                !IsValidCameraPointer(boneValue) ||
                !TryReadAscii(processHandle, new IntPtr(boneValue),
                    out string boneName) ||
                !IsAllowedTruckBone(assetId, boneName) ||
                !TryReadPointer(processHandle,
                    new IntPtr(resourceValue + FollowTargetResourceDefinitionOffset),
                    out long resourceDefinition) || resourceDefinition != definitionValue ||
                !TryReadPointer(processHandle,
                    new IntPtr(nodeValue + FollowTargetNodeModelOffset),
                    out long nodeModel) || nodeModel != modelValue ||
                !TryReadPointer(processHandle,
                    new IntPtr(nodeValue + FollowTargetNodeSelfOffset),
                    out long nodeSelf) || nodeSelf != nodeValue)
                return false;

            if (!TryReadTransform(processHandle, new IntPtr(nodeValue),
                    FollowTargetNodeTransformOffset, out Vector3 nodePosition,
                    out float[] nodeBasis) ||
                !TryReadTransform(processHandle, new IntPtr(bodyValue),
                    FollowTargetBodyTransformOffset, out Vector3 bodyPosition,
                    out float[] bodyBasis))
                return false;

            target = new TruckTargetSnapshot
            {
                Owner = new IntPtr(ownerValue),
                Action = action,
                ChassisBody = new IntPtr(bodyValue),
                Frame = new IntPtr(nodeValue),
                ActionVtable = actionVtable,
                ChassisVtable = chassisVtable,
                AssetId = assetId,
                BoneName = boneName,
                Position = bodyPosition,
                Basis = bodyBasis,
                IsActive = new IntPtr(ownerValue) == activeOwner
            };
            return true;
        }

        private static bool TryReadValidatedTruckOwner(IntPtr processHandle,
            IntPtr owner, IntPtr actionVtable, IntPtr chassisVtable,
            IntPtr activeOwner, out TruckTargetSnapshot target)
        {
            target = null;
            if (!IsValidCameraPointer(owner.ToInt64()) ||
                !TryReadPointer(processHandle,
                    new IntPtr(owner.ToInt64() + FollowTargetOwnerActionOffset),
                    out long actionValue) || !IsValidCameraPointer(actionValue))
                return false;
            if (!TryReadValidatedTruckTarget(processHandle, new IntPtr(actionValue),
                    actionVtable, chassisVtable, activeOwner, out target))
                return false;
            return target.Owner == owner;
        }

        private static bool IsAllowedTruckBone(string assetId, string boneName)
        {
            if (string.IsNullOrEmpty(assetId) || string.IsNullOrEmpty(boneName))
                return false;
            return boneName == "BoneChassis_cdt";
        }

        private static bool TryReadTransform(IntPtr processHandle, IntPtr frame,
            int transformOffset, out Vector3 position, out float[] basis)
        {
            position = default;
            basis = null;
            byte[] bytes;
            if (!TryReadBytes(processHandle,
                    new IntPtr(frame.ToInt64() + transformOffset),
                    out bytes, FollowTargetTransformSize))
                return false;

            float[] values = new float[16];
            for (int index = 0; index < values.Length; index++)
                values[index] = BitConverter.ToSingle(bytes, index * 4);
            basis = new float[9]
            {
                values[0], values[1], values[2],
                values[4], values[5], values[6],
                values[8], values[9], values[10]
            };
            if (!IsValidFollowTargetBasis(basis))
                return false;
            Vector3 stored = new Vector3(values[12], values[13], values[14]);
            position = StoredZyxToWorld(stored);
            return IsFiniteVector(position);
        }

        private static bool TryReadAsciiPointer(IntPtr processHandle, IntPtr address,
            out string text)
        {
            text = null;
            if (!TryReadPointer(processHandle, address, out long value) ||
                !IsValidCameraPointer(value))
                return false;
            return TryReadAscii(processHandle, new IntPtr(value), out text);
        }

        private static bool TryReadAscii(IntPtr processHandle, IntPtr address,
            out string text)
        {
            text = null;
            byte[] bytes;
            if (!TryReadBytes(processHandle, address, out bytes,
                    FollowTargetMaxAssetLength))
                return false;
            int length = Array.IndexOf(bytes, (byte)0);
            if (length <= 0)
                return false;
            for (int index = 0; index < length; index++)
            {
                if (bytes[index] < 0x20 || bytes[index] > 0x7E)
                    return false;
            }
            text = Encoding.ASCII.GetString(bytes, 0, length);
            return true;
        }

        private static bool TryReadBytes(IntPtr processHandle, IntPtr address,
            out byte[] buffer, int size)
        {
            buffer = new byte[size];
            return TryReadBytes(processHandle, address, buffer);
        }

        private static void NudgeCameraPosition(Process snowRunnerProcess)
        {
            lock (CameraWriteGate)
            {
                if (IsFollowingPlayer)
                {
                    IsFollowingPlayer = false;
                    CameraFollowGeneration++;
                    ResetCameraFollowSmoothingLocked(default(Vector3));
                    ResetCameraOrbitLocked();
                    RecordCameraFollowStoppedLocked("Stopped before camera nudge.");
                    Console.WriteLine("Camera follow stopped before camera nudge.");
                }
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
                lock (CameraWriteGate)
                {
                    if (IsFollowingPlayer)
                    {
                        Console.WriteLine("Camera follow restarted during nudge guard; nudge refused.");
                        return;
                    }
                    CameraSourceSnapshot source;
                    CameraSourceSnapshot prewrite;
                    if (!TryReadCameraSourceSnapshot(processHandle, moduleBase, true, true, out source) ||
                        !TryReadCameraSourceSnapshot(processHandle, moduleBase, true, true, out prewrite) ||
                        MaxAbsDifference(source.Matrix, prewrite.Matrix) > CameraSourceMatrixGuardTolerance)
                    {
                        Console.WriteLine("Validated source camera state changed during nudge guard.");
                        return;
                    }
                    source = prewrite;

                    byte[] originalTranslation;
                    byte[] targetTranslation;
                    bool restoring = HasCameraNudge;
                    if (restoring)
                    {
                        if (CameraNudgeTransform != source.Transform || CameraNudgeOriginalTranslation == null)
                        {
                            Console.WriteLine("Camera nudge source changed; refusing restore.");
                            return;
                        }
                        originalTranslation = CameraNudgeOriginalTranslation;
                        targetTranslation = originalTranslation;
                    }
                    else
                    {
                        originalTranslation = TranslationBytes(source.Matrix);
                        targetTranslation = BuildTranslationBytes(source.Matrix,
                            source.WorldPosition + new Vector3(0.0f, CameraSourceHeightNudge, 0.0f));
                    }

                    if (!TryWriteSourceTranslation(processHandle, source.Transform, targetTranslation))
                    {
                        Console.WriteLine("Camera source nudge did not read back or invalidate correctly.");
                        return;
                    }
                    if (!restoring)
                    {
                        CameraNudgeOriginalTranslation = originalTranslation;
                        CameraNudgeTransform = source.Transform;
                        HasCameraNudge = true;
                        Console.WriteLine("Camera source height nudged by one unit.");
                    }
                    else
                    {
                        CameraNudgeOriginalTranslation = null;
                        CameraNudgeTransform = IntPtr.Zero;
                        HasCameraNudge = false;
                        Console.WriteLine("Camera source height restored.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Camera source nudge failed: {ex.Message}");
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static void TrackCameraFollow(Process snowRunnerProcess,
            IntPtr targetOwner, IntPtr expectedAction, IntPtr expectedBody,
            IntPtr expectedFrame, string expectedAssetId, IntPtr moduleBase,
            IntPtr expectedTransform, int generation)
        {
            IntPtr processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation,
                false,
                snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
            {
                StopCameraFollow(generation, "Could not open SnowRunner for camera follow mode.");
                return;
            }

            bool timerResolutionActive = false;
            try
            {
                timerResolutionActive = TimeBeginPeriod(1) == 0;
                Vector3 previousPlayer = default;
                bool hasPreviousPlayer = false;
                DateTime lastStatus = DateTime.UtcNow;
                long periodTicks = Math.Max(1L, (long)(Stopwatch.Frequency *
                    (CameraFollowPeriodMilliseconds / 1000.0)));
                long nextDeadline = Stopwatch.GetTimestamp();
                int matrixGuardMismatchCount = 0;
                while (true)
                {
                    nextDeadline += periodTicks;
                    string failure = null;
                    lock (CameraWriteGate)
                    {
                        if (!IsFollowingGeneration(generation))
                            return;

                        long now = Stopwatch.GetTimestamp();
                        float deltaSeconds = CameraFollowLastSampleTimestamp == 0
                            ? CameraFollowPeriodMilliseconds / 1000.0f
                            : (float)(now - CameraFollowLastSampleTimestamp) / Stopwatch.Frequency;
                        CameraFollowLastSampleTimestamp = now;
                        deltaSeconds = Math.Max(0.001f, Math.Min(0.10f, deltaSeconds));

                        CameraSourceSnapshot source = default(CameraSourceSnapshot);
                        TruckTargetSnapshot target = null;
                        bool transientFailure = false;
                        bool skipTick = false;
                        string transientReason = null;
                        if (!TryReadTruckScanIdentity(processHandle, moduleBase,
                                out IntPtr activeOwner, out IntPtr actionVtable,
                                out IntPtr chassisVtable))
                        {
                            transientFailure = true;
                            transientReason = "Truck identity read was temporarily unavailable.";
                        }
                        else if (!TryReadValidatedTruckOwner(processHandle, targetOwner,
                                actionVtable, chassisVtable, activeOwner,
                                out target))
                        {
                            transientFailure = true;
                            transientReason = "Validated truck snapshot was temporarily unavailable.";
                        }
                        else if (target.Action != expectedAction ||
                            target.ChassisBody != expectedBody ||
                            target.Frame != expectedFrame ||
                            !string.Equals(target.AssetId, expectedAssetId,
                                StringComparison.Ordinal))
                        {
                            failure = "Camera follow target identity changed; follow stopped.";
                        }
                        else if (!TryReadCameraSourceSnapshot(processHandle, moduleBase,
                            false, false, out source))
                        {
                            transientFailure = true;
                            transientReason = "Camera source snapshot was temporarily unavailable.";
                        }
                        else if (source.Transform != expectedTransform)
                        {
                            failure = "Camera source pointer changed; follow stopped.";
                        }
                        else if (hasPreviousPlayer &&
                            Vector3.Distance(target.Position, previousPlayer) > CameraFollowMaxTruckJump)
                        {
                            // A single 64-byte transform read can straddle the
                            // game's physics update. Confirm a large jump before
                            // treating it as a real teleport or identity fault.
                            if (TryReadValidatedTruckOwner(processHandle, targetOwner,
                                    actionVtable, chassisVtable, activeOwner,
                                    out TruckTargetSnapshot retryTarget) &&
                                retryTarget.Action == expectedAction &&
                                retryTarget.ChassisBody == expectedBody &&
                                retryTarget.Frame == expectedFrame &&
                                string.Equals(retryTarget.AssetId, expectedAssetId,
                                    StringComparison.Ordinal) &&
                                Vector3.Distance(retryTarget.Position, previousPlayer) <=
                                    CameraFollowMaxTruckJump)
                            {
                                target = retryTarget;
                            }
                            else
                            {
                                failure = "Truck position jumped too far; follow stopped before writing.";
                            }
                        }

                        if (transientFailure)
                        {
                            if (RecordCameraFollowTransientFailureLocked(transientReason))
                            {
                                failure = "Camera follow stopped after repeated transient read failures: " +
                                    transientReason;
                            }
                            else
                            {
                                skipTick = true;
                            }
                        }

                        if (!skipTick && failure == null)
                        {
                            // Rebase against any bounded native camera movement,
                            // even if the key was released between follow ticks.
                            // Sampling GetAsyncKeyState here used to miss short
                            // I/K/J/L/O/U presses and the smoothing filter would
                            // pull the camera back to its old offset on the next
                            // successful tick.
                            if (CameraFollowHasLastRequested)
                            {
                                Vector3 residual = source.WorldPosition - CameraFollowLastRequested;
                                float residualLength = residual.Length();
                                if (residualLength > CameraFollowManualRebaseMaxIncrement)
                                    failure = "Native camera movement exceeded the per-frame follow bound.";
                                else if (residualLength > CameraFollowNoiseThreshold)
                                {
                                    CameraFollowLocalOffset += WorldOffsetToLocal(residual, target.Basis);
                                    // Native I/K/J/L/O/U movement is an intentional
                                    // change of offset. Rebase the filter so it does
                                    // not pull the camera back toward its old state.
                                    CameraFollowSmoothedPosition = source.WorldPosition;
                                    CameraFollowHasSmoothedPosition = true;
                                    InitializeCameraOrbitLocked();
                                }
                            }

                            if (failure == null)
                                UpdateCameraOrbitLocked(snowRunnerProcess, deltaSeconds);

                            CameraFollowOffset = LocalOffsetToWorld(CameraFollowLocalOffset, target.Basis);
                            Vector3 rawTargetPosition = target.Position + CameraFollowOffset;
                            Vector3 desiredPosition = rawTargetPosition;

                            if (!CameraFollowHasSmoothedPosition)
                            {
                                CameraFollowSmoothedPosition = source.WorldPosition;
                                CameraFollowHasSmoothedPosition = true;
                            }
                            float smoothingAlpha = 1.0f - (float)Math.Exp(
                                -deltaSeconds / CameraFollowPositionSmoothingSeconds);
                            CameraFollowSmoothedPosition +=
                                (desiredPosition - CameraFollowSmoothedPosition) * smoothingAlpha;
                            Vector3 targetPosition = CameraFollowSmoothedPosition;
                            Vector3 increment = targetPosition - source.WorldPosition;
                            byte[] lookAtMatrix = null;
                            bool matrixGuardMismatch = false;
                            if (failure == null && increment.Length() > CameraFollowMaxIncrement &&
                                TryReadCameraSourceSnapshot(processHandle, moduleBase, false, true,
                                    out CameraSourceSnapshot stableSource) &&
                                stableSource.Transform == expectedTransform)
                            {
                                // Confirm an apparent far jump against a stable
                                // matrix before stopping. This filters a torn
                                // source read while retaining the hard movement
                                // bound for a real source discontinuity.
                                source = stableSource;
                                increment = targetPosition - source.WorldPosition;
                            }
                            if (failure == null && increment.Length() > CameraFollowMaxIncrement)
                            {
                                failure = "Camera follow increment exceeded the per-frame bound.";
                            }
                            else if (failure == null && FaceTruckEnabled &&
                                !TryBuildLookAtMatrixBytes(source.Matrix, targetPosition,
                                    target.Position, deltaSeconds, out lookAtMatrix))
                            {
                                failure = "Camera follow look-at basis could not be constructed.";
                            }
                            else if (failure == null && FaceTruckEnabled &&
                                !TryWriteSourceMatrix(processHandle, source.Transform,
                                    lookAtMatrix, MatrixBytes(source.Matrix),
                                    out matrixGuardMismatch))
                            {
                                if (matrixGuardMismatch)
                                {
                                    matrixGuardMismatchCount++;
                                    RecordCameraFollowWriteFailureLocked(
                                        "Camera source matrix changed during look-at write; retrying.");
                                    RecordCameraFollowTransientFailureLocked(
                                        CameraFollowDiagnosticReason);
                                    skipTick = true;
                                    if (matrixGuardMismatchCount >= CameraFollowMaxMatrixGuardSkips)
                                        failure = "Camera source matrix changed repeatedly during guarded look-at writes.";
                                }
                                else
                                {
                                    failure = "Camera follow could not write the validated source matrix.";
                                }
                            }
                            else if (failure == null && !FaceTruckEnabled)
                            {
                                bool translationGuardMismatch;
                                if (!TryWriteSourceTranslation(processHandle, source.Transform,
                                    source.Matrix, targetPosition, out translationGuardMismatch))
                                {
                                    RecordCameraFollowWriteFailureLocked(
                                        translationGuardMismatch
                                            ? "Camera source basis changed during translation write; retrying."
                                            : "Camera source translation write was temporarily unavailable.");
                                    if (RecordCameraFollowTransientFailureLocked(
                                        CameraFollowDiagnosticReason))
                                    {
                                        failure = "Camera follow stopped after repeated source translation write failures.";
                                    }
                                    else
                                    {
                                        skipTick = true;
                                    }
                                }
                            }
                            if (!skipTick && failure == null)
                            {
                                matrixGuardMismatchCount = 0;
                                CameraFollowLastRequested = targetPosition;
                                CameraFollowHasLastRequested = true;
                                RecordCameraFollowSuccessLocked(source.WorldPosition,
                                    targetPosition, CameraFollowOffset);
                                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1.0)
                                {
                                    Console.WriteLine($"FOLLOW {target.AssetId} source {source.WorldPosition.X:F2} " +
                                        $"{source.WorldPosition.Y:F2} {source.WorldPosition.Z:F2}; " +
                                        $"target {targetPosition.X:F2} {targetPosition.Y:F2} {targetPosition.Z:F2}; " +
                                        $"truck {target.Position.X:F2} {target.Position.Y:F2} {target.Position.Z:F2}; " +
                                        $"offset {CameraFollowOffset.X:F2} {CameraFollowOffset.Y:F2} {CameraFollowOffset.Z:F2}");
                                    lastStatus = DateTime.UtcNow;
                                }
                            }
                            if (!hasPreviousPlayer ||
                                Vector3.DistanceSquared(target.Position, previousPlayer) >
                                    CameraFollowNoiseThreshold * CameraFollowNoiseThreshold)
                            {
                                previousPlayer = target.Position;
                                hasPreviousPlayer = true;
                            }
                        }

                        if (failure != null)
                        {
                            RecordCameraFollowStoppedLocked(failure);
                            StopCameraFollowLocked(generation);
                        }
                    }

                    if (failure != null)
                    {
                        Console.WriteLine(failure);
                        return;
                    }

                    // Keep a monotonic deadline. A fixed sleep after the read/write
                    // pass compounds the pass duration and produces visible jitter.
                    long remainingTicks = nextDeadline - Stopwatch.GetTimestamp();
                    if (remainingTicks <= 0)
                    {
                        nextDeadline = Stopwatch.GetTimestamp();
                    }
                    else
                    {
                        int sleepMilliseconds = (int)(remainingTicks * 1000L /
                            Stopwatch.Frequency);
                        if (sleepMilliseconds > 1)
                            Thread.Sleep(Math.Min(sleepMilliseconds, 4));
                        else
                            Thread.SpinWait(250);
                    }
                }
            }
            catch (Exception ex)
            {
                StopCameraFollow(generation, $"Camera follow failed: {ex.Message}");
            }
            finally
            {
                if (timerResolutionActive)
                    TimeEndPeriod(1);
                CloseHandle(processHandle);
            }
        }

        private static void UpdateCameraOrbitLocked(Process snowRunnerProcess,
            float deltaSeconds)
        {
            float mouseX = CameraOrbitPendingMouseX;
            float mouseY = CameraOrbitPendingMouseY;
            CameraOrbitPendingMouseX = 0;
            CameraOrbitPendingMouseY = 0;
            if (!CameraOrbitHasState)
                InitializeCameraOrbitLocked();
            if (!CameraOrbitHasState || !IsSnowRunnerForeground(snowRunnerProcess) ||
                SpectatorNativeMenuVisible ||
                DateTime.UtcNow.Ticks - SpectatorNativeInputTimestamp > TimeSpan.TicksPerSecond)
                return;

            Vector2 stick = ReadCameraOrbitStick();
            float azimuthDelta = mouseX * CameraOrbitMouseRadiansPerPixel +
                stick.X * CameraOrbitAngularSpeed * deltaSeconds;
            float elevationDelta = -mouseY * CameraOrbitMouseRadiansPerPixel +
                stick.Y * CameraOrbitAngularSpeed * deltaSeconds;
            CameraOrbitAzimuth = (CameraOrbitAzimuth + azimuthDelta) %
                (2.0f * (float)Math.PI);
            CameraOrbitElevation = Math.Max(-CameraOrbitMaxElevation,
                Math.Min(CameraOrbitMaxElevation,
                    CameraOrbitElevation + elevationDelta));
            if (azimuthDelta == 0.0f && elevationDelta == 0.0f)
                return;

            float cosElevation = (float)Math.Cos(CameraOrbitElevation);
            CameraFollowLocalOffset = new Vector3(
                (float)Math.Sin(CameraOrbitAzimuth) * cosElevation * CameraOrbitRadius,
                (float)Math.Sin(CameraOrbitElevation) * CameraOrbitRadius,
                (float)Math.Cos(CameraOrbitAzimuth) * cosElevation * CameraOrbitRadius);
        }

        private static Vector2 ReadCameraOrbitStick()
        {
            if (CameraOrbitXInputUnavailable)
                return Vector2.Zero;
            try
            {
                for (uint index = 0; index < 4; index++)
                {
                    if (CameraXInputGetState(index, out CameraGamepadState state) != 0)
                        continue;
                    Vector2 stick = new Vector2(state.Gamepad.RightX / 32767.0f,
                        state.Gamepad.RightY / 32767.0f);
                    float length = stick.Length();
                    if (length <= CameraOrbitStickDeadzone)
                        continue;
                    return stick / length * Math.Min(1.0f,
                        (length - CameraOrbitStickDeadzone) / (1.0f - CameraOrbitStickDeadzone));
                }
            }
            catch (DllNotFoundException) { CameraOrbitXInputUnavailable = true; }
            catch (EntryPointNotFoundException) { CameraOrbitXInputUnavailable = true; }
            return Vector2.Zero;
        }

        private static bool IsVirtualKeyHeld(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static bool TryBuildLookAtMatrixBytes(float[] sourceMatrix,
            Vector3 cameraWorld, Vector3 targetWorld, float deltaSeconds,
            out byte[] matrixBytes)
        {
            matrixBytes = null;
            if (sourceMatrix == null || sourceMatrix.Length != 16 ||
                !IsFiniteVector(cameraWorld) || !IsFiniteVector(targetWorld))
                return false;

            Vector3 storedForward = WorldToStored(targetWorld - cameraWorld);
            if (!IsFiniteVector(storedForward) || storedForward.LengthSquared() < 0.0001f)
                return false;
            storedForward = Vector3.Normalize(storedForward);
            if (!CameraFollowHasSmoothedForward)
            {
                CameraFollowSmoothedForward = storedForward;
                CameraFollowHasSmoothedForward = true;
            }
            else
            {
                float rotationAlpha = 1.0f - (float)Math.Exp(
                    -Math.Max(0.001f, Math.Min(0.10f, deltaSeconds)) /
                    CameraFollowRotationSmoothingSeconds);
                CameraFollowSmoothedForward +=
                    (storedForward - CameraFollowSmoothedForward) * rotationAlpha;
                if (CameraFollowSmoothedForward.LengthSquared() < 0.0001f)
                    return false;
                CameraFollowSmoothedForward = Vector3.Normalize(CameraFollowSmoothedForward);
            }
            storedForward = CameraFollowSmoothedForward;

            Vector3 storedUp = new Vector3(0.0f, 1.0f, 0.0f);
            if (Math.Abs(Vector3.Dot(storedUp, storedForward)) > 0.98f)
                storedUp = Vector3.Normalize(new Vector3(sourceMatrix[1],
                    sourceMatrix[5], sourceMatrix[9]));
            if (!IsFiniteVector(storedUp) || storedUp.LengthSquared() < 0.5f ||
                Math.Abs(Vector3.Dot(storedUp, storedForward)) > 0.98f)
                storedUp = new Vector3(1.0f, 0.0f, 0.0f);

            Vector3 storedRight = Vector3.Cross(storedUp, storedForward);
            if (storedRight.LengthSquared() < 0.0001f)
                return false;
            storedRight = Vector3.Normalize(storedRight);
            storedUp = Vector3.Normalize(Vector3.Cross(storedForward, storedRight));

            float sourceDeterminant = MatrixBasisDeterminant(sourceMatrix);
            float desiredDeterminant = Vector3.Dot(storedRight,
                Vector3.Cross(storedUp, storedForward));
            if (!IsFiniteFloat(sourceDeterminant) || sourceDeterminant <= 0.25f ||
                !IsFiniteFloat(desiredDeterminant) || desiredDeterminant <= 0.25f)
                return false;
            // The validated source uses the positive determinant in its stored
            // Z,Y,X basis. Refuse an unexpected handedness rather than silently
            // introducing a roll or swapping the renderer's convention.

            Vector3 storedCamera = WorldToStored(cameraWorld);
            float[] matrix = new float[16];
            matrix[0] = storedRight.X;
            matrix[1] = storedUp.X;
            matrix[2] = storedForward.X;
            matrix[4] = storedRight.Y;
            matrix[5] = storedUp.Y;
            matrix[6] = storedForward.Y;
            matrix[8] = storedRight.Z;
            matrix[9] = storedUp.Z;
            matrix[10] = storedForward.Z;
            matrix[15] = 1.0f;
            matrix[12] = -Vector3.Dot(storedCamera, storedRight);
            matrix[13] = -Vector3.Dot(storedCamera, storedUp);
            matrix[14] = -Vector3.Dot(storedCamera, storedForward);
            if (matrix.Any(value => !IsFiniteFloat(value)))
                return false;

            matrixBytes = MatrixBytes(matrix);
            return matrixBytes != null && matrixBytes.Length == CameraSourceMatrixSize;
        }

        private static Vector3 WorldToStored(Vector3 world)
        {
            return new Vector3(world.Z, world.Y, world.X);
        }

        private static float MatrixBasisDeterminant(float[] matrix)
        {
            Vector3 column0 = new Vector3(matrix[0], matrix[4], matrix[8]);
            Vector3 column1 = new Vector3(matrix[1], matrix[5], matrix[9]);
            Vector3 column2 = new Vector3(matrix[2], matrix[6], matrix[10]);
            return Vector3.Dot(column0, Vector3.Cross(column1, column2));
        }

        private static byte[] MatrixBytes(float[] matrix)
        {
            if (matrix == null || matrix.Length != 16)
                return null;
            byte[] bytes = new byte[CameraSourceMatrixSize];
            for (int index = 0; index < matrix.Length; index++)
                Buffer.BlockCopy(BitConverter.GetBytes(matrix[index]), 0, bytes, index * 4, 4);
            return bytes;
        }

        private static bool TryWriteSourceMatrix(IntPtr processHandle,
            IntPtr transform, byte[] matrix, byte[] expectedPrior,
            out bool guardMismatch)
        {
            guardMismatch = false;
            if (matrix == null || matrix.Length != CameraSourceMatrixSize)
                return false;
            byte[] prior = new byte[CameraSourceMatrixSize];
            if (!TryReadBytes(processHandle, new IntPtr(transform.ToInt64()), prior))
                return false;
            if (expectedPrior != null && !prior.SequenceEqual(expectedPrior))
            {
                guardMismatch = true;
                return false;
            }

            bool wrote = TryWriteBytes(processHandle, new IntPtr(transform.ToInt64()), matrix);
            byte[] readback = new byte[CameraSourceMatrixSize];
            bool readbackAvailable = wrote &&
                TryReadBytes(processHandle, new IntPtr(transform.ToInt64()), readback);
            bool readBackExactly = readbackAvailable && readback.SequenceEqual(matrix);
            bool coherentBasisRace = false;
            if (readbackAvailable && !readBackExactly)
            {
                if (TryAcceptSmallMatrixReadbackDelta(matrix, readback,
                    out float acceptedBasisDelta, out float acceptedTranslationResidual,
                    out float acceptedRawTranslationDelta,
                    out float acceptedPositionDelta, out float acceptedDeterminant))
                {
                    if ((DateTime.UtcNow - LastMatrixReadbackAdjustmentLog).TotalSeconds >= 1.0)
                    {
                        Console.WriteLine($"Camera source matrix readback adjusted by game: " +
                            $"basis {acceptedBasisDelta:F7}, raw translation {acceptedRawTranslationDelta:F7}, " +
                            $"coherent residual {acceptedTranslationResidual:F7}, " +
                            $"position {acceptedPositionDelta:F4}, det {acceptedDeterminant:F5}; accepting bounded delta.");
                        LastMatrixReadbackAdjustmentLog = DateTime.UtcNow;
                    }
                    readBackExactly = true;
                }
                else
                {
                    TryMeasureMatrixReadbackDelta(matrix, readback,
                        out float measuredBasisDelta, out float measuredTranslationResidual,
                        out float measuredRawTranslationDelta,
                        out float measuredPositionDelta, out float measuredDeterminant);
                    // A coherent game-side rotation can exceed the strict success
                    // tolerance. It remains a failed write; retry only after the
                    // original bytes and invalidated caches have been verified.
                    coherentBasisRace = IsRetryableMatrixBasisRace(matrix, readback);
                    Console.WriteLine($"Camera source matrix readback mismatch: " +
                        $"basis {measuredBasisDelta:F7}, raw translation {measuredRawTranslationDelta:F7}, " +
                        $"coherent residual {measuredTranslationResidual:F7}, " +
                        $"position {measuredPositionDelta:F4}, det {measuredDeterminant:F5}; rolling back.");
                }
            }
            bool cachesInvalidated = readBackExactly &&
                TryInvalidateSourceCaches(processHandle, transform);
            if (readBackExactly && cachesInvalidated)
                return true;

            bool rollbackWrite = TryWriteBytes(processHandle,
                new IntPtr(transform.ToInt64()), prior);
            byte[] rollbackReadback = new byte[CameraSourceMatrixSize];
            bool rollbackRead = rollbackWrite &&
                TryReadBytes(processHandle, new IntPtr(transform.ToInt64()), rollbackReadback) &&
                rollbackReadback.SequenceEqual(prior);
            bool rollbackCaches = TryInvalidateSourceCaches(processHandle, transform);
            if (!rollbackWrite || !rollbackRead || !rollbackCaches)
                Console.WriteLine("Camera source matrix write failed and byte-exact rollback verification failed.");
            else if (coherentBasisRace)
            {
                // Reuse the caller's bounded guard-skip counter. Never classify
                // partial writes, incoherent matrices, or failed rollback as races.
                byte[] cacheA = new byte[4];
                byte[] cacheB = new byte[4];
                guardMismatch =
                    TryReadBytes(processHandle,
                        new IntPtr(transform.ToInt64() + CameraSourceCacheFlagOffsetA), cacheA) &&
                    TryReadBytes(processHandle,
                        new IntPtr(transform.ToInt64() + CameraSourceCacheFlagOffsetB), cacheB) &&
                    BitConverter.ToInt32(cacheA, 0) == -1 &&
                    BitConverter.ToInt32(cacheB, 0) == -1;
            }
            return false;
        }

        private static bool IsRetryableMatrixBasisRace(byte[] expected, byte[] actual)
        {
            float[] expectedMatrix = MatrixFloats(expected);
            float[] actualMatrix = MatrixFloats(actual);
            if (!TryReconstructSourcePosition(expectedMatrix, out _) ||
                !TryReconstructSourcePosition(actualMatrix, out _))
                return false;
            float expectedDeterminant = MatrixBasisDeterminant(expectedMatrix);
            if (!IsFiniteFloat(expectedDeterminant) || expectedDeterminant <= 0.25f)
                return false;
            TryMeasureMatrixReadbackDelta(expected, actual,
                out float basisDelta, out float translationResidual,
                out _, out float positionDelta, out float actualDeterminant);
            // Proper orthonormal bases bound their own elements. Native camera
            // rotation can span any angle; coherence and verified rollback,
            // rather than an arbitrary angular delta, decide whether to retry.
            return IsFiniteFloat(basisDelta) &&
                basisDelta > CameraSourceReadbackBasisTolerance &&
                IsFiniteFloat(translationResidual) &&
                translationResidual <= CameraSourceReadbackTranslationResidualTolerance &&
                IsFiniteFloat(positionDelta) &&
                positionDelta <= CameraSourceReadbackPositionTolerance &&
                IsFiniteFloat(actualDeterminant) && actualDeterminant > 0.25f;
        }

        private static bool TryAcceptSmallMatrixReadbackDelta(byte[] expected,
            byte[] actual, out float basisDelta, out float translationResidual,
            out float rawTranslationDelta, out float positionDelta,
            out float actualDeterminant)
        {
            TryMeasureMatrixReadbackDelta(expected, actual, out basisDelta,
                out translationResidual, out rawTranslationDelta, out positionDelta,
                out actualDeterminant);
            return basisDelta <= CameraSourceReadbackBasisTolerance &&
                translationResidual <= CameraSourceReadbackTranslationResidualTolerance &&
                positionDelta <= CameraSourceReadbackPositionTolerance &&
                actualDeterminant > 0.25f;
        }

        private static void TryMeasureMatrixReadbackDelta(byte[] expected,
            byte[] actual, out float basisDelta, out float translationResidual,
            out float rawTranslationDelta, out float positionDelta,
            out float actualDeterminant)
        {
            basisDelta = float.PositiveInfinity;
            translationResidual = float.PositiveInfinity;
            rawTranslationDelta = float.PositiveInfinity;
            positionDelta = float.PositiveInfinity;
            actualDeterminant = float.NaN;
            if (expected == null || actual == null ||
                expected.Length != CameraSourceMatrixSize ||
                actual.Length != CameraSourceMatrixSize)
                return;
            float[] expectedMatrix = MatrixFloats(expected);
            float[] actualMatrix = MatrixFloats(actual);
            if (expectedMatrix == null || actualMatrix == null ||
                expectedMatrix.Any(value => !IsFiniteFloat(value)) ||
                actualMatrix.Any(value => !IsFiniteFloat(value)))
                return;
            int[] basisIndices = { 0, 1, 2, 4, 5, 6, 8, 9, 10 };
            basisDelta = 0.0f;
            foreach (int index in basisIndices)
                basisDelta = Math.Max(basisDelta,
                    Math.Abs(expectedMatrix[index] - actualMatrix[index]));
            rawTranslationDelta = 0.0f;
            for (int index = 12; index <= 14; index++)
                rawTranslationDelta = Math.Max(rawTranslationDelta,
                    Math.Abs(expectedMatrix[index] - actualMatrix[index]));
            if (TryReconstructSourcePosition(expectedMatrix, out Vector3 expectedPosition) &&
                TryReconstructSourcePosition(actualMatrix, out Vector3 actualPosition))
            {
                positionDelta = MaxAbsDifference(expectedPosition, actualPosition);
                Vector3 recomposedTranslation = new Vector3(
                    -(expectedPosition.X * actualMatrix[0] + expectedPosition.Y * actualMatrix[4] + expectedPosition.Z * actualMatrix[8]),
                    -(expectedPosition.X * actualMatrix[1] + expectedPosition.Y * actualMatrix[5] + expectedPosition.Z * actualMatrix[9]),
                    -(expectedPosition.X * actualMatrix[2] + expectedPosition.Y * actualMatrix[6] + expectedPosition.Z * actualMatrix[10]));
                Vector3 actualTranslation = new Vector3(actualMatrix[12],
                    actualMatrix[13], actualMatrix[14]);
                translationResidual = MaxAbsDifference(actualTranslation,
                    recomposedTranslation);
            }
            actualDeterminant = MatrixBasisDeterminant(actualMatrix);
        }

        private static float[] MatrixFloats(byte[] bytes)
        {
            if (bytes == null || bytes.Length != CameraSourceMatrixSize)
                return null;
            float[] matrix = new float[16];
            for (int index = 0; index < matrix.Length; index++)
                matrix[index] = BitConverter.ToSingle(bytes, index * 4);
            return matrix;
        }

        private static bool IsFollowingGeneration(int generation)
        {
            return IsFollowingPlayer && CameraFollowGeneration == generation;
        }

        private static void StopCameraFollowLocked(int generation)
        {
            if (IsFollowingPlayer && CameraFollowGeneration == generation)
            {
                IsFollowingPlayer = false;
                CameraFollowGeneration++;
                ResetCameraFollowSmoothingLocked(default(Vector3));
                ResetCameraOrbitLocked();
                if (CameraFollowDiagnosticStatus != "Stopped")
                    RecordCameraFollowStoppedLocked(CameraFollowDiagnosticReason);
            }
        }

        private static void StopCameraFollow(int generation, string message)
        {
            bool stopped;
            lock (CameraWriteGate)
            {
                stopped = IsFollowingPlayer && CameraFollowGeneration == generation;
                StopCameraFollowLocked(generation);
                if (stopped)
                    RecordCameraFollowStoppedLocked(message);
            }
            if (stopped)
                Console.WriteLine(message);
        }

        private static bool TryReadCameraSourceSnapshot(
            IntPtr processHandle, IntPtr moduleBase, bool strictPosition, bool stableMatrix,
            out CameraSourceSnapshot snapshot)
        {
            snapshot = default;
            byte[] freecamFlag = new byte[1];
            if (!TryReadBytes(processHandle,
                new IntPtr(moduleBase.ToInt64() + FlyModeFlagOffset), freecamFlag) ||
                freecamFlag[0] != 1)
                return false;

            if (!TryReadPointer(processHandle,
                new IntPtr(moduleBase.ToInt64() + CameraSourceOwnerModuleOffset), out long ownerValue) ||
                !IsValidCameraPointer(ownerValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(ownerValue + CameraSourceTransformOffset), out long transformValue) ||
                !IsValidCameraPointer(transformValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(ownerValue + CameraSourceControllerOffset), out long controllerValue) ||
                !IsValidCameraPointer(controllerValue) ||
                !TryReadPointer(processHandle,
                    new IntPtr(controllerValue + CameraSourceControllerTransformOffset),
                    out long controllerTransformValue) ||
                controllerTransformValue != transformValue)
                return false;

            byte[] matrixBytes = new byte[CameraSourceMatrixSize];
            bool matrixRead = false;
            byte[] previousMatrix = null;
            int attempts = stableMatrix ? CameraSourceStableReadAttempts : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (!TryReadBytes(processHandle, new IntPtr(transformValue), matrixBytes))
                    return false;
                if (!stableMatrix || (previousMatrix != null && previousMatrix.SequenceEqual(matrixBytes)))
                {
                    matrixRead = true;
                    break;
                }
                previousMatrix = (byte[])matrixBytes.Clone();
                if (attempt + 1 < attempts)
                    Thread.Sleep(CameraSourceStableReadDelayMilliseconds);
            }
            if (!matrixRead)
                return false;

            float[] matrix = new float[16];
            for (int index = 0; index < matrix.Length; index++)
                matrix[index] = BitConverter.ToSingle(matrixBytes, index * 4);
            if (!TryReconstructSourcePosition(matrix, out Vector3 reconstructedZyx))
                return false;
            if (!TryReadVector3(processHandle,
                new IntPtr(transformValue + CameraSourceWorldPositionOffset), out Vector3 cachedZyx))
                return false;

            if (!TryReadRenderedCameraVector(processHandle, moduleBase,
                out Vector3 renderedWorld, out Vector3 renderedZyx))
                return false;
            if (strictPosition &&
                (MaxAbsDifference(reconstructedZyx, cachedZyx) > CameraSourcePositionTolerance ||
                 MaxAbsDifference(reconstructedZyx, renderedZyx) > CameraSourcePositionTolerance))
                return false;

            snapshot = new CameraSourceSnapshot
            {
                Owner = new IntPtr(ownerValue),
                Transform = new IntPtr(transformValue),
                Controller = new IntPtr(controllerValue),
                Matrix = matrix,
                ReconstructedZyx = reconstructedZyx,
                WorldPosition = new Vector3(reconstructedZyx.Z, reconstructedZyx.Y, reconstructedZyx.X),
                CachedZyx = cachedZyx,
                RenderedZyx = renderedZyx
            };
            return true;
        }

        private static bool TryReconstructSourcePosition(float[] matrix, out Vector3 reconstructedZyx)
        {
            reconstructedZyx = default;
            if (matrix == null || matrix.Length != 16 || matrix.Any(value => !IsFiniteFloat(value)))
                return false;

            Vector3 row0 = new Vector3(matrix[0], matrix[1], matrix[2]);
            Vector3 row1 = new Vector3(matrix[4], matrix[5], matrix[6]);
            Vector3 row2 = new Vector3(matrix[8], matrix[9], matrix[10]);
            if (Math.Abs(row0.Length() - 1.0f) > CameraSourceBasisTolerance ||
                Math.Abs(row1.Length() - 1.0f) > CameraSourceBasisTolerance ||
                Math.Abs(row2.Length() - 1.0f) > CameraSourceBasisTolerance ||
                Math.Abs(Vector3.Dot(row0, row1)) > CameraSourceBasisTolerance ||
                Math.Abs(Vector3.Dot(row0, row2)) > CameraSourceBasisTolerance ||
                Math.Abs(Vector3.Dot(row1, row2)) > CameraSourceBasisTolerance ||
                Math.Abs(matrix[3]) > CameraSourceBasisTolerance ||
                Math.Abs(matrix[7]) > CameraSourceBasisTolerance ||
                Math.Abs(matrix[11]) > CameraSourceBasisTolerance ||
                Math.Abs(matrix[15] - 1.0f) > CameraSourceBasisTolerance)
                return false;

            Vector3 translation = new Vector3(matrix[12], matrix[13], matrix[14]);
            reconstructedZyx = new Vector3(
                -(translation.X * row0.X + translation.Y * row0.Y + translation.Z * row0.Z),
                -(translation.X * row1.X + translation.Y * row1.Y + translation.Z * row1.Z),
                -(translation.X * row2.X + translation.Y * row2.Y + translation.Z * row2.Z));
            return IsFiniteVector(reconstructedZyx);
        }

        private static bool IsValidCameraPointer(long value)
        {
            return value >= 0x10000 && value < 0x800000000000L;
        }

        private static float MaxAbsDifference(Vector3 first, Vector3 second)
        {
            return Math.Max(Math.Abs(first.X - second.X),
                Math.Max(Math.Abs(first.Y - second.Y), Math.Abs(first.Z - second.Z)));
        }

        private static float MaxAbsDifference(float[] first, float[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return float.PositiveInfinity;
            float result = 0.0f;
            for (int index = 0; index < first.Length; index++)
                result = Math.Max(result, Math.Abs(first[index] - second[index]));
            return result;
        }

        private static byte[] TranslationBytes(float[] matrix)
        {
            byte[] bytes = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(matrix[12]), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(matrix[13]), 0, bytes, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(matrix[14]), 0, bytes, 8, 4);
            return bytes;
        }

        private static byte[] BuildTranslationBytes(float[] matrix, Vector3 targetWorld)
        {
            Vector3 targetZyx = new Vector3(targetWorld.Z, targetWorld.Y, targetWorld.X);
            float tx = -(targetZyx.X * matrix[0] + targetZyx.Y * matrix[4] + targetZyx.Z * matrix[8]);
            float ty = -(targetZyx.X * matrix[1] + targetZyx.Y * matrix[5] + targetZyx.Z * matrix[9]);
            float tz = -(targetZyx.X * matrix[2] + targetZyx.Y * matrix[6] + targetZyx.Z * matrix[10]);
            byte[] bytes = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(tx), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ty), 0, bytes, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(tz), 0, bytes, 8, 4);
            return bytes;
        }

        private static bool TryWriteSourceTranslation(
            IntPtr processHandle, IntPtr transform, byte[] translation)
        {
            if (translation == null || translation.Length != 12)
                return false;
            byte[] prior = new byte[12];
            if (!TryReadBytes(processHandle,
                new IntPtr(transform.ToInt64() + CameraSourceTranslationOffset), prior))
                return false;

            bool wroteTranslation = TryWriteBytes(processHandle,
                new IntPtr(transform.ToInt64() + CameraSourceTranslationOffset), translation);
            byte[] readback = new byte[12];
            bool readBackExactly = wroteTranslation &&
                TryReadBytes(processHandle,
                    new IntPtr(transform.ToInt64() + CameraSourceTranslationOffset), readback) &&
                MaxAbsDifference(
                    new Vector3(BitConverter.ToSingle(translation, 0),
                        BitConverter.ToSingle(translation, 4), BitConverter.ToSingle(translation, 8)),
                    new Vector3(BitConverter.ToSingle(readback, 0),
                        BitConverter.ToSingle(readback, 4), BitConverter.ToSingle(readback, 8))) <=
                    CameraSourceMatrixGuardTolerance;
            bool cachesInvalidated = TryInvalidateSourceCaches(processHandle, transform);
            if (readBackExactly && cachesInvalidated)
                return true;

            // A partial translation write, readback mismatch, or cache failure
            // must not leave a half-updated source.  Best-effort restoration is
            // followed by another cache invalidation so stale derived fields
            // are never trusted after this method reports failure.
            IntPtr translationAddress =
                new IntPtr(transform.ToInt64() + CameraSourceTranslationOffset);
            bool rollbackWrite = TryWriteBytes(processHandle, translationAddress, prior);
            byte[] rollbackReadback = new byte[12];
            bool rollbackRead = rollbackWrite &&
                TryReadBytes(processHandle, translationAddress, rollbackReadback) &&
                rollbackReadback.SequenceEqual(prior);
            bool rollbackCaches = TryInvalidateSourceCaches(processHandle, transform);
            if (!rollbackWrite || !rollbackRead || !rollbackCaches)
                Console.WriteLine("Camera source write failed and rollback verification failed.");
            return false;
        }

        // Follow's default path preserves the native camera basis.  The game can
        // rebuild that basis on the same frame that we write the translation, so
        // computing bytes once from the earlier snapshot can place the camera at
        // a distant, perfectly valid point.  Re-read the full matrix around the
        // translation write and recompute against the newest basis when needed.
        private static bool TryWriteSourceTranslation(
            IntPtr processHandle, IntPtr transform, float[] expectedPrior,
            Vector3 targetWorld, out bool guardMismatch)
        {
            guardMismatch = false;
            if (!IsFiniteVector(targetWorld) || !IsValidCameraPointer(transform.ToInt64()))
                return false;

            IntPtr translationAddress = new IntPtr(
                transform.ToInt64() + CameraSourceTranslationOffset);
            Vector3 targetZyx = WorldToStored(targetWorld);
            bool enforceExpectedPrior = expectedPrior != null &&
                expectedPrior.Length == 16;
            for (int attempt = 0; attempt < CameraFollowTranslationWriteAttempts;
                attempt++)
            {
                byte[] beforeBytes = new byte[CameraSourceMatrixSize];
                if (!TryReadBytes(processHandle, transform, beforeBytes))
                    return false;
                float[] before = MatrixFloats(beforeBytes);
                if (!TryReconstructSourcePosition(before,
                    out Vector3 beforeZyx))
                    return false;
                if (enforceExpectedPrior &&
                    MaxAbsDifference(before, expectedPrior) >
                        CameraSourceMatrixGuardTolerance)
                {
                    // The source moved between the guarded snapshot and this
                    // write. Rebase the guard to the newest complete matrix and
                    // retry; never write bytes derived from the stale snapshot.
                    guardMismatch = true;
                    expectedPrior = before;
                    continue;
                }
                enforceExpectedPrior = false;

                byte[] translation = BuildTranslationBytes(before, targetWorld);
                if (!TryWriteBytes(processHandle, translationAddress, translation))
                {
                    TryRestoreSourcePosition(processHandle, transform, beforeZyx,
                        out _);
                    return false;
                }

                byte[] afterBytes = new byte[CameraSourceMatrixSize];
                if (!TryReadBytes(processHandle, transform, afterBytes))
                {
                    guardMismatch = true;
                    if (!TryRestoreSourcePosition(processHandle, transform,
                        beforeZyx, out _))
                        return false;
                    continue;
                }
                float[] after = MatrixFloats(afterBytes);
                if (!TryReconstructSourcePosition(after,
                    out Vector3 afterZyx))
                {
                    guardMismatch = true;
                    if (!TryRestoreSourcePosition(processHandle, transform,
                        beforeZyx, out _))
                        return false;
                    continue;
                }

                // Basis changes are expected while the player rotates. Recompute
                // from the newest coherent matrix instead of restoring the old
                // world position through a newly changed basis. Restoring the
                // stale position here can fight the game's rotation update and
                // make the camera appear frozen or jump far away. The next
                // attempt is guarded by the matrix we just observed.
                if (MaxAbsBasisDifference(before, after) >
                    CameraFollowTranslationBasisGuardTolerance ||
                    MaxAbsDifference(afterZyx, targetZyx) >
                    CameraFollowTranslationPositionGuardTolerance)
                {
                    guardMismatch = true;
                    expectedPrior = after;
                    enforceExpectedPrior = true;
                    continue;
                }

                if (!TryInvalidateSourceCaches(processHandle, transform))
                {
                    guardMismatch = true;
                    if (!TryRestoreSourcePosition(processHandle, transform,
                        beforeZyx, out _))
                        return false;
                    continue;
                }

                // Verify the coherent position after cache invalidation. This
                // prevents a raw 12-byte readback from being treated as success
                // when the game's matrix writer won the race.
                byte[] verifiedBytes = new byte[CameraSourceMatrixSize];
                if (!TryReadBytes(processHandle, transform, verifiedBytes))
                {
                    guardMismatch = true;
                    if (!TryRestoreSourcePosition(processHandle, transform,
                        beforeZyx, out _))
                        return false;
                    continue;
                }
                float[] verified = MatrixFloats(verifiedBytes);
                if (!TryReconstructSourcePosition(verified,
                    out Vector3 verifiedZyx) ||
                    MaxAbsDifference(verifiedZyx, targetZyx) >
                    CameraFollowTranslationPositionGuardTolerance)
                {
                    guardMismatch = true;
                    if (!TryRestoreSourcePosition(processHandle, transform,
                        beforeZyx, out _))
                        return false;
                    continue;
                }
                return true;
            }

            return false;
        }

        private static bool TryRestoreSourcePosition(IntPtr processHandle,
            IntPtr transform, Vector3 restoreZyx, out bool guardMismatch)
        {
            guardMismatch = false;
            Vector3 restoreWorld = new Vector3(restoreZyx.Z, restoreZyx.Y,
                restoreZyx.X);
            IntPtr translationAddress = new IntPtr(
                transform.ToInt64() + CameraSourceTranslationOffset);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[] currentBytes = new byte[CameraSourceMatrixSize];
                if (!TryReadBytes(processHandle, transform, currentBytes))
                    return false;
                float[] current = MatrixFloats(currentBytes);
                if (!TryReconstructSourcePosition(current, out _))
                    return false;
                byte[] restoreTranslation = BuildTranslationBytes(current,
                    restoreWorld);
                if (!TryWriteBytes(processHandle, translationAddress,
                    restoreTranslation))
                    return false;

                byte[] readbackBytes = new byte[CameraSourceMatrixSize];
                if (!TryReadBytes(processHandle, transform, readbackBytes))
                {
                    guardMismatch = true;
                    continue;
                }
                float[] readback = MatrixFloats(readbackBytes);
                if (!TryReconstructSourcePosition(readback,
                    out Vector3 readbackZyx) ||
                    MaxAbsDifference(readbackZyx, restoreZyx) >
                        CameraFollowTranslationPositionGuardTolerance)
                {
                    guardMismatch = true;
                    continue;
                }
                if (TryInvalidateSourceCaches(processHandle, transform))
                    return true;
                guardMismatch = true;
            }
            Console.WriteLine("Camera source translation rollback could not be verified.");
            return false;
        }

        private static float MaxAbsBasisDifference(float[] first, float[] second)
        {
            if (first == null || second == null || first.Length != 16 ||
                second.Length != 16)
                return float.PositiveInfinity;
            int[] basisIndices = { 0, 1, 2, 4, 5, 6, 8, 9, 10 };
            float result = 0.0f;
            foreach (int index in basisIndices)
                result = Math.Max(result, Math.Abs(first[index] - second[index]));
            return result;
        }

        private static bool TryInvalidateSourceCaches(IntPtr processHandle, IntPtr transform)
        {
            bool cacheA = TryWriteBytes(processHandle,
                new IntPtr(transform.ToInt64() + CameraSourceCacheFlagOffsetA), BitConverter.GetBytes(-1));
            bool cacheB = TryWriteBytes(processHandle,
                new IntPtr(transform.ToInt64() + CameraSourceCacheFlagOffsetB), BitConverter.GetBytes(-1));
            return cacheA && cacheB;
        }

        private static bool TryWriteBytes(IntPtr processHandle, IntPtr address, byte[] buffer)
        {
            return buffer != null && buffer.Length > 0 &&
                WriteProcessMemory(processHandle, address, buffer, buffer.Length,
                    out IntPtr bytesWritten) && bytesWritten.ToInt64() == buffer.Length;
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
                if (!TryReadRenderedCameraBase(processHandle, moduleBase, out long cameraBase))
                {
                    Console.WriteLine("Rendered camera chain did not resolve.");
                    return;
                }

                const int snapshotStart = 0;
                const int snapshotSize = 0x100;
                byte[] snapshot = new byte[snapshotSize];
                if (!TryReadBytes(processHandle,
                    new IntPtr(cameraBase + snapshotStart),
                    snapshot))
                {
                    Console.WriteLine("Could not read a consistent camera-object snapshot.");
                    return;
                }

                Console.WriteLine($"Rendered camera transform: 0x{cameraBase:X}. " +
                    $"Snapshot: +0x{snapshotStart:X}-+0x{snapshotStart + snapshotSize - 1:X}.");
                Vector3 stored = ReadSnapshotVector3(snapshot, snapshotStart, 0);
                Console.WriteLine($"RENDERED CAMERA: world XYZ {stored.Z:F2},{stored.Y:F2},{stored.X:F2}; " +
                    $"stored ZYX {stored.X:F2},{stored.Y:F2},{stored.Z:F2}");
                Console.WriteLine($"Basis candidates (stored order): +0x{RenderedCameraBasisOffsetA:X3} " +
                    $"{ReadSnapshotVector3(snapshot, snapshotStart, RenderedCameraBasisOffsetA)}, " +
                    $"+0x{RenderedCameraBasisOffsetB:X3} " +
                    $"{ReadSnapshotVector3(snapshot, snapshotStart, RenderedCameraBasisOffsetB)}, " +
                    $"+0x{RenderedCameraBasisOffsetC:X3} " +
                    $"{ReadSnapshotVector3(snapshot, snapshotStart, RenderedCameraBasisOffsetC)}");
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
                for (int offset = snapshotStart; offset <= snapshotStart + snapshotSize - 12; offset += 4)
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
                    if (!TryReadRenderedCameraVector(processHandle, moduleBase, out Vector3 value, out Vector3 stored))
                    {
                        Console.WriteLine("Rendered camera coordinate chain no longer resolves.");
                        IsTrackingCameraCoords = false;
                        break;
                    }

                    Console.WriteLine($"RENDERED CAMERA: world XYZ {value.X:F2} {value.Y:F2} {value.Z:F2} | " +
                        $"stored ZYX {stored.X:F2} {stored.Y:F2} {stored.Z:F2}");
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

        private static bool TryReadRenderedCameraBase(IntPtr processHandle, IntPtr moduleBase, out long cameraBase)
        {
            return TryReadPointer(processHandle,
                new IntPtr(moduleBase.ToInt64() + RenderedCameraModuleOffset), out cameraBase);
        }

        private static bool TryReadRenderedCameraVector(
            IntPtr processHandle, IntPtr moduleBase, out Vector3 world, out Vector3 stored)
        {
            world = default;
            stored = default;
            if (!TryReadRenderedCameraBase(processHandle, moduleBase, out long cameraBase) ||
                !TryReadVector3(processHandle, new IntPtr(cameraBase), out stored) || !IsFiniteVector(stored))
                return false;
            world = new Vector3(stored.Z, stored.Y, stored.X);
            return true;
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
                0x1A0);
            return true;
        }

        private static bool IsValidFollowTargetBasis(float[] basis)
        {
            if (basis == null || basis.Length != 9 || basis.Any(value => !IsFiniteFloat(value)))
                return false;
            Vector3 row0 = new Vector3(basis[0], basis[1], basis[2]);
            Vector3 row1 = new Vector3(basis[3], basis[4], basis[5]);
            Vector3 row2 = new Vector3(basis[6], basis[7], basis[8]);
            return Math.Abs(row0.Length() - 1.0f) <= FollowTargetBasisTolerance &&
                   Math.Abs(row1.Length() - 1.0f) <= FollowTargetBasisTolerance &&
                   Math.Abs(row2.Length() - 1.0f) <= FollowTargetBasisTolerance &&
                   Math.Abs(Vector3.Dot(row0, row1)) <= FollowTargetBasisTolerance &&
                   Math.Abs(Vector3.Dot(row0, row2)) <= FollowTargetBasisTolerance &&
                   Math.Abs(Vector3.Dot(row1, row2)) <= FollowTargetBasisTolerance;
        }

        private static Vector3 WorldOffsetToLocal(Vector3 worldOffset, float[] basis)
        {
            Vector3 stored = new Vector3(worldOffset.Z, worldOffset.Y, worldOffset.X);
            Vector3 row0 = new Vector3(basis[0], basis[1], basis[2]);
            Vector3 row1 = new Vector3(basis[3], basis[4], basis[5]);
            Vector3 row2 = new Vector3(basis[6], basis[7], basis[8]);
            return new Vector3(Vector3.Dot(stored, row0),
                Vector3.Dot(stored, row1), Vector3.Dot(stored, row2));
        }

        private static Vector3 LocalOffsetToWorld(Vector3 localOffset, float[] basis)
        {
            Vector3 row0 = new Vector3(basis[0], basis[1], basis[2]);
            Vector3 row1 = new Vector3(basis[3], basis[4], basis[5]);
            Vector3 row2 = new Vector3(basis[6], basis[7], basis[8]);
            Vector3 stored = row0 * localOffset.X + row1 * localOffset.Y + row2 * localOffset.Z;
            return new Vector3(stored.Z, stored.Y, stored.X);
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
                0x1A0);
            Vector3 stored = playerCoord.Read();
            if (!IsFiniteVector(stored))
                return false;
            playerVector = StoredZyxToWorld(stored);
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
                Console.WriteLine("No filtered candidates are available. Capture and narrow freecam movement with Ctrl+F6 before pointer tracing.");
                return;
            }

            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not open SnowRunner for pointer scanning.");

            try
            {
                var candidateAddresses = LastCameraCandidateAddresses.ToList();
                IntPtr moduleBase = snowRunnerProcess.MainModule.BaseAddress;
                var targets = BuildPointerTargets(candidateAddresses);

                long moduleStart = moduleBase.ToInt64();
                long moduleEnd = moduleStart + snowRunnerProcess.MainModule.ModuleMemorySize;
                var references = MergePointerReferences(
                    ScanModuleForPointerReferences(processHandle, moduleStart, moduleEnd, targets),
                    ScanAllMemoryForPointerReferences(processHandle, targets));

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
                    var secondLevelReferences = MergePointerReferences(
                        ScanModuleForPointerReferences(processHandle, moduleStart, moduleEnd, secondLevelTargets),
                        ScanAllMemoryForPointerReferences(processHandle, secondLevelTargets));

                    Console.WriteLine($"Second-level references to stable heap slots: {secondLevelReferences.Count}");
                    var resolvedChains = secondLevelReferences
                        .Select(reference => TryBuildCameraPointerChain(
                            processHandle, snowRunnerProcess.MainModule.BaseAddress, reference, references))
                        .Where(result => result.HasValue)
                        .Select(result => result.Value)
                        .GroupBy(result => result.Chain)
                        .Select(group => group.First())
                        .ToList();
                    Console.WriteLine($"Structurally resolved candidate pointer chains: {resolvedChains.Count}. Rendered-camera identity still requires controlled movement checks.");
                    foreach (var resolved in resolvedChains.Take(20))
                    {
                        Console.WriteLine($"SnowRunner.exe+0x{resolved.Chain.ModuleOffset:X} -> " +
                            $"[ptr]+0x{resolved.Chain.StableOffset:X} -> " +
                            $"[ptr]+0x{resolved.Chain.VectorOffset:X} = " +
                            $"{resolved.Value.X:F2},{resolved.Value.Y:F2},{resolved.Value.Z:F2}");
                    }

                    foreach (var reference in secondLevelReferences.Take(40))
                    {
                        string location = reference.ReferenceOffset >= 0
                            ? $"SnowRunner.exe+0x{reference.ReferenceOffset:X}"
                            : $"0x{reference.ReferenceAddress.ToInt64():X}";
                        long stableSlotAddress = reference.Candidate.ToInt64();
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

        private static List<PointerReference> MergePointerReferences(
            List<PointerReference> moduleReferences,
            List<PointerReference> heapReferences)
        {
            return moduleReferences.Concat(heapReferences)
                .GroupBy(reference => new
                {
                    reference.ReferenceAddress,
                    reference.Candidate,
                    reference.Delta
                })
                .Select(group => group.First())
                .ToList();
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

        private struct ResolvedCandidateChain
        {
            public CameraPointerChain Chain;
            public Vector3 Value;
        }

        private static ResolvedCandidateChain? TryBuildCameraPointerChain(
            IntPtr processHandle,
            IntPtr moduleBase,
            PointerReference secondLevelReference,
            List<PointerReference> firstLevelReferences)
        {
            if (secondLevelReference.ReferenceOffset < 0)
                return null;

            var firstLevelMatch = firstLevelReferences.FirstOrDefault(first =>
                first.ReferenceAddress.ToInt64() ==
                secondLevelReference.Candidate.ToInt64());
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

            return new ResolvedCandidateChain
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
            if (targets.Count == 0)
                return references;
            IntPtr address = IntPtr.Zero;
            int informationSize = Marshal.SizeOf(typeof(MemoryBasicInformation));
            var stopwatch = Stopwatch.StartNew();
            long nextProgressSeconds = 5;
            long scannedBytes = 0;
            const int referenceLimit = 5000;
            const int timeLimitSeconds = 60;
            Console.WriteLine("Scanning private heap pointer references (60-second / 5000-reference limit)...");

            while (VirtualQueryEx(processHandle, address, out MemoryBasicInformation region, (uint)informationSize) != 0)
            {
                if (references.Count >= referenceLimit || stopwatch.Elapsed.TotalSeconds >= timeLimitSeconds)
                    break;
                long regionBase = region.BaseAddress.ToInt64();
                long regionSize = region.RegionSize.ToInt64();
                if (regionSize <= 0)
                    break;

                // Module references are searched separately. For the fallback heap
                // pass, skip image and mapped regions so a pointer search does not
                // spend minutes rereading executable and file-backed memory.
                if (region.State == MemoryCommit && region.Type == MemPrivate && IsReadable(region.Protect))
                {
                    for (long offset = 0; offset < regionSize && references.Count < referenceLimit; offset += CameraScanChunkSize)
                    {
                        if (stopwatch.Elapsed.TotalSeconds >= timeLimitSeconds)
                            break;
                        if (stopwatch.Elapsed.TotalSeconds >= nextProgressSeconds)
                        {
                            Console.WriteLine($"Heap pointer scan: {scannedBytes / (1024 * 1024)} MiB read, {references.Count} references, {stopwatch.Elapsed.TotalSeconds:F0}s elapsed.");
                            nextProgressSeconds = (long)stopwatch.Elapsed.TotalSeconds + 5;
                        }
                        int bytesToRead = (int)Math.Min(CameraScanChunkSize, regionSize - offset);
                        byte[] buffer = new byte[bytesToRead];
                        if (!ReadProcessMemory(processHandle, new IntPtr(regionBase + offset), buffer, bytesToRead, out IntPtr bytesRead))
                            continue;

                        int available = Math.Min(bytesToRead, bytesRead.ToInt32());
                        scannedBytes += available;
                        for (int index = 0; index + 8 <= available && references.Count < referenceLimit; index += 8)
                        {
                            long value = BitConverter.ToInt64(buffer, index);
                            if (!targets.TryGetValue(value, out List<PointerTarget> matches))
                                continue;

                            foreach (PointerTarget match in matches)
                            {
                                if (references.Count >= referenceLimit)
                                    break;
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

            if (references.Count >= referenceLimit || stopwatch.Elapsed.TotalSeconds >= timeLimitSeconds)
                Console.WriteLine($"Heap pointer scan incomplete: reached {(references.Count >= referenceLimit ? "reference" : "time")} limit; returning partial results.");
            else
                Console.WriteLine($"Heap pointer scan completed: {scannedBytes / (1024 * 1024)} MiB read, {references.Count} references.");
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

        private static Vector3 StoredZyxToWorld(Vector3 stored)
        {
            return new Vector3(stored.Z, stored.Y, stored.X);
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


        private static bool EnableFreeCam(Process snowRunnerProcess)
        {
            lock (FreeCamTransitionGate)
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
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return false;
                }
            }
        }

        private static bool DisableFreeCam(Process snowRunnerProcess)
        {
            lock (FreeCamTransitionGate)
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
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return false;
                }
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

        private static bool ReadActualFreeCamFlag(Process snowRunnerProcess)
        {
            IntPtr processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead,
                false, snowRunnerProcess.Id);
            if (processHandle == IntPtr.Zero)
                return false;
            try
            {
                byte[] flag = new byte[1];
                return TryReadBytes(processHandle,
                    new IntPtr(snowRunnerProcess.MainModule.BaseAddress.ToInt64() + FlyModeFlagOffset),
                    flag) && flag[0] == 1;
            }
            finally
            {
                CloseHandle(processHandle);
            }
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
            Console.WriteLine("Ctrl + "+ config.ProbeCameraChain +": \t Probe rendered camera position (read-only)\n");
            Console.WriteLine("Ctrl + "+ config.ToggleCameraTracking +": \t Toggle rendered camera tracking (read-only)\n");
            Console.WriteLine("Ctrl + "+ config.ProbeCameraTransform +": \t Dump rendered camera transform (read-only)\n");
            Console.WriteLine("Ctrl + "+ config.ToggleCameraFollow +": \t Toggle validated source camera follow\n");
            Console.WriteLine("Ctrl + Shift + "+ config.NudgeCameraPosition +": \t Nudge/restore validated source camera height\n");
            Console.WriteLine("Ctrl + Shift + "+ config.ToggleTargetOverlay +": \t Open/close spectator target overlay\n");
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
                ProbeRenderedCameraChain(snowRunnerProcess);
            }
            else if (e2.Key == config.ToggleCameraTracking)
            {
                ToggleCameraCoordTracking(snowRunnerProcess);
            }
            else if (e2.Key == config.ToggleTargetOverlay &&
                e2.Modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                ToggleTargetOverlay(snowRunnerProcess);
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
