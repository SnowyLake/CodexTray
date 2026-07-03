#define WIN32_LEAN_AND_MEAN

#include "PluginInterface.h"

#include <windows.h>
#include <winhttp.h>

#include <array>
#include <string>
#include <utility>

#pragma comment(lib, "winhttp.lib")

namespace TrafficMonitorPlugin
{
constexpr wchar_t k_DefaultUsageUrl[] = L"http://127.0.0.1:17890/codex-monitor";
constexpr wchar_t k_ConfigFileName[] = L"CodexMonitor.ini";
constexpr wchar_t k_ConfigSection[] = L"CodexMonitor";
constexpr wchar_t k_ConfigUsageUrlKey[] = L"UsageUrl";

HMODULE g_Module = nullptr;

struct UsageValues
{
    std::wstring fiveHour;
    std::wstring weekly;
};

class WinHttpHandle
{
public:
    /// Stores a WinHTTP handle for scoped cleanup.
    explicit WinHttpHandle(HINTERNET handle = nullptr) : m_Handle(handle)
    {
    }

    /// Closes the wrapped WinHTTP handle.
    ~WinHttpHandle()
    {
        if (m_Handle != nullptr)
        {
            WinHttpCloseHandle(m_Handle);
        }
    }

    /// Returns the wrapped WinHTTP handle.
    operator HINTERNET() const
    {
        return m_Handle;
    }

private:
    HINTERNET m_Handle;
};

/// Converts a UTF-8 string to a wide string.
std::wstring Utf8ToWide(const std::string& value)
{
    if (value.empty())
    {
        return {};
    }

    int requiredLength = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (requiredLength <= 0)
    {
        return {};
    }

    std::wstring result(static_cast<size_t>(requiredLength), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), requiredLength);
    return result;
}

/// Returns the directory containing this plugin DLL.
std::wstring GetModuleDirectory()
{
    std::array<wchar_t, MAX_PATH> path{};
    DWORD length = GetModuleFileNameW(g_Module, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size())
    {
        return {};
    }

    std::wstring result(path.data(), length);
    size_t separator = result.find_last_of(L"\\/");
    if (separator == std::wstring::npos)
    {
        return {};
    }

    return result.substr(0, separator);
}

/// Returns the path to the plugin configuration file.
std::wstring GetConfigPath()
{
    std::wstring directory = GetModuleDirectory();
    if (directory.empty())
    {
        return k_ConfigFileName;
    }

    return directory + L"\\" + k_ConfigFileName;
}

/// Reads the bridge usage URL from the plugin configuration file.
std::wstring ReadUsageUrl()
{
    std::array<wchar_t, 2048> url{};
    GetPrivateProfileStringW(k_ConfigSection, k_ConfigUsageUrlKey, k_DefaultUsageUrl, url.data(), static_cast<DWORD>(url.size()), GetConfigPath().c_str());
    return url.data()[0] == L'\0' ? k_DefaultUsageUrl : url.data();
}

/// Decodes a JSON string escape sequence into a byte string.
void AppendJsonEscape(std::string& output, char escaped)
{
    switch (escaped)
    {
    case '"':
        output.push_back('"');
        break;
    case '\\':
        output.push_back('\\');
        break;
    case '/':
        output.push_back('/');
        break;
    case 'b':
        output.push_back('\b');
        break;
    case 'f':
        output.push_back('\f');
        break;
    case 'n':
        output.push_back('\n');
        break;
    case 'r':
        output.push_back('\r');
        break;
    case 't':
        output.push_back('\t');
        break;
    default:
        output.push_back(escaped);
        break;
    }
}

/// Extracts a simple UTF-8 JSON string property from a response body.
bool ExtractJsonString(const std::string& json, const char* key, std::string& value)
{
    std::string quotedKey = std::string("\"") + key + "\"";
    size_t keyPosition = json.find(quotedKey);
    if (keyPosition == std::string::npos)
    {
        return false;
    }

    size_t colonPosition = json.find(':', keyPosition + quotedKey.size());
    if (colonPosition == std::string::npos)
    {
        return false;
    }

    size_t quotePosition = json.find('"', colonPosition + 1);
    if (quotePosition == std::string::npos)
    {
        return false;
    }

    std::string result;
    for (size_t index = quotePosition + 1; index < json.size(); index++)
    {
        char current = json[index];
        if (current == '"')
        {
            value = result;
            return true;
        }

        if (current == '\\' && index + 1 < json.size())
        {
            AppendJsonEscape(result, json[++index]);
            continue;
        }

        result.push_back(current);
    }

    return false;
}

/// Fetches a URL with WinHTTP and returns the response body.
bool FetchUrl(const std::wstring& url, std::string& body)
{
    std::array<wchar_t, 256> host{};
    std::array<wchar_t, 2048> path{};
    std::array<wchar_t, 1024> extra{};
    URL_COMPONENTS components{};
    components.dwStructSize = sizeof(components);
    components.lpszHostName = host.data();
    components.dwHostNameLength = static_cast<DWORD>(host.size());
    components.lpszUrlPath = path.data();
    components.dwUrlPathLength = static_cast<DWORD>(path.size());
    components.lpszExtraInfo = extra.data();
    components.dwExtraInfoLength = static_cast<DWORD>(extra.size());

    if (!WinHttpCrackUrl(url.c_str(), static_cast<DWORD>(url.size()), 0, &components))
    {
        return false;
    }

    std::wstring requestPath(path.data(), components.dwUrlPathLength);
    requestPath.append(extra.data(), components.dwExtraInfoLength);
    if (requestPath.empty())
    {
        requestPath = L"/";
    }

    WinHttpHandle session(WinHttpOpen(L"CodexMonitor/TrafficMonitorPlugin 1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
    if (session == nullptr)
    {
        return false;
    }

    WinHttpSetTimeouts(session, 1000, 1000, 1000, 2000);

    WinHttpHandle connection(WinHttpConnect(session, host.data(), components.nPort, 0));
    if (connection == nullptr)
    {
        return false;
    }

    DWORD flags = components.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    WinHttpHandle request(WinHttpOpenRequest(connection, L"GET", requestPath.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags));
    if (request == nullptr)
    {
        return false;
    }

    if (!WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(request, nullptr))
    {
        return false;
    }

    body.clear();
    DWORD availableBytes = 0;
    while (WinHttpQueryDataAvailable(request, &availableBytes) && availableBytes > 0)
    {
        std::string chunk(availableBytes, '\0');
        DWORD readBytes = 0;
        if (!WinHttpReadData(request, chunk.data(), availableBytes, &readBytes))
        {
            return false;
        }

        chunk.resize(readBytes);
        body += chunk;
    }

    return !body.empty();
}

/// Fetches Codex usage display values from the local bridge service.
bool FetchUsageValues(UsageValues& values)
{
    std::string body;
    if (!FetchUrl(ReadUsageUrl(), body))
    {
        return false;
    }

    std::string fiveHour;
    std::string weekly;
    if (!ExtractJsonString(body, "codex_5h", fiveHour) ||
        !ExtractJsonString(body, "codex_weekly", weekly))
    {
        return false;
    }

    values.fiveHour = Utf8ToWide(fiveHour);
    values.weekly = Utf8ToWide(weekly);
    return !values.fiveHour.empty() && !values.weekly.empty();
}

class CodexUsageItem final : public IPluginItem
{
public:
    /// Creates one Codex usage display item.
    CodexUsageItem(const wchar_t* name, const wchar_t* id, const wchar_t* label, const wchar_t* sampleText)
        : m_Name(name), m_Id(id), m_Label(label), m_SampleText(sampleText), m_Value(L"unavailable")
    {
    }

    /// Updates the displayed item value.
    void SetValue(std::wstring value)
    {
        m_Value = value.empty() ? L"unavailable" : std::move(value);
    }

    /// Returns the display item name.
    const wchar_t* GetItemName() const override
    {
        return m_Name.c_str();
    }

    /// Returns the display item unique identifier.
    const wchar_t* GetItemId() const override
    {
        return m_Id.c_str();
    }

    /// Returns the display item label text.
    const wchar_t* GetItemLableText() const override
    {
        return m_Label.c_str();
    }

    /// Returns the display item value text.
    const wchar_t* GetItemValueText() const override
    {
        return m_Value.c_str();
    }

    /// Returns the sample value text used by TrafficMonitor.
    const wchar_t* GetItemValueSampleText() const override
    {
        return m_SampleText.c_str();
    }

private:
    std::wstring m_Name;
    std::wstring m_Id;
    std::wstring m_Label;
    std::wstring m_SampleText;
    std::wstring m_Value;
};

class CodexMonitorPlugin final : public ITMPlugin
{
public:
    /// Creates the TrafficMonitor plugin singleton.
    CodexMonitorPlugin()
        : m_FiveHourItem(L"Codex 5h", L"CodexMonitor5H", L"Codex 5h", L"88% [2h 45m]"),
          m_WeeklyItem(L"Codex Weekly", L"CodexMonitorWeekly", L"Codex Weekly", L"66% [3d 04h]"),
          m_Tooltip(L"CodexMonitor waiting for data")
    {
    }

    /// Returns a display item by index.
    IPluginItem* GetItem(int index) override
    {
        switch (index)
        {
        case 0:
            return &m_FiveHourItem;
        case 1:
            return &m_WeeklyItem;
        default:
            return nullptr;
        }
    }

    /// Refreshes display data from the local CodexMonitor service.
    void DataRequired() override
    {
        UsageValues values;
        if (!FetchUsageValues(values))
        {
            m_FiveHourItem.SetValue(L"offline");
            m_WeeklyItem.SetValue(L"offline");
            m_Tooltip = L"CodexMonitor bridge unavailable";
            return;
        }

        m_FiveHourItem.SetValue(values.fiveHour);
        m_WeeklyItem.SetValue(values.weekly);
        m_Tooltip = L"Codex 5h: " + values.fiveHour + L"\nCodex Weekly: " + values.weekly;
    }

    /// Returns plugin metadata by index.
    const wchar_t* GetInfo(PluginInfoIndex index) override
    {
        switch (index)
        {
        case TMI_NAME:
            return L"Codex Monitor";
        case TMI_DESCRIPTION:
            return L"Displays Codex 5 hour and weekly quota from CodexMonitor.";
        case TMI_AUTHOR:
            return L"SnowyLake";
        case TMI_COPYRIGHT:
            return L"MIT";
        case TMI_VERSION:
            return L"1.0.0";
        case TMI_URL:
            return L"";
        default:
            return L"";
        }
    }

    /// Returns tooltip text for the current values.
    const wchar_t* GetTooltipInfo() override
    {
        return m_Tooltip.c_str();
    }

private:
    CodexUsageItem m_FiveHourItem;
    CodexUsageItem m_WeeklyItem;
    std::wstring m_Tooltip;
};

/// Returns the plugin singleton instance.
CodexMonitorPlugin& GetPluginInstance()
{
    static CodexMonitorPlugin plugin;
    return plugin;
}
}

/// Stores this module handle for configuration lookup.
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        TrafficMonitorPlugin::g_Module = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

extern "C"
{
    /// Returns the TrafficMonitor plugin instance.
    __declspec(dllexport) ITMPlugin* TMPluginGetInstance()
    {
        return &TrafficMonitorPlugin::GetPluginInstance();
    }
}
