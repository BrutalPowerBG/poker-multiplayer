using UnityEngine;
using System.Text;

public static class Log
{
    public static void Info(string tag, string message, Object context = null)
    {
        UnityEngine.Debug.Log(Format(tag, message), context);
    }

    public static void Warn(string tag, string message, Object context = null)
    {
        UnityEngine.Debug.LogWarning(Format(tag, message), context);
    }

    public static void Error(string tag, string message, Object context = null)
    {
        UnityEngine.Debug.LogError(Format(tag, message), context);
    }

    public static void Info(string tag, string action, Object context, params (string key, object value)[] data)
    {
        UnityEngine.Debug.Log(Format(tag, action, data), context);
    }

    public static void Warn(string tag, string action, Object context, params (string key, object value)[] data)
    {
        UnityEngine.Debug.LogWarning(Format(tag, action, data), context);
    }

    public static void Error(string tag, string action, Object context, params (string key, object value)[] data)
    {
        UnityEngine.Debug.LogError(Format(tag, action, data), context);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    public static void Debug(string tag, string message, Object context = null)
    {
        UnityEngine.Debug.Log(Format(tag, message), context);
    }

    private static string Format(string tag, string message) => $"[{tag}] {message}";

    private static string Format(string tag, string action, (string key, object value)[] data)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(tag).Append("] ").Append(action);
        if (data.Length > 0)
        {
            sb.Append(" \u2014 ");
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(data[i].key).Append('=').Append(data[i].value ?? "<none>");
            }
        }
        return sb.ToString();
    }
}
