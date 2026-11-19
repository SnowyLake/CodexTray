#define WIN32_LEAN_AND_MEAN

#include "PluginInterface.h"

#include <array>
#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <utility>

#include <windows.h>
#include <winhttp.h>

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
constexpr size_t k_MaxResponseBytes = 4096;
constexpr ULONGLONG k_RequestTimeoutMilliseconds = 5000;

HMODULE g_Module = nullptr;

struct UsageValues
{
    std::wstring codex;
    std::wstring grok;
    std::wstring deepseek;
};

struct SharedUsage
{
    std::mutex mutex;
    UsageValues values{ k_FallbackValue, k_FallbackValue, k_FallbackValue };
    std::wstring tooltip = L"CodexTray waiting for data";
};

struct OptionsDialogState
{
    std::wstring currentUrl;
    std::wstring updatedUrl;
    HWND editControl = nullptr;
    bool completed = false;
    bool accepted = false;
};

struct AsyncRequestContext
{
    HANDLE completed = nullptr;
    DWORD status = 0;
    DWORD readBytes = 0;
    std::array<char, k_MaxResponseBytes + 1> buffer{};
};

void CALLBACK OnRequestStatus(HINTERNET, DWORD_PTR context, DWORD status, void*, DWORD length)
{
    auto& state = *reinterpret_cast<AsyncRequestContext*>(context);
    state.status = status;
    state.readBytes = length;
    SetEvent(state.completed);
}

class AsyncRequest
{
public:
    AsyncRequest() = default;
    AsyncRequest(const AsyncRequest&) = delete;
    AsyncRequest& operator=(const AsyncRequest&) = delete;

    ~AsyncRequest()
    {
        if (m_Request != nullptr)
        {
            WinHttpCloseHandle(m_Request);
        }
        if (m_Connection != nullptr)
        {
            WinHttpCloseHandle(m_Connection);
        }
        if (m_Session != nullptr)
        {
            WinHttpCloseHandle(m_Session);
        }
        if (m_UnloadRegistered)
        {
            // Keep the callback context alive until every session callback has returned.
            WaitForSingleObject(m_Unloaded, INFINITE);
        }
        if (m_Context.completed != nullptr)
        {
            CloseHandle(m_Context.completed);
        }
        if (m_Unloaded != nullptr)
        {
            CloseHandle(m_Unloaded);
        }
    }

    bool Initialize(const wchar_t* host, INTERNET_PORT port, const wchar_t* path, DWORD flags)
    {
        m_Context.completed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        m_Unloaded = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (m_Context.completed == nullptr || m_Unloaded == nullptr)
        {
            return false;
        }

        m_Session = WinHttpOpen(L"CodexTray/TrafficMonitorPlugin 1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, WINHTTP_FLAG_ASYNC);
        if (m_Session == nullptr || !WinHttpSetOption(m_Session, WINHTTP_OPTION_UNLOAD_NOTIFY_EVENT, &m_Unloaded, sizeof(m_Unloaded)))
        {
            return false;
        }
        m_UnloadRegistered = true;
        WinHttpSetTimeouts(m_Session, 1000, 1000, 1000, 2000);
        m_Connection = WinHttpConnect(m_Session, host, port, 0);
        if (m_Connection == nullptr)
        {
            return false;
        }
        m_Request = WinHttpOpenRequest(m_Connection, L"GET", path, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
        if (m_Request == nullptr)
        {
            return false;
        }

        DWORD_PTR context = reinterpret_cast<DWORD_PTR>(&m_Context);
        if (!WinHttpSetOption(m_Request, WINHTTP_OPTION_CONTEXT_VALUE, &context, sizeof(context)))
        {
            return false;
        }

        constexpr DWORD notifications = WINHTTP_CALLBACK_FLAG_SENDREQUEST_COMPLETE | WINHTTP_CALLBACK_FLAG_HEADERS_AVAILABLE |
            WINHTTP_CALLBACK_FLAG_READ_COMPLETE | WINHTTP_CALLBACK_FLAG_REQUEST_ERROR;
        return WinHttpSetStatusCallback(m_Request, OnRequestStatus, notifications, 0) != WINHTTP_INVALID_STATUS_CALLBACK;
    }

    bool WaitFor(DWORD status, ULONGLONG deadline) const
    {
        const ULONGLONG now = GetTickCount64();
        return now < deadline && WaitForSingleObject(m_Context.completed, static_cast<DWORD>(deadline - now)) == WAIT_OBJECT_0 &&
            GetTickCount64() < deadline && m_Context.status == status;
    }

    operator HINTERNET() const { return m_Request; }
    AsyncRequestContext& Context() { return m_Context; }

private:
    HINTERNET m_Session = nullptr;
    HINTERNET m_Connection = nullptr;
    HINTERNET m_Request = nullptr;
    AsyncRequestContext m_Context;
    HANDLE m_Unloaded = nullptr;
    bool m_UnloadRegistered = false;
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
    while (!state.completed)
    {
        const auto result = GetMessageW(&message, nullptr, 0, 0);
        if (result <= 0)
        {
            DestroyWindow(window);
            if (result == 0)
            {
                PostQuitMessage(static_cast<int>(message.wParam));
            }
            break;
        }
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

/// Fetches a URL and keeps the WinHTTP context alive until its session is unloaded.
bool FetchUrlInBackground(const std::wstring& url, std::string& body, ULONGLONG deadline)
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

    DWORD flags = components.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    AsyncRequest request;
    if (!request.Initialize(host.data(), components.nPort, requestPath.c_str(), flags))
    {
        return false;
    }

    auto& context = request.Context();
    if (GetTickCount64() >= deadline ||
        !WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, reinterpret_cast<DWORD_PTR>(&context)) ||
        !request.WaitFor(WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE, deadline) ||
        !WinHttpReceiveResponse(request, nullptr) || !request.WaitFor(WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE, deadline))
    {
        return false;
    }

    body.clear();
    while (true)
    {
        const auto capacity = static_cast<DWORD>(k_MaxResponseBytes - body.size() + 1);
        if (!WinHttpReadData(request, context.buffer.data(), capacity, nullptr) || !request.WaitFor(WINHTTP_CALLBACK_STATUS_READ_COMPLETE, deadline))
        {
            return false;
        }

        if (context.readBytes == 0)
        {
            return !body.empty();
        }
        if (context.readBytes > k_MaxResponseBytes - body.size())
        {
            return false;
        }
        body.append(context.buffer.data(), context.readBytes);
    }
}

/// Fetches Codex, Grok, then DeepSeek display values from the local three-line bridge service.
bool FetchUsageValues(UsageValues& values, ULONGLONG deadline)
{
    std::string body;
    if (!FetchUrlInBackground(ReadUsageUrl(), body, deadline))
    {
        return false;
    }

    std::wstring text = Utf8ToWide(body);
    size_t firstSeparator = text.find(L'\n');
    if (firstSeparator == std::wstring::npos)
    {
        return false;
    }

    size_t secondSeparator = text.find(L'\n', firstSeparator + 1);
    if (secondSeparator == std::wstring::npos)
    {
        return false;
    }

    values.codex = TrimString(text.substr(0, firstSeparator));
    values.grok = TrimString(text.substr(firstSeparator + 1, secondSeparator - firstSeparator - 1));
    values.deepseek = TrimString(text.substr(secondSeparator + 1));
    return !values.codex.empty() && !values.grok.empty() && !values.deepseek.empty();
}

void PublishUsage(const std::shared_ptr<SharedUsage>& usage, UsageValues values, bool available)
{
    std::wstring tooltip;
    if (available)
    {
        tooltip = L"Codex: " + values.codex + L"\nGrok: " + values.grok + L"\nDeepSeek: " + values.deepseek;
    }
    else
    {
        values = { k_FallbackValue, k_FallbackValue, k_FallbackValue };
        tooltip = L"CodexTray bridge unavailable";
    }

    std::lock_guard<std::mutex> lock(usage->mutex);
    usage->values = std::move(values);
    usage->tooltip = std::move(tooltip);
}

std::atomic<bool> g_RequestPending = false;

struct RefreshTask
{
    std::shared_ptr<SharedUsage> usage;
    UsageValues values;
    ULONGLONG deadline = 0;
    HANDLE completed = nullptr;
    HMODULE coordinatorModule = nullptr;
    HMODULE networkModule = nullptr;
    bool success = false;
    bool ownsSlot = false;

    ~RefreshTask()
    {
        if (completed != nullptr)
        {
            CloseHandle(completed);
        }
        if (ownsSlot)
        {
            g_RequestPending = false;
        }
    }
};

void CALLBACK RunNetworkTask(PTP_CALLBACK_INSTANCE instance, void* context)
{
    std::unique_ptr<std::shared_ptr<RefreshTask>> owner(static_cast<std::shared_ptr<RefreshTask>*>(context));
    const auto task = *owner;
    FreeLibraryWhenCallbackReturns(instance, task->networkModule);
    CallbackMayRunLong(instance);
    try
    {
        task->success = FetchUsageValues(task->values, task->deadline);
    }
    catch (...)
    {
        task->success = false;
    }
    SetEvent(task->completed);
}

void CALLBACK RunRefreshTask(PTP_CALLBACK_INSTANCE instance, void* context)
{
    std::unique_ptr<std::shared_ptr<RefreshTask>> owner(static_cast<std::shared_ptr<RefreshTask>*>(context));
    const auto task = *owner;
    FreeLibraryWhenCallbackReturns(instance, task->coordinatorModule);
    CallbackMayRunLong(instance);
    try
    {
        bool available = false;
        auto networkContext = std::make_unique<std::shared_ptr<RefreshTask>>(task);
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(g_Module), &task->networkModule))
        {
            if (TrySubmitThreadpoolCallback(RunNetworkTask, networkContext.get(), nullptr))
            {
                networkContext.release();
                const ULONGLONG now = GetTickCount64();
                available = now < task->deadline && WaitForSingleObject(task->completed, static_cast<DWORD>(task->deadline - now)) == WAIT_OBJECT_0 &&
                    GetTickCount64() < task->deadline && task->success;
            }
            else
            {
                FreeLibrary(task->networkModule);
            }
        }
        PublishUsage(task->usage, available ? std::move(task->values) : UsageValues{}, available);
    }
    catch (...)
    {
        try
        {
            PublishUsage(task->usage, {}, false);
        }
        catch (...)
        {
        }
    }
}

class UsageItem final : public IPluginItem
{
public:
    /// Creates one usage display item.
    UsageItem(const wchar_t* name, const wchar_t* id, const wchar_t* label, const wchar_t* sampleText,
              SharedUsage& usage, std::wstring UsageValues::*value, size_t index)
        : m_Name(name), m_Id(id), m_Label(label), m_SampleText(sampleText), m_Usage(usage), m_Value(value), m_Index(index)
    {
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

    /// The returned pointer is valid until this thread next reads the same item.
    const wchar_t* GetItemValueText() const override
    {
        thread_local std::array<std::wstring, 3> values;
        std::lock_guard<std::mutex> lock(m_Usage.mutex);
        values[m_Index] = m_Usage.values.*m_Value;
        return values[m_Index].c_str();
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
    SharedUsage& m_Usage;
    std::wstring UsageValues::*m_Value;
    size_t m_Index;
};

class CodexTrayPlugin final : public ITMPlugin
{
public:
    /// Creates the TrafficMonitor plugin singleton.
    CodexTrayPlugin()
        : m_Usage(std::make_shared<SharedUsage>()),
          m_CodexItem(L"Codex", L"CodexTrayCodex", L"Codex", L"100%", *m_Usage, &UsageValues::codex, 0),
          m_GrokItem(L"Grok", L"CodexTrayGrok", L"Grok", L"100%", *m_Usage, &UsageValues::grok, 1),
          m_DeepSeekItem(L"DeepSeek", L"CodexTrayDeepSeek", L"DeepSeek", L"¥9999", *m_Usage, &UsageValues::deepseek, 2)
    {
    }

    /// Returns a display item by index.
    IPluginItem* GetItem(int index) override
    {
        switch (index)
        {
        case 0:
            return &m_CodexItem;
        case 1:
            return &m_GrokItem;
        case 2:
            return &m_DeepSeekItem;
        default:
            return nullptr;
        }
    }

    /// Starts a refresh without blocking the host's monitoring thread.
    void DataRequired() override
    {
        try
        {
            auto task = std::make_shared<RefreshTask>();
            task->usage = m_Usage;
            task->deadline = GetTickCount64() + k_RequestTimeoutMilliseconds;
            task->completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            auto context = std::make_unique<std::shared_ptr<RefreshTask>>(task);
            if (g_RequestPending.exchange(true))
            {
                return;
            }
            task->ownsSlot = true;
            if (task->completed == nullptr ||
                !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(g_Module), &task->coordinatorModule))
            {
                PublishUsage(m_Usage, {}, false);
                return;
            }
            if (!TrySubmitThreadpoolCallback(RunRefreshTask, context.get(), nullptr))
            {
                FreeLibrary(task->coordinatorModule);
                PublishUsage(m_Usage, {}, false);
                return;
            }
            context.release();
        }
        catch (...)
        {
            try
            {
                PublishUsage(m_Usage, {}, false);
            }
            catch (...)
            {
            }
        }
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

        std::lock_guard<std::mutex> lock(m_Usage->mutex);
        m_Usage->tooltip = L"CodexTray backend URL updated";
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
            return L"Displays Codex and Grok quota and DeepSeek balance from CodexTray.";
        case TMI_AUTHOR:
            return L"SnowyLake";
        case TMI_COPYRIGHT:
            return L"MIT";
        case TMI_VERSION:
            return L"6.4.1";
        case TMI_URL:
            return L"";
        default:
            return L"";
        }
    }

    /// The returned pointer is valid until this thread next reads the tooltip.
    const wchar_t* GetTooltipInfo() override
    {
        thread_local std::wstring tooltip;
        std::lock_guard<std::mutex> lock(m_Usage->mutex);
        tooltip = m_Usage->tooltip;
        return tooltip.c_str();
    }

private:
    std::shared_ptr<SharedUsage> m_Usage;
    UsageItem m_CodexItem;
    UsageItem m_GrokItem;
    UsageItem m_DeepSeekItem;
};

}

/// Stores this module handle for configuration lookup.
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        TrafficMonitorPlugin::g_Module = module;
    }

    return TRUE;
}

extern "C"
{
    /// Returns the TrafficMonitor plugin instance.
    __declspec(dllexport) ITMPlugin* TMPluginGetInstance()
    {
        static TrafficMonitorPlugin::CodexTrayPlugin plugin;
        return &plugin;
    }
}
