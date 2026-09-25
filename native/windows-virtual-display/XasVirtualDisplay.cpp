#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <wdf.h>
#include <iddcx.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include <initguid.h>
#include <strsafe.h>
#include <cstring>
#include <new>
#include <wrl/client.h>

// This driver supplies a display target for input-coordinate topology only.
// The swapchain worker releases frames without reading or exporting surfaces.

class SwapchainDiscarder {
public:
    SwapchainDiscarder(IDDCX_SWAPCHAIN chain, LUID luid, HANDLE frameEvent) : chain_(chain), luid_(luid), frameEvent_(frameEvent) {}
    ~SwapchainDiscarder() {
        if (terminate_) SetEvent(terminate_);
        if (thread_) { WaitForSingleObject(thread_, INFINITE); CloseHandle(thread_); }
        if (terminate_) CloseHandle(terminate_);
    }
    bool Start() {
        terminate_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!terminate_) return false;
        thread_ = CreateThread(nullptr, 0, ThreadMain, this, 0, nullptr);
        if (!thread_) { CloseHandle(terminate_); terminate_ = nullptr; return false; }
        return true;
    }
private:
    static DWORD WINAPI ThreadMain(void* arg) { static_cast<SwapchainDiscarder*>(arg)->Run(); return 0; }
    void Run() {
        using Microsoft::WRL::ComPtr;
        ComPtr<IDXGIFactory4> factory; ComPtr<IDXGIAdapter> adapter; ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGIDevice> dxgiDevice;
        HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) hr = factory->EnumAdapterByLuid(luid_, IID_PPV_ARGS(&adapter));
        if (SUCCEEDED(hr)) hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
        if (SUCCEEDED(hr)) hr = device.As(&dxgiDevice);
        if (SUCCEEDED(hr)) {
            IDARG_IN_SWAPCHAINSETDEVICE setDevice{}; setDevice.pDevice = dxgiDevice.Get();
            hr = IddCxSwapChainSetDevice(chain_, &setDevice);
        }
        while (SUCCEEDED(hr)) {
            IDARG_OUT_RELEASEANDACQUIREBUFFER buffer{};
            hr = IddCxSwapChainReleaseAndAcquireBuffer(chain_, &buffer);
            if (hr == E_PENDING) {
                HANDLE events[] = { frameEvent_, terminate_ };
                DWORD wait = WaitForMultipleObjects(ARRAYSIZE(events), events, FALSE, INFINITE);
                if (wait == WAIT_OBJECT_0 || wait == WAIT_TIMEOUT) { hr = S_OK; continue; }
                break;
            }
            if (FAILED(hr)) break;
            // Intentionally discard the frame. Releasing the COM reference and notifying
            // IddCx lets the OS advance this monitor's swapchain without frame transport.
            if (buffer.MetaData.pSurface) buffer.MetaData.pSurface->Release();
            hr = IddCxSwapChainFinishedProcessingFrame(chain_);
        }
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(chain_));
        chain_ = nullptr;
    }
    IDDCX_SWAPCHAIN chain_;
    LUID luid_;
    HANDLE frameEvent_;
    HANDLE terminate_ = nullptr;
    HANDLE thread_ = nullptr;
};

struct MonitorContext { CRITICAL_SECTION lock; SwapchainDiscarder* discarder; };
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MonitorContext, GetMonitorContext);
EVT_WDF_OBJECT_CONTEXT_CLEANUP MonitorContextCleanup;

static const BYTE Edid[128] = {
    0x00,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0x00, 0x60,0x33,0x01,0x00,0x01,0x00,0x00,0x00,
    0x01,0x20,0x01,0x04,0x80,0x34,0x20,0x78, 0x0A,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x01,0x01, 0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,
    0x01,0x01,0x01,0x01,0x01,0x01,0x58,0x2C, 0x80,0xA0,0x70,0xB0,0x23,0x40,0x30,0x20,
    0x36,0x00,0x10,0x00,0x00,0x00,0x00,0x1A, 0x00,0x00,0x00,0xFC,0x00,0x58,0x41,0x53,
    0x20,0x4C,0x69,0x6E,0x75,0x78,0x20,0x20, 0x20,0x20,0x20,0x20,0x00,0x00,0x00,0xFD,
    0x00,0x3C,0x3C,0x1E,0x46,0x0B,0x00,0x0A, 0x20,0x20,0x20,0x20,0x20,0x20,0x00,0x00,
    0x20,0x20,0x00,0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x48
};

static void Signal(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& s, UINT w, UINT h, UINT hz, bool monitorMode) {
    s = {};
    s.pixelRate = static_cast<UINT64>(w) * h * hz;
    s.hSyncFreq.Numerator = h * hz; s.hSyncFreq.Denominator = 1;
    s.vSyncFreq.Numerator = hz; s.vSyncFreq.Denominator = 1;
    s.activeSize.cx = s.totalSize.cx = w;
    s.activeSize.cy = s.totalSize.cy = h;
    s.AdditionalSignalInfo.videoStandard = 255;
    s.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
    s.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
}

static IDDCX_MONITOR_MODE MonitorMode(UINT w, UINT h, UINT hz,
    IDDCX_MONITOR_MODE_ORIGIN origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER) {
    IDDCX_MONITOR_MODE m{}; m.Size = sizeof(m); m.Origin = origin;
    Signal(m.MonitorVideoSignalInfo, w, h, hz, true); return m;
}
static IDDCX_TARGET_MODE TargetMode(UINT w, UINT h, UINT hz) {
    IDDCX_TARGET_MODE m{}; m.Size = sizeof(m); Signal(m.TargetVideoSignalInfo.targetVideoSignalInfo, w, h, hz, false); return m;
}

EVT_WDF_DRIVER_DEVICE_ADD DeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY DeviceD0Entry;
EVT_IDD_CX_ADAPTER_INIT_FINISHED AdapterInitFinished;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES QueryTargetModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION ParseDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES DefaultModes;
EVT_IDD_CX_ADAPTER_COMMIT_MODES CommitModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN AssignSwapchain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN UnassignSwapchain;

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING path) {
    WDF_DRIVER_CONFIG config; WDF_DRIVER_CONFIG_INIT(&config, DeviceAdd);
    return WdfDriverCreate(driver, path, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS DeviceAdd(WDFDRIVER, PWDFDEVICE_INIT init) {
    WDF_PNPPOWER_EVENT_CALLBACKS power; WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&power);
    power.EvtDeviceD0Entry = DeviceD0Entry; WdfDeviceInitSetPnpPowerEventCallbacks(init, &power);
    IDD_CX_CLIENT_CONFIG cx; IDD_CX_CLIENT_CONFIG_INIT(&cx);
    cx.EvtIddCxAdapterInitFinished = AdapterInitFinished;
    cx.EvtIddCxParseMonitorDescription = ParseDescription;
    cx.EvtIddCxMonitorGetDefaultDescriptionModes = DefaultModes;
    cx.EvtIddCxMonitorQueryTargetModes = QueryTargetModes;
    cx.EvtIddCxAdapterCommitModes = CommitModes;
    cx.EvtIddCxMonitorAssignSwapChain = AssignSwapchain;
    cx.EvtIddCxMonitorUnassignSwapChain = UnassignSwapchain;
    NTSTATUS status = IddCxDeviceInitConfig(init, &cx); if (!NT_SUCCESS(status)) return status;
    WDF_OBJECT_ATTRIBUTES attr; WDF_OBJECT_ATTRIBUTES_INIT(&attr);
    WDFDEVICE device; status = WdfDeviceCreate(&init, &attr, &device);
    if (!NT_SUCCESS(status)) return status;
    return IddCxDeviceInitialize(device);
}

NTSTATUS DeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE) {
    IDDCX_ADAPTER_CAPS caps{}; caps.Size = sizeof(caps); caps.MaxMonitorsSupported = 1;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"XAS Virtual Display";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"XAS";
    caps.EndPointDiagnostics.pEndPointModelName = L"Linux Display";
    IDDCX_ENDPOINT_VERSION version{}; version.Size = sizeof(version); version.MajorVer = 1;
    caps.EndPointDiagnostics.pHardwareVersion = &version; caps.EndPointDiagnostics.pFirmwareVersion = &version;
    WDF_OBJECT_ATTRIBUTES attrs; WDF_OBJECT_ATTRIBUTES_INIT(&attrs);
    IDARG_IN_ADAPTER_INIT in{}; in.WdfDevice = device; in.pCaps = &caps; in.ObjectAttributes = &attrs;
    IDARG_OUT_ADAPTER_INIT out{};
    NTSTATUS status = IddCxAdapterInitAsync(&in, &out);
    return NT_SUCCESS(status) ? STATUS_SUCCESS : status;
}

NTSTATUS AdapterInitFinished(IDDCX_ADAPTER adapter, const IDARG_IN_ADAPTER_INIT_FINISHED* in) {
    if (!NT_SUCCESS(in->AdapterInitStatus)) return in->AdapterInitStatus;
    IDDCX_MONITOR_INFO info{}; info.Size = sizeof(info); info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER;
    info.ConnectorIndex = 0; info.MonitorDescription.Size = sizeof(info.MonitorDescription);
    info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    info.MonitorDescription.DataSize = sizeof(Edid); info.MonitorDescription.pData = const_cast<BYTE*>(Edid);
    info.MonitorContainerId = GUID{ 0x5a415800, 0x5841, 0x4c49, { 0x4e, 0x55, 0x58, 0x00, 0x00, 0x01, 0x00, 0x01 } };
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, MonitorContext);
    attributes.EvtCleanupCallback = MonitorContextCleanup;
    IDARG_IN_MONITORCREATE create{}; create.pMonitorInfo = &info; create.ObjectAttributes = &attributes;
    IDARG_OUT_MONITORCREATE created{}; NTSTATUS status = IddCxMonitorCreate(adapter, &create, &created);
    if (!NT_SUCCESS(status)) return status;
    auto* monitorContext = GetMonitorContext(created.MonitorObject);
    InitializeCriticalSection(&monitorContext->lock);
    monitorContext->discarder = nullptr;
    IDARG_OUT_MONITORARRIVAL arrived{}; return IddCxMonitorArrival(created.MonitorObject, &arrived);
}

NTSTATUS ParseDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* in, IDARG_OUT_PARSEMONITORDESCRIPTION* out) {
    if (in->MonitorDescription.DataSize != sizeof(Edid) || memcmp(in->MonitorDescription.pData, Edid, sizeof(Edid)) != 0)
        return STATUS_INVALID_PARAMETER;
    constexpr UINT count = 3; out->MonitorModeBufferOutputCount = count;
    if (in->MonitorModeBufferInputCount < count) return in->MonitorModeBufferInputCount ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    in->pMonitorModes[0] = MonitorMode(1920,1080,60, IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    in->pMonitorModes[1] = MonitorMode(1600,900,60, IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    in->pMonitorModes[2] = MonitorMode(1280,720,60, IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    out->PreferredMonitorModeIdx = 0; return STATUS_SUCCESS;
}
NTSTATUS DefaultModes(IDDCX_MONITOR, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* in, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* out) {
    constexpr UINT count = 3; out->DefaultMonitorModeBufferOutputCount = count;
    if (in->DefaultMonitorModeBufferInputCount < count)
        return in->DefaultMonitorModeBufferInputCount ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    in->pDefaultMonitorModes[0] = MonitorMode(1920,1080,60);
    in->pDefaultMonitorModes[1] = MonitorMode(1600,900,60);
    in->pDefaultMonitorModes[2] = MonitorMode(1280,720,60);
    out->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}
NTSTATUS QueryTargetModes(IDDCX_MONITOR, const IDARG_IN_QUERYTARGETMODES* in, IDARG_OUT_QUERYTARGETMODES* out) {
    constexpr UINT count = 3; out->TargetModeBufferOutputCount = count;
    if (in->TargetModeBufferInputCount >= count) {
        in->pTargetModes[0] = TargetMode(1920,1080,60); in->pTargetModes[1] = TargetMode(1600,900,60);
        in->pTargetModes[2] = TargetMode(1280,720,60);
    }
    return STATUS_SUCCESS;
}
NTSTATUS CommitModes(IDDCX_ADAPTER, const IDARG_IN_COMMITMODES*) { return STATUS_SUCCESS; }

NTSTATUS AssignSwapchain(IDDCX_MONITOR monitor, const IDARG_IN_SETSWAPCHAIN* in) {
    auto* context = GetMonitorContext(monitor);
    EnterCriticalSection(&context->lock);
    auto* old = context->discarder; context->discarder = nullptr;
    LeaveCriticalSection(&context->lock);
    delete old;
    auto* discarder = new (std::nothrow) SwapchainDiscarder(in->hSwapChain, in->RenderAdapterLuid, in->hNextSurfaceAvailable);
    if (discarder && discarder->Start()) {
        EnterCriticalSection(&context->lock);
        context->discarder = discarder;
        LeaveCriticalSection(&context->lock);
    }
    else {
        delete discarder;
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(in->hSwapChain));
    }
    return STATUS_SUCCESS;
}
NTSTATUS UnassignSwapchain(IDDCX_MONITOR monitor) {
    auto* context = GetMonitorContext(monitor);
    EnterCriticalSection(&context->lock);
    auto* discarder = context->discarder; context->discarder = nullptr;
    LeaveCriticalSection(&context->lock);
    delete discarder;
    return STATUS_SUCCESS;
}
void MonitorContextCleanup(WDFOBJECT object) {
    auto* context = GetMonitorContext(object);
    EnterCriticalSection(&context->lock);
    auto* discarder = context->discarder; context->discarder = nullptr;
    LeaveCriticalSection(&context->lock);
    delete discarder;
    DeleteCriticalSection(&context->lock);
}
