#include "pch.h"
#include "IApplication.h"
#include "WrapperExtension.h"
#include "json.hpp"
#include "BridgeSerializer.h"

// ---------------------------------------------------------------------------
// Reference the global defined in WrapperExtension.cpp
// ---------------------------------------------------------------------------
extern WrapperExtension* g_Extension;

// Forward-declare WrapperExtInit defined in WrapperExtension.cpp
extern "C" IExtension* WrapperExtInit(IApplication* iApplication);

// ---------------------------------------------------------------------------
// Function pointer types passed from C#
// ---------------------------------------------------------------------------
typedef void(__stdcall* WebMessageCallbackFn)(
    const char* messageId,
    const char* paramsJson,
    double asyncId);

typedef void(__stdcall* LogCallbackFn)(
    int level,
    const char* message);

// ---------------------------------------------------------------------------
// IApplication implementation for C# interop.
// Adapts the Construct IApplication vtable to flat C function pointers
// that C# can pass via P/Invoke. WrapperExtension.cpp is unchanged.
// ---------------------------------------------------------------------------
class CSharpApplication : public IApplication
{
public:
    CSharpApplication(
        const char* appId,
        bool isDev,
        WebMessageCallbackFn msgCb,
        LogCallbackFn logCb)
        : _msgCallback(msgCb)
        , _logCallback(logCb)
    {
        // Build the minimal package.json that WrapperExtension::Init() expects.
        // Matches the Construct exported-properties format.
        nlohmann::json j;
        j["exported-properties"]["scirra-steam"]["app-id"]           = appId ? appId : "0";
        j["exported-properties"]["scirra-steam"]["development-mode"] = isDev;
        _packageJson = j.dump();

        // Derive app folder from the running EXE path.
        char buf[MAX_PATH] = {};
        GetModuleFileNameA(nullptr, buf, MAX_PATH);
        std::string path(buf);
        const auto pos = path.find_last_of("\\/");
        _appFolder = (pos != std::string::npos) ? path.substr(0, pos) : ".";
        _webFolder = _appFolder + "\\www";
    }

    // Send a message from the DLL to C#.
    // Converts NamedExtensionParameterPOD array to a JSON object string.
    void SendWebMessage(
        const char* messageId,
        size_t paramCount,
        const NamedExtensionParameterPOD* paramArr,
        double asyncId) override
    {
        const std::string json = SerializeNamedParamsToJson(paramCount, paramArr);
        if (_msgCallback)
            _msgCallback(messageId, json.c_str(), asyncId);
    }

    bool RegisterComponentId(const char*) override              { return true; }

    const char* GetAppFolder() override                         { return _appFolder.c_str(); }
    const char* GetWebResourceFolder() override                 { return _webFolder.c_str(); }
    const char* GetCurrentAppDataFolder() override              { return _appFolder.c_str(); }
    const char* GetPackageJsonContent() override                { return _packageJson.c_str(); }
    const char* GetPathForKnownPickerTag(const char*) override  { return ""; }

    void SetSdkVersion(int) override {}

    void SetSharedPtr(const char*, void*) override {}
    void* GetSharedPtr(const char*) override        { return nullptr; }
    void RemoveSharedPtr(const char*) override      {}

    uint32_t GetWrapperApiVersion() override        { return WRAPPER_EXT_SDK_VERSION; }

    void LogToConsole(LogLevel level, const char* message) override
    {
        if (_logCallback)
            _logCallback(static_cast<int>(level), message ? message : "");
    }

private:
    WebMessageCallbackFn _msgCallback;
    LogCallbackFn        _logCallback;
    std::string          _packageJson;
    std::string          _appFolder;
    std::string          _webFolder;
};

// ---------------------------------------------------------------------------
// Module-level instances
// ---------------------------------------------------------------------------
static CSharpApplication* g_App = nullptr;

// ---------------------------------------------------------------------------
// Flat C exports for C# P/Invoke
// ---------------------------------------------------------------------------
extern "C"
{
    // Initialize Steam. Returns true even if Steam is not running;
    // failure is reported via the log callback.
    DLLEXPORT bool SteamBridge_Init(
        const char* appId,
        bool isDev,
        WebMessageCallbackFn msgCallback,
        LogCallbackFn logCallback)
    {
        if (g_Extension) return true;   // prevent double-init

        g_App = new CSharpApplication(appId, isDev, msgCallback, logCallback);

        // WrapperExtInit sets g_Extension (defined in WrapperExtension.cpp).
        WrapperExtInit(g_App);

        // Init() reads package.json and calls SteamAPI_Init().
        if (g_Extension)
            g_Extension->Init();

        return true;
    }

    // Call on application exit.
    DLLEXPORT void SteamBridge_Shutdown()
    {
        if (g_Extension)
        {
            g_Extension->Release();
            delete g_Extension;
            g_Extension = nullptr;
        }
        if (g_App)
        {
            delete g_App;
            g_App = nullptr;
        }
    }

    // Forward a message from JS to the DLL.
    // paramsJson must be a JSON array string: [val0, val1, ...]
    DLLEXPORT void SteamBridge_SendMessage(
        const char* messageId,
        const char* paramsJson,
        double asyncId)
    {
        if (!g_Extension) return;

        auto parsed = ParseJsonArrayToParams(paramsJson);

        g_Extension->OnWebMessage(
            messageId,
            parsed.params.size(),
            parsed.params.empty() ? nullptr : parsed.params.data(),
            asyncId);
    }

    // Trigger SteamAPI_RunCallbacks() via the "run-callbacks" message.
    // Called periodically from App.cs timer (every 100 ms).
    DLLEXPORT void SteamBridge_RunCallbacks()
    {
        SteamBridge_SendMessage("run-callbacks", "[]", -1.0);
    }
}
