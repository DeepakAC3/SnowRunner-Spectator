#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

#include <MinHook.h>
#include "imgui.h"
#include "imgui_impl_dx11.h"
#include "imgui_impl_win32.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "SpectatorUI.h"

namespace
{
constexpr uint32_t kProtocolMagic = 0x53504631u;
constexpr uint32_t kMaxTargets = 64;
constexpr uint32_t kFollow = 1u;
constexpr uint32_t kFace = 2u;
constexpr uint32_t kFreeCam = 8u;
constexpr uint32_t kScanning = 16u;

#pragma pack(push, 1)
struct WireRequest
{
    uint32_t magic;
    uint32_t command;
    uint64_t owner;
    float value;
    float value2;
    uint32_t flags;
    uint32_t reserved;
};

struct WireResponseHeader
{
    uint32_t magic;
    uint32_t flags;
    uint64_t selectedOwner;
    char status[256];
    uint32_t count;
};

struct WireTarget
{
    uint64_t owner;
    float x;
    float y;
    float z;
    uint32_t flags;
    char asset[128];
};
#pragma pack(pop)

static_assert(sizeof(WireRequest) == 32, "C# request protocol changed");
static_assert(sizeof(WireResponseHeader) == 276, "C# response protocol changed");
static_assert(sizeof(WireTarget) == 152, "C# target protocol changed");

struct Target
{
    uint64_t owner = 0;
    float x = 0;
    float y = 0;
    float z = 0;
    uint32_t flags = 0;
    std::string asset;
};

struct State
{
    uint32_t flags = 0;
    uint64_t selectedOwner = 0;
    std::string status;
    std::vector<Target> targets;
};

struct QueuedCommand
{
    WireRequest request{};
};

struct Win32Message
{
    HWND hwnd;
    UINT message;
    WPARAM wParam;
    LPARAM lParam;
};

using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
using ResizeBuffersFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

HMODULE g_module = nullptr;
std::atomic<bool> g_running{ false };
std::atomic<bool> g_shutdownRequested{ false };
std::atomic<bool> g_bootstrapStarted{ false };
std::atomic<bool> g_hooksInstalled{ false };
std::atomic<bool> g_imguiReady{ false };
std::atomic<bool> g_connected{ false };
std::atomic<bool> g_menuVisible{ true };
std::atomic<bool> g_captureMouse{ false };
std::atomic<bool> g_captureKeyboard{ false };
std::atomic<bool> g_followActive{ false };
std::atomic<bool> g_dragOwned{ false };
std::mutex g_gestureMutex;
float g_mouseDeltaX = 0;
float g_mouseDeltaY = 0;
std::atomic<bool> g_rawMouseSeen{ false };
bool g_legacyMouseBaseline = false;
POINT g_legacyMousePosition{};
std::atomic<uint64_t> g_selectedOwner{ 0 };
std::atomic<int64_t> g_lastResponseTicks{ 0 };

std::mutex g_stateMutex;
State g_state;
std::mutex g_commandMutex;
std::deque<QueuedCommand> g_commands;
std::mutex g_messageMutex;
std::deque<Win32Message> g_messages;

std::mutex g_renderMutex;
std::atomic<HWND> g_window{ nullptr };
std::atomic<WNDPROC> g_originalWndProc{ nullptr };
IDXGISwapChain* g_swapChain = nullptr;
ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_context = nullptr;
ID3D11RenderTargetView* g_renderTarget = nullptr;
std::atomic<HANDLE> g_pipeHandle{ nullptr };
std::thread g_pipeThread;
std::atomic<HANDLE> g_bootstrapHandle{ nullptr };
std::atomic<DWORD> g_bootstrapThreadId{ 0 };
std::ofstream g_log;
std::mutex g_logMutex;

PresentFn g_originalPresent = nullptr;
ResizeBuffersFn g_originalResizeBuffers = nullptr;
LPVOID g_presentTarget = nullptr;
LPVOID g_resizeTarget = nullptr;

bool g_uiFace = false;
float g_uiDistance = 12.0f;
float g_uiHeight = 3.0f;
float g_uiSmoothing = 120.0f;
uint64_t g_uiSelectedOwner = 0;
uint64_t g_uiAuthoritativeOwner = 0;

LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
HRESULT __stdcall HookedPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags);
HRESULT __stdcall HookedResizeBuffers(IDXGISwapChain* swapChain, UINT bufferCount, UINT width,
                                      UINT height, DXGI_FORMAT format, UINT swapChainFlags);

void Log(const std::string& line)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    OutputDebugStringA(("[SpectatorUI] " + line + "\n").c_str());
    if (g_log.is_open())
        g_log << line << std::endl;
}

std::string ReadFixedUtf8(const char* data, size_t length)
{
    size_t actual = 0;
    while (actual < length && data[actual] != '\0')
        ++actual;
    return std::string(data, actual);
}

bool WriteAll(HANDLE pipe, const void* data, size_t size)
{
    const auto* bytes = static_cast<const uint8_t*>(data);
    while (size > 0)
    {
        DWORD written = 0;
        if (!WriteFile(pipe, bytes, static_cast<DWORD>(std::min<size_t>(size, 1u << 20)), &written, nullptr) || written == 0)
            return false;
        bytes += written;
        size -= written;
    }
    return true;
}

bool ReadAll(HANDLE pipe, void* data, size_t size)
{
    auto* bytes = static_cast<uint8_t*>(data);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (size > 0)
    {
        DWORD available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
            return false;
        if (available == 0)
        {
            if (!g_running.load(std::memory_order_acquire) ||
                std::chrono::steady_clock::now() >= deadline)
                return false;
            Sleep(5);
            continue;
        }
        DWORD read = 0;
        const DWORD requested = static_cast<DWORD>(std::min<size_t>(size, std::min<size_t>(available, 1u << 20)));
        if (!ReadFile(pipe, bytes, requested, &read, nullptr) || read == 0)
            return false;
        bytes += read;
        size -= read;
    }
    return true;
}

void ClearMouseGesture()
{
    g_dragOwned.store(false, std::memory_order_release);
    std::lock_guard<std::mutex> lock(g_gestureMutex);
    g_mouseDeltaX = g_mouseDeltaY = 0;
    g_legacyMouseBaseline = false;
}

bool CanOrbitMouse(HWND window)
{
    const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    return window && GetForegroundWindow() == window &&
        g_connected.load(std::memory_order_acquire) &&
        g_followActive.load(std::memory_order_acquire) &&
        !g_menuVisible.load(std::memory_order_acquire) &&
        now - g_lastResponseTicks.load(std::memory_order_acquire) < 1000;
}

void ClearStateDisconnected()
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_followActive.store(false, std::memory_order_release);
    ClearMouseGesture();
    g_state = State{};
    g_lastResponseTicks.store(0, std::memory_order_release);
}

bool ReadResponse(HANDLE pipe)
{
    WireResponseHeader header{};
    if (!ReadAll(pipe, &header, sizeof(header)) || header.magic != kProtocolMagic || header.count > kMaxTargets)
        return false;

    g_followActive.store((header.flags & kFollow) != 0, std::memory_order_release);
    if ((header.flags & kFollow) == 0)
        ClearMouseGesture();

    State next;
    next.flags = header.flags;
    next.selectedOwner = header.selectedOwner;
    next.status = ReadFixedUtf8(header.status, sizeof(header.status));
    next.targets.reserve(header.count);
    for (uint32_t index = 0; index < header.count; ++index)
    {
        WireTarget wire{};
        if (!ReadAll(pipe, &wire, sizeof(wire)))
            return false;
        Target target;
        target.owner = wire.owner;
        target.x = wire.x;
        target.y = wire.y;
        target.z = wire.z;
        target.flags = wire.flags;
        target.asset = ReadFixedUtf8(wire.asset, sizeof(wire.asset));
        next.targets.push_back(std::move(target));
    }

    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state = std::move(next);
    }
    g_lastResponseTicks.store(std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count(),
        std::memory_order_release);
    return true;
}

bool RoundTrip(HANDLE pipe, const QueuedCommand& command)
{
    WireRequest request = command.request;
    request.flags = g_menuVisible.load(std::memory_order_acquire) ? 1u : 0u;
    return WriteAll(pipe, &request, sizeof(request)) && ReadResponse(pipe);
}

QueuedCommand MakeCommand(uint32_t command, uint64_t owner = 0, float value = 0, float value2 = 0)
{
    QueuedCommand result;
    result.request.magic = kProtocolMagic;
    result.request.command = command;
    result.request.owner = owner;
    result.request.value = value;
    result.request.value2 = value2;
    return result;
}

void QueueCommand(uint32_t command, uint64_t owner = 0, float value = 0, float value2 = 0)
{
    if (!g_running.load(std::memory_order_acquire) ||
        !g_connected.load(std::memory_order_acquire))
        return;
    if (command == 2 || command == 3 || command == 9)
        ClearMouseGesture();
    std::lock_guard<std::mutex> lock(g_commandMutex);
    if (!g_running.load(std::memory_order_acquire) ||
        !g_connected.load(std::memory_order_acquire))
        return;
    if (g_commands.size() >= 32)
        g_commands.pop_front();
    g_commands.push_back(MakeCommand(command, owner, value, value2));
}

void ClearQueuedCommands()
{
    std::lock_guard<std::mutex> lock(g_commandMutex);
    g_commands.clear();
}

bool TryTakeCommand(QueuedCommand& command)
{
    std::lock_guard<std::mutex> lock(g_commandMutex);
    if (g_commands.empty())
        return false;
    command = g_commands.front();
    g_commands.pop_front();
    return true;
}

bool TryTakeMouseGesture(QueuedCommand& command)
{
    if (!CanOrbitMouse(g_window.load(std::memory_order_acquire)))
    {
        ClearMouseGesture();
        return false;
    }
    std::lock_guard<std::mutex> lock(g_gestureMutex);
    if (g_mouseDeltaX == 0 && g_mouseDeltaY == 0)
        return false;
    command = MakeCommand(8, 0, g_mouseDeltaX, g_mouseDeltaY);
    g_mouseDeltaX = g_mouseDeltaY = 0;
    return true;
}

void SetPipeHandle(HANDLE pipe)
{
    g_pipeHandle.store(pipe, std::memory_order_release);
}

HANDLE ConnectPipe()
{
    char name[64]{};
    sprintf_s(name, "\\\\.\\pipe\\SnowRunnerSpectator-%lu", GetCurrentProcessId());
    wchar_t wideName[128]{};
    MultiByteToWideChar(CP_ACP, 0, name, -1, wideName, static_cast<int>(std::size(wideName)));

    while (g_running.load(std::memory_order_acquire))
    {
        HANDLE pipe = CreateFileW(wideName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE)
            return pipe;
        if (GetLastError() != ERROR_PIPE_BUSY)
            Sleep(250);
        else
            WaitNamedPipeW(wideName, 250);
    }
    return nullptr;
}

void PipeWorker()
{
    while (g_running.load(std::memory_order_acquire))
    {
        HANDLE pipe = ConnectPipe();
        if (!pipe)
        {
            if (g_running.load(std::memory_order_acquire))
                Sleep(250);
            continue;
        }
        SetPipeHandle(pipe);

        Log("Connected to helper pipe");
        bool connected = RoundTrip(pipe, MakeCommand(1));
        if (connected)
        {
            g_connected.store(true, std::memory_order_release);
            auto nextHeartbeat = std::chrono::steady_clock::now() + std::chrono::milliseconds(100);
            while (g_running.load(std::memory_order_acquire))
            {
                QueuedCommand command;
                if (TryTakeCommand(command) || TryTakeMouseGesture(command))
                {
                    if (!RoundTrip(pipe, command))
                        break;
                    nextHeartbeat = std::chrono::steady_clock::now() + std::chrono::milliseconds(100);
                    continue;
                }
                if (std::chrono::steady_clock::now() >= nextHeartbeat)
                {
                    if (!RoundTrip(pipe, MakeCommand(0)))
                        break;
                    nextHeartbeat = std::chrono::steady_clock::now() + std::chrono::milliseconds(100);
                }
                else
                    Sleep(5);
            }
        }

        g_connected.store(false, std::memory_order_release);
        ClearStateDisconnected();
        ClearQueuedCommands();
        SetPipeHandle(nullptr);
        CloseHandle(pipe);
        Log("Helper pipe disconnected; retrying");
    }
    g_connected.store(false, std::memory_order_release);
    ClearStateDisconnected();
    ClearQueuedCommands();
}

void DrainWindowMessages()
{
    std::deque<Win32Message> messages;
    {
        std::lock_guard<std::mutex> lock(g_messageMutex);
        messages.swap(g_messages);
    }
    for (const Win32Message& message : messages)
    {
        if (g_imguiReady.load(std::memory_order_acquire) &&
            (g_menuVisible.load(std::memory_order_acquire) || message.message == WM_KILLFOCUS))
            ImGui_ImplWin32_WndProcHandler(message.hwnd, message.message, message.wParam, message.lParam);
    }
}

void RestoreWindowProc()
{
    HWND window = g_window.load(std::memory_order_acquire);
    WNDPROC original = g_originalWndProc.load(std::memory_order_acquire);
    if (window && original &&
        GetWindowLongPtrW(window, GWLP_WNDPROC) == reinterpret_cast<LONG_PTR>(HookedWndProc))
        SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(original));
    g_originalWndProc.store(nullptr, std::memory_order_release);
    g_window.store(nullptr, std::memory_order_release);
}

void ReleaseRenderTarget()
{
    if (g_renderTarget)
    {
        g_renderTarget->Release();
        g_renderTarget = nullptr;
    }
}

bool CreateRenderTarget(IDXGISwapChain* swapChain)
{
    ID3D11Texture2D* backBuffer = nullptr;
    if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer))) || !backBuffer)
        return false;
    HRESULT result = g_device->CreateRenderTargetView(backBuffer, nullptr, &g_renderTarget);
    backBuffer->Release();
    return SUCCEEDED(result);
}

bool InitializeRenderBackend(IDXGISwapChain* swapChain)
{
    DXGI_SWAP_CHAIN_DESC description{};
    if (FAILED(swapChain->GetDesc(&description)))
        return false;

    if (g_imguiReady.load(std::memory_order_acquire))
    {
        ClearMouseGesture();
        g_imguiReady.store(false, std::memory_order_release);
        RestoreWindowProc();
        ImGui_ImplDX11_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
        ReleaseRenderTarget();
        if (g_context) { g_context->Release(); g_context = nullptr; }
        if (g_device) { g_device->Release(); g_device = nullptr; }
    }

    if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&g_device))) || !g_device)
        return false;
    g_device->GetImmediateContext(&g_context);
    if (!g_context || !CreateRenderTarget(swapChain))
    {
        ReleaseRenderTarget();
        if (g_context) { g_context->Release(); g_context = nullptr; }
        if (g_device) { g_device->Release(); g_device = nullptr; }
        return false;
    }

    g_swapChain = swapChain;
    HWND window = description.OutputWindow;
    g_window.store(window, std::memory_order_release);
    WNDPROC original = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(HookedWndProc)));
    g_originalWndProc.store(original, std::memory_order_release);
    if (!original)
    {
        ReleaseRenderTarget();
        g_context->Release(); g_context = nullptr;
        g_device->Release(); g_device = nullptr;
        g_swapChain = nullptr;
        return false;
    }

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
    ImGui::StyleColorsDark();
    if (!ImGui_ImplWin32_Init(window) || !ImGui_ImplDX11_Init(g_device, g_context))
    {
        RestoreWindowProc();
        ImGui_ImplDX11_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
        ReleaseRenderTarget();
        g_context->Release(); g_context = nullptr;
        g_device->Release(); g_device = nullptr;
        g_swapChain = nullptr;
        return false;
    }
    g_imguiReady.store(true, std::memory_order_release);
    g_captureMouse.store(g_menuVisible.load(std::memory_order_acquire), std::memory_order_release);
    g_captureKeyboard.store(g_menuVisible.load(std::memory_order_acquire), std::memory_order_release);
    Log("D3D11 ImGui backend initialized");
    return true;
}

void CopyState(State& state)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    state = g_state;
}

const char* CurrentAsset(const State& state)
{
    for (const Target& target : state.targets)
        if (target.owner == state.selectedOwner)
            return target.asset.empty() ? "truck" : target.asset.c_str();
    return "truck";
}

void DrawHud(const State& state)
{
    ImDrawList* draw = ImGui::GetForegroundDrawList();
    std::string line;
    if (state.flags & kFollow)
        line = std::string("Spectating: ") + CurrentAsset(state);
    else
        line = "Free camera";
    if (!g_connected.load(std::memory_order_acquire))
        line += "  |  helper offline";
    const int64_t responseTicks = g_lastResponseTicks.load(std::memory_order_acquire);
    const int64_t nowTicks = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    if (responseTicks == 0 || nowTicks - responseTicks > 1000)
        line += "  |  stale";
    ImVec2 position(24.0f, 24.0f);
    draw->AddText(ImVec2(position.x + 1.0f, position.y + 1.0f), IM_COL32(0, 0, 0, 220), line.c_str());
    draw->AddText(position, IM_COL32(240, 240, 240, 255), line.c_str());
    if (!state.status.empty())
    {
        ImVec2 statusPosition(24.0f, 48.0f);
        draw->AddText(statusPosition, IM_COL32(200, 200, 200, 240), state.status.c_str());
    }
}

void DrawMenu(State state)
{
    // Keep the pending truck choice until Spectate is pressed. Reconcile it
    // only when the helper's selection changes, including changes while hidden.
    if (state.selectedOwner != g_uiAuthoritativeOwner)
    {
        g_uiAuthoritativeOwner = state.selectedOwner;
        g_uiSelectedOwner = state.selectedOwner;
    }
    const auto hasTarget = [&state](uint64_t owner)
    {
        return owner != 0 && std::any_of(state.targets.begin(), state.targets.end(),
            [owner](const Target& target) { return target.owner == owner; });
    };
    if (!hasTarget(g_uiSelectedOwner))
        g_uiSelectedOwner = hasTarget(state.selectedOwner) ? state.selectedOwner :
            (state.targets.empty() ? 0 : state.targets.front().owner);
    g_uiFace = (state.flags & kFace) != 0;
    ImGuiViewport* viewport = ImGui::GetMainViewport();
    ImGui::SetNextWindowPos(ImVec2(viewport->WorkPos.x + 24.0f, viewport->WorkPos.y + 24.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(560.0f, 620.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowBgAlpha(0.94f);
    if (!ImGui::Begin("SnowRunner Spectator", nullptr, ImGuiWindowFlags_NoCollapse))
    {
        ImGui::End();
        return;
    }

    ImGui::TextUnformatted(g_connected.load(std::memory_order_acquire) ? "Helper connected" : "Helper offline");
    if (state.flags & kScanning)
        ImGui::SameLine(), ImGui::TextUnformatted("Scanning...");
    if (!state.status.empty())
        ImGui::TextWrapped("%s", state.status.c_str());

    ImGui::Separator();
    ImGui::Text("Validated trucks (%u)", static_cast<unsigned>(state.targets.size()));
    ImGui::BeginChild("TruckList", ImVec2(0, 260), true);
    for (const Target& target : state.targets)
    {
        std::string name = target.asset.empty() ? "truck" : target.asset;
        name += "##" + std::to_string(target.owner);
        bool selected = (g_uiSelectedOwner == target.owner);
        if (ImGui::Selectable(name.c_str(), selected))
            g_uiSelectedOwner = target.owner;
        if (selected && ImGui::IsItemHovered())
        {
            ImGui::SetTooltip("Position %.1f, %.1f, %.1f", target.x, target.y, target.z);
        }
    }
    ImGui::EndChild();

    if (ImGui::Button("Refresh", ImVec2(100, 30)))
        QueueCommand(1);
    ImGui::SameLine();
    if (ImGui::Button("Spectate", ImVec2(110, 30)) && g_uiSelectedOwner != 0)
        QueueCommand(2, g_uiSelectedOwner);
    ImGui::SameLine();
    const bool freeCameraOn = (state.flags & kFreeCam) != 0;
    if (freeCameraOn)
        ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.15f, 0.45f, 0.30f, 1.0f));
    if (ImGui::Button(freeCameraOn ? "Freecamera: On" : "Freecamera: Off", ImVec2(140, 30)))
        QueueCommand(9);
    if (freeCameraOn)
        ImGui::PopStyleColor();
    if (ImGui::IsItemHovered())
        ImGui::SetTooltip(freeCameraOn
            ? "Disable freecamera and stop following."
            : "Enable freecamera.");

    bool face = g_uiFace;
    if (ImGui::Checkbox("Face truck", &face) && face != g_uiFace)
    {
        g_uiFace = face;
        QueueCommand(4, 0, face ? 1.0f : 0.0f);
    }
    ImGui::TextWrapped("While following: hold right mouse button and drag, or use the controller right stick, to move around the truck. Hide this menu with Delete first.");

    ImGui::Separator();
    ImGui::TextUnformatted("Follow framing");
    ImGui::SliderFloat("Distance", &g_uiDistance, 1.0f, 80.0f, "%.1f m");
    if (ImGui::IsItemDeactivatedAfterEdit())
        QueueCommand(6, 0, g_uiDistance, g_uiHeight);
    ImGui::SliderFloat("Height", &g_uiHeight, -10.0f, 30.0f, "%.1f m");
    if (ImGui::IsItemDeactivatedAfterEdit())
        QueueCommand(6, 0, g_uiDistance, g_uiHeight);
    ImGui::SliderFloat("Smoothing", &g_uiSmoothing, 5.0f, 500.0f, "%.0f ms");
    if (ImGui::IsItemDeactivatedAfterEdit())
        QueueCommand(7, 0, g_uiSmoothing);

    ImGui::Separator();
    ImGui::TextUnformatted("Delete toggles this menu");
    ImGui::End();
}

void DrawFrame()
{
    State state;
    CopyState(state);
    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();
    if (g_menuVisible.load(std::memory_order_acquire))
        DrawMenu(state);
    else
        DrawHud(state);

    ImGuiIO& io = ImGui::GetIO();
    g_captureMouse.store(g_menuVisible.load(std::memory_order_acquire) && io.WantCaptureMouse, std::memory_order_release);
    g_captureKeyboard.store(g_menuVisible.load(std::memory_order_acquire) && io.WantCaptureKeyboard, std::memory_order_release);
    ImGui::Render();

    ID3D11RenderTargetView* oldRenderTargets[8]{};
    ID3D11DepthStencilView* oldDepthStencil = nullptr;
    ID3D11HullShader* oldHullShader = nullptr;
    ID3D11DomainShader* oldDomainShader = nullptr;
    ID3D11ComputeShader* oldComputeShader = nullptr;
    ID3D11ClassInstance* oldHullInstances[256]{};
    ID3D11ClassInstance* oldDomainInstances[256]{};
    ID3D11ClassInstance* oldComputeInstances[256]{};
    UINT oldHullInstanceCount = 256;
    UINT oldDomainInstanceCount = 256;
    UINT oldComputeInstanceCount = 256;
    g_context->OMGetRenderTargets(8, oldRenderTargets, &oldDepthStencil);
    g_context->HSGetShader(&oldHullShader, oldHullInstances, &oldHullInstanceCount);
    g_context->DSGetShader(&oldDomainShader, oldDomainInstances, &oldDomainInstanceCount);
    g_context->CSGetShader(&oldComputeShader, oldComputeInstances, &oldComputeInstanceCount);
    g_context->OMSetRenderTargets(1, &g_renderTarget, nullptr);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    g_context->OMSetRenderTargets(8, oldRenderTargets, oldDepthStencil);
    g_context->HSSetShader(oldHullShader, oldHullInstances, oldHullInstanceCount);
    g_context->DSSetShader(oldDomainShader, oldDomainInstances, oldDomainInstanceCount);
    g_context->CSSetShader(oldComputeShader, oldComputeInstances, oldComputeInstanceCount);
    for (ID3D11RenderTargetView* oldRenderTarget : oldRenderTargets)
        if (oldRenderTarget) oldRenderTarget->Release();
    if (oldDepthStencil) oldDepthStencil->Release();
    if (oldHullShader) oldHullShader->Release();
    if (oldDomainShader) oldDomainShader->Release();
    if (oldComputeShader) oldComputeShader->Release();
    for (ID3D11ClassInstance* instance : oldHullInstances)
        if (instance) instance->Release();
    for (ID3D11ClassInstance* instance : oldDomainInstances)
        if (instance) instance->Release();
    for (ID3D11ClassInstance* instance : oldComputeInstances)
        if (instance) instance->Release();
}

void ReleaseImGui()
{
    ClearMouseGesture();
    g_imguiReady.store(false, std::memory_order_release);
    g_captureMouse.store(false, std::memory_order_release);
    g_captureKeyboard.store(false, std::memory_order_release);
    RestoreWindowProc();
    if (ImGui::GetCurrentContext())
    {
        ImGui_ImplDX11_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
    }
    ReleaseRenderTarget();
    if (g_context) { g_context->Release(); g_context = nullptr; }
    if (g_device) { g_device->Release(); g_device = nullptr; }
    g_swapChain = nullptr;
}

void ShutdownInternal()
{
    if (g_shutdownRequested.exchange(true, std::memory_order_acq_rel))
        return;
    g_running.store(false, std::memory_order_release);
    HANDLE bootstrap = g_bootstrapHandle.load(std::memory_order_acquire);
    if (bootstrap && GetCurrentThreadId() != g_bootstrapThreadId.load(std::memory_order_acquire))
    {
        WaitForSingleObject(bootstrap, INFINITE);
        if (g_bootstrapHandle.exchange(nullptr, std::memory_order_acq_rel) == bootstrap)
            CloseHandle(bootstrap);
    }
    // Present owns pipe-thread creation while holding this mutex. Taking the
    // same lock before joining closes the race where an in-flight Present
    // passes the running check just before shutdown begins.
    std::unique_lock<std::mutex> renderLock(g_renderMutex);
    if (g_pipeThread.joinable())
    {
        CancelSynchronousIo(g_pipeThread.native_handle());
        g_pipeThread.join();
    }

    if (g_hooksInstalled.exchange(false, std::memory_order_acq_rel))
    {
        MH_DisableHook(MH_ALL_HOOKS);
        if (g_presentTarget)
            MH_RemoveHook(g_presentTarget);
        if (g_resizeTarget)
            MH_RemoveHook(g_resizeTarget);
        MH_Uninitialize();
    }

    ReleaseImGui();
    renderLock.unlock();
    Log("Spectator UI shutdown complete");
    std::lock_guard<std::mutex> logLock(g_logMutex);
    if (g_log.is_open())
        g_log.close();
}

LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    HWND window = g_window.load(std::memory_order_acquire);
    WNDPROC original = g_originalWndProc.load(std::memory_order_acquire);
    const auto forward = [&]()
    {
        return original ? CallWindowProcW(original, hwnd, message, wParam, lParam) :
            DefWindowProcW(hwnd, message, wParam, lParam);
    };
    if (!g_imguiReady.load(std::memory_order_acquire) || hwnd != window)
        return forward();

    // Focus loss must be processed even after the foreground window changes.
    if (message == WM_KILLFOCUS || (message == WM_ACTIVATEAPP && !wParam))
    {
        ClearMouseGesture();
        g_captureMouse.store(false, std::memory_order_release);
        g_captureKeyboard.store(false, std::memory_order_release);
        {
            std::lock_guard<std::mutex> lock(g_messageMutex);
            g_messages.clear();
            g_messages.push_back({ hwnd, WM_KILLFOCUS, 0, 0 });
        }
        return forward();
    }
    if (GetForegroundWindow() != hwnd)
        return forward();

    if (message == WM_KEYDOWN && wParam == VK_DELETE && (lParam & (1ll << 30)) == 0)
    {
        g_menuVisible.store(!g_menuVisible.load(std::memory_order_acquire), std::memory_order_release);
        ClearMouseGesture();
        g_captureMouse.store(false, std::memory_order_release);
        g_captureKeyboard.store(false, std::memory_order_release);
        // Reset ImGui button/key state across menu transitions.
        std::lock_guard<std::mutex> lock(g_messageMutex);
        g_messages.clear();
        g_messages.push_back({ hwnd, WM_KILLFOCUS, 0, 0 });
        return 0;
    }

    if (CanOrbitMouse(hwnd))
    {
        if (message == WM_INPUT)
        {
            RAWINPUT input{};
            UINT size = sizeof(input);
            const UINT read = GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT,
                &input, &size, sizeof(RAWINPUTHEADER));
            if (read != static_cast<UINT>(-1) && read >= offsetof(RAWINPUT, data) + sizeof(RAWMOUSE) &&
                input.header.dwType == RIM_TYPEMOUSE)
            {
                const RAWMOUSE& mouse = input.data.mouse;
                // Some games route raw input to a different window. Legacy
                // motion is a fallback only until this HWND proves raw support.
                if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0 &&
                    !g_rawMouseSeen.exchange(true, std::memory_order_acq_rel))
                {
                    std::lock_guard<std::mutex> lock(g_gestureMutex);
                    g_mouseDeltaX = g_mouseDeltaY = 0;
                    g_legacyMouseBaseline = false;
                }
                const bool owned = g_dragOwned.load(std::memory_order_acquire);
                const bool down = (mouse.usButtonFlags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0;
                const bool up = (mouse.usButtonFlags & RI_MOUSE_RIGHT_BUTTON_UP) != 0;
                const bool held = owned || down || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
                if (held)
                {
                    g_dragOwned.store(!up, std::memory_order_release);
                    if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0)
                    {
                        std::lock_guard<std::mutex> lock(g_gestureMutex);
                        g_mouseDeltaX = std::clamp(g_mouseDeltaX + static_cast<float>(mouse.lLastX), -512.0f, 512.0f);
                        g_mouseDeltaY = std::clamp(g_mouseDeltaY + static_cast<float>(mouse.lLastY), -512.0f, 512.0f);
                    }
                    // Foreground raw input requires DefWindowProc cleanup even
                    // when withheld from the game's native camera input.
                    if (GET_RAWINPUT_CODE_WPARAM(wParam) == RIM_INPUT)
                        DefWindowProcW(hwnd, message, wParam, lParam);
                    return 0;
                }
            }
        }
        const bool wasOwned = g_dragOwned.load(std::memory_order_acquire);
        if (message == WM_RBUTTONDOWN || message == WM_RBUTTONDBLCLK)
        {
            g_dragOwned.store(true, std::memory_order_release);
            {
                std::lock_guard<std::mutex> lock(g_gestureMutex);
                g_legacyMousePosition = { static_cast<short>(LOWORD(lParam)), static_cast<short>(HIWORD(lParam)) };
                g_legacyMouseBaseline = true;
            }
            return 0;
        }
        if (message == WM_RBUTTONUP && wasOwned)
        {
            g_dragOwned.store(false, std::memory_order_release);
            {
                std::lock_guard<std::mutex> lock(g_gestureMutex);
                g_legacyMouseBaseline = false;
            }
            return 0;
        }
        if (message == WM_MOUSEMOVE && (wasOwned || (wParam & MK_RBUTTON) != 0))
        {
            if (!g_rawMouseSeen.load(std::memory_order_acquire))
            {
                const POINT position{ static_cast<short>(LOWORD(lParam)), static_cast<short>(HIWORD(lParam)) };
                std::lock_guard<std::mutex> lock(g_gestureMutex);
                if (g_legacyMouseBaseline)
                {
                    g_mouseDeltaX = std::clamp(g_mouseDeltaX + static_cast<float>(position.x - g_legacyMousePosition.x), -512.0f, 512.0f);
                    g_mouseDeltaY = std::clamp(g_mouseDeltaY + static_cast<float>(position.y - g_legacyMousePosition.y), -512.0f, 512.0f);
                }
                g_legacyMousePosition = position;
                g_legacyMouseBaseline = true;
            }
            return 0;
        }
    }
    else
        ClearMouseGesture();

    const bool focusMessage = message == WM_KILLFOCUS || message == WM_SETFOCUS;
    const bool mouseMessage = message >= WM_MOUSEFIRST && message <= WM_MOUSELAST;
    const bool keyboardMessage = (message >= WM_KEYFIRST && message <= WM_KEYLAST) ||
                                 message == WM_CHAR || message == WM_SYSCHAR;
    if (g_menuVisible.load(std::memory_order_acquire) && (mouseMessage || keyboardMessage || focusMessage))
    {
        std::lock_guard<std::mutex> lock(g_messageMutex);
        // Coalesce motion, but always retain releases/focus events. A busy
        // frame must never leave an ImGui button held indefinitely.
        if (message == WM_MOUSEMOVE && !g_messages.empty() && g_messages.back().message == WM_MOUSEMOVE)
            g_messages.back() = { hwnd, message, wParam, lParam };
        else if (message != WM_MOUSEMOVE || g_messages.size() < 256)
            g_messages.push_back({ hwnd, message, wParam, lParam });
        if ((mouseMessage && g_captureMouse.load(std::memory_order_acquire)) ||
            (keyboardMessage && g_captureKeyboard.load(std::memory_order_acquire)))
            return 0;
    }
    return forward();
}

HRESULT __stdcall HookedResizeBuffers(IDXGISwapChain* swapChain, UINT bufferCount, UINT width,
                                      UINT height, DXGI_FORMAT format, UINT swapChainFlags)
{
    if (!g_originalResizeBuffers)
        return E_FAIL;
    if (swapChain != g_swapChain || !g_imguiReady.load(std::memory_order_acquire))
        return g_originalResizeBuffers(swapChain, bufferCount, width, height, format, swapChainFlags);

    std::lock_guard<std::mutex> renderLock(g_renderMutex);
    ImGui_ImplDX11_InvalidateDeviceObjects();
    ReleaseRenderTarget();
    HRESULT result = g_originalResizeBuffers(swapChain, bufferCount, width, height, format, swapChainFlags);
    if (SUCCEEDED(result))
    {
        if (CreateRenderTarget(swapChain))
            ImGui_ImplDX11_CreateDeviceObjects();
        else
            ReleaseImGui();
    }
    else if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET)
        ReleaseImGui();
    return result;
}

HRESULT __stdcall HookedPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
{
    if (!g_originalPresent)
        return E_FAIL;
    if (!g_running.load(std::memory_order_acquire) || g_shutdownRequested.load(std::memory_order_acquire))
        return g_originalPresent(swapChain, syncInterval, flags);

    if (!g_renderMutex.try_lock())
        return g_originalPresent(swapChain, syncInterval, flags);
    std::unique_lock<std::mutex> renderLock(g_renderMutex, std::adopt_lock);

    if (!g_imguiReady.load(std::memory_order_acquire))
    {
        if (!InitializeRenderBackend(swapChain))
            return g_originalPresent(swapChain, syncInterval, flags);
        if (!g_pipeThread.joinable())
            g_pipeThread = std::thread(PipeWorker);
    }
    else if (swapChain != g_swapChain)
    {
        // SnowRunner can present from auxiliary swap chains. Ignore those,
        // but rebind when the primary chain was recreated for the same window
        // after a resize/device reset.
        DXGI_SWAP_CHAIN_DESC description{};
        if (FAILED(swapChain->GetDesc(&description)) ||
            description.OutputWindow != g_window.load(std::memory_order_acquire))
            return g_originalPresent(swapChain, syncInterval, flags);
        ReleaseImGui();
        if (!InitializeRenderBackend(swapChain))
            return g_originalPresent(swapChain, syncInterval, flags);
    }
    if (!g_context)
        return g_originalPresent(swapChain, syncInterval, flags);
    if (!g_renderTarget)
    {
        if (!CreateRenderTarget(swapChain))
            return g_originalPresent(swapChain, syncInterval, flags);
        ImGui_ImplDX11_CreateDeviceObjects();
    }
    DrainWindowMessages();
    DrawFrame();
    HRESULT result = g_originalPresent(swapChain, syncInterval, flags);
    if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET)
        ReleaseImGui();
    return result;
}

LRESULT CALLBACK DummyWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

bool InstallHooks()
{
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = DummyWndProc;
    windowClass.hInstance = g_module;
    windowClass.lpszClassName = L"SnowFlyer2SpectatorUI.Dummy";
    if (!RegisterClassExW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        return false;
    HWND dummyWindow = CreateWindowExW(0, windowClass.lpszClassName, L"SnowFlyer2 Spectator UI",
                                       WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, g_module, nullptr);
    if (!dummyWindow)
        return false;

    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferCount = 1;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.OutputWindow = dummyWindow;
    description.SampleDesc.Count = 1;
    description.Windowed = TRUE;
    D3D_FEATURE_LEVEL featureLevel{};
    IDXGISwapChain* dummySwapChain = nullptr;
    ID3D11Device* dummyDevice = nullptr;
    ID3D11DeviceContext* dummyContext = nullptr;
    HRESULT result = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                                                    nullptr, 0, D3D11_SDK_VERSION, &description,
                                                    &dummySwapChain, &dummyDevice, &featureLevel, &dummyContext);
    if (FAILED(result))
    {
        result = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0,
                                                nullptr, 0, D3D11_SDK_VERSION, &description,
                                                &dummySwapChain, &dummyDevice, &featureLevel, &dummyContext);
    }
    if (FAILED(result))
    {
        DestroyWindow(dummyWindow);
        UnregisterClassW(windowClass.lpszClassName, g_module);
        return false;
    }

    void** table = *reinterpret_cast<void***>(dummySwapChain);
    MH_STATUS status = MH_Initialize();
    g_presentTarget = table[8];
    g_resizeTarget = table[13];
    if (status == MH_OK)
        status = MH_CreateHook(g_presentTarget, reinterpret_cast<LPVOID>(HookedPresent), reinterpret_cast<LPVOID*>(&g_originalPresent));
    if (status == MH_OK)
        status = MH_CreateHook(g_resizeTarget, reinterpret_cast<LPVOID>(HookedResizeBuffers), reinterpret_cast<LPVOID*>(&g_originalResizeBuffers));
    if (status == MH_OK)
        status = MH_EnableHook(MH_ALL_HOOKS);
    if (status != MH_OK)
    {
        Log(std::string("MinHook setup failed: ") + MH_StatusToString(status));
        MH_Uninitialize();
    }
    else
    {
        g_hooksInstalled.store(true, std::memory_order_release);
        Log("Present and ResizeBuffers hooks installed");
    }

    dummyContext->Release();
    dummyDevice->Release();
    dummySwapChain->Release();
    DestroyWindow(dummyWindow);
    UnregisterClassW(windowClass.lpszClassName, g_module);
    return status == MH_OK;
}

DWORD WINAPI BootstrapThread(LPVOID)
{
    g_bootstrapThreadId.store(GetCurrentThreadId(), std::memory_order_release);
    wchar_t modulePath[MAX_PATH]{};
    GetModuleFileNameW(g_module, modulePath, static_cast<DWORD>(std::size(modulePath)));
    std::filesystem::path logPath(modulePath);
    logPath.replace_filename(L"SpectatorUI.log");
    {
        std::lock_guard<std::mutex> lock(g_logMutex);
        g_log.open(logPath, std::ios::out | std::ios::app);
    }
    Log("Bootstrap started");
    if (g_shutdownRequested.load(std::memory_order_acquire))
        return 0;
    g_running.store(true, std::memory_order_release);
    if (!InstallHooks())
    {
        Log("Unable to install D3D11 hooks");
        g_running.store(false, std::memory_order_release);
    }
    return 0;
}
}

extern "C" __declspec(dllexport) void InitializeSpectatorUI()
{
    if (g_bootstrapStarted.exchange(true, std::memory_order_acq_rel))
        return;
    // Create suspended so ShutdownSpectatorUI can always observe and wait for
    // the bootstrap handle before disabling hooks.
    HANDLE thread = CreateThread(nullptr, 0, BootstrapThread, nullptr, CREATE_SUSPENDED, nullptr);
    if (thread)
    {
        g_bootstrapHandle.store(thread, std::memory_order_release);
        ResumeThread(thread);
    }
    else
        g_bootstrapStarted.store(false, std::memory_order_release);
}

extern "C" __declspec(dllexport) void ShutdownSpectatorUI()
{
    ShutdownInternal();
}

BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        HMODULE pinned = nullptr;
        // Hooks and the Present callback can outlive the caller that loaded
        // this DLL. Keep the module resident; explicit shutdown is the only
        // supported teardown path.
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_PIN,
                           reinterpret_cast<LPCWSTR>(module), &pinned);
        // Do not create threads or touch graphics state from DllMain.  The
        // loader-lock is held here, and BootstrapThread performs filesystem,
        // window-class, Direct3D, and MinHook work that is not loader-lock
        // safe.  The managed helper calls the exported initializer after
        // LoadLibraryW has returned in the game process.
    }
    return TRUE;
}

