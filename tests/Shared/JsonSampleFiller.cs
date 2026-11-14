using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace DownloadManager.Tests.Shared;

/// <summary>
/// PERF.10 tests: builds a populated sample of any DTO or stored record by reflection, with every public property
/// set to a non-default value (strings, numbers, dates, GUIDs, enums, lists, dictionaries, nested records, JsonElement).
/// Used to compare the generated JSON metadata with the reflection serializer for every registered type.
/// </summary>
internal static class JsonSampleFiller
{
    private const int MaxDepth = 8;

    public static object? Create(Type type, int variant = 0) => Build(type, 0, "Sample", variant);

    public static T Create<T>(int variant = 0) => (T)Build(typeof(T), 0, "Sample", variant)!;

    private static object? Build(Type type, int depth, string name, int variant)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) type = underlying;

        if (type == typeof(string)) return $"{name}-{variant}";
        if (type == typeof(bool)) return variant % 2 == 0;
        if (type == typeof(int)) return 7 + variant;
        if (type == typeof(uint)) return 7u + (uint)variant;
        if (type == typeof(long)) return 1_234_567_890_123L + variant;
        if (type == typeof(ulong)) return 1_234_567_890_123UL + (ulong)variant;
        if (type == typeof(short)) return (short)(11 + variant);
        if (type == typeof(ushort)) return (ushort)(11 + variant);
        if (type == typeof(byte)) return (byte)(5 + variant);
        if (type == typeof(sbyte)) return (sbyte)(5 + variant);
        if (type == typeof(double)) return 3.25 + variant;
        if (type == typeof(float)) return 2.5f + variant;
        if (type == typeof(decimal)) return 12.75m + variant;
        if (type == typeof(char)) return 'x';
        if (type == typeof(Guid)) return new Guid(new byte[] { (byte)(0x5b + variant), 0xad, 0x8f, 0x0f, 0xcb, 0xd9, 0x9f, 0x46, 0xa1, 0x65, 0x70, 0x86, 0x77, 0x28, 0x95, 0x0e });
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 11, 14, 10, 30, 15, 123, TimeSpan.FromHours(2)).AddDays(variant);
        if (type == typeof(DateTime)) return new DateTime(2026, 11, 14, 10, 30, 15, 123, DateTimeKind.Utc).AddDays(variant);
        if (type == typeof(DateOnly)) return new DateOnly(2026, 11, 14).AddDays(variant);
        if (type == typeof(TimeOnly)) return new TimeOnly(10, 30, 15);
        if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(90.5 + variant);
        if (type == typeof(Uri)) return new Uri("https://example.test/a/b?c=" + variant);
        if (type == typeof(Version)) return new Version(1, 2, 3 + variant);
        if (type == typeof(JsonElement)) return JsonDocument.Parse("{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":2.5}}").RootElement.Clone();
        if (type == typeof(byte[])) return new byte[] { 1, 2, 3, (byte)variant };
        if (type == typeof(object)) return "object-" + variant;

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().Where(v => !IsDefault(v)).ToArray();
            if (values.Length == 0) values = Enum.GetValues(type).Cast<object>().ToArray();
            return values[variant % values.Length];
        }

        if (depth >= MaxDepth) return Default(type);

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var array = Array.CreateInstance(element, Count(depth));
            for (var i = 0; i < Count(depth); i++) array.SetValue(Build(element, depth + 1, name, variant + i), i);
            return array;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>) ||
                definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args[0]))!;
                for (var i = 0; i < Count(depth); i++) list.Add(Build(args[0], depth + 1, name, variant + i));
                return list;
            }

            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>))
            {
                var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args[0]))!;
                var add = set.GetType().GetMethod("Add")!;
                for (var i = 0; i < Count(depth); i++) add.Invoke(set, [Build(args[0], depth + 1, name, variant + i)]);
                return set;
            }

            if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                for (var i = 0; i < Count(depth); i++)
                {
                    var key = Build(args[0], depth + 1, "key", variant + i)!;
                    dictionary[key] = Build(args[1], depth + 1, name, variant + i);
                }

                return dictionary;
            }
        }

        if (type.IsAbstract || type.IsInterface) return Default(type);

        // The constructor with the most parameters (a record's primary constructor), else the parameterless one.
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        object? instance;
        if (ctor is null)
        {
            if (!type.IsValueType) return null;
            instance = Activator.CreateInstance(type);
        }
        else
        {
            var parameters = ctor.GetParameters();
            var arguments = parameters.Select(p => Build(p.ParameterType, depth + 1, Pascal(p.Name!), variant)).ToArray();
            try { instance = ctor.Invoke(arguments); }
            catch (TargetInvocationException) { return Default(type); }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            if (property.GetCustomAttributes().Any(a => a.GetType().Name == "JsonIgnoreAttribute" && IsAlways(a))) continue;
            try
            {
                if (property.SetMethod is { IsPublic: true })
                {
                    property.SetValue(instance, Build(property.PropertyType, depth + 1, property.Name, variant));
                }
                else if (property.GetMethod is not null && instance is not null && property.GetValue(instance) is { } existing)
                {
                    // Get-only collection: add to the one the constructor made.
                    if (existing is IList { IsReadOnly: false, IsFixedSize: false } list && property.PropertyType.IsGenericType)
                    {
                        var item = Build(property.PropertyType.GetGenericArguments()[0], depth + 1, property.Name, variant);
                        list.Add(item);
                    }
                    else if (existing is IDictionary { IsReadOnly: false } dict && property.PropertyType.IsGenericType)
                    {
                        var args = property.PropertyType.GetGenericArguments();
                        dict[Build(args[0], depth + 1, "key", variant)!] = Build(args[1], depth + 1, property.Name, variant);
                    }
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidCastException or NotSupportedException)
            {
                // A setter that validates: the property stays at its default.
            }
        }

        return instance;
    }

    /// <summary>Two items near the root, one deeper down, so a big snapshot stays small.</summary>
    private static int Count(int depth) => depth <= 3 ? 2 : 1;

    private static bool IsAlways(Attribute attribute)
    {
        var condition = attribute.GetType().GetProperty("Condition")?.GetValue(attribute)?.ToString();
        return condition is null or "Always";
    }

    private static bool IsDefault(object value) => Convert.ToInt64(value) == 0;

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
