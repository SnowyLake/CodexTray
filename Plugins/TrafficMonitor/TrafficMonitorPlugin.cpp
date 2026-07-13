#define WIN32_LEAN_AND_MEAN

#include "PluginInterface.h"

#include <windows.h>
#include <winhttp.h>

#include <array>
#include <string>

#pragma comment(lib, "winhttp.lib")

namespace TrafficMonitorPlugin
{
constexpr wchar_t k_DefaultUsageUrl[] = L"http://127.0.0.1:17890/codex-tray.txt";
constexpr wchar_t k_ConfigFileName[] = L"CodexTray.ini";
constexpr wchar_t k_ConfigSection[] = L"CodexTray";
constexpr wchar_t k_ConfigUsageUrlKey[] = L"UsageUrl";
constexpr wchar_t k_OptionsWindowClassName[] = L"CodexTrayOptionsWindow";
constexpr int k_OptionsUrlEditId = 1001;
constexpr wchar_t k_FallbackValue[] = L"N/A";

HMODULE g_Module = nullptr;

struct UsageValues
{
    std::wstring fiveHour;
    std::wstring sevenDay;
};

struct OptionsDialogState
{
    std::wstring currentUrl;
    std::wstring updatedUrl;
    HWND editControl = nullptr;
    bool completed = false;
    bool accepted = false;
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

/// Writes the bridge usage URL to the plugin configuration file.
bool WriteUsageUrl(const std::wstring& url)
{
    return WritePrivateProfileStringW(k_ConfigSection, k_ConfigUsageUrlKey, url.c_str(), GetConfigPath().c_str()) != FALSE;
}

/// Returns a copy of a string without leading or trailing whitespace.
std::wstring TrimString(const std::wstring& value)
{
    size_t first = value.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos)
    {
        return {};
    }

    size_t last = value.find_last_not_of(L" \t\r\n");
    return value.substr(first, last - first + 1);
}

/// Returns whether a URL can be used by the WinHTTP backend request.
bool IsValidUsageUrl(const std::wstring& url)
{
    if (url.empty())
    {
        return false;
    }

    std::array<wchar_t, 16> scheme{};
    std::array<wchar_t, 256> host{};
    URL_COMPONENTS components{};
    components.dwStructSize = sizeof(components);
    components.lpszScheme = scheme.data();
    components.dwSchemeLength = static_cast<DWORD>(scheme.size());
    components.lpszHostName = host.data();
    components.dwHostNameLength = static_cast<DWORD>(host.size());

    if (!WinHttpCrackUrl(url.c_str(), static_cast<DWORD>(url.size()), 0, &components))
    {
        return false;
    }

    return host.data()[0] != L'\0' &&
        (components.nScheme == INTERNET_SCHEME_HTTP || components.nScheme == INTERNET_SCHEME_HTTPS);
}

/// Applies the default GUI font to a child window.
void ApplyDefaultFont(HWND window)
{
    if (window != nullptr)
    {
        SendMessageW(window, WM_SETFONT, reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
    }
}

/// Centers a window over its owner or over the work area.
void CenterWindow(HWND window, HWND owner)
{
    RECT windowRect{};
    RECT targetRect{};
    GetWindowRect(window, &windowRect);

    if (owner != nullptr && IsWindow(owner))
    {
        GetWindowRect(owner, &targetRect);
    }
    else
    {
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &targetRect, 0);
    }

    int width = windowRect.right - windowRect.left;
    int height = windowRect.bottom - windowRect.top;
    int x = targetRect.left + ((targetRect.right - targetRect.left) - width) / 2;
    int y = targetRect.top + ((targetRect.bottom - targetRect.top) - height) / 2;
    SetWindowPos(window, nullptr, x, y, 0, 0, SWP_NOZORDER | SWP_NOSIZE);
}

/// Returns the current text in an edit control.
std::wstring GetWindowTextString(HWND window)
{
    int length = GetWindowTextLengthW(window);
    if (length <= 0)
    {
        return {};
    }

    std::wstring text(static_cast<size_t>(length) + 1, L'\0');
    GetWindowTextW(window, text.data(), length + 1);
    text.resize(static_cast<size_t>(length));
    return text;
}

/// Creates a child control and applies the default GUI font.
HWND CreateOptionsControl(const wchar_t* className, const wchar_t* text, DWORD style, int x, int y, int width, int height, HWND parent, HMENU id)
{
    HWND control = CreateWindowExW(0, className, text, style | WS_CHILD | WS_VISIBLE, x, y, width, height, parent, id, g_Module, nullptr);
    ApplyDefaultFont(control);
    return control;
}

/// Converts a numeric control identifier to a Win32 menu handle.
HMENU ControlId(int id)
{
    return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

/// Completes the options dialog with a cancellation result.
void CancelOptionsDialog(HWND window, OptionsDialogState* state)
{
    state->accepted = false;
    state->completed = true;
    DestroyWindow(window);
}

/// Saves the options dialog URL when valid.
void AcceptOptionsDialog(HWND window, OptionsDialogState* state)
{
    std::wstring url = TrimString(GetWindowTextString(state->editControl));
    if (!IsValidUsageUrl(url))
    {
        MessageBoxW(window, L"Enter a valid HTTP or HTTPS backend URL.", L"CodexTray", MB_ICONWARNING | MB_OK);
        SetFocus(state->editControl);
        return;
    }

    state->updatedUrl = url;
    state->accepted = true;
    state->completed = true;
    DestroyWindow(window);
}

/// Handles messages for the options dialog window.
LRESULT CALLBACK OptionsWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    OptionsDialogState* state = reinterpret_cast<OptionsDialogState*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    switch (message)
    {
    case WM_CREATE:
    {
        auto createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
        state = reinterpret_cast<OptionsDialogState*>(createStruct->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));

        CreateOptionsControl(L"STATIC", L"Backend URL:", 0, 14, 18, 360, 18, window, nullptr);
        state->editControl = CreateOptionsControl(L"EDIT", state->currentUrl.c_str(), WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL, 14, 42, 426, 24, window, ControlId(k_OptionsUrlEditId));
        CreateOptionsControl(L"BUTTON", L"OK", WS_TABSTOP | BS_DEFPUSHBUTTON, 284, 84, 74, 26, window, ControlId(IDOK));
        CreateOptionsControl(L"BUTTON", L"Cancel", WS_TABSTOP, 366, 84, 74, 26, window, ControlId(IDCANCEL));
        return 0;
    }
    case WM_COMMAND:
        if (state != nullptr && LOWORD(wParam) == IDOK)
        {
            AcceptOptionsDialog(window, state);
            return 0;
        }

        if (state != nullptr && LOWORD(wParam) == IDCANCEL)
        {
            CancelOptionsDialog(window, state);
            return 0;
        }

        break;
    case WM_KEYDOWN:
        if (state != nullptr && wParam == VK_ESCAPE)
        {
            CancelOptionsDialog(window, state);
            return 0;
        }

        if (state != nullptr && wParam == VK_RETURN)
        {
            AcceptOptionsDialog(window, state);
            return 0;
        }

        break;
    case WM_CLOSE:
        if (state != nullptr)
        {
            CancelOptionsDialog(window, state);
            return 0;
        }

        break;
    }

    return DefWindowProcW(window, message, wParam, lParam);
}

/// Registers the options dialog window class.
bool RegisterOptionsWindowClass()
{
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = OptionsWindowProc;
    windowClass.hInstance = g_Module;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_BTNFACE + 1);
    windowClass.lpszClassName = k_OptionsWindowClassName;

    return RegisterClassExW(&windowClass) != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
}

/// Shows the backend URL options dialog.
bool ShowUsageUrlOptionsDialog(HWND owner, std::wstring& url)
{
    owner = owner != nullptr && IsWindow(owner) ? owner : nullptr;

    if (!RegisterOptionsWindowClass())
    {
        MessageBoxW(owner, L"Unable to open the CodexTray options dialog.", L"CodexTray", MB_ICONERROR | MB_OK);
        return false;
    }

    OptionsDialogState state{};
    state.currentUrl = url;
    state.updatedUrl = url;

    HWND window = CreateWindowExW(
        WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT,
        k_OptionsWindowClassName,
        L"CodexTray Options",
        WS_CAPTION | WS_SYSMENU | WS_POPUP,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        470,
        154,
        owner,
        nullptr,
        g_Module,
        &state);
    if (window == nullptr)
    {
        MessageBoxW(owner, L"Unable to open the CodexTray options dialog.", L"CodexTray", MB_ICONERROR | MB_OK);
        return false;
    }

    CenterWindow(window, owner);
    if (owner != nullptr)
    {
        EnableWindow(owner, FALSE);
    }

    ShowWindow(window, SW_SHOW);
    UpdateWindow(window);
    SetFocus(state.editControl);

    MSG message{};
    while (!state.completed && GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        if (!IsDialogMessageW(window, &message))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    if (owner != nullptr)
    {
        EnableWindow(owner, TRUE);
        SetForegroundWindow(owner);
    }

    if (state.accepted)
    {
        url = state.updatedUrl;
    }

    return state.accepted;
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

    WinHttpHandle session(WinHttpOpen(L"CodexTray/TrafficMonitorPlugin 1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
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

    std::wstring text = Utf8ToWide(body);
    size_t separator = text.find(L'\n');
    if (separator == std::wstring::npos)
    {
        return false;
    }

    values.fiveHour = TrimString(text.substr(0, separator));
    values.sevenDay = TrimString(text.substr(separator + 1));
    return !values.fiveHour.empty() && !values.sevenDay.empty();
}

class CodexUsageItem final : public IPluginItem
{
public:
    /// Creates one Codex usage display item.
    CodexUsageItem(const wchar_t* name, const wchar_t* id, const wchar_t* label, const wchar_t* sampleText)
        : m_Name(name), m_Id(id), m_Label(label), m_SampleText(sampleText), m_Value(k_FallbackValue)
    {
    }

    /// Updates the displayed item value.
    void SetValue(std::wstring value)
    {
        m_Value = value.empty() ? k_FallbackValue : std::move(value);
    }

    /// Updates the displayed item value to the fallback text.
    void SetFallback()
    {
        m_Value = k_FallbackValue;
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

class CodexTrayPlugin final : public ITMPlugin
{
public:
    /// Creates the TrafficMonitor plugin singleton.
    CodexTrayPlugin()
        : m_FiveHourItem(L"Codex 5-Hour", L"CodexTray5H", L"Codex-5H", L"100% 4h59m"),
          m_SevenDayItem(L"Codex 7-Day", L"CodexTray7D", L"Codex-7D", L"100% 6d23h"),
          m_Tooltip(L"CodexTray waiting for data")
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
            return &m_SevenDayItem;
        default:
            return nullptr;
        }
    }

    /// Refreshes display data from the local CodexTray service.
    void DataRequired() override
    {
        UsageValues values;
        if (!FetchUsageValues(values))
        {
            m_FiveHourItem.SetFallback();
            m_SevenDayItem.SetFallback();
            m_Tooltip = L"CodexTray bridge unavailable";
            return;
        }

        m_FiveHourItem.SetValue(values.fiveHour);
        m_SevenDayItem.SetValue(values.sevenDay);
        m_Tooltip = L"Codex 5-Hour: " + values.fiveHour + L"\nCodex 7-Day: " + values.sevenDay;
    }

    /// Shows plugin options for editing the backend URL.
    OptionReturn ShowOptionsDialog(void* hParent) override
    {
        std::wstring originalUrl = ReadUsageUrl();
        std::wstring updatedUrl = originalUrl;
        if (!ShowUsageUrlOptionsDialog(static_cast<HWND>(hParent), updatedUrl))
        {
            return OR_OPTION_UNCHANGED;
        }

        if (updatedUrl == originalUrl)
        {
            return OR_OPTION_UNCHANGED;
        }

        if (!WriteUsageUrl(updatedUrl))
        {
            MessageBoxW(static_cast<HWND>(hParent), L"Unable to save CodexTray options.", L"CodexTray", MB_ICONERROR | MB_OK);
            return OR_OPTION_UNCHANGED;
        }

        m_Tooltip = L"CodexTray backend URL updated";
        return OR_OPTION_CHANGED;
    }

    /// Returns plugin metadata by index.
    const wchar_t* GetInfo(PluginInfoIndex index) override
    {
        switch (index)
        {
        case TMI_NAME:
            return L"CodexTray";
        case TMI_DESCRIPTION:
            return L"Displays Codex 5-Hour and 7-Day quota from CodexTray.";
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
    CodexUsageItem m_SevenDayItem;
    std::wstring m_Tooltip;
};

/// Returns the plugin singleton instance.
CodexTrayPlugin& GetPluginInstance()
{
    static CodexTrayPlugin plugin;
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
