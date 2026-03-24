// pch.h: This is a precompiled header file.
// Files listed below are compiled only once, improving build performance for future builds.
// This also affects IntelliSense performance, including code completion and many code browsing features.
// However, files listed here are ALL re-compiled if any one of them is updated between builds.
// Do not add files here that you will be updating frequently as this negates the performance advantage.

#ifndef PCH_H
#define PCH_H

// add headers that you want to pre-compile here

#ifdef _WIN32

// Windows Header Files
#define WIN32_LEAN_AND_MEAN		// Exclude rarely-used stuff from Windows headers
#include <windows.h>

#define DLLEXPORT __declspec(dllexport)

// Include Steamworks SDK for Windows.
// The include/library search paths are configured by the project file and should point
// to a locally provisioned Steamworks SDK, typically via STEAMWORKS_SDK_ROOT.
#if __has_include(<steam/steam_api.h>)

	// NOTE: the project properties define _CRT_SECURE_NO_WARNINGS to suppress security errors in the Steamworks SDK
	#include <steam/steam_api.h>

	// Link Steamworks lib file
	#if defined(_M_X64)
		#pragma comment(lib, "steam_api64.lib")
	#elif defined(_M_IX86)
		#pragma comment(lib, "steam_api.lib")
	#else
		#error "Unable to identify architecture for Steamworks lib file"
	#endif

#else
	#error "Unable to find steam/steam_api.h. Configure Steamworks SDK include paths (for example via STEAMWORKS_SDK_ROOT)."
#endif

#else

// Empty defines for non-Microsoft platforms
#define DECLSPEC_NOVTABLE
#define DLLEXPORT

// Include Steamworks SDK for macOS/Linux.
#if __has_include(<steam/steam_api.h>)

	#include <steam/steam_api.h>

#else
	#error "Unable to find steam/steam_api.h. Configure Steamworks SDK include paths."
#endif

#endif

// STL includes
#include <vector>		// std::vector
#include <map>			// std::map
#include <string>		// std::string, std::wstring
#include <sstream>		// std::stringstream
#include <memory>		// std::unique_ptr

// SDK utilities
#include "Utils.h"

#endif //PCH_H
