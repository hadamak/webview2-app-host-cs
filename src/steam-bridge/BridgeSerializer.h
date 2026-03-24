#pragma once

// BridgeSerializer.h
// Pure JSON conversion functions between the C# bridge and the Steamworks
// extension message format. Extracted here so they can be tested
// independently without the Steamworks SDK or WrapperExtension.
//
// Dependencies: IExtension.h (for POD types), json.hpp

#include "IExtension.h"
#include "json.hpp"
#include <string>
#include <vector>

// ---------------------------------------------------------------------------
// ParsedParams
// Holds the parsed parameter array together with the string storage that
// owns the memory pointed to by EPT_String entries.
// Both must stay alive for the duration of the OnWebMessage() call.
// ---------------------------------------------------------------------------
struct ParsedParams
{
    std::vector<ExtensionParameterPOD> params;
    std::vector<std::string>           strStorage;
};

// ---------------------------------------------------------------------------
// ParseJsonArrayToParams
// Converts a JSON array string from JS into an ExtensionParameterPOD array.
//
// Expected format: [val0, val1, ...]
//   - boolean  -> EPT_Boolean (stored in number field, 1.0 or 0.0)
//   - number   -> EPT_Number
//   - string   -> EPT_String  (pointer into strStorage)
//   - other    -> skipped
//
// Returns an empty result on parse failure or if input is not an array.
// ---------------------------------------------------------------------------
inline ParsedParams ParseJsonArrayToParams(const char* paramsJson)
{
    ParsedParams result;
    try
    {
        auto j = nlohmann::json::parse(paramsJson ? paramsJson : "[]");
        if (!j.is_array()) return result;

        result.params.reserve(j.size());

        for (const auto& item : j)
        {
            ExtensionParameterPOD ep = {};
            if (item.is_boolean())
            {
                ep.type   = EPT_Boolean;
                ep.number = item.get<bool>() ? 1.0 : 0.0;
            }
            else if (item.is_number())
            {
                ep.type   = EPT_Number;
                ep.number = item.get<double>();
            }
            else if (item.is_string())
            {
                ep.type = EPT_String;
                result.strStorage.push_back(item.get<std::string>());
                ep.str  = result.strStorage.back().c_str();
            }
            else
            {
                // null / object / array -> skip
                continue;
            }
            result.params.push_back(ep);
        }
    }
    catch (...) {}

    return result;
}

// ---------------------------------------------------------------------------
// SerializeNamedParamsToJson
// Converts a NamedExtensionParameterPOD array (from the DLL) into a JSON
// object string to be sent to C# and then forwarded to JS.
//
// Output format: {"key1": true, "key2": 42, "key3": "value", ...}
// ---------------------------------------------------------------------------
inline std::string SerializeNamedParamsToJson(
    size_t paramCount,
    const NamedExtensionParameterPOD* paramArr)
{
    nlohmann::json obj = nlohmann::json::object();
    for (size_t i = 0; i < paramCount; ++i)
    {
        const auto& p = paramArr[i];
        if (!p.key) continue;

        switch (p.value.type)
        {
        case EPT_Boolean: obj[p.key] = (p.value.number != 0.0);           break;
        case EPT_Number:  obj[p.key] = p.value.number;                    break;
        case EPT_String:  obj[p.key] = (p.value.str ? p.value.str : ""); break;
        default: break;
        }
    }
    return obj.dump();
}
