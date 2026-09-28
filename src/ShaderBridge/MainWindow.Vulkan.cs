using System.Text;
using System.Windows;
using Microsoft.Win32;
using ShaderBridge.Models;

namespace ShaderBridge;

public partial class MainWindow
{
    private void ProbeVulkanDevices()
    {
        var devices = _vulkanProbe.EnumerateDevices();
        VulkanDeviceCombo.ItemsSource = devices;
        if (devices.Count > 0) VulkanDeviceCombo.SelectedIndex = 0;
        VulkanOutputText.Text = devices.Count == 0
            ? "No Vulkan loader/device could be queried. The rest of ShaderBridge does not require Vulkan."
            : string.Join(Environment.NewLine + Environment.NewLine, devices.Select(DescribeVulkanDevice));
    }

    private void ProbeVulkan_Click(object sender, RoutedEventArgs e) => ProbeVulkanDevices();

    private void BrowseVulkan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select a Vulkan pipeline cache", Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) VulkanFileText.Text = dialog.FileName;
    }

    private void InspectVulkan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var header = _vulkanCache.ReadHeader(VulkanFileText.Text.Trim());
            var sb = new StringBuilder();
            sb.AppendLine("Vulkan pipeline cache header");
            sb.AppendLine($"Header size: {header.HeaderSize}");
            sb.AppendLine($"Header version: {header.HeaderVersion}");
            sb.AppendLine($"Vendor ID: {header.VendorHex}");
            sb.AppendLine($"Device ID: {header.DeviceHex}");
            sb.AppendLine($"pipelineCacheUUID: {header.UuidHex}");
            if (VulkanDeviceCombo.SelectedItem is VulkanDeviceInfo device)
            {
                sb.AppendLine();
                sb.AppendLine("Selected current device");
                sb.AppendLine(DescribeVulkanDevice(device));
                sb.AppendLine();
                sb.AppendLine(header.VendorId == device.VendorId && header.DeviceId == device.DeviceId
                    ? header.PipelineCacheUuid.SequenceEqual(device.PipelineCacheUuid)
                        ? "UUID already matches the current Vulkan device/driver."
                        : "Same vendor/device but different pipelineCacheUUID: candidate migration can be created."
                    : "Vendor/device mismatch: migration is blocked.");
            }
            VulkanOutputText.Text = sb.ToString();
        }
        catch (Exception ex) { ShowError("Vulkan cache inspection failed", ex); }
    }

    private async void CreateVulkanCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (VulkanDeviceCombo.SelectedItem is not VulkanDeviceInfo device)
        {
            MessageBox.Show(this, "Probe Vulkan and select a current device first.", "ShaderBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var source = VulkanFileText.Text.Trim();
            var header = _vulkanCache.ReadHeader(source);
            if (header.PipelineCacheUuid.SequenceEqual(device.PipelineCacheUuid))
            {
                MessageBox.Show(this, "This cache already carries the current device's pipelineCacheUUID.", "ShaderBridge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var result = MessageBox.Show(this,
                "Create an experimental COPY with only the Vulkan pipelineCacheUUID changed to the currently selected device?\n\nThe payload remains old driver data and may still be rejected. ShaderBridge will never overwrite the original.",
                "Create migration candidate", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            var destination = await _vulkanCache.CreateCandidateAsync(source, device);
            VulkanOutputText.Text += Environment.NewLine + Environment.NewLine + "Candidate created:" + Environment.NewLine + destination;
            Log($"Created Vulkan UUID migration candidate: {destination}");
        }
        catch (Exception ex) { ShowError("Candidate creation failed", ex); }
    }

    private static string DescribeVulkanDevice(VulkanDeviceInfo d) =>
        $"{d.DeviceName}\nVendor/Device: {d.VendorHex}/{d.DeviceHex}\nDriver version (raw Vulkan value): {d.DriverVersion}\npipelineCacheUUID: {d.UuidHex}";
}
