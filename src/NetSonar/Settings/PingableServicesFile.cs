using NetSonar.Avalonia.Network;
using ObservableCollections;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using NetSonar.Avalonia.Converters;
using ZLinq;

namespace NetSonar.Avalonia.Settings;

public sealed class PingableServicesFile : RootCollectionFile<PingableServicesFile, PingableService>
{
    #region Members
    private readonly Lock _pingRepliesLock = new();
    private Timer? _savePingRepliesTimer;
    #endregion

    /// <inheritdoc />
    [JsonIgnore]
    public override string FileName => "ping_services.json";

    [JsonIgnore]
    public const string RepliesFileName = "ping_replies.json";

    /// <inheritdoc />
    [JsonIgnore]
    protected override JsonSerializerOptions JsonOptions => new(App.JsonSerializerOptions)
    {
        TypeInfoResolver = new ConditionalPingRepliesResolver(includePings: AppSettings.Instance.PingServices.ResilientReplies)
    };

    /// <inheritdoc />
    public override void OnLoaded(bool fromFile)
    {
        var delay = TimeSpan.FromSeconds(60);
        _savePingRepliesTimer = new Timer(_ => SavePingReplies(), null, delay, delay);

        if (!fromFile
            || Count == 0
            || !AppSettings.Instance.PingServices.ResilientReplies) return;
        try
        {
            var pingRepliesFilePath = Path.Combine(DirectoryPath, RepliesFileName);
            if (!File.Exists(pingRepliesFilePath)) return;

            using var pingRepliesStream = new FileStream(
                pingRepliesFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            var dictionary = JsonSerializer.Deserialize<Dictionary<Guid, ObservableList<PingableServiceReply>>>(pingRepliesStream, App.JsonSerializerOptions);

            if (dictionary is null) return;
            var asValueEnumerable = this.AsValueEnumerable();
            foreach (var keyValue in dictionary)
            {
                var service = asValueEnumerable
                    .Where(service => service.Id == keyValue.Key)
                    .FirstOrDefault();
                if (service is null) continue;
                service.Pings.AddRange(keyValue.Value);
                service.RebuildStatistic();
            }
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, $"Read {RepliesFileName}");
        }
    }

    /// <summary>
    /// Saves the ping replies of all services to a file.
    /// </summary>
    public void SavePingReplies()
    {
        if (Count == 0) return;
        if (!AppSettings.Instance.PingServices.ResilientReplies) return;

        lock (_pingRepliesLock)
        {
            try
            {
                var replies = new Dictionary<Guid, ObservableList<PingableServiceReply>>(Count);
                foreach (var service in this)
                {
                    replies[service.Id] = service.Pings;
                }

                using var stream = new FileStream(
                    Path.Combine(DirectoryPath, RepliesFileName),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough);

                JsonSerializer.Serialize(stream, replies, JsonOptions);
            }
            catch (Exception e)
            {
                App.HandleSafeException(e, $"Save {RepliesFileName}");
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _savePingRepliesTimer?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Saves the ping replies for the current instance if it has been created.
    /// </summary>
    /// <remarks>This method performs no action if the instance has not been initialized. Use this method to
    /// persist ping reply data associated with the singleton instance.</remarks>
    public static void SavePingRepliesInstance()
    {
        if (!IsInstanceCreated) return;
        Instance.SavePingReplies();
    }
}