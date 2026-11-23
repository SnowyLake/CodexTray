#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

#include "../PluginInterface.h"

#pragma comment(lib, "Ws2_32.lib")

using GetPlugin = ITMPlugin* (*)();
using namespace std::chrono_literals;

void Require(bool condition, const char* message)
{
    if (!condition)
    {
        std::cerr << "FAIL: " << message << '\n';
        std::exit(1);
    }
}

bool SendAll(SOCKET client, const std::string& data)
{
    size_t sent = 0;
    while (sent < data.size())
    {
        const int count = send(client, data.data() + sent, static_cast<int>(data.size() - sent), 0);
        if (count <= 0)
        {
            return false;
        }
        sent += count;
    }
    return true;
}

class TestServer
{
public:
    TestServer()
    {
        WSADATA data{};
        Require(WSAStartup(MAKEWORD(2, 2), &data) == 0, "start Winsock");
        m_Listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        Require(m_Listener != INVALID_SOCKET, "create test listener");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        Require(bind(m_Listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0, "bind test listener");
        int addressSize = sizeof(address);
        Require(getsockname(m_Listener, reinterpret_cast<sockaddr*>(&address), &addressSize) == 0, "read test port");
        m_Port = ntohs(address.sin_port);
        Require(listen(m_Listener, SOMAXCONN) == 0, "start test listener");
        m_Thread = std::thread([this]
        {
            while (true)
            {
                SOCKET client = accept(m_Listener, nullptr, nullptr);
                if (client == INVALID_SOCKET)
                {
                    return;
                }
                ++m_RequestCount;
                m_Clients.emplace_back(HandleClient, client);
            }
        });
    }

    ~TestServer()
    {
        shutdown(m_Listener, SD_BOTH);
        closesocket(m_Listener);
        m_Thread.join();
        for (auto& client : m_Clients)
        {
            client.join();
        }
        WSACleanup();
    }

    unsigned short Port() const { return m_Port; }
    int RequestCount() const { return m_RequestCount; }

private:
    static void HandleClient(SOCKET client)
    {
        std::string request;
        char buffer[1024];
        while (request.find("\r\n\r\n") == std::string::npos && request.size() < 8192)
        {
            const int count = recv(client, buffer, sizeof(buffer), 0);
            if (count <= 0)
            {
                closesocket(client);
                return;
            }
            request.append(buffer, count);
        }
        const size_t start = request.find(' ') + 1;
        const std::string path = request.substr(start, request.find(' ', start) - start);
        if (path == "/slow-header")
        {
            bool open = SendAll(client, "HTTP/1.1 200 OK\r\nX-Test: ");
            for (int i = 0; i < 14 && open; ++i)
            {
                std::this_thread::sleep_for(500ms);
                open = SendAll(client, "a");
            }
            if (open)
            {
                SendAll(client, "\r\nContent-Length: 15\r\n\r\n42%\n73%\n88%\n123");
            }
            closesocket(client);
            return;
        }

        std::string status = "200 OK";
        std::string body = path == "/second" ? "11%\n22%\n33%\n44%\n" : "42%\n73%\n88%\n91%\n";
        if (path == "/crlf")
        {
            body = "41%\r\n72%\r\n87%\r\n95%\r\n";
        }
        else if (path == "/error-503")
        {
            status = "503 Service Unavailable";
            body = "42%\n73%\n88%\n123\n";
        }
        else if (path == "/extra-line")
        {
            body = "42%\n73%\n88%\n123\nextra\n";
        }
        else if (path == "/blank-fourth")
        {
            body = "42%\n73%\n88%\n\n123\n";
        }
        else if (path == "/limit" || path == "/over-limit")
        {
            body = std::string(path == "/limit" ? 4089 : 4090, 'x') + "\ny\nz\nw\n";
        }
        const std::string headers = "HTTP/1.1 " + status + "\r\nContent-Length: " + std::to_string(body.size()) + "\r\n\r\n";
        if (SendAll(client, headers))
        {
            if (path == "/slow-body")
            {
                for (const char byte : body)
                {
                    std::this_thread::sleep_for(500ms);
                    if (!SendAll(client, std::string(1, byte)))
                    {
                        break;
                    }
                }
            }
            else
            {
                SendAll(client, body);
            }
        }
        closesocket(client);
    }

    SOCKET m_Listener = INVALID_SOCKET;
    unsigned short m_Port = 0;
    std::atomic<int> m_RequestCount = 0;
    std::thread m_Thread;
    std::vector<std::thread> m_Clients;
};

bool WaitForValue(IPluginItem* item, const std::wstring& expected, std::chrono::milliseconds timeout)
{
    bool found = false;
    std::thread reader([&]
    {
        const auto deadline = std::chrono::steady_clock::now() + timeout;
        while (std::chrono::steady_clock::now() < deadline)
        {
            if (std::wstring(item->GetItemValueText()) == expected)
            {
                found = true;
                return;
            }
            std::this_thread::sleep_for(10ms);
        }
    });
    reader.join();
    return found;
}

int wmain(int argc, wchar_t** argv)
{
    Require(argc == 2, "expected isolated plugin DLL path");
    TestServer server;
    const std::wstring dllPath = argv[1];
    const auto separator = dllPath.find_last_of(L"\\/");
    Require(separator != std::wstring::npos, "plugin path has a directory");
    const auto configPath = dllPath.substr(0, separator + 1) + L"CodexTray.ini";
    const auto base = L"http://127.0.0.1:" + std::to_wstring(server.Port());
    auto setPath = [&](const wchar_t* path)
    {
        const std::wstring url = base + path;
        Require(WritePrivateProfileStringW(L"CodexTray", L"UsageUrl", url.c_str(), configPath.c_str()) != FALSE, "write isolated config");
    };

    HMODULE module = LoadLibraryW(dllPath.c_str());
    Require(module != nullptr, "load plugin DLL");
    const auto getPlugin = reinterpret_cast<GetPlugin>(GetProcAddress(module, "TMPluginGetInstance"));
    Require(getPlugin != nullptr, "find plugin entry point");
    ITMPlugin* plugin = getPlugin();
    IPluginItem* codex = plugin->GetItem(0);
    IPluginItem* grok = plugin->GetItem(1);
    IPluginItem* gemini = plugin->GetItem(2);
    IPluginItem* opencode = plugin->GetItem(3);
    Require(codex && grok && gemini && opencode && !plugin->GetItem(4), "plugin items");
    Require(std::wstring(opencode->GetItemName()) == L"OpenCode" && std::wstring(opencode->GetItemId()) == L"CodexTrayOpenCode" && std::wstring(opencode->GetItemValueSampleText()) == L"100%", "OpenCode item metadata");

    auto refresh = [&](const wchar_t* path, const std::wstring& expected)
    {
        setPath(path);
        plugin->DataRequired();
        Require(WaitForValue(codex, expected, 2500ms), "publish response");
        std::this_thread::sleep_for(25ms);
    };
    refresh(L"/first", L"42%");
    const wchar_t* oldCodex = codex->GetItemValueText();
    const wchar_t* oldGrok = grok->GetItemValueText();
    const wchar_t* oldGemini = gemini->GetItemValueText();
    const wchar_t* oldOpenCode = opencode->GetItemValueText();
    const wchar_t* oldTooltip = plugin->GetTooltipInfo();
    refresh(L"/second", L"11%");
    Require(std::wstring(oldCodex) == L"42%" && std::wstring(oldGrok) == L"73%" && std::wstring(oldGemini) == L"88%" && std::wstring(oldOpenCode) == L"91%", "old value pointers survive update");
    Require(std::wstring(oldTooltip) == L"Codex: 42%\nGrok: 73%\nGemini: 88%\nOpenCode: 91%", "old tooltip pointer survives update");
    Require(std::wstring(gemini->GetItemValueText()) == L"33%" && std::wstring(opencode->GetItemValueText()) == L"44%", "updated values publish in order");

    std::atomic<bool> stop = false;
    std::atomic<bool> invalid = false;
    std::thread reader([&]
    {
        while (!stop)
        {
            const wchar_t* value = codex->GetItemValueText();
            const std::wstring previous(value);
            std::this_thread::yield();
            if (std::wstring(value) != previous)
            {
                invalid = true;
            }
        }
    });
    for (int i = 0; i < 12; ++i)
    {
        refresh(i % 2 == 0 ? L"/first" : L"/second", i % 2 == 0 ? L"42%" : L"11%");
    }
    stop = true;
    reader.join();
    Require(!invalid, "concurrent publication preserves borrowed pointers");

    refresh(L"/limit", std::wstring(4089, L'x'));
    refresh(L"/over-limit", L"N/A");
    refresh(L"/crlf", L"41%");
    Require(std::wstring(grok->GetItemValueText()) == L"72%" && std::wstring(gemini->GetItemValueText()) == L"87%" && std::wstring(opencode->GetItemValueText()) == L"95%", "CRLF UTF-8 four-line response parses");
    refresh(L"/error-503", L"N/A");
    refresh(L"/first", L"42%");
    refresh(L"/extra-line", L"N/A");
    refresh(L"/first", L"42%");
    refresh(L"/blank-fourth", L"N/A");
    for (const wchar_t* path : { L"/slow-header", L"/slow-body" })
    {
        refresh(L"/first", L"42%");
        setPath(path);
        const int requestsBefore = server.RequestCount();
        const auto start = std::chrono::steady_clock::now();
        plugin->DataRequired();
        const auto returned = std::chrono::steady_clock::now();
        Require(returned - start < 1s, "refresh does not block the host");
        Require(std::wstring(codex->GetItemValueText()) == L"42%", "last snapshot remains visible during refresh");
        plugin->DataRequired();
        Require(WaitForValue(codex, L"N/A", 6500ms), "slow response becomes unavailable");
        const auto elapsed = std::chrono::steady_clock::now() - start;
        Require(elapsed >= 4s && elapsed < 6500ms, "slow response respects deadline");
        Require(server.RequestCount() == requestsBefore + 1, "overlapping refresh does not start another request");
        std::wcout << path << L": " << std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count() << L" ms\n";
        std::this_thread::sleep_for(250ms);
    }

    refresh(L"/first", L"42%");
    setPath(L"/slow-body");
    plugin->DataRequired();
    Require(FreeLibrary(module) != FALSE, "release plugin with active refresh");
    Require(GetModuleHandleW(dllPath.c_str()) != nullptr, "active refresh retains the plugin module");
    const auto unloadDeadline = std::chrono::steady_clock::now() + 8s;
    while (GetModuleHandleW(dllPath.c_str()) != nullptr && std::chrono::steady_clock::now() < unloadDeadline)
    {
        std::this_thread::sleep_for(20ms);
    }
    Require(GetModuleHandleW(dllPath.c_str()) == nullptr, "plugin unloads after the refresh finishes");
    module = LoadLibraryW(dllPath.c_str());
    Require(module != nullptr && FreeLibrary(module), "reload plugin after active refresh");
    std::cout << "Native plugin tests passed.\n";
    return 0;
}
