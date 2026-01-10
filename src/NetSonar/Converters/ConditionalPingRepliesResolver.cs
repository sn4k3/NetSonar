using NetSonar.Avalonia.Network;
using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ZLinq;

namespace NetSonar.Avalonia.Converters;

public class ConditionalPingRepliesResolver(bool includePings) : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        if (typeInfo.Type == typeof(PingableService))
        {
            var pingsProperty = typeInfo.Properties.AsValueEnumerable()
                .FirstOrDefault(p => p.Name == nameof(PingableService.Pings));
            if (pingsProperty is not null && !includePings)
            {
                pingsProperty.ShouldSerialize = (obj, value) => false;
            }
        }

        return typeInfo;
    }
}