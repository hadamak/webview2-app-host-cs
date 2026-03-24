
#include "pch.h"
#include "WrapperExtension.h"

#include "json.hpp"
#include <algorithm>

#if defined(__APPLE__) || defined(__linux__)
#include <stdlib.h>		// for setenv()
#endif

const char* COMPONENT_ID = "scirra-steam";

//////////////////////////////////////////////////////
// Boilerplate stuff
WrapperExtension* g_Extension = nullptr;

// Main DLL export function to initialize extension.
extern "C" {
	DLLEXPORT IExtension* WrapperExtInit(IApplication* iApplication)
	{
		g_Extension = new WrapperExtension(iApplication);
		return g_Extension;
	}
}

class PendingSteamCall {
public:
	explicit PendingSteamCall(WrapperExtension& owner_)
		: owner(owner_)
	{
		owner.AddPendingSteamCall(this);
	}

	virtual ~PendingSteamCall()
	{
		owner.RemovePendingSteamCall(this);
	}

private:
	WrapperExtension& owner;
};

namespace {

std::string BoolToString(bool value)
{
	return value ? "true" : "false";
}

std::string SteamIdToString(const CSteamID& steamId)
{
	return std::to_string(steamId.ConvertToUint64());
}

std::string LeaderboardHandleToString(SteamLeaderboard_t handle)
{
	return std::to_string(static_cast<uint64>(handle));
}

bool TryParseLeaderboardHandle(const std::string& handleStr, SteamLeaderboard_t& outHandle)
{
	try {
		outHandle = static_cast<SteamLeaderboard_t>(std::stoull(handleStr));
		return true;
	}
	catch (...)
	{
		outHandle = 0;
		return false;
	}
}

std::vector<int32> ParseIntCsv(const std::string& csv)
{
	std::vector<int32> values;

	if (csv.empty())
		return values;

	for (const std::string& part : SplitString(csv, ","))
	{
		if (part.empty())
			continue;

		try {
			values.push_back(std::stoi(part));
		}
		catch (...)
		{
		}
	}

	return values;
}

class PendingLeaderboardFindCall : public PendingSteamCall {
public:
	PendingLeaderboardFindCall(WrapperExtension& owner_, double asyncId_, const std::string& name_)
		: PendingSteamCall(owner_),
		  owner(owner_),
		  asyncId(asyncId_),
		  leaderboardName(name_)
	{
	}

	void Set(SteamAPICall_t apiCall)
	{
		callResult.Set(apiCall, this, &PendingLeaderboardFindCall::OnResult);
	}

private:
	void OnResult(LeaderboardFindResult_t* pCallback, bool isIoFailure)
	{
		if (!pCallback || isIoFailure || !pCallback->m_bLeaderboardFound)
		{
			owner.SendAsyncResponse({
				{ "isOk", false },
				{ "leaderboardName", leaderboardName }
			}, asyncId);
			delete this;
			return;
		}

		const SteamLeaderboard_t handle = pCallback->m_hSteamLeaderboard;
		owner.SendAsyncResponse({
			{ "isOk", true },
			{ "leaderboardName", leaderboardName },
			{ "leaderboardHandle", LeaderboardHandleToString(handle) }
		}, asyncId);
		delete this;
	}

	WrapperExtension& owner;
	double asyncId;
	std::string leaderboardName;
	CCallResult<PendingLeaderboardFindCall, LeaderboardFindResult_t> callResult;
};

class PendingLeaderboardUploadCall : public PendingSteamCall {
public:
	PendingLeaderboardUploadCall(WrapperExtension& owner_, double asyncId_, const std::string& leaderboardHandleStr_)
		: PendingSteamCall(owner_),
		  owner(owner_),
		  asyncId(asyncId_),
		  leaderboardHandleStr(leaderboardHandleStr_)
	{
	}

	void Set(SteamAPICall_t apiCall)
	{
		callResult.Set(apiCall, this, &PendingLeaderboardUploadCall::OnResult);
	}

private:
	void OnResult(LeaderboardScoreUploaded_t* pCallback, bool isIoFailure)
	{
		if (!pCallback || isIoFailure || !pCallback->m_bSuccess)
		{
			owner.SendAsyncResponse({
				{ "isOk", false },
				{ "leaderboardHandle", leaderboardHandleStr }
			}, asyncId);
			delete this;
			return;
		}

		owner.SendAsyncResponse({
			{ "isOk", true },
			{ "leaderboardHandle", leaderboardHandleStr },
			{ "wasScoreChanged", pCallback->m_bScoreChanged != 0 },
			{ "globalRankNew", static_cast<double>(pCallback->m_nGlobalRankNew) },
			{ "globalRankPrevious", static_cast<double>(pCallback->m_nGlobalRankPrevious) }
		}, asyncId);
		delete this;
	}

	WrapperExtension& owner;
	double asyncId;
	std::string leaderboardHandleStr;
	CCallResult<PendingLeaderboardUploadCall, LeaderboardScoreUploaded_t> callResult;
};

class PendingLeaderboardDownloadCall : public PendingSteamCall {
public:
	PendingLeaderboardDownloadCall(WrapperExtension& owner_, double asyncId_, const std::string& leaderboardHandleStr_)
		: PendingSteamCall(owner_),
		  owner(owner_),
		  asyncId(asyncId_),
		  leaderboardHandleStr(leaderboardHandleStr_)
	{
	}

	void Set(SteamAPICall_t apiCall)
	{
		callResult.Set(apiCall, this, &PendingLeaderboardDownloadCall::OnResult);
	}

private:
	void OnResult(LeaderboardScoresDownloaded_t* pCallback, bool isIoFailure)
	{
		if (!pCallback || isIoFailure)
		{
			owner.SendAsyncResponse({
				{ "isOk", false },
				{ "leaderboardHandle", leaderboardHandleStr }
			}, asyncId);
			delete this;
			return;
		}

		nlohmann::json entries = nlohmann::json::array();
		for (int index = 0; index < pCallback->m_cEntryCount; ++index)
		{
			LeaderboardEntry_t entry = {};
			std::vector<int32> details(16, 0);
			if (!SteamUserStats()->GetDownloadedLeaderboardEntry(
				pCallback->m_hSteamLeaderboardEntries,
				index,
				&entry,
				details.data(),
				static_cast<int>(details.size())))
			{
				continue;
			}

			nlohmann::json item = {
				{ "steamId64Bit", SteamIdToString(entry.m_steamIDUser) },
				{ "globalRank", entry.m_nGlobalRank },
				{ "score", entry.m_nScore },
				{ "ugcHandle", std::to_string(entry.m_hUGC) },
				{ "personaName", SteamFriends()->GetFriendPersonaName(entry.m_steamIDUser) }
			};

			nlohmann::json detailJson = nlohmann::json::array();
			for (int detailIndex = 0; detailIndex < entry.m_cDetails && detailIndex < static_cast<int>(details.size()); ++detailIndex)
				detailJson.push_back(details[detailIndex]);

			item["details"] = detailJson;
			entries.push_back(item);
		}

		owner.SendAsyncResponse({
			{ "isOk", true },
			{ "leaderboardHandle", leaderboardHandleStr },
			{ "entryCount", static_cast<double>(pCallback->m_cEntryCount) },
			{ "entriesJson", entries.dump() }
		}, asyncId);
		delete this;
	}

	WrapperExtension& owner;
	double asyncId;
	std::string leaderboardHandleStr;
	CCallResult<PendingLeaderboardDownloadCall, LeaderboardScoresDownloaded_t> callResult;
};

}

// Helper method to call HandleWebMessage() with more useful types, as OnWebMessage() must deal with
// plain-old-data types for crossing a DLL boundary.
void WrapperExtension::OnWebMessage(const char* messageId_, size_t paramCount, const ExtensionParameterPOD* paramArr, double asyncId)
{
	HandleWebMessage(messageId_, UnpackExtensionParameterArray(paramCount, paramArr), asyncId);
}

void WrapperExtension::SendWebMessage(const std::string& messageId, const std::map<std::string, ExtensionParameter>& params, double asyncId)
{
	std::vector<NamedExtensionParameterPOD> paramArr = PackNamedExtensionParameters(params);
	iApplication->SendWebMessage(messageId.c_str(), paramArr.size(), paramArr.empty() ? nullptr : paramArr.data(), asyncId);
}

// Helper method for sending a response to an async message (when asyncId is not -1.0).
// In this case the message ID is not used, so this just calls SendWebMessage() with an empty message ID.
void WrapperExtension::SendAsyncResponse(const std::map<std::string, ExtensionParameter>& params, double asyncId)
{
	SendWebMessage("", params, asyncId);
}

//////////////////////////////////////////////////////
// WrapperExtension
WrapperExtension::WrapperExtension(IApplication* iApplication_)
	: iApplication(iApplication_),
	  didSteamInitOk(false),
	  areUserStatsReady(false),
	  didCreateSteamCallbacks(false),
	  pendingAuthTicketForWebApiAsyncId(-1)
{
	LogMessage("Loaded extension");

	// Tell the host application the SDK version used. Don't change this.
	iApplication->SetSdkVersion(WRAPPER_EXT_SDK_VERSION);

	// Register the "scirra-steam" component for JavaScript messaging
	iApplication->RegisterComponentId(COMPONENT_ID);
}

void WrapperExtension::Init()
{
	// Called during startup after all other extensions have been loaded.
	// Parse the content of package.json and read the exported app ID and development mode properties.
	// These are exported with the SetWrapperExportProperties() method and end up in package.json like this:
	//{
	//	...
	//	"exported-properties": {
	//		"scirra-steam": {
	//			"app-id": "480",
	//			"development-mode": true
	//		}
	//	}
	//}
	// Note the app ID is a string.
	std::string appId;
	bool isDevelopmentMode = false;

	try {
		auto packageJson = nlohmann::json::parse(iApplication->GetPackageJsonContent());
		const auto& steamProps = packageJson["exported-properties"][COMPONENT_ID];
		appId = steamProps["app-id"].get<std::string>();
		TrimString(appId);
		isDevelopmentMode = steamProps["development-mode"].get<bool>();

		std::stringstream ss;
		ss << "Parsed package JSON (app ID " << appId << ", development mode " << isDevelopmentMode << ")";
		LogMessage(ss.str());
	}
	catch (...)
	{
		LogMessage("Failed to read properties package JSON", IApplication::LogLevel::error);
		return;
	}

	InitSteamworksSDK(appId, isDevelopmentMode);
}

void WrapperExtension::Release()
{
	LogMessage("Releasing extension");
	areUserStatsReady = false;
	didCreateSteamCallbacks = false;
	while (!pendingSteamCalls.empty())
		delete pendingSteamCalls.back();

	if (didSteamInitOk)
	{
		// Destroy SteamCallbacks class.
		steamCallbacks.reset(nullptr);

		// Shut down Steam API.
		SteamAPI_Shutdown();
	}
}

void WrapperExtension::InitSteamworksSDK(const std::string& initAppId, bool isDevelopmentMode)
{
	// Before calling SteamAPI_Init(), check if the plugin has an app ID set.
	if (!initAppId.empty())
	{
		// If development mode is set and an app ID is provided, then store the app ID
		// in the SteamAppId environment variable for this process. This is undocumented but
		// works OK and is used in other Steam codebases, and is a more convenient way to specify
		// the app ID during testing than having to use steam_appid.txt. Without this (if no
		// app ID is provided, or if development mode is turned off for release) then Steam will
		// determine the app ID automatically (including checking steam_appid.txt if anyone
		// prefers using that), but initialization will fail if Steam cannot determine any app ID.
		if (isDevelopmentMode)
		{
#ifdef _WIN32
			std::wstring initAppIdW = Utf8ToWide(initAppId);
			SetEnvironmentVariable(L"SteamAppId", initAppIdW.c_str());
#else
			setenv("SteamAppId", initAppId.c_str(), 1);
#endif
		}
		else
		{
			// When not in development mode, call SteamAPI_RestartAppIfNecessary() with the
			// provided app ID and quit the app if it returns true. This requires the app ID
			// as a number, so convert the string to a number (ignoring any exception). 
			// The presence of the SteamAppId environment variable (or steam_appid.txt)
			// suppresses SteamAPI_RestartAppIfNecessary() returning true, so this is
			// only done if the development mode setting is turned off.
			uint32 appId = 0;
			try {
				appId = std::stoul(initAppId);
			}
			catch (...)
			{
				appId = 0;				// ignore exception
			}

			if (appId != 0 && SteamAPI_RestartAppIfNecessary(appId))
			{
				LogMessage("SteamAPI_RestartAppIfNecessary() returned true; quitting app", IApplication::LogLevel::warning);
#ifdef _WIN32
				PostQuitMessage(0);
#else
				exit(0);
#endif

				// There's no point doing anything else now the app is quitting, so return.
				return;
			}
		}
	}

	// Initialize the Steam API.
	didSteamInitOk = SteamAPI_Init();
	if (didSteamInitOk)
	{
		LogMessage("Steam API initialized successfully");
	}
	else
	{
		LogMessage("Steam API failed to initialize", IApplication::LogLevel::error);
	}
}

void WrapperExtension::LogMessage(const std::string& msg, IApplication::LogLevel level)
{
#ifndef _DEBUG
	if (level == IApplication::LogLevel::normal)
		return;
#endif

	// Log messages both to the browser console with the LogToConsole() method, and also to the debug output
	// with the DebugLog() helper function, to ensure whichever log we're looking at includes the log messages.
	std::stringstream ss;
	ss << "[Steamworks] " << msg;
	iApplication->LogToConsole(level, ss.str().c_str());
	
	// Add trailing newline for debug output
	ss << "\n";
	DebugLog(ss.str().c_str());
}

bool WrapperExtension::EnsureSteamReady(double asyncId, const std::string& actionName)
{
	if (didSteamInitOk)
		return true;

	LogMessage(actionName + " rejected because Steam is not initialized", IApplication::LogLevel::warning);
	if (asyncId != -1.0)
	{
		SendAsyncResponse({
			{ "isOk", false }
		}, asyncId);
	}
	return false;
}

bool WrapperExtension::EnsureUserStatsReady(double asyncId, const std::string& actionName)
{
	if (!EnsureSteamReady(asyncId, actionName))
		return false;

	if (areUserStatsReady)
		return true;

	LogMessage(actionName + " rejected because user stats are not ready", IApplication::LogLevel::warning);
	if (asyncId != -1.0)
	{
		SendAsyncResponse({
			{ "isOk", false }
		}, asyncId);
	}
	return false;
}

void WrapperExtension::AddPendingSteamCall(PendingSteamCall* pendingCall)
{
	pendingSteamCalls.push_back(pendingCall);
}

void WrapperExtension::RemovePendingSteamCall(PendingSteamCall* pendingCall)
{
	auto it = std::remove(pendingSteamCalls.begin(), pendingSteamCalls.end(), pendingCall);
	pendingSteamCalls.erase(it, pendingSteamCalls.end());
}

#ifdef _WIN32
void WrapperExtension::OnMainWindowCreated(HWND hWnd)
{
}
#else
void WrapperExtension::OnMainWindowCreated()
{
}
#endif

void WrapperExtension::OnGameOverlayActivated(bool isShowing)
{
	// Send message to JavaScript to fire overlay trigger.
	SendWebMessage("on-game-overlay-activated", {
		{ "isShowing", isShowing }
	});
}

void WrapperExtension::OnUserStatsReceived(EResult eResult)
{
	// Current Steamworks SDKs synchronize stats before game launch and no longer expose
	// RequestCurrentStats(). Keep this callback for diagnostics and to explicitly mark
	// stats as ready when Steam notifies us for the current app.
	if (eResult == k_EResultOK)
	{
		areUserStatsReady = true;
		LogMessage("User stats received and marked ready");
	}
	else
	{
		LogMessage("User stats receive callback returned failure: EResult " + std::to_string(eResult),
			IApplication::LogLevel::warning);
	}
}

void WrapperExtension::OnUserStatsStored(EResult eResult)
{
	if (eResult == k_EResultOK)
	{
		LogMessage("User stats stored successfully");
	}
	else
	{
		LogMessage("User stats store callback returned failure: EResult " + std::to_string(eResult),
			IApplication::LogLevel::warning);
	}
}

void WrapperExtension::OnDLCInstalledCallback(AppId_t appId)
{
	// Send message to JavaScript to fire corresponding trigger.
	SendWebMessage("on-dlc-installed", {
		{ "appId", static_cast<double>(appId) }
	});
}

// For handling a message sent from JavaScript.
// This method mostly just unpacks parameters and calls a dedicated method to handle the message.
void WrapperExtension::HandleWebMessage(const std::string& messageId, const std::vector<ExtensionParameter>& params, double asyncId)
{
	if (messageId == "init")
	{
		OnInitMessage(asyncId);
	}
	else if (messageId == "run-callbacks")
	{
		SteamAPI_RunCallbacks();
	}
	else if (messageId == "show-overlay")
	{
		size_t option = static_cast<size_t>(params[0].GetNumber());

		OnShowOverlayMessage(option);
	}
	else if (messageId == "show-overlay-url")
	{
		const std::string& url = params[0].GetString();
		bool isModal = params[1].GetBool();

		OnShowOverlayURLMessage(url, isModal);
	}
	else if (messageId == "show-overlay-invite-dialog")
	{
		const std::string& steamIdLobbyStr = params[0].GetString();

		OnShowOverlayInviteDialog(steamIdLobbyStr);
	}
	else if (messageId == "set-achievement")
	{
		const std::string& name = params[0].GetString();

		OnSetAchievementMessage(name, asyncId);
	}
	else if (messageId == "clear-achievement")
	{
		const std::string& name = params[0].GetString();

		OnClearAchievementMessage(name, asyncId);
	}
	else if (messageId == "is-dlc-installed")
	{
		const std::string& appIdStr = params[0].GetString();

		OnIsDLCInstalledMessage(appIdStr, asyncId);
	}
	else if (messageId == "install-dlc")
	{
		AppId_t appId = static_cast<AppId_t>(params[0].GetNumber());

		OnInstallDLCMessage(appId);
	}
	else if (messageId == "uninstall-dlc")
	{
		AppId_t appId = static_cast<AppId_t>(params[0].GetNumber());

		OnUninstallDLCMessage(appId);
	}
	else if (messageId == "get-auth-ticket-for-web-api")
	{
		const std::string& identity = params[0].GetString();

		OnGetAuthTicketForWebApi(identity, asyncId);
	}
	else if (messageId == "cancel-auth-ticket")
	{
		HAuthTicket hAuthTicket = static_cast<HAuthTicket>(params[0].GetNumber());

		OnCancelAuthTicket(hAuthTicket);
	}
	else if (messageId == "set-rich-presence")
	{
		const std::string& key = params[0].GetString();
		const std::string& value = params[1].GetString();

		OnSetRichPresence(key, value);
	}
	else if (messageId == "clear-rich-presence")
	{
		OnClearRichPresence();
	}
	else if (messageId == "trigger-screenshot")
	{
		OnTriggerScreenshot();
	}
	else if (messageId == "cloud-get-status")
	{
		OnGetCloudStatusMessage(asyncId);
	}
	else if (messageId == "cloud-list-files")
	{
		OnListCloudFilesMessage(asyncId);
	}
	else if (messageId == "cloud-file-exists")
	{
		OnCloudFileExistsMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "cloud-read-file")
	{
		OnReadCloudFileMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "cloud-write-file")
	{
		const std::string& fileName = params[0].GetString();
		const std::string& base64data = params[1].GetString();
		OnWriteCloudFileMessage(fileName, base64data, asyncId);
	}
	else if (messageId == "cloud-delete-file")
	{
		OnDeleteCloudFileMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "get-stat-int")
	{
		OnGetStatIntMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "get-stat-float")
	{
		OnGetStatFloatMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "set-stat-int")
	{
		OnSetStatIntMessage(params[0].GetString(), static_cast<int32>(params[1].GetNumber()), asyncId);
	}
	else if (messageId == "set-stat-float")
	{
		OnSetStatFloatMessage(params[0].GetString(), static_cast<float>(params[1].GetNumber()), asyncId);
	}
	else if (messageId == "store-stats")
	{
		OnStoreStatsMessage(asyncId);
	}
	else if (messageId == "get-achievement-state")
	{
		OnGetAchievementStateMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "get-app-ownership-info")
	{
		OnGetAppOwnershipInfoMessage(asyncId);
	}
	else if (messageId == "is-subscribed-app")
	{
		OnIsSubscribedAppMessage(static_cast<AppId_t>(params[0].GetNumber()), asyncId);
	}
	else if (messageId == "get-dlc-list")
	{
		OnGetDLCListMessage(asyncId);
	}
	else if (messageId == "find-leaderboard")
	{
		OnFindLeaderboardMessage(params[0].GetString(), asyncId);
	}
	else if (messageId == "find-or-create-leaderboard")
	{
		OnFindOrCreateLeaderboardMessage(
			params[0].GetString(),
			static_cast<ELeaderboardSortMethod>(params[1].GetNumber()),
			static_cast<ELeaderboardDisplayType>(params[2].GetNumber()),
			asyncId);
	}
	else if (messageId == "upload-leaderboard-score")
	{
		OnUploadLeaderboardScoreMessage(
			params[0].GetString(),
			static_cast<ELeaderboardUploadScoreMethod>(params[1].GetNumber()),
			static_cast<int>(params[2].GetNumber()),
			params[3].GetString(),
			asyncId);
	}
	else if (messageId == "download-leaderboard-entries")
	{
		OnDownloadLeaderboardEntriesMessage(
			params[0].GetString(),
			static_cast<ELeaderboardDataRequest>(params[1].GetNumber()),
			static_cast<int>(params[2].GetNumber()),
			static_cast<int>(params[3].GetNumber()),
			asyncId);
	}
	else if (messageId == "screenshot-data")
	{
		const std::string& base64data = params[0].GetString();
		int width = static_cast<int>(params[1].GetNumber());
		int height = static_cast<int>(params[2].GetNumber());

		OnScreenshotData(base64data, width, height);
	}
}

void WrapperExtension::OnInitMessage(double asyncId)
{
	// Note the actual initialization is done in InitSteamworksSDK(). This just sends the result
	// of initialization back to the Construct plugin.
	if (didSteamInitOk)
	{
		if (!didCreateSteamCallbacks)
		{
			// Create SteamCallbacks class.
			// Note the Steamworks SDK documentation states that Steam should be initialized before creating
			// objects that listen for callbacks, which SteamCallbacks does, hence it being a separate class.
			steamCallbacks.reset(new SteamCallbacks(*this));
			didCreateSteamCallbacks = true;
		}

		// When the overlay workaround is in use, it will create a separate surface for the overlay
		// to render in to. This surface is the one captured by default for screenshots but it doesn't
		// have any interesting content in it. To work around this, hook the Steam screenshot event and
		// pass it to the Construct runtime to take a screenshot of the actual game content and return
		// it to the wrapper extension here.
		SteamScreenshots()->HookScreenshots(true);		// enables ScreenshotRequested_t events
		LogMessage(std::string("HookScreenshots(true) applied; IsScreenshotsHooked=") +
			(SteamScreenshots()->IsScreenshotsHooked() ? "true" : "false"));

		// On current Steamworks SDKs RequestCurrentStats() has been removed because the Steam
		// client synchronizes stats before launch. Treat stats as ready only after init succeeds,
		// and still accept UserStatsReceived_t later for diagnostics/fallback.
		areUserStatsReady = true;

		// Get current steam user ID for accessing account IDs
		CSteamID steamId = SteamUser()->GetSteamID();

		// Get app owner ID as well, since it can be different from the current user if accessing the
		// app via Family Sharing.
		CSteamID appOwnerId = SteamApps()->GetAppOwner();

		// Send init data back to JavaScript with key details from the API.
		SendAsyncResponse({
			{ "isAvailable",				true },
			{ "isRunningOnSteamDeck",		SteamUtils()->IsSteamRunningOnSteamDeck() },

			{ "personaName",				SteamFriends()->GetPersonaName() },
			{ "accountId",					static_cast<double>(steamId.GetAccountID()) },

			// Note the 64-bit Steam ID and static account key are uint64s which aren't guaranteed
			// to fit in JavaScript's number type (as a double has only 53 bits of integer precision).
			// So convert these to strings for passing to JavaScript.
			{ "steamId64Bit",				std::to_string(steamId.ConvertToUint64()) },
			{ "staticAccountKey",			std::to_string(steamId.GetStaticAccountKey()) },

			{ "playerSteamLevel",			static_cast<double>(SteamUser()->GetPlayerSteamLevel()) },

			// App owner account IDs, as per above
			{ "appOwnerAccountId",			static_cast<double>(appOwnerId.GetAccountID()) },
			{ "appOwnerSteamId64Bit",		std::to_string(appOwnerId.ConvertToUint64()) },
			{ "appOwnerStaticAccountKey",	std::to_string(appOwnerId.GetStaticAccountKey()) },

			{ "appId",						static_cast<double>(SteamUtils()->GetAppID()) },
			{ "steamUILanguage",			SteamUtils()->GetSteamUILanguage() },
			{ "currentGameLanguage",		SteamApps()->GetCurrentGameLanguage() },
			{ "availableGameLanguages",		SteamApps()->GetAvailableGameLanguages() }
		}, asyncId);
	}
	else
	{
		// If Steam did not initialize successfully none of the other details can be sent,
		// so just send a response with isAvailable set to false
		SendAsyncResponse({
			{ "isAvailable", false }
		}, asyncId);
	}
}

// String parameters for ActivateGameOverlay() in order they are specified by the addon
const char* overlayOptions[] = {
	"friends", "community", "players", "settings", "officialgamegroup", "stats", "achievements"
};

void WrapperExtension::OnShowOverlayMessage(size_t option)
{
	// The option is an index in to a combo matching the order of overlayOptions.
	size_t maxOpt = sizeof(overlayOptions) / sizeof(overlayOptions[0]);
	if (option >= maxOpt)
		return;
	
	SteamFriends()->ActivateGameOverlay(overlayOptions[option]);
}

void WrapperExtension::OnShowOverlayURLMessage(const std::string& url, bool isModal)
{
	SteamFriends()->ActivateGameOverlayToWebPage(url.c_str(),
		isModal ? k_EActivateGameOverlayToWebPageMode_Modal : k_EActivateGameOverlayToWebPageMode_Default);
}

void WrapperExtension::OnShowOverlayInviteDialog(const std::string& steamIdLobbyStr)
{
	// Convert string to uint64, then that to a CSteamID
	CSteamID steamId(std::stoull(steamIdLobbyStr));

	SteamFriends()->ActivateGameOverlayInviteDialog(steamId);
}

void WrapperExtension::OnSetAchievementMessage(const std::string& name, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "SetAchievement()"))
		return;

	if (SteamUserStats()->SetAchievement(name.c_str()))
	{
		// Successfully set achievement. Proceed to immediately send the changed
		// achievement data for permanent storage.
		if (SteamUserStats()->StoreStats())
		{
			// Successfully sent store request. Note this isn't actually guaranteed to be
			// successful until a UserStatsStored_t callback comes back with a success result,
			// but due to the complexity of handling async code in C++ this just isn't checked,
			// so reaching this point is treated as a successful change of achievement.
			SendAsyncResponse({
				{ "isOk", true }
			}, asyncId);
			return;
		}
	}
	
	// If reached here then something above failed, so send a failure result.
	SendAsyncResponse({
		{ "isOk", false }
	}, asyncId);
}

void WrapperExtension::OnClearAchievementMessage(const std::string& name, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "ClearAchievement()"))
		return;

	// As with OnSetAchievementMessage() but calls ClearAchievement().
	if (SteamUserStats()->ClearAchievement(name.c_str()))
	{
		if (SteamUserStats()->StoreStats())
		{
			SendAsyncResponse({
				{ "isOk", true }
			}, asyncId);
			return;
		}
	}

	SendAsyncResponse({
		{ "isOk", false }
	}, asyncId);
}

void WrapperExtension::OnIsDLCInstalledMessage(const std::string& appIdStr, double asyncId)
{
	std::vector<std::string> appIdArr = SplitString(appIdStr, ",");
	std::vector<std::string> results;

	for (auto i = appIdArr.begin(), end = appIdArr.end(); i != end; ++i)
	{
		AppId_t appId;
		try {
			appId = std::stoul(*i);
		}
		catch (...)
		{
			appId = 0;
		}

		results.push_back(appId != 0 && SteamApps()->BIsDlcInstalled(appId) ? "true" : "false");
	}

	SendAsyncResponse({
		{ "isOk", true },
		{ "results", JoinStrings(results, ",") }
	}, asyncId);
}

void WrapperExtension::OnInstallDLCMessage(AppId_t appId)
{
	SteamApps()->InstallDLC(appId);
}

void WrapperExtension::OnUninstallDLCMessage(AppId_t appId)
{
	SteamApps()->UninstallDLC(appId);
}

void WrapperExtension::OnGetCloudStatusMessage(double asyncId)
{
	if (!EnsureSteamReady(asyncId, "RemoteStorage status"))
		return;

	uint64 totalBytes = 0;
	uint64 availableBytes = 0;
	const bool hasQuota = SteamRemoteStorage()->GetQuota(&totalBytes, &availableBytes);

	SendAsyncResponse({
		{ "isOk", true },
		{ "isCloudEnabledForAccount", SteamRemoteStorage()->IsCloudEnabledForAccount() },
		{ "isCloudEnabledForApp", SteamRemoteStorage()->IsCloudEnabledForApp() },
		{ "hasQuota", hasQuota },
		{ "totalBytes", static_cast<double>(totalBytes) },
		{ "availableBytes", static_cast<double>(availableBytes) }
	}, asyncId);
}

void WrapperExtension::OnListCloudFilesMessage(double asyncId)
{
	if (!EnsureSteamReady(asyncId, "RemoteStorage list files"))
		return;

	nlohmann::json files = nlohmann::json::array();
	const int fileCount = SteamRemoteStorage()->GetFileCount();
	for (int index = 0; index < fileCount; ++index)
	{
		int32 fileSize = 0;
		const char* fileName = SteamRemoteStorage()->GetFileNameAndSize(index, &fileSize);
		if (!fileName)
			continue;

		files.push_back({
			{ "name", fileName },
			{ "size", fileSize },
			{ "isPersisted", SteamRemoteStorage()->FilePersisted(fileName) },
			{ "timestamp", static_cast<double>(SteamRemoteStorage()->GetFileTimestamp(fileName)) }
		});
	}

	SendAsyncResponse({
		{ "isOk", true },
		{ "fileCount", static_cast<double>(fileCount) },
		{ "filesJson", files.dump() }
	}, asyncId);
}

void WrapperExtension::OnCloudFileExistsMessage(const std::string& fileName, double asyncId)
{
	if (!EnsureSteamReady(asyncId, "RemoteStorage file exists"))
		return;

	SendAsyncResponse({
		{ "isOk", true },
		{ "fileName", fileName },
		{ "exists", SteamRemoteStorage()->FileExists(fileName.c_str()) }
	}, asyncId);
}

void WrapperExtension::OnReadCloudFileMessage(const std::string& fileName, double asyncId)
{
	if (!EnsureSteamReady(asyncId, "RemoteStorage read file"))
		return;

	if (!SteamRemoteStorage()->FileExists(fileName.c_str()))
	{
		SendAsyncResponse({
			{ "isOk", false },
			{ "fileName", fileName }
		}, asyncId);
		return;
	}

	const int32 fileSize = SteamRemoteStorage()->GetFileSize(fileName.c_str());
	if (fileSize < 0)
	{
		SendAsyncResponse({
			{ "isOk", false },
			{ "fileName", fileName }
		}, asyncId);
		return;
	}

	std::vector<uint8_t> data(static_cast<size_t>(fileSize));
	const int32 bytesRead = fileSize == 0 ? 0 : SteamRemoteStorage()->FileRead(fileName.c_str(), data.data(), fileSize);
	if (bytesRead < 0)
	{
		SendAsyncResponse({
			{ "isOk", false },
			{ "fileName", fileName }
		}, asyncId);
		return;
	}

	SendAsyncResponse({
		{ "isOk", true },
		{ "fileName", fileName },
		{ "size", static_cast<double>(bytesRead) },
		{ "dataBase64", base64_encode(data.data(), static_cast<size_t>(bytesRead)) }
	}, asyncId);
}

void WrapperExtension::OnWriteCloudFileMessage(const std::string& fileName, const std::string& base64data, double asyncId)
{
	if (!EnsureSteamReady(asyncId, "RemoteStorage write file"))
		return;

	const std::string decodedData = base64_decode(base64data);
	const bool isOk = SteamRemoteStorage()->FileWrite(
		fileName.c_str(),
		decodedData.empty() ? "" : decodedData.data(),
		static_cast<int32>(decodedData.size()));

	SendAsyncResponse({
		{ "isOk", isOk },
		{ "fileName", fileName },
		{ "size", static_cast<double>(decodedData.size()) }
	}, asyncId);
}

void WrapperExtension::OnDeleteCloudFileMessage(const std::string& fileName, double asyncId)
{
	if (!EnsureSteamReady(asyncId, "RemoteStorage delete file"))
		return;

	SendAsyncResponse({
		{ "isOk", SteamRemoteStorage()->FileDelete(fileName.c_str()) },
		{ "fileName", fileName }
	}, asyncId);
}

void WrapperExtension::OnGetStatIntMessage(const std::string& name, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "GetStat(int)"))
		return;

	int32 value = 0;
	const bool isOk = SteamUserStats()->GetStat(name.c_str(), &value);
	if (!isOk)
		LogMessage("GetStat(int) failed for '" + name + "'. The stat may be undefined or not an int stat.",
			IApplication::LogLevel::warning);
	SendAsyncResponse({
		{ "isOk", isOk },
		{ "name", name },
		{ "value", static_cast<double>(value) },
		{ "reason", isOk ? "" : "stat-not-found-or-type-mismatch" }
	}, asyncId);
}

void WrapperExtension::OnGetStatFloatMessage(const std::string& name, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "GetStat(float)"))
		return;

	float value = 0;
	const bool isOk = SteamUserStats()->GetStat(name.c_str(), &value);
	if (!isOk)
		LogMessage("GetStat(float) failed for '" + name + "'. The stat may be undefined or not a float stat.",
			IApplication::LogLevel::warning);
	SendAsyncResponse({
		{ "isOk", isOk },
		{ "name", name },
		{ "value", static_cast<double>(value) },
		{ "reason", isOk ? "" : "stat-not-found-or-type-mismatch" }
	}, asyncId);
}

void WrapperExtension::OnSetStatIntMessage(const std::string& name, int32 value, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "SetStat(int)"))
		return;

	const bool isOk = SteamUserStats()->SetStat(name.c_str(), value);
	if (!isOk)
		LogMessage("SetStat(int) failed for '" + name + "'. The stat may be undefined or not an int stat.",
			IApplication::LogLevel::warning);
	SendAsyncResponse({
		{ "isOk", isOk },
		{ "name", name },
		{ "value", static_cast<double>(value) },
		{ "reason", isOk ? "" : "stat-not-found-or-type-mismatch" }
	}, asyncId);
}

void WrapperExtension::OnSetStatFloatMessage(const std::string& name, float value, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "SetStat(float)"))
		return;

	const bool isOk = SteamUserStats()->SetStat(name.c_str(), value);
	if (!isOk)
		LogMessage("SetStat(float) failed for '" + name + "'. The stat may be undefined or not a float stat.",
			IApplication::LogLevel::warning);
	SendAsyncResponse({
		{ "isOk", isOk },
		{ "name", name },
		{ "value", static_cast<double>(value) },
		{ "reason", isOk ? "" : "stat-not-found-or-type-mismatch" }
	}, asyncId);
}

void WrapperExtension::OnStoreStatsMessage(double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "StoreStats()"))
		return;

	SendAsyncResponse({
		{ "isOk", SteamUserStats()->StoreStats() }
	}, asyncId);
}

void WrapperExtension::OnGetAchievementStateMessage(const std::string& name, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "GetAchievement()"))
		return;

	bool isUnlocked = false;
	const bool isOk = SteamUserStats()->GetAchievement(name.c_str(), &isUnlocked);
	if (!isOk)
		LogMessage("GetAchievement() failed for '" + name + "'. The achievement may be undefined.",
			IApplication::LogLevel::warning);
	SendAsyncResponse({
		{ "isOk", isOk },
		{ "name", name },
		{ "isUnlocked", isUnlocked },
		{ "reason", isOk ? "" : "achievement-not-found" }
	}, asyncId);
}

void WrapperExtension::OnGetAppOwnershipInfoMessage(double asyncId)
{
	if (!EnsureSteamReady(asyncId, "App ownership info"))
		return;

	SendAsyncResponse({
		{ "isOk", true },
		{ "isSubscribed", SteamApps()->BIsSubscribed() },
		{ "isSubscribedFromFamilySharing", SteamApps()->BIsSubscribedFromFamilySharing() },
		{ "isSubscribedFromFreeWeekend", SteamApps()->BIsSubscribedFromFreeWeekend() },
		{ "isVACBanned", SteamApps()->BIsVACBanned() },
		{ "appBuildId", static_cast<double>(SteamApps()->GetAppBuildId()) }
	}, asyncId);
}

void WrapperExtension::OnIsSubscribedAppMessage(AppId_t appId, double asyncId)
{
	if (!EnsureSteamReady(asyncId, "BIsSubscribedApp()"))
		return;

	SendAsyncResponse({
		{ "isOk", true },
		{ "appId", static_cast<double>(appId) },
		{ "isSubscribed", SteamApps()->BIsSubscribedApp(appId) }
	}, asyncId);
}

void WrapperExtension::OnGetDLCListMessage(double asyncId)
{
	if (!EnsureSteamReady(asyncId, "GetDLCCount()"))
		return;

	nlohmann::json dlcList = nlohmann::json::array();
	const int dlcCount = SteamApps()->GetDLCCount();

	for (int index = 0; index < dlcCount; ++index)
	{
		AppId_t appId = 0;
		bool isAvailable = false;
		char name[256] = {};

		if (!SteamApps()->BGetDLCDataByIndex(index, &appId, &isAvailable, name, sizeof(name)))
			continue;

		dlcList.push_back({
			{ "appId", appId },
			{ "name", name },
			{ "isAvailable", isAvailable },
			{ "isInstalled", SteamApps()->BIsDlcInstalled(appId) }
		});
	}

	SendAsyncResponse({
		{ "isOk", true },
		{ "dlcCount", static_cast<double>(dlcCount) },
		{ "dlcJson", dlcList.dump() }
	}, asyncId);
}

void WrapperExtension::OnFindLeaderboardMessage(const std::string& name, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "FindLeaderboard()"))
		return;

	SteamAPICall_t apiCall = SteamUserStats()->FindLeaderboard(name.c_str());
	auto* pendingCall = new PendingLeaderboardFindCall(*this, asyncId, name);
	pendingCall->Set(apiCall);
}

void WrapperExtension::OnFindOrCreateLeaderboardMessage(const std::string& name, ELeaderboardSortMethod sortMethod, ELeaderboardDisplayType displayType, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "FindOrCreateLeaderboard()"))
		return;

	SteamAPICall_t apiCall = SteamUserStats()->FindOrCreateLeaderboard(name.c_str(), sortMethod, displayType);
	auto* pendingCall = new PendingLeaderboardFindCall(*this, asyncId, name);
	pendingCall->Set(apiCall);
}

void WrapperExtension::OnUploadLeaderboardScoreMessage(const std::string& leaderboardHandleStr, ELeaderboardUploadScoreMethod uploadMethod, int score, const std::string& detailsCsv, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "UploadLeaderboardScore()"))
		return;

	SteamLeaderboard_t leaderboardHandle = 0;
	if (!TryParseLeaderboardHandle(leaderboardHandleStr, leaderboardHandle) || leaderboardHandle == 0)
	{
		SendAsyncResponse({
			{ "isOk", false },
			{ "leaderboardHandle", leaderboardHandleStr }
		}, asyncId);
		return;
	}

	const std::vector<int32> details = ParseIntCsv(detailsCsv);
	SteamAPICall_t apiCall = SteamUserStats()->UploadLeaderboardScore(
		leaderboardHandle,
		uploadMethod,
		score,
		details.empty() ? nullptr : details.data(),
		static_cast<int>(details.size()));

	auto* pendingCall = new PendingLeaderboardUploadCall(*this, asyncId, leaderboardHandleStr);
	pendingCall->Set(apiCall);
}

void WrapperExtension::OnDownloadLeaderboardEntriesMessage(const std::string& leaderboardHandleStr, ELeaderboardDataRequest requestType, int rangeStart, int rangeEnd, double asyncId)
{
	if (!EnsureUserStatsReady(asyncId, "DownloadLeaderboardEntries()"))
		return;

	SteamLeaderboard_t leaderboardHandle = 0;
	if (!TryParseLeaderboardHandle(leaderboardHandleStr, leaderboardHandle) || leaderboardHandle == 0)
	{
		SendAsyncResponse({
			{ "isOk", false },
			{ "leaderboardHandle", leaderboardHandleStr }
		}, asyncId);
		return;
	}

	SteamAPICall_t apiCall = SteamUserStats()->DownloadLeaderboardEntries(
		leaderboardHandle,
		requestType,
		rangeStart,
		rangeEnd);

	auto* pendingCall = new PendingLeaderboardDownloadCall(*this, asyncId, leaderboardHandleStr);
	pendingCall->Set(apiCall);
}

void WrapperExtension::OnGetAuthTicketForWebApi(const std::string& identity, double asyncId)
{
	// Save the pending async ID and wait for the GetTicketForWebApiResponse_t event, which then calls
	// OnGetTicketForWebApiResponse(). Note that as there does not appear to be any way to correlate
	// this call with the resulting event, this can only correctly handle one request at a time.
	// Therefore if another call is made while another is still pending, return a failure.
	if (pendingAuthTicketForWebApiAsyncId != -1)
	{
		LogMessage("GetAuthTicketForWebApi(): another call is in progress so failing", IApplication::LogLevel::warning);
		SendAsyncResponse({
			{ "isOk", false }
		}, asyncId);
		return;
	}

	pendingAuthTicketForWebApiAsyncId = asyncId;

	// If the identity is an empty string, pass nullptr instead of a string to indicate none provided.
	SteamUser()->GetAuthTicketForWebApi(identity.empty() ? nullptr : identity.c_str());
}

void WrapperExtension::OnGetTicketForWebApiResponse(GetTicketForWebApiResponse_t* pCallback)
{
	// Use the asyncId from the call to OnGetAuthTicketForWebApi().
	double asyncId = pendingAuthTicketForWebApiAsyncId;
	pendingAuthTicketForWebApiAsyncId = -1;

	// If the asyncId is -1, then we weren't expecting this callback. Just log a message and bail out.
	if (asyncId == -1)
	{
		LogMessage("GetAuthTicketForWebApi() callback ignored due to no async id", IApplication::LogLevel::warning);
		return;
	}

	if (pCallback->m_eResult != k_EResultOK || pCallback->m_hAuthTicket == k_HAuthTicketInvalid)
	{
		// Handle failure result
		LogMessage("GetAuthTicketForWebApi() failed: EResult " + std::to_string(pCallback->m_eResult), IApplication::LogLevel::warning);

		SendAsyncResponse({
			{ "isOk", false }
		}, asyncId);
	}
	else
	{
		// Success. Convert the ticket binary data to a hex string (validating the data looks OK).
		// Also note HAuthTicket is really a uint32 and so can safely be returned to JavaScript as a number.
		std::string ticketHexStr;
		if (pCallback->m_cubTicket > 0 && pCallback->m_cubTicket < GetTicketForWebApiResponse_t::k_nCubTicketMaxLength)
		{
			std::vector<uint8_t> ticketBytes(pCallback->m_rgubTicket, pCallback->m_rgubTicket + pCallback->m_cubTicket);
			ticketHexStr = BytesToHexString(ticketBytes);
		}

		SendAsyncResponse({
			{ "isOk", true },
			{ "authTicket", static_cast<double>(pCallback->m_hAuthTicket) },
			{ "ticketHexStr", ticketHexStr }
		}, asyncId);
	}
}

void WrapperExtension::OnCancelAuthTicket(HAuthTicket hAuthTicket)
{
	SteamUser()->CancelAuthTicket(hAuthTicket);
}

void WrapperExtension::OnSetRichPresence(const std::string& key, const std::string& value)
{
	// Note the value may be an empty string to remove the key. Returns a boolean which is true if successful.
	bool result = SteamFriends()->SetRichPresence(key.c_str(), value.c_str());

	// If failed just log a diagnostic
	if (!result)
		LogMessage("SetRichPresence() failed", IApplication::LogLevel::warning);
}

void WrapperExtension::OnClearRichPresence()
{
	SteamFriends()->ClearRichPresence();
}

void WrapperExtension::OnTriggerScreenshot()
{
	LogMessage("TriggerScreenshot() requested from host");
	SteamScreenshots()->TriggerScreenshot();
	LogMessage("SteamScreenshots()->TriggerScreenshot() invoked");
}

void WrapperExtension::OnScreenshotRequested()
{
	// Construct runtime will take screenshot and send data back via the "screenshot-data"
	// message, which is handled by OnScreenshotData().
	LogMessage("Received ScreenshotRequested_t callback from Steam");
	SendWebMessage("screenshot-requested", {});
	LogMessage("Forwarded screenshot-requested event to host");
}

void WrapperExtension::OnScreenshotReady(ScreenshotReady_t* pCallback)
{
	if (!pCallback)
	{
		LogMessage("Received ScreenshotReady_t callback with null payload");
		return;
	}

	LogMessage("Received ScreenshotReady_t callback (handle=" + std::to_string(pCallback->m_hLocal) +
		", result=" + std::to_string(pCallback->m_eResult) + ")");
}

void WrapperExtension::OnScreenshotData(const std::string& base64data, int width, int height)
{
	// Decode the base64 screenshot data and then call WriteScreenshot() to write this data as
	// a screenshot in Steam
	LogMessage("Received screenshot-data from host (" + std::to_string(width) + "x" + std::to_string(height) +
		", base64 bytes=" + std::to_string(base64data.size()) + ")");
	std::string decodedData = base64_decode(base64data);
	LogMessage("Decoded screenshot-data payload to " + std::to_string(decodedData.size()) + " RGB bytes");

	SteamScreenshots()->WriteScreenshot(const_cast<char*>(decodedData.data()), static_cast<uint32>(decodedData.size()), width, height);
	LogMessage("SteamScreenshots()->WriteScreenshot() invoked");
}
