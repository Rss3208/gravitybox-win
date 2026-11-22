using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GravityBox.App.Core;

public static class JsonNodeExtensions
{
    public static string? AsString(this JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<string>(out var s)) return s;
            if (val.TryGetValue<JsonElement>(out var el))
            {
                return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            }
        }
        return node.ToString();
    }

    public static long AsInt64(this JsonNode? node, long fallback = 0)
    {
        if (node == null) return fallback;
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<long>(out var l)) return l;
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<double>(out var d)) return (long)d;
                if (val.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
                {
                    return el.TryGetInt64(out var parsed) ? parsed : (long)el.GetDouble();
                }
            }
        }
        catch
        {
            // Fallback
        }
        return fallback;
    }

    public static double AsDouble(this JsonNode? node, double fallback = 0.0)
    {
        if (node == null) return fallback;
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<double>(out var d)) return d;
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<long>(out var l)) return l;
                if (val.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
                {
                    return el.GetDouble();
                }
            }
        }
        catch
        {
            // Fallback
        }
        return fallback;
    }

    public static double? AsNullableDouble(this JsonNode? node)
    {
        if (node == null) return null;
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<double>(out var d)) return d;
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<long>(out var l)) return l;
                if (val.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
                {
                    return el.GetDouble();
                }
            }
        }
        catch
        {
            // Fallback
        }
        return null;
    }

    public static bool AsBool(this JsonNode? node, bool fallback = false)
    {
        if (node == null) return fallback;
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<bool>(out var b)) return b;
                if (val.TryGetValue<JsonElement>(out var el))
                {
                    if (el.ValueKind == JsonValueKind.True) return true;
                    if (el.ValueKind == JsonValueKind.False) return false;
                }
            }
        }
        catch
        {
            // Fallback
        }
        return fallback;
    }
}
