using System;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max) { }
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class FilePathAttribute : Attribute
    {
        public enum Location { ProjectFolder }
        public FilePathAttribute(string path, Location location) { }
    }

    public class ScriptableSingleton<T> where T : new()
    {
        protected static readonly T instance = new T();
        protected void Save(bool saveAsText) { }
    }

    public static class EditorPrefs
    {
        public static string GetString(string key, string defaultValue) { return defaultValue; }
        public static void SetString(string key, string value) { }
    }
}