#pragma once

class IPluginItem
{
public:
    /// Returns the display item name.
    virtual const wchar_t* GetItemName() const = 0;

    /// Returns the display item unique identifier.
    virtual const wchar_t* GetItemId() const = 0;

    /// Returns the display item label text.
    virtual const wchar_t* GetItemLableText() const = 0;

    /// Returns the display item value text.
    virtual const wchar_t* GetItemValueText() const = 0;

    /// Returns the display item sample value text.
    virtual const wchar_t* GetItemValueSampleText() const = 0;

    /// Returns whether the item draws itself.
    virtual bool IsCustomDraw() const { return false; }

    /// Returns the custom drawing width for 96 DPI.
    virtual int GetItemWidth() const { return 0; }

    /// Draws the item when custom drawing is enabled.
    virtual void DrawItem(void* hDC, int x, int y, int w, int h, bool darkMode) {}

    /// Returns the custom drawing width for a device context.
    virtual int GetItemWidthEx(void* hDC) const { return 0; }

    enum MouseEventType
    {
        MT_LCLICKED,
        MT_RCLICKED,
        MT_DBCLICKED,
        MT_WHEEL_UP,
        MT_WHEEL_DOWN,
    };

    enum MouseEventFlag
    {
        MF_TASKBAR_WND = 1 << 0,
    };

    /// Handles a mouse event on the item.
    virtual int OnMouseEvent(MouseEventType type, int x, int y, void* hWnd, int flag) { return 0; }

    enum KeyboardEventFlag
    {
        KF_TASKBAR_WND = 1 << 0,
    };

    /// Handles a keyboard event on the item.
    virtual int OnKeboardEvent(int key, bool ctrl, bool shift, bool alt, void* hWnd, int flag) { return 0; }

    enum ItemInfoType
    {
    };

    /// Handles reserved item information callbacks.
    virtual void* OnItemInfo(ItemInfoType, void* para1, void* para2) { return nullptr; }

    /// Returns whether the item draws a resource usage graph.
    virtual int IsDrawResourceUsageGraph() const { return 0; }

    /// Returns the resource usage graph value.
    virtual float GetResourceUsageGraphValue() const { return 0.0f; }
};

class ITrafficMonitor;

class ITMPlugin
{
public:
    /// Returns the TrafficMonitor plugin API version.
    virtual int GetAPIVersion() const { return 7; }

    /// Returns a display item by index.
    virtual IPluginItem* GetItem(int index) = 0;

    /// Refreshes all plugin data.
    virtual void DataRequired() = 0;

    enum OptionReturn
    {
        OR_OPTION_CHANGED,
        OR_OPTION_UNCHANGED,
        OR_OPTION_NOT_PROVIDED,
    };

    /// Shows an optional settings dialog.
    virtual OptionReturn ShowOptionsDialog(void* hParent) { return OR_OPTION_NOT_PROVIDED; }

    enum PluginInfoIndex
    {
        TMI_NAME,
        TMI_DESCRIPTION,
        TMI_AUTHOR,
        TMI_COPYRIGHT,
        TMI_VERSION,
        TMI_URL,
        TMI_MAX,
    };

    /// Returns plugin metadata by index.
    virtual const wchar_t* GetInfo(PluginInfoIndex index) = 0;

    struct MonitorInfo
    {
        unsigned long long up_speed{};
        unsigned long long down_speed{};
        int cpu_usage{};
        int memory_usage{};
        int gpu_usage{};
        int hdd_usage{};
        int cpu_temperature{};
        int gpu_temperature{};
        int hdd_temperature{};
        int main_board_temperature{};
        int cpu_freq{};
    };

    /// Receives TrafficMonitor monitor data.
    virtual void OnMonitorInfo(const MonitorInfo& monitorInfo) {}

    /// Returns tooltip text.
    virtual const wchar_t* GetTooltipInfo() { return L""; }

    enum ExtendedInfoIndex
    {
        EI_LABEL_TEXT_COLOR,
        EI_VALUE_TEXT_COLOR,
        EI_DRAW_TASKBAR_WND,
        EI_NAIN_WND_NET_SPEED_SHORT_MODE,
        EI_MAIN_WND_SPERATE_WITH_SPACE,
        EI_MAIN_WND_UNIT_BYTE,
        EI_MAIN_WND_UNIT_SELECT,
        EI_MAIN_WND_NOT_SHOW_UNIT,
        EI_MAIN_WND_NOT_SHOW_PERCENT,
        EI_TASKBAR_WND_NET_SPEED_SHORT_MODE,
        EI_TASKBAR_WND_SPERATE_WITH_SPACE,
        EI_TASKBAR_WND_VALUE_RIGHT_ALIGN,
        EI_TASKBAR_WND_NET_SPEED_WIDTH,
        EI_TASKBAR_WND_UNIT_BYTE,
        EI_TASKBAR_WND_UNIT_SELECT,
        EI_TASKBAR_WND_NOT_SHOW_UNIT,
        EI_TASKBAR_WND_NOT_SHOW_PERCENT,
        EI_CONFIG_DIR,
    };

    /// Receives extended TrafficMonitor information.
    virtual void OnExtenedInfo(ExtendedInfoIndex index, const wchar_t* data) {}

    /// Returns the plugin icon handle.
    virtual void* GetPluginIcon() { return nullptr; }

    /// Returns the number of plugin commands.
    virtual int GetCommandCount() { return 0; }

    /// Returns a plugin command name.
    virtual const wchar_t* GetCommandName(int commandIndex) { return nullptr; }

    /// Returns a plugin command icon handle.
    virtual void* GetCommandIcon(int commandIndex) { return nullptr; }

    /// Handles a plugin command.
    virtual void OnPluginCommand(int commandIndex, void* hWnd, void* para) {}

    /// Returns whether a plugin command is checked.
    virtual int IsCommandChecked(int commandIndex) { return false; }

    /// Initializes the plugin with the TrafficMonitor application interface.
    virtual void OnInitialize(ITrafficMonitor* app) {}
};

class ITrafficMonitor
{
public:
    /// Returns the TrafficMonitor application API version.
    virtual int GetAPIVersion() = 0;

    /// Returns the TrafficMonitor version.
    virtual const wchar_t* GetVersion() = 0;

    enum MonitorItem
    {
        MI_UP,
        MI_DOWN,
        MI_CPU,
        MI_MEMORY,
        MI_GPU_USAGE,
        MI_CPU_TEMP,
        MI_GPU_TEMP,
        MI_HDD_TEMP,
        MI_MAIN_BOARD_TEMP,
        MI_HDD_USAGE,
        MI_CPU_FREQ,
        MI_TODAY_UP_TRAFFIC,
        MI_TODAY_DOWN_TRAFFIC,
    };

    /// Returns a numeric monitor value.
    virtual double GetMonitorValue(MonitorItem item) = 0;

    /// Returns a monitor value string.
    virtual const wchar_t* GetMonitorValueString(MonitorItem item, int isMainWindow = false) = 0;

    /// Shows a TrafficMonitor notification.
    virtual void ShowNotifyMessage(const wchar_t* message) = 0;

    /// Returns the current language identifier.
    virtual unsigned short GetLanguageId() const = 0;

    /// Returns the plugin configuration directory.
    virtual const wchar_t* GetPluginConfigDir() const = 0;

    enum DPIType
    {
        DPI_MAIN_WND,
        DPI_TASKBAR,
    };

    /// Returns a TrafficMonitor DPI value.
    virtual int GetDPI(DPIType type) const = 0;

    /// Returns the current theme color.
    virtual unsigned int GetThemeColor() const = 0;

    /// Returns a localized string resource.
    virtual const wchar_t* GetStringRes(const wchar_t* key, const wchar_t* section) = 0;
};
