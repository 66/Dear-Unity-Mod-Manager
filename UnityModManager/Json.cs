﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace UnityModManagerNet;

// Really simple JSON parser in ~300 lines
// - Attempts to parse JSON files with minimal GC allocation
// - Nice and simple "[1,2,3]".FromJson<List<int>>() API
// - Classes and structs can be parsed too!
//      class Foo { public int Value; }
//      "{\"Value\":10}".FromJson<Foo>()
// - Can parse JSON without type information into Dictionary<string,object> and List<object> e.g.
//      "[1,2,3]".FromJson<object>().GetType() == typeof(List<object>)
//      "{\"Value\":10}".FromJson<object>().GetType() == typeof(Dictionary<string,object>)
// - No JIT Emit support to support AOT compilation on iOS
// - Attempts are made to NOT throw an exception if the JSON is corrupted or invalid: returns null instead.
// - Only public fields and property setters on classes/structs will be written to
//
// Limitations:
// - No JIT Emit support to parse structures quickly
// - Limited to parsing <2GB JSON files (due to int.MaxValue)
// - Parsing of abstract classes or interfaces is NOT supported and will throw an exception.
public static class JSONParser
{
    [ThreadStatic] private static Stack<List<string>> splitArrayPool;
    [ThreadStatic] private static StringBuilder stringBuilder;
    [ThreadStatic] private static Dictionary<Type, Dictionary<string, FieldInfo>> fieldInfoCache;
    [ThreadStatic] private static Dictionary<Type, Dictionary<string, PropertyInfo>> propertyInfoCache;

    public static T FromJson<T>(this string json)
    {
        // Initialize, if needed, the ThreadStatic variables
        if (propertyInfoCache == null)
            propertyInfoCache = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
        if (fieldInfoCache == null)
            fieldInfoCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();
        if (stringBuilder == null)
            stringBuilder = new StringBuilder();
        if (splitArrayPool == null)
            splitArrayPool = new Stack<List<string>>();

        //Remove all whitespace not within strings to make parsing simpler
        stringBuilder.Length = 0;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '"')
            {
                i = AppendUntilStringEnd(true, i, json);
                continue;
            }

            if (char.IsWhiteSpace(c))
                continue;

            stringBuilder.Append(c);
        }

        //Parse the thing!
        return (T)ParseValue(typeof(T), stringBuilder.ToString());
    }

    private static int AppendUntilStringEnd(bool appendEscapeCharacter, int startIdx, string json)
    {
        stringBuilder.Append(json[startIdx]);
        for (var i = startIdx + 1; i < json.Length; i++)
            if (json[i] == '\\')
            {
                if (appendEscapeCharacter)
                    stringBuilder.Append(json[i]);
                stringBuilder.Append(json[i + 1]);
                i++; //Skip next character as it is escaped
            }
            else if (json[i] == '"')
            {
                stringBuilder.Append(json[i]);
                return i;
            }
            else
            {
                stringBuilder.Append(json[i]);
            }

        return json.Length - 1;
    }

    //Splits { <value>:<value>, <value>:<value> } and [ <value>, <value> ] into a list of <value> strings
    private static List<string> Split(string json)
    {
        var splitArray = splitArrayPool.Count > 0 ? splitArrayPool.Pop() : new List<string>();
        splitArray.Clear();
        if (json.Length == 2)
            return splitArray;
        var parseDepth = 0;
        stringBuilder.Length = 0;
        for (var i = 1; i < json.Length - 1; i++)
        {
            switch (json[i])
            {
                case '[':
                case '{':
                    parseDepth++;
                    break;
                case ']':
                case '}':
                    parseDepth--;
                    break;
                case '"':
                    i = AppendUntilStringEnd(true, i, json);
                    continue;
                case ',':
                case ':':
                    if (parseDepth == 0)
                    {
                        splitArray.Add(stringBuilder.ToString());
                        stringBuilder.Length = 0;
                        continue;
                    }
                    break;
            }

            stringBuilder.Append(json[i]);
        }

        splitArray.Add(stringBuilder.ToString());

        return splitArray;
    }

    internal static object ParseValue(Type type, string json)
    {
        if (type == typeof(string))
        {
            if (json.Length <= 2)
                return string.Empty;
            var parseStringBuilder = new StringBuilder(json.Length);
            for (var i = 1; i < json.Length - 1; ++i)
            {
                if (json[i] == '\\' && i + 1 < json.Length - 1)
                {
                    var j = "\"\\nrtbf/".IndexOf(json[i + 1]);
                    if (j >= 0)
                    {
                        parseStringBuilder.Append("\"\\\n\r\t\b\f/"[j]);
                        ++i;
                        continue;
                    }

                    if (json[i + 1] == 'u' && i + 5 < json.Length - 1)
                    {
                        if (uint.TryParse(json.Substring(i + 2, 4), NumberStyles.AllowHexSpecifier, null, out var c))
                        {
                            parseStringBuilder.Append((char)c);
                            i += 5;
                            continue;
                        }
                    }
                }

                parseStringBuilder.Append(json[i]);
            }

            return parseStringBuilder.ToString();
        }

        if (type.IsPrimitive)
        {
            var result = Convert.ChangeType(json, type, CultureInfo.InvariantCulture);
            return result;
        }

        if (type == typeof(decimal))
        {
            decimal.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var result);
            return result;
        }

        if (json == "null") return null;
        if (type.IsEnum)
        {
            if (json[0] == '"')
                json = json.Substring(1, json.Length - 2);
            try
            {
                return Enum.Parse(type, json, false);
            }
            catch
            {
                return 0;
            }
        }

        if (type.IsArray)
        {
            var arrayType = type.GetElementType();
            if (json[0] != '[' || json[json.Length - 1] != ']')
                return null;

            var elems = Split(json);
            var newArray = Array.CreateInstance(arrayType, elems.Count);
            for (var i = 0; i < elems.Count; i++)
                newArray.SetValue(ParseValue(arrayType, elems[i]), i);
            splitArrayPool.Push(elems);
            return newArray;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var listType = type.GetGenericArguments()[0];
            if (json[0] != '[' || json[json.Length - 1] != ']')
                return null;

            var elems = Split(json);
            var list = (IList)type.GetConstructor(new[] { typeof(int) })?.Invoke(new object[] { elems.Count });
            foreach (var t in elems)
                list?.Add(ParseValue(listType, t));

            splitArrayPool.Push(elems);
            return list;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            Type keyType, valueType;
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
            }

            //Refuse to parse dictionary keys that aren't of type string
            if (keyType != typeof(string))
                return null;
            //Must be a valid dictionary element
            if (json[0] != '{' || json[json.Length - 1] != '}')
                return null;
            //The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
            var elems = Split(json);
            if (elems.Count % 2 != 0)
                return null;

            var dictionary =
                (IDictionary)type.GetConstructor(new[] { typeof(int) })?.Invoke(new object[] { elems.Count / 2 });
            for (var i = 0; i < elems.Count; i += 2)
            {
                if (elems[i].Length <= 2)
                    continue;
                var keyValue = elems[i].Substring(1, elems[i].Length - 2);
                var val = ParseValue(valueType, elems[i + 1]);
                dictionary?.Add(keyValue, val);
            }

            return dictionary;
        }

        if (type == typeof(object)) return ParseAnonymousValue(json);
        if (json[0] == '{' && json[json.Length - 1] == '}') return ParseObject(type, json);

        return null;
    }

    private static object ParseAnonymousValue(string json)
    {
        if (json.Length == 0)
            return null;
        if (json[0] == '{' && json[json.Length - 1] == '}')
        {
            var elems = Split(json);
            if (elems.Count % 2 != 0)
                return null;
            var dict = new Dictionary<string, object>(elems.Count / 2);
            for (var i = 0; i < elems.Count; i += 2)
                dict.Add(elems[i].Substring(1, elems[i].Length - 2), ParseAnonymousValue(elems[i + 1]));
            return dict;
        }

        if (json[0] == '[' && json[json.Length - 1] == ']')
        {
            var items = Split(json);
            var finalList = new List<object>(items.Count);
            finalList.AddRange(items.Select(ParseAnonymousValue));
            return finalList;
        }

        if (json[0] == '"' && json[json.Length - 1] == '"')
        {
            var str = json.Substring(1, json.Length - 2);
            return str.Replace("\\", string.Empty);
        }

        if (char.IsDigit(json[0]) || json[0] == '-')
        {
            if (json.Contains("."))
            {
                double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var result);
                return result;
            }
            else
            {
                int.TryParse(json, out var result);
                return result;
            }
        }

        switch (json)
        {
            case "true":
                return true;
            case "false":
                return false;
            default:
                // handles json == "null" as well as invalid JSON
                return null;
        }
    }

    private static Dictionary<string, T> CreateMemberNameDictionary<T>(T[] members) where T : MemberInfo
    {
        var nameToMember = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var name = member.Name;
            nameToMember.Add(name, member);
        }

        return nameToMember;
    }

    private static object ParseObject(Type type, string json)
    {
        var instance = FormatterServices.GetUninitializedObject(type);

        //The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
        var elems = Split(json);
        if (elems.Count % 2 != 0)
            return instance;

        if (!fieldInfoCache.TryGetValue(type, out var nameToField))
        {
            nameToField = CreateMemberNameDictionary(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
            fieldInfoCache.Add(type, nameToField);
        }

        if (!propertyInfoCache.TryGetValue(type, out var nameToProperty))
        {
            nameToProperty = CreateMemberNameDictionary(
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
            propertyInfoCache.Add(type, nameToProperty);
        }

        for (var i = 0; i < elems.Count; i += 2)
        {
            if (elems[i].Length <= 2)
                continue;
            var key = elems[i].Substring(1, elems[i].Length - 2);
            var value = elems[i + 1];

            if (nameToField.TryGetValue(key, out var fieldInfo))
                fieldInfo.SetValue(instance, ParseValue(fieldInfo.FieldType, value));
            else if (nameToProperty.TryGetValue(key, out var propertyInfo))
                propertyInfo.SetValue(instance, ParseValue(propertyInfo.PropertyType, value), null);
        }

        return instance;
    }
}

//Really simple JSON writer
//- Outputs JSON structures from an object
//- Really simple API (new List<int> { 1, 2, 3 }).ToJson() == "[1,2,3]"
//- Will only output public fields and property getters on objects
public static class JSONWriter
{
    public static string ToJson(this object item)
    {
        var stringBuilder = new StringBuilder();
        AppendValue(stringBuilder, item);
        return stringBuilder.ToString();
    }

    private static void AppendValue(StringBuilder stringBuilder, object item)
    {
        if (item == null)
        {
            stringBuilder.Append("null");
            return;
        }

        var type = item.GetType();
        if (type == typeof(string))
        {
            stringBuilder.Append('"');
            var str = (string)item;
            foreach (var t in str)
                if (t < ' ' || t == '"' || t == '\\')
                {
                    stringBuilder.Append('\\');
                    var j = "\"\\\n\r\t\b\f".IndexOf(t);
                    if (j >= 0)
                        stringBuilder.Append("\"\\nrtbf"[j]);
                    else
                        stringBuilder.AppendFormat("u{0:X4}", (uint)t);
                }
                else
                {
                    stringBuilder.Append(t);
                }

            stringBuilder.Append('"');
        }
        else if (type == typeof(byte) || type == typeof(int))
        {
            stringBuilder.Append(item);
        }
        else if (type == typeof(float))
        {
            stringBuilder.Append(((float)item).ToString(CultureInfo.InvariantCulture));
        }
        else if (type == typeof(double))
        {
            stringBuilder.Append(((double)item).ToString(CultureInfo.InvariantCulture));
        }
        else if (type == typeof(bool))
        {
            stringBuilder.Append((bool)item ? "true" : "false");
        }
        else if (type.IsEnum)
        {
            stringBuilder.Append('"');
            stringBuilder.Append(item);
            stringBuilder.Append('"');
        }
        else if (item is IList list)
        {
            stringBuilder.Append('[');
            var isFirst = true;
            foreach (var t in list)
            {
                if (isFirst)
                    isFirst = false;
                else
                    stringBuilder.Append(',');
                AppendValue(stringBuilder, t);
            }

            stringBuilder.Append(']');
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var keyType = type.GetGenericArguments()[0];

            //Refuse to output dictionary keys that aren't of type string
            if (keyType != typeof(string))
            {
                stringBuilder.Append("{}");
                return;
            }

            stringBuilder.Append('{');
            var dict = item as IDictionary;
            var isFirst = true;
            foreach (var key in dict.Keys)
            {
                if (isFirst)
                    isFirst = false;
                else
                    stringBuilder.Append(',');
                stringBuilder.Append('\"');
                stringBuilder.Append((string)key);
                stringBuilder.Append("\":");
                AppendValue(stringBuilder, dict[key]);
            }

            stringBuilder.Append('}');
        }
        else
        {
            stringBuilder.Append('{');

            var isFirst = true;
            var fieldInfos =
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            foreach (var t in fieldInfos)
            {
                var value = t.GetValue(item);
                if (value == null) continue;
                if (isFirst)
                    isFirst = false;
                else
                    stringBuilder.Append(',');
                stringBuilder.Append('\"');
                stringBuilder.Append(GetMemberName(t));
                stringBuilder.Append("\":");
                AppendValue(stringBuilder, value);
            }

            var propertyInfo =
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            foreach (var t in propertyInfo)
            {
                var value = t.GetValue(item, null);
                if (value == null) continue;
                if (isFirst)
                    isFirst = false;
                else
                    stringBuilder.Append(',');
                stringBuilder.Append('\"');
                stringBuilder.Append(GetMemberName(t));
                stringBuilder.Append("\":");
                AppendValue(stringBuilder, value);
            }

            stringBuilder.Append('}');
        }
    }

    private static string GetMemberName(MemberInfo member)
    {
        return member.Name;
    }
}