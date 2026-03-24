// BridgeSerializerTests.cpp
// Unit tests for BridgeSerializer.h.
// No Steamworks SDK, no WrapperExtension -- pure JSON conversion logic only.
// Build and run as a console executable. Exit code 0 = all passed.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Satisfy DECLSPEC_NOVTABLE used in IExtension.h on non-MSVC builds.
#ifndef DECLSPEC_NOVTABLE
#define DECLSPEC_NOVTABLE __declspec(novtable)
#endif

#include "../../src/steam-bridge/IExtension.h"
#include "../../src/steam-bridge/BridgeSerializer.h"

#include <cassert>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>

// ---------------------------------------------------------------------------
// Minimal assertion helpers
// ---------------------------------------------------------------------------
static int g_pass = 0;
static int g_fail = 0;

static void check(bool condition, const char* expr, const char* file, int line)
{
    if (condition)
    {
        ++g_pass;
    }
    else
    {
        ++g_fail;
        fprintf(stderr, "FAILED  %s:%d  %s\n", file, line, expr);
    }
}

#define EXPECT(expr)       check(!!(expr), #expr, __FILE__, __LINE__)
#define EXPECT_EQ(a, b)    check((a) == (b), #a " == " #b, __FILE__, __LINE__)
#define EXPECT_STREQ(a, b) check(std::string(a) == std::string(b), \
                                 #a " == " #b, __FILE__, __LINE__)

// ---------------------------------------------------------------------------
// ParseJsonArrayToParams tests
// ---------------------------------------------------------------------------

static void test_empty_array()
{
    auto r = ParseJsonArrayToParams("[]");
    EXPECT(r.params.empty());
    EXPECT(r.strStorage.empty());
}

static void test_null_input()
{
    auto r = ParseJsonArrayToParams(nullptr);
    EXPECT(r.params.empty());
}

static void test_invalid_json()
{
    auto r = ParseJsonArrayToParams("{not valid}");
    EXPECT(r.params.empty());
}

static void test_not_an_array()
{
    // JSON object at top level -- should be ignored, return empty.
    auto r = ParseJsonArrayToParams("{\"key\":1}");
    EXPECT(r.params.empty());
}

static void test_boolean_true()
{
    auto r = ParseJsonArrayToParams("[true]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_Boolean);
    EXPECT_EQ(r.params[0].number, 1.0);
}

static void test_boolean_false()
{
    auto r = ParseJsonArrayToParams("[false]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_Boolean);
    EXPECT_EQ(r.params[0].number, 0.0);
}

static void test_number_integer()
{
    auto r = ParseJsonArrayToParams("[42]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_Number);
    EXPECT_EQ(r.params[0].number, 42.0);
}

static void test_number_float()
{
    auto r = ParseJsonArrayToParams("[3.14]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_Number);
    // Tolerate floating-point rounding with epsilon check
    double diff = r.params[0].number - 3.14;
    EXPECT(diff > -1e-9 && diff < 1e-9);
}

static void test_number_negative()
{
    auto r = ParseJsonArrayToParams("[-1]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_Number);
    EXPECT_EQ(r.params[0].number, -1.0);
}

static void test_string()
{
    auto r = ParseJsonArrayToParams("[\"FIRST_CLEAR\"]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_String);
    EXPECT_STREQ(r.params[0].str, "FIRST_CLEAR");
}

static void test_string_empty()
{
    auto r = ParseJsonArrayToParams("[\"\"]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_String);
    EXPECT_STREQ(r.params[0].str, "");
}

static void test_string_ptr_valid_after_reserve()
{
    // Ensures strStorage does not invalidate pointers after push_back.
    // ParseJsonArrayToParams reserves capacity, so this should hold.
    auto r = ParseJsonArrayToParams("[\"hello\",\"world\"]");
    EXPECT_EQ(r.params.size(), 2u);
    EXPECT_STREQ(r.params[0].str, "hello");
    EXPECT_STREQ(r.params[1].str, "world");
}

static void test_mixed_types()
{
    // Matches the typical set-achievement call: ["ACH_WIN"]
    auto r = ParseJsonArrayToParams("[\"ACH_WIN\"]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_String);
    EXPECT_STREQ(r.params[0].str, "ACH_WIN");
}

static void test_mixed_bool_number_string()
{
    auto r = ParseJsonArrayToParams("[true, 99, \"hello\"]");
    EXPECT_EQ(r.params.size(), 3u);
    EXPECT_EQ(r.params[0].type, EPT_Boolean);
    EXPECT_EQ(r.params[0].number, 1.0);
    EXPECT_EQ(r.params[1].type, EPT_Number);
    EXPECT_EQ(r.params[1].number, 99.0);
    EXPECT_EQ(r.params[2].type, EPT_String);
    EXPECT_STREQ(r.params[2].str, "hello");
}

static void test_null_value_skipped()
{
    // null entries must be skipped (no EPT_Invalid leaking out).
    auto r = ParseJsonArrayToParams("[null]");
    EXPECT(r.params.empty());
}

static void test_object_value_skipped()
{
    auto r = ParseJsonArrayToParams("[{}]");
    EXPECT(r.params.empty());
}

static void test_nested_array_skipped()
{
    auto r = ParseJsonArrayToParams("[[1,2]]");
    EXPECT(r.params.empty());
}

static void test_dlc_comma_string()
{
    // is-dlc-installed passes appIds as comma-joined string: "123,456"
    auto r = ParseJsonArrayToParams("[\"123,456\"]");
    EXPECT_EQ(r.params.size(), 1u);
    EXPECT_EQ(r.params[0].type, EPT_String);
    EXPECT_STREQ(r.params[0].str, "123,456");
}

// ---------------------------------------------------------------------------
// SerializeNamedParamsToJson tests
// ---------------------------------------------------------------------------

static NamedExtensionParameterPOD MakeBool(const char* key, bool val)
{
    NamedExtensionParameterPOD p = {};
    p.key           = key;
    p.value.type    = EPT_Boolean;
    p.value.number  = val ? 1.0 : 0.0;
    return p;
}

static NamedExtensionParameterPOD MakeNum(const char* key, double val)
{
    NamedExtensionParameterPOD p = {};
    p.key           = key;
    p.value.type    = EPT_Number;
    p.value.number  = val;
    return p;
}

static NamedExtensionParameterPOD MakeStr(const char* key, const char* val)
{
    NamedExtensionParameterPOD p = {};
    p.key           = key;
    p.value.type    = EPT_String;
    p.value.str     = val;
    return p;
}

static void test_serialize_empty()
{
    auto json = SerializeNamedParamsToJson(0, nullptr);
    EXPECT_STREQ(json, "{}");
}

static void test_serialize_bool_true()
{
    NamedExtensionParameterPOD p = MakeBool("isOk", true);
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(1, &p));
    EXPECT(json["isOk"].get<bool>() == true);
}

static void test_serialize_bool_false()
{
    NamedExtensionParameterPOD p = MakeBool("isAvailable", false);
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(1, &p));
    EXPECT(json["isAvailable"].get<bool>() == false);
}

static void test_serialize_number()
{
    NamedExtensionParameterPOD p = MakeNum("accountId", 12345.0);
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(1, &p));
    EXPECT_EQ(json["accountId"].get<double>(), 12345.0);
}

static void test_serialize_string()
{
    NamedExtensionParameterPOD p = MakeStr("personaName", "Player1");
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(1, &p));
    EXPECT_STREQ(json["personaName"].get<std::string>().c_str(), "Player1");
}

static void test_serialize_null_str_becomes_empty()
{
    NamedExtensionParameterPOD p = MakeStr("key", nullptr);
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(1, &p));
    EXPECT_STREQ(json["key"].get<std::string>().c_str(), "");
}

static void test_serialize_init_response()
{
    // Mirrors WrapperExtension::OnInitMessage success response
    NamedExtensionParameterPOD arr[] = {
        MakeBool("isAvailable",         true),
        MakeBool("isRunningOnSteamDeck",false),
        MakeStr ("personaName",         "TestPlayer"),
        MakeNum ("accountId",           99999.0),
        MakeStr ("steamId64Bit",        "76561198000000000"),
        MakeNum ("appId",               480.0),
        MakeStr ("steamUILanguage",     "english"),
        MakeStr ("currentGameLanguage", "english"),
        MakeStr ("availableGameLanguages", "english"),
    };
    const size_t count = sizeof(arr) / sizeof(arr[0]);
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(count, arr));

    EXPECT(json["isAvailable"].get<bool>()           == true);
    EXPECT(json["isRunningOnSteamDeck"].get<bool>()  == false);
    EXPECT_STREQ(json["personaName"].get<std::string>().c_str(), "TestPlayer");
    EXPECT_EQ(json["accountId"].get<double>(),        99999.0);
    EXPECT_STREQ(json["steamId64Bit"].get<std::string>().c_str(), "76561198000000000");
    EXPECT_EQ(json["appId"].get<double>(),            480.0);
}

static void test_serialize_achievement_result()
{
    NamedExtensionParameterPOD p = MakeBool("isOk", true);
    auto json = nlohmann::json::parse(SerializeNamedParamsToJson(1, &p));
    EXPECT(json["isOk"].get<bool>() == true);
}

// ---------------------------------------------------------------------------
// Round-trip tests
// serialize DLL output -> parse as JS input (not directly applicable,
// but validates format consistency)
// ---------------------------------------------------------------------------

static void test_roundtrip_string_param()
{
    // Simulate: JS sends ["DEFEAT_BOSS"] -> DLL receives via ParseJsonArrayToParams
    // Then DLL responds with {isOk: true} -> C# receives via SerializeNamedParamsToJson
    auto parsed = ParseJsonArrayToParams("[\"DEFEAT_BOSS\"]");
    EXPECT_EQ(parsed.params.size(), 1u);
    EXPECT_STREQ(parsed.params[0].str, "DEFEAT_BOSS");

    NamedExtensionParameterPOD resp = MakeBool("isOk", true);
    auto respJson = nlohmann::json::parse(SerializeNamedParamsToJson(1, &resp));
    EXPECT(respJson["isOk"].get<bool>() == true);
}


// ---------------------------------------------------------------------------
// ExtractParamsJson tests (via SteamBridge internal helper)
// Defined in SteamBridge.cs but the logic is mirrored in BridgeSerializer.h
// for C++ validation. Here we test an equivalent C++ reimplementation.
// ---------------------------------------------------------------------------

// Re-implement the same logic as SteamBridge.ExtractParamsJson in C++
// so the algorithm can be tested without C# / CLR.
static std::string ExtractParamsJsonCpp(const std::string& json)
{
    const std::string key = "\"params\":";
    auto keyIdx = json.find(key);
    if (keyIdx == std::string::npos) return "[]";

    size_t start = keyIdx + key.size();
    while (start < json.size() && json[start] == ' ') start++;
    if (start >= json.size()) return "[]";

    char opener = json[start];
    if (opener != '[' && opener != '{') return "[]";
    char closer = (opener == '[') ? ']' : '}';

    int depth = 0;
    bool inStr = false;
    for (size_t i = start; i < json.size(); i++)
    {
        char c = json[i];
        if (inStr)
        {
            if (c == '\\') i++;
            else if (c == '"') inStr = false;
        }
        else if (c == '"')    inStr = true;
        else if (c == opener) depth++;
        else if (c == closer && --depth == 0)
            return json.substr(start, i - start + 1);
    }
    return "[]";
}

static void test_extract_array()
{
    std::string json = "{\"source\":\"steam\",\"messageId\":\"set-achievement\",\"params\":[\"FIRST_CLEAR\"],\"asyncId\":1}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "[\"FIRST_CLEAR\"]");
}

static void test_extract_empty_array()
{
    std::string json = "{\"source\":\"steam\",\"params\":[],\"asyncId\":1}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "[]");
}

static void test_extract_mixed_array()
{
    std::string json = "{\"params\":[true,99,\"hello\"]}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "[true,99,\"hello\"]");
}

static void test_extract_missing_key()
{
    std::string json = "{\"source\":\"steam\",\"messageId\":\"run-callbacks\",\"asyncId\":-1}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "[]");
}

static void test_extract_object_params()
{
    // Rare case: params could be an object for future messages
    std::string json = "{\"params\":{\"key\":\"value\"}}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "{\"key\":\"value\"}");
}

static void test_extract_nested_array()
{
    std::string json = "{\"params\":[[1,2],[3,4]]}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "[[1,2],[3,4]]");
}

static void test_extract_string_with_escaped_bracket()
{
    // String value containing ']' must not terminate the array early
    std::string json = "{\"params\":[\"he]llo\"]}";
    EXPECT_STREQ(ExtractParamsJsonCpp(json).c_str(), "[\"he]llo\"]");
}

static void test_extract_string_with_escaped_quote()
{
    std::string json = "{\"params\":[\"say \\\"hi\\\"\"]}";
    // Just check it returns a non-empty result starting with [
    std::string result = ExtractParamsJsonCpp(json);
    EXPECT(result.size() > 0 && result[0] == '[');
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

int main()
{
    puts("=== BridgeSerializer Tests ===");

    // ParseJsonArrayToParams
    puts("\n-- ParseJsonArrayToParams --");
    test_empty_array();
    test_null_input();
    test_invalid_json();
    test_not_an_array();
    test_boolean_true();
    test_boolean_false();
    test_number_integer();
    test_number_float();
    test_number_negative();
    test_string();
    test_string_empty();
    test_string_ptr_valid_after_reserve();
    test_mixed_types();
    test_mixed_bool_number_string();
    test_null_value_skipped();
    test_object_value_skipped();
    test_nested_array_skipped();
    test_dlc_comma_string();

    // SerializeNamedParamsToJson
    puts("\n-- SerializeNamedParamsToJson --");
    test_serialize_empty();
    test_serialize_bool_true();
    test_serialize_bool_false();
    test_serialize_number();
    test_serialize_string();
    test_serialize_null_str_becomes_empty();
    test_serialize_init_response();
    test_serialize_achievement_result();

    // Round-trip
    puts("\n-- Round-trip --");
    test_roundtrip_string_param();

    // ExtractParamsJson
    puts("\n-- ExtractParamsJson --");
    test_extract_array();
    test_extract_empty_array();
    test_extract_mixed_array();
    test_extract_missing_key();
    test_extract_object_params();
    test_extract_nested_array();
    test_extract_string_with_escaped_bracket();
    test_extract_string_with_escaped_quote();

    // Summary
    const int total = g_pass + g_fail;
    printf("\n%d / %d passed", g_pass, total);
    if (g_fail == 0)
        puts("  -- ALL PASSED");
    else
        printf("  -- %d FAILED\n", g_fail);

    return g_fail == 0 ? 0 : 1;
}
