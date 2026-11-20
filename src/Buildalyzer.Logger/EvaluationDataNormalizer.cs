using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Build.Framework;

namespace Buildalyzer.Logger;

/// <summary>
/// Rewrites a live <see cref="ProjectEvaluationFinishedEventArgs"/> into the shape XenoAtom's pipe serializer
/// understands.
/// </summary>
/// <remarks>
/// In a live build the evaluation's properties and items are MSBuild's own objects, not the
/// <see cref="DictionaryEntry"/> pairs a replayed binary log (or a ProjectStarted event) carries:
/// <list type="bullet">
/// <item>properties are ProjectPropertyInstances;</item>
/// <item>items are an ItemDictionary, which enumerates as flat ProjectItemInstances through
/// <see cref="IEnumerable{T}"/> but as per-item-type groups, KeyValuePair&lt;string, List&lt;ProjectItemInstance&gt;&gt;,
/// through the non-generic <see cref="IEnumerable"/>.</item>
/// </list>
/// XenoAtom's serializer keeps only DictionaryEntry (and, for properties, KeyValuePair&lt;string, string&gt;)
/// entries and silently drops everything else, so a live evaluation would cross the pipe with no properties
/// and no items. Those MSBuild types live in Microsoft.Build.dll, which this logger does not reference
/// (Microsoft.Build.Framework has no interface for them), so their members are read by name - the same
/// members MSBuild's own binary log writer reads.
/// </remarks>
internal static class EvaluationDataNormalizer
{
    private static readonly ConcurrentDictionary<(Type Type, string Member), PropertyInfo?> Members = new();

    /// <summary>Returns a copy of <paramref name="e"/> whose properties and items are DictionaryEntry pairs.</summary>
    /// <remarks>
    /// A copy, because the same event instance is handed to every other logger in the build (the binary logger
    /// among them).
    /// </remarks>
    public static ProjectEvaluationFinishedEventArgs Normalize(ProjectEvaluationFinishedEventArgs e) =>
        new(e.Message)
        {
            BuildEventContext = e.BuildEventContext,
            ProjectFile = e.ProjectFile,
            Properties = NormalizeProperties(e.Properties),
            Items = NormalizeItems(e.Items),
        };

    internal static List<DictionaryEntry>? NormalizeProperties(IEnumerable? properties)
    {
        if (properties is null)
        {
            return null;
        }

        List<DictionaryEntry> result = [];
        foreach (object property in properties)
        {
            if (property is DictionaryEntry entry)
            {
                result.Add(entry);
            }
            else if (property is KeyValuePair<string, string> pair)
            {
                result.Add(new DictionaryEntry(pair.Key, pair.Value));
            }
            else if (Read(property, "Name") is string name)
            {
                result.Add(new DictionaryEntry(name, Read(property, "EvaluatedValue") as string ?? string.Empty));
            }
        }

        return result;
    }

    internal static List<DictionaryEntry>? NormalizeItems(IEnumerable? items)
    {
        if (items is null)
        {
            return null;
        }

        List<DictionaryEntry> result = [];
        foreach (object item in items)
        {
            if (item is DictionaryEntry entry)
            {
                result.Add(entry);
            }
            else if (item is ITaskItem taskItem && Read(item, "ItemType") is string itemType)
            {
                result.Add(new DictionaryEntry(itemType, taskItem));
            }
            else if (Read(item, "Key") is string groupItemType && Read(item, "Value") is IEnumerable group)
            {
                foreach (object member in group)
                {
                    if (member is ITaskItem groupItem)
                    {
                        result.Add(new DictionaryEntry(groupItemType, groupItem));
                    }
                }
            }
        }

        return result;
    }

    private static object? Read(object instance, string member)
        => Members.GetOrAdd(
            (instance.GetType(), member),
            static key => key.Type.GetProperty(key.Member, BindingFlags.Public | BindingFlags.Instance))
            ?.GetValue(instance);
}
