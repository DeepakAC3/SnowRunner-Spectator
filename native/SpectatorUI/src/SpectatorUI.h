#pragma once

#ifdef _WIN32
extern "C" __declspec(dllexport) void InitializeSpectatorUI();
extern "C" __declspec(dllexport) void ShutdownSpectatorUI();
#endif

