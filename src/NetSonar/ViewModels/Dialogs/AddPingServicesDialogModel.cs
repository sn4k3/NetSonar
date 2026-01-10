using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Models;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using ZLinq;

namespace NetSonar.Avalonia.ViewModels.Dialogs;

public partial class AddPingServicesDialogModel : DialogViewModelBase
{
    public FastObservableCollection<NewPingService> Services { get; } = [];

    public AddPingServicesDialogModel(ISukiDialog dialog) : base(dialog)
    {
        AddEmpty();
    }


    [RelayCommand]
    public void Clear()
    {
        Services.Clear();
        AddEmpty();
    }

    [RelayCommand]
    public void AddEmpty()
    {
        Services.Add(new NewPingService());
    }

    [RelayCommand]
    public async Task ImportFromJson()
    {
        var files = await TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            AllowMultiple = true,
            FileTypeFilter = AvaloniaExtensions.FilePickerJson,
        });

        if (files.Count == 0) return;

        foreach (var file in files)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                var services = await JsonSerializer.DeserializeAsync<PingableService[]>(stream,  App.JsonSerializerOptions);
                if (services is null) continue;
                PurgeEmptyRecords();
                AddUniques(services.Select(service => new NewPingService(service)));
            }
            catch (Exception e)
            {
                App.ShowExceptionToast("Unable to import from json", "The provided json file is invalid or malformed.");
                 App.HandleSafeException(e, "Import service from json");
            }

        }
    }

    [RelayCommand]
    public void ImportPublicDns()
    {
        PurgeEmptyRecords();
        AddUniques(DnsProvider.DnsProviders
            .Where(dnsProvider => dnsProvider.DNSv4PrimaryAddress.IsValid())
            .Select(dnsProvider => new NewPingService(ServiceProtocolType.ICMP, dnsProvider.DNSv4PrimaryAddress,
                $"{dnsProvider.ProviderName} ({string.Join(", ", dnsProvider.BlockCategories)})", "DNS")));
    }

    [RelayCommand]
    public void ImportNetworkGateways()
    {
        var cache = new List<NewPingService>();
        var gatewayCount = 0;
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var address in network.GetIPProperties().GatewayAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || !address.Address.IsValid()) continue;
                cache.Add(new NewPingService(ServiceProtocolType.ICMP, address.Address, $"Gateway {++gatewayCount}", "Network Gateway"));
            }
        }

        if (cache.Count == 0) return;
        PurgeEmptyRecords();
        AddUniques(cache);
    }

    [RelayCommand]
    public void RemoveServices(IList list)
    {
        Services.RemoveRange(list.Cast<NewPingService>());
    }

    private void PurgeEmptyRecords()
    {
        for (var i = Services.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(Services[0].IpAddressOrUrl)) Services.RemoveAt(i);
        }
    }

    private void AddUniques(IEnumerable<NewPingService> services)
    {
        Services.AddRange(services.Where(service => !Services.Contains(service)));
    }

    protected override bool ValidateInternal()
    {
        foreach (var service in Services)
        {
            if (service.Validate()) continue;
            CustomErrors.Add(service.GetErrorsRaw());
        }

        return base.ValidateInternal();
    }

    protected override Task<bool> ApplyInternal()
    {
        var importCount = 0;

        foreach (var service in Services)
        {
            // Check for duplicates
            if (PingableServicesFile.Instance.AsValueEnumerable().FirstOrDefault(pingableService =>
                    pingableService.ProtocolType == service.ProtocolType &&
                    pingableService.IpAddressOrUrl == service.IpAddressOrUrl) is not null) continue;
            try
            {
                PingableServicesFile.Instance.Add(new PingableService(service));
                importCount++;
            }
            catch (Exception ex)
            {
                 App.HandleSafeException(ex, "Add Service");
            }
        }

        ToastManager.CreateSimpleInfoToast()
            .WithContent($"{importCount} services were imported.")
            .Queue();

        return base.ApplyInternal();
    }
}