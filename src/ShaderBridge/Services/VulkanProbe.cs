using System.Runtime.InteropServices;
using System.Text;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class VulkanProbe
{
    private const uint VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    private const uint VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    private const int VK_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkCreateInstanceDelegate(IntPtr createInfo, IntPtr allocator, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkEnumeratePhysicalDevicesDelegate(IntPtr instance, ref uint count, IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDevicePropertiesDelegate(IntPtr physicalDevice, IntPtr properties);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkDestroyInstanceDelegate(IntPtr instance, IntPtr allocator);

    public IReadOnlyList<VulkanDeviceInfo> EnumerateDevices()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var library)) return [];
        IntPtr instance = IntPtr.Zero;
        IntPtr appName = IntPtr.Zero;
        IntPtr engineName = IntPtr.Zero;
        IntPtr appInfoPtr = IntPtr.Zero;
        IntPtr createInfoPtr = IntPtr.Zero;
        try
        {
            var createInstance = GetDelegate<VkCreateInstanceDelegate>(library, "vkCreateInstance");
            var enumerate = GetDelegate<VkEnumeratePhysicalDevicesDelegate>(library, "vkEnumeratePhysicalDevices");
            var getProps = GetDelegate<VkGetPhysicalDevicePropertiesDelegate>(library, "vkGetPhysicalDeviceProperties");
            var destroy = GetDelegate<VkDestroyInstanceDelegate>(library, "vkDestroyInstance");

            appName = Marshal.StringToHGlobalAnsi("ShaderBridge");
            engineName = Marshal.StringToHGlobalAnsi("ShaderBridge");
            var appInfo = new VkApplicationInfo
            {
                sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                pNext = IntPtr.Zero,
                pApplicationName = appName,
                applicationVersion = 1,
                pEngineName = engineName,
                engineVersion = 1,
                apiVersion = 1u << 22
            };
            appInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(appInfo, appInfoPtr, false);

            var createInfo = new VkInstanceCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = appInfoPtr
            };
            createInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkInstanceCreateInfo>());
            Marshal.StructureToPtr(createInfo, createInfoPtr, false);

            var result = createInstance(createInfoPtr, IntPtr.Zero, out instance);
            if (result != VK_SUCCESS || instance == IntPtr.Zero) return [];

            uint count = 0;
            if (enumerate(instance, ref count, IntPtr.Zero) != VK_SUCCESS || count == 0) return [];

            var ptrSize = IntPtr.Size;
            var deviceArray = Marshal.AllocHGlobal(checked((int)count * ptrSize));
            try
            {
                if (enumerate(instance, ref count, deviceArray) != VK_SUCCESS) return [];
                var devices = new List<VulkanDeviceInfo>();
                for (var i = 0; i < count; i++)
                {
                    var physicalDevice = Marshal.ReadIntPtr(deviceArray, checked((int)i * ptrSize));
                    var buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        getProps(physicalDevice, buffer);
                        var apiVersion = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                        var driverVersion = unchecked((uint)Marshal.ReadInt32(buffer, 4));
                        var vendorId = unchecked((uint)Marshal.ReadInt32(buffer, 8));
                        var deviceId = unchecked((uint)Marshal.ReadInt32(buffer, 12));
                        var nameBytes = new byte[256];
                        Marshal.Copy(buffer + 20, nameBytes, 0, nameBytes.Length);
                        var zeroIndex = Array.IndexOf(nameBytes, (byte)0);
                        if (zeroIndex < 0) zeroIndex = nameBytes.Length;
                        var name = Encoding.UTF8.GetString(nameBytes, 0, zeroIndex);
                        var uuid = new byte[16];
                        Marshal.Copy(buffer + 276, uuid, 0, uuid.Length);
                        devices.Add(new VulkanDeviceInfo(name, apiVersion, driverVersion, vendorId, deviceId, uuid));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                return devices;
            }
            finally { Marshal.FreeHGlobal(deviceArray); }
        }
        catch
        {
            return [];
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                try
                {
                    var destroy = GetDelegate<VkDestroyInstanceDelegate>(library, "vkDestroyInstance");
                    destroy(instance, IntPtr.Zero);
                }
                catch { }
            }
            if (createInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(createInfoPtr);
            if (appInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(appInfoPtr);
            if (engineName != IntPtr.Zero) Marshal.FreeHGlobal(engineName);
            if (appName != IntPtr.Zero) Marshal.FreeHGlobal(appName);
            NativeLibrary.Free(library);
        }
    }

    private static T GetDelegate<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
}
