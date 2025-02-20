using System.Collections.Generic;

namespace FroggyFilter.Framework.Extensions;

internal static class CollectionExtensions
{
    public static TValue GetOrCreate<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        where TValue : new()
    {
        if (dictionary.TryGetValue(key, out var value))
        {
            return value;
        }

        dictionary[key] = defaultValue;
        return defaultValue;
    }
}